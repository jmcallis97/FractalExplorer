using System.Diagnostics;
using Fractal.Deep;

namespace Fractal;

/// <summary>
/// The extra numbers per pixel (stripes, angle, distance, period) that some effects need.
/// Never computed as part of rendering: only when asked for (Compute in the Effects panel, or
/// on arriving at a place saved with such effects), for the finished image on screen.
/// </summary>
public sealed partial class MainForm
{
    private ExtraField? _extra;                // for _extra.View only
    private CancellationTokenSource? _extraCts;
    private RenderProgress? _extraProgress;    // non-null while computing
    private readonly Stopwatch _extraClock = new();
    private double _extraSeconds;
    private bool _extraWhenRendered;           // compute once the current view's render lands
    private bool _extraComputingStripes;       // the computation running includes stripes

    private void ComputeExtra()
    {
        if (_baseLayer.Field is not { } field || field.View != _view.Current() || _pipeline.IsRunning)
        {
            // Not finished yet: do it when the render lands.
            _extraWhenRendered = true;
            UpdateExtraStatus();
            return;
        }
        _extraWhenRendered = false;
        _extraCts?.Cancel();
        var cts = new CancellationTokenSource();
        _extraCts = cts;
        var progress = new RenderProgress();
        _extraProgress = progress;
        _extraClock.Restart();

        var v = field.View;
        var values = field.Values.ToArray(); // the field's buffer is reused for later renders
        bool stripes = _colours.Effects.Stripes;
        _extraComputingStripes = stripes;
        var renderer = _renderer;
        int workers = _renderer.WorkerCount;
        Task.Run(() =>
        {
            ReferenceOrbit? orbit = v.Deep != null ? renderer.GetOrbit(v.Deep, v.MaxIterations, cts.Token) : null;
            return ExtraRenderer.Compute(v, values, orbit, stripes, workers, progress, cts.Token);
        }, cts.Token).ContinueWith(t => Post(() =>
        {
            cts.Dispose();
            if (_extraCts != cts) return; // replaced or cancelled
            _extraCts = null;
            _extraProgress = null;
            if (t.IsFaulted) ShowToast("Couldn't compute the extra numbers: " + t.Exception?.GetBaseException().Message, 4);
            else if (t.IsCompletedSuccessfully)
            {
                _extra = t.Result;
                _extraSeconds = _extraClock.Elapsed.TotalSeconds;
                Recolor();
            }
            UpdateExtraStatus();
        }), TaskScheduler.Default);
        UpdateExtraStatus();
    }

    private void CancelExtra()
    {
        _extraWhenRendered = false;
        if (_extraCts == null) return;
        _extraCts.Cancel();
        _extraCts = null;
        _extraProgress = null;
        UpdateExtraStatus();
    }

    /// <summary>
    /// True if <paramref name="e"/> needs extra numbers this view doesn't have (or isn't already
    /// computing): none at all, or no stripes when Stripes is on.
    /// </summary>
    private bool ExtraMissing(Effects e)
    {
        if (!e.NeedsExtra) return false;
        if (_extraProgress != null || _extraWhenRendered) return e.Stripes && !_extraComputingStripes;
        bool have = _extra != null && _baseLayer.Field?.View == _extra.View;
        return !have || (e.Stripes && !_extra!.HasStripes);
    }

    /// <summary>After a render lands: start a computation that was waiting for it.</summary>
    private void MaybeComputeExtra(Field target)
    {
        if (_extraWhenRendered && target.View == _view.Current() && !_pipeline.IsRunning) ComputeExtra();
    }

    private (Field? Field, Viewport View, bool Inside) _insideCheck;

    /// <summary>True if any pixel of <paramref name="f"/> is inside the set (remembered per image: the status updates often).</summary>
    private bool HasInside(Field f)
    {
        if (_insideCheck.Field != f || _insideCheck.View != f.View) _insideCheck = (f, f.View, f.Values.Any(v => v < 0));
        return _insideCheck.Inside;
    }

    /// <summary>What the Effects panel says about the extra numbers.</summary>
    private void UpdateExtraStatus()
    {
        if (_effectsPanel == null) return;
        var e = _colours.Effects;
        string text;
        if (_extraProgress is { } p)
            text = $"Computing... {p.Fraction:P0} ({_extraClock.Elapsed.TotalSeconds:0} s)";
        else if (_extraWhenRendered)
            text = "Will compute when the render finishes";
        else if (_extra != null && _baseLayer.Field?.View == _extra.View)
            text = $"Ready for this view (took {_extraSeconds:0.0} s)" +
                   (e.Stripes && !_extra.HasStripes ? ".\nStripes weren't included: Compute again for them" : "");
        else
            text = e.NeedsExtra ? "Not computed for this view: press Compute" : "Not computed for this view";
        if (e.InsidePeriod && _baseLayer.Field is { } f && !HasInside(f))
            text += "\n(This view has no inside parts, so Inside by period has nothing to colour.)";
        _effectsPanel.SetStatus(text, _extraProgress != null || _extraWhenRendered);
    }
}
