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

    /// <summary>After a render lands: start a computation that was waiting for it.</summary>
    private void MaybeComputeExtra(Field target)
    {
        if (_extraWhenRendered && target.View == _view.Current() && !_pipeline.IsRunning) ComputeExtra();
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
        _effectsPanel.SetStatus(text, _extraProgress != null || _extraWhenRendered);
    }
}
