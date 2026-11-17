using System.Diagnostics;
using System.Runtime.InteropServices;
using Fractal.Gpu;

namespace Fractal;

/// <summary>
/// The explorer window. It ties together the view (<see cref="ViewState"/>), background
/// rendering (<see cref="RenderPipeline"/>), colouring (<see cref="ColourState"/>), snap dives
/// (<see cref="DiveController"/>) and what's drawn on top (<see cref="Overlay"/>).
/// Input handling is in MainForm.Input.cs; snap, dive and locations in MainForm.Snap.cs;
/// places you've been in MainForm.Places.cs.
/// </summary>
public sealed partial class MainForm : Form
{
    /// <summary>Renders slower than this show a progress bar and fill in on screen as they go.</summary>
    private const double NoticeableSeconds = 0.12;
    private const double PartialRefreshSeconds = 0.1;

    // Auto iterations: after each render, if a noticeable number of pixels only escaped near the
    // limit, others were probably cut off by it (painted as "inside" while really escaping later,
    // which happens a lot near cusps such as -0.75 and +0.25). Then the limit is doubled and the
    // view rendered again, until the detail stops changing or a render gets too slow.
    private const int AutoIterationCap = 1 << 24;
    private const double AutoIterationMaxSeconds = 20;
    private bool _autoIterations; // off by default; A toggles

    private readonly MandelbrotRenderer _renderer = new();
    private readonly ViewState _view;
    private readonly RenderPipeline _pipeline;
    private readonly DiveController _dive;
    private readonly ColourState _colours = new();
    private readonly Overlay _overlay;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private double _lastTick, _lastPartialRefresh;
    private string _gpuStatus = "starting…";

    // Display, drawn bottom to top.
    private readonly DisplayLayer _baseLayer = new();                            // last completed render
    private readonly DisplayLayer _previewLayer = new() { SmoothUpscale = true }; // low-res preview
    private readonly DisplayLayer _partialLayer = new();                         // in-progress render

    private bool _showInfo = true, _showHelp;
    private bool _showFilled; // tint pixels that interior fill skipped

    /// <param name="startLocation">Optional location text (as copied with Ctrl+C) to open at.</param>
    public MainForm(string? startLocation = null)
    {
        Text = "Mandelbrot";
        Icon = AppIcon.Get();
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _view = new ViewState(() => ClientSize);
        _view.Reset();
        _dive = new DiveController(_view);
        _overlay = new Overlay(_colours);
        BuildKeyMap();
        _pipeline = new RenderPipeline(_renderer, Post, WantedView);
        _pipeline.PreviewReady += OnPreviewReady;
        _pipeline.Completed += OnRenderCompleted;
        _pipeline.Stopped += OnRenderStopped;

        _colours.LoadUserPalettes();
        LoadPlaces();
        if (startLocation != null && !TryGoTo(startLocation, out string error))
            ShowToast("Couldn't open location: " + error, 4);

        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RequestRender();
        if (_pendingSnap != null)
        {
            StartSnap(false, _pendingSnap.SnapZoom);
            _pendingSnap = null;
        }

        // Find and self-test the GPU in the background (takes a few hundred ms); deep renders
        // use the CPU until it's ready, and for good if there isn't a usable one.
        Task.Run(() =>
        {
            var gpu = GpuDeep.TryCreate(out string reason);
            Post(() =>
            {
                if (IsDisposed) { gpu?.Dispose(); return; }
                _renderer.Gpu = gpu;
                _gpuStatus = gpu != null ? gpu.Name : $"none ({reason})";
                if (gpu != null && _view.UseDeep) ForceFullRender();
                Invalidate();
            });
        });
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RequestRender(); // overlap with the previous size is reused
    }

    private void Post(Action action)
    {
        if (IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* form closing */ }
    }

    // =========================================================================
    // Rendering
    // =========================================================================

    /// <summary>
    /// The view to render, or null when there's nothing to render into. (Resize fires from the
    /// constructor before the window exists; a render started then could never marshal its
    /// result back, so wait for the handle.)
    /// </summary>
    private Viewport? WantedView() =>
        IsHandleCreated && ClientSize.Width > 0 && ClientSize.Height > 0 ? _view.Current() : null;

    private void RequestRender()
    {
        if (WantedView() is { } want) _pipeline.Request(want);
        Invalidate();
    }

    /// <summary>Re-renders everything from scratch, e.g. to time a kernel change.</summary>
    private void ForceFullRender()
    {
        if (WantedView() is { } want) _pipeline.ForceFull(want);
        Invalidate();
    }

