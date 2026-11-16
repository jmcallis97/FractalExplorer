using System.Diagnostics;

namespace Fractal;

/// <summary>
/// Runs renders in the background, at most one at a time. If the view changes meanwhile, the
/// next render starts when this one finishes (a pan builds on it) or as soon as it is cancelled
/// (a zoom or a new iteration limit can't reuse its work). After each render it "chases" the
/// view: whatever the user did meanwhile gets rendered next.
///
/// Renders predicted to be slow get quick low-resolution previews first. All events are raised
/// on the UI thread (through <paramref name="post"/>); <paramref name="wantedView"/> gives the
/// view to chase, or null when there is nothing to render (no window yet, or minimised).
/// </summary>
internal sealed class RenderPipeline(MandelbrotRenderer renderer, Action<Action> post, Func<Viewport?> wantedView)
{
    /// <summary>Renders predicted to take longer than this get a low-resolution preview first.</summary>
    private const double PreviewThresholdMs = 100;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float[]? _spare; // recycled buffer to avoid big allocations while dragging
    private Task? _running;
    private CancellationTokenSource? _runningCts;
    private Viewport _runningView;
    private double _runningStarted;
    private bool _forceFull; // next render ignores the previous field (for honest timing)

    /// <summary>The last completed render.</summary>
    public Field? Field { get; private set; }

    /// <summary>The render in progress (its values fill in as it goes), or null.</summary>
    public Field? RunningTarget { get; private set; }

    public RenderProgress? Progress { get; private set; }
    public bool IsRunning => _running != null;
    public double RunningSeconds => _running != null ? _clock.Elapsed.TotalSeconds - _runningStarted : 0;
    public long LastRenderMs { get; private set; }
    public long LastRenderPixels { get; private set; }

    /// <summary>From the last full (non-reuse) render; predicts how long the next one will take.</summary>
    public double MsPerMegapixel { get; private set; }

    /// <summary>Show previews (and, in the form, renders as they fill in).</summary>
    public bool Progressive { get; set; } = true;

    /// <summary>A preview of the running render is ready: (running render, preview).</summary>
    public event Action<Field, Field>? PreviewReady;

    /// <summary>A render finished; it is now <see cref="Field"/>.</summary>
    public event Action<Field>? Completed;

    /// <summary>A render was cancelled (exception null) or failed. Its values may still be on screen.</summary>
    public event Action<Field, Exception?>? Stopped;

    /// <summary>Renders <paramref name="want"/> unless it is already shown or on its way.</summary>
    public void Request(Viewport want)
    {
        if (_running != null)
        {
            // A pan can build on the in-flight render once it lands; a zoom can't, so drop it.
            if (!_runningView.CanReuse(want)) _runningCts!.Cancel();
        }
        else if (Field == null || Field.View != want)
        {
            Start(want);
        }
    }

    /// <summary>Re-renders everything from scratch, e.g. to time a kernel change.</summary>
    public void ForceFull(Viewport want)
    {
        _forceFull = true;
        if (_running != null) _runningCts!.Cancel();
        else Start(want);
    }

    /// <summary>Starts <paramref name="want"/> if nothing is running; never cancels a render.</summary>
    public void StartIfIdle(Viewport want)
    {
        if (_running == null && (Field == null || Field.View != want)) Start(want);
    }

    /// <summary>Hands back a stopped render's buffer once it is no longer on screen.</summary>
    public void Recycle(Field field) => _spare = field.Values;

    public void Cancel() => _runningCts?.Cancel();

    private void Start(Viewport view)
    {
        var source = _forceFull ? null : Field;
        _forceFull = false;
        int n = view.Width * view.Height;
        var values = _spare?.Length == n ? _spare : new float[n];
        _spare = null;

        var target = new Field(view, values);
        var cts = new CancellationTokenSource();
        var progress = new RenderProgress();
        _runningCts = cts;
        _runningView = view;
        Progress = progress;
        RunningTarget = target;
        _runningStarted = _clock.Elapsed.TotalSeconds;

        bool reuse = source != null && source.View.CanReuse(view);
        int[] previewFactors = reuse ? [] : ChoosePreviewFactors(view);

        var sw = Stopwatch.StartNew();
        _running = Task.Run(() =>
        {
            // Coarse previews first: tiny cost (1/64 of the pixels at factor 8) but something
            // sharp-ish on screen almost immediately.
            foreach (int factor in previewFactors)
            {
                var pv = PreviewView(view, factor);
                var pf = new Field(pv, new float[pv.Width * pv.Height]);
                renderer.Render(pf, null, new RenderProgress(), cts.Token);
                post(() => OnPreviewReady(target, pf));
            }
            renderer.Render(target, source, progress, cts.Token);
        }, cts.Token);
        _running.ContinueWith(t =>
        {
            long ms = sw.ElapsedMilliseconds;
            post(() => OnFinished(t, target, progress, ms, reuse));
        }, TaskScheduler.Default);
    }

    private int[] ChoosePreviewFactors(Viewport view)
    {
        if (!Progressive) return [];
        double estimateMs = MsPerMegapixel * view.Width * view.Height / 1e6;
        if (estimateMs < PreviewThresholdMs) return [];
        return estimateMs < 1500 ? [8] : [16, 4];
    }

    /// <summary>
    /// Same anchor and iteration limit, <paramref name="factor"/>× coarser pixels, centred to
    /// within one coarse pixel. Sharing the anchor means a deep preview reuses the reference orbit.
    /// </summary>
    private static Viewport PreviewView(Viewport v, int factor) =>
        new(v.AnchorX, v.AnchorY, v.Scale * factor,
            (long)Math.Round((double)v.PanX / factor), (long)Math.Round((double)v.PanY / factor),
            (v.Width + factor - 1) / factor, (v.Height + factor - 1) / factor, v.MaxIterations, v.Deep);

    private void OnPreviewReady(Field target, Field preview)
    {
        if (RunningTarget != target) return; // a newer render has started
        PreviewReady?.Invoke(target, preview);
    }

    private void OnFinished(Task task, Field target, RenderProgress progress, long ms, bool reuse)
    {
        _running = null;
        _runningCts?.Dispose();
        _runningCts = null;
        Progress = null;
        RunningTarget = null;

        if (task.IsCompletedSuccessfully)
        {
            _spare = Field?.Values;
            Field = target;
            LastRenderMs = ms;
            LastRenderPixels = progress.Total;
            if (!reuse) MsPerMegapixel = ms / (target.View.Width * (double)target.View.Height / 1e6);
            Completed?.Invoke(target);
        }
        else
        {
            Stopped?.Invoke(target, task.IsFaulted ? task.Exception?.GetBaseException() : null);
        }

        // Chase whatever the user has done since this render started (a failed render isn't
        // retried for the same view, or it would fail forever).
        if (wantedView() is { } want)
        {
            bool sameAsFailed = task.IsFaulted && want == target.View;
            if (!sameAsFailed && (Field == null || Field.View != want || _forceFull)) Start(want);
        }
    }
}