    private void OnPreviewReady(Field target, Field preview)
    {
        _dive.PacedScale = target.View.Scale;
        _previewLayer.Field = preview;
        _partialLayer.Field = null; // an older partial would sit on top of the newer preview
        ColorizeLayer(_previewLayer);
        Invalidate();
    }

    private void OnRenderCompleted(Field target)
    {
        NotePlaceRendered(target);
        _dive.PacedScale = target.View.Scale;
        UpdateColourScale(target);
        if (_extra != null && _extra.View != target.View) _extra = null; // big; only good for the view it was computed for
        _baseLayer.Field = target;
        UpdateExtraStatus();
        _previewLayer.Field = null;
        _partialLayer.Field = null;
        Recolor();
        if (_autoIterations) MaybeRaiseIterations(target); // the pipeline then renders the new limit
        MaybeComputeExtra(target);
    }

    private void OnRenderStopped(Field target, Exception? error)
    {
        // Keep a cancelled render's partial image on screen (it's still the newest imagery),
        // so don't recycle its buffer while that layer shows it.
        if (_partialLayer.Field != target) _pipeline.Recycle(target);
        if (error != null) ShowToast("Render failed: " + error.Message);
        Invalidate();
    }

    /// <summary>
    /// Doubles the iteration limit when the finished render shows signs of being cut off by it:
    /// some pixels inside, and at least 0.5% of them escaping in the top fifth of the limit.
    /// Only changes the limit; the caller starts the new render.
    /// </summary>
    private bool MaybeRaiseIterations(Field field)
    {
        int limit = field.View.MaxIterations;
        if (limit != _view.MaxIterations || limit >= AutoIterationCap) return false;
        // Don't start a render that will take too long: doubling the limit roughly doubles the
        // time of a full render at this window size (measured on the last full render).
        double nextMs = 2 * _pipeline.MsPerMegapixel * field.View.Width * (double)field.View.Height / 1e6;
        if (nextMs > AutoIterationMaxSeconds * 1000) return false;
        float high = 0.8f * limit;
        int inside = 0, nearLimit = 0;
        foreach (float v in field.Values)
        {
            if (v < 0) inside++;
            else if (v > high) nearLimit++;
        }
        if (inside == 0 || nearLimit < Math.Max(16, field.Values.Length / 200)) return false;
        _view.IterationBoost += Math.Min(limit, AutoIterationCap - limit);
        ShowToast($"Iterations raised to {_view.MaxIterations:N0} (detail was cut off; A turns this off)");
        return true;
    }

    /// <summary>Re-applies the palette to every visible layer. No fractal maths involved.</summary>
    private void Recolor()
    {
        ColorizeLayer(_baseLayer);
        ColorizeLayer(_previewLayer);
        ColorizeLayer(_partialLayer);
        Invalidate();
    }

    private void ColorizeLayer(DisplayLayer layer) =>
        layer.Colorize(_colours.Active, _colours.Mapping, _colours.Density, _colours.Offset, _colourScale,
                       _colours.Effects, _extra, _renderer.WorkerCount, _showFilled, filling: layer == _partialLayer);

    // The floor and spread of the view's values, for the Sqrt, Log and Equalize mappings (see
    // ColourScale). Taken from the first finished render of each new view (a zoom or a jump),
    // then kept while you pan or change iterations, so colours don't shift under you.
    private ColourScale? _colourScale;
    private Viewport? _colourScaleView;
    private double? _arrivalFloor; // a place's saved floor, for when its render lands

    private void UpdateColourScale(Field target)
    {
        if (_colourScale != null && _colourScaleView is { } v && v.SameGrid(target.View) && _arrivalFloor == null) return;
        _colourScale = ColourScale.From(target, _arrivalFloor);
        _colourScaleView = target.View;
        _arrivalFloor = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastTick, 0.1);
        _lastTick = now;

        // Show the in-progress render as it fills in (only for renders slow enough to notice).
        bool refreshPartial = _pipeline.Progressive && _pipeline.RunningTarget != null
                              && _pipeline.RunningSeconds > NoticeableSeconds
                              && now - _lastPartialRefresh >= PartialRefreshSeconds;
        if (refreshPartial)
        {
            _partialLayer.Field = _pipeline.RunningTarget;
            _lastPartialRefresh = now;
        }

        if (_dive.Active && !_dive.Paused) StepDive(now, dt);
        if (_extraProgress != null) UpdateExtraStatus();

        if (_pipeline.Field != null && _colours.Advance(dt))
        {
            Recolor();
        }
        else if (refreshPartial)
        {
            ColorizeLayer(_partialLayer);
            Invalidate();
        }
        else if (_pipeline.IsRunning)
        {
            Invalidate(); // progress bar
        }

        if (_overlay.ExpireToast(now)) Invalidate();
    }

    // =========================================================================
    // Painting
    // =========================================================================

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        // Each layer is drawn transformed into the current view, so while panning or zooming the
        // newest imagery follows the mouse until the exact render replaces it.
        // A GDI+ error escaping OnPaint makes WinForms give up on the window for good (red X),
        // so a layer that can't be drawn is skipped instead.
        var cur = _view.Current();
        foreach (var layer in (ReadOnlySpan<DisplayLayer>)[_baseLayer, _previewLayer, _partialLayer])
        {
            try { layer.Draw(g, cur); }
            catch (Exception ex) when (ex is OverflowException or ArgumentException or ExternalException) { }
        }

        double? progress = _pipeline.Progress != null && _pipeline.RunningSeconds > NoticeableSeconds
            ? _pipeline.Progress.Fraction : null;
        _overlay.Draw(g, ClientSize, progress, _showInfo ? InfoText(cur) : null, _showHelp);
    }

    private string InfoText(in Viewport v)
    {
        string status = _pipeline.Progress is { } p ? $"Rendering {p.Fraction:P0}" : $"Last render: {_pipeline.LastRenderMs} ms";
        var (cx, cy) = _view.CenterText(v);
        const int maxShown = 40;
        string Clip(string s) => s.Length <= maxShown ? s : s[..maxShown] + "…";
        string precision = v.Deep == null
            ? $"Precision: double{(_view.DeepMode == DeepMode.Off ? " (deep zoom off)" : "")}"
            : $"Precision: perturbation, {v.Deep.X.Bits}-bit reference" +
              (_renderer.LastOrbit is { } o ? $" ({o.Length - 1:N0} iters, {o.ComputeTime.TotalMilliseconds:0} ms)" : "");
        return
            $"Center: {Clip(cx)}, {Clip(cy)}\n" +
            $"Zoom: {_view.Zoom:G4}x   Iterations: {_view.MaxIterations}   {precision}\n" +
            (_dive.TargetScale is double target
                ? _dive.Paused
                    ? $"Zoom to {Mandelbrot.ZoomFor(target, ClientSize.Width):G3}x paused: Space goes on, the wheel moves along the way, Ctrl+S saves, Esc stops\n"
                    : $"Zooming in to {Mandelbrot.ZoomFor(target, ClientSize.Width):G3}x (Space pauses, Esc stops)\n"
                : "") +
            $"{EngineText(v)}   {(_renderer.UseSimd ? "SIMD" : "Scalar")}{(_renderer.UseInteriorFill ? "+Fill" : "")}{(_pipeline.Progressive ? "+Prog" : "")}   {status} ({_pipeline.LastRenderPixels:N0} px rendered)\n" +
            $"Palette: {_colours.Active.Name}   Mapping: {_colours.Mapping}   Density: {_colours.Density:G3}x   Effects: {_colours.Effects}   Cycling: {(_colours.Cycling ? $"{_colours.CycleSpeed:0.##}/s" : "off")}\n" +
            "H: help   P: palettes   L: places   I: hide info";
    }

    private string EngineText(in Viewport v)
    {
        string cpu = $"Threads: {_renderer.WorkerCount} of {Environment.ProcessorCount} cores";
        string gpu = _renderer.GpuFailure != null ? $"GPU failed: {_renderer.GpuFailure}"
            : _renderer.Gpu == null ? $"GPU: {_gpuStatus}"
            : !_renderer.UseGpu ? "GPU: off (F5)"
            : v.Deep != null && _renderer.LastRenderUsedGpu ? $"GPU: {_renderer.Gpu.Name}"
            : v.Deep != null && _renderer.LastRenderGpuImprecise ? $"GPU: {_renderer.Gpu.Name} (not precise enough here: CPU)"
            : $"GPU: {_renderer.Gpu.Name} (deep zoom only)";
        return $"{cpu}   {gpu}";
    }

    private void ShowToast(string text, double seconds = 1.5)
    {
        _overlay.ShowToast(text, _clock.Elapsed.TotalSeconds + seconds);
        Invalidate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel) RecordPlaceOnClose(); // while the image is still on screen
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _pipeline.Cancel();
        _snapCts?.Cancel();
        _baseLayer.Dispose();
        _previewLayer.Dispose();
        _partialLayer.Dispose();
        _overlay.Dispose();
        base.OnFormClosed(e);
    }
}
