using System.Globalization;
using System.Runtime.InteropServices;
using Fractal.Deep;

namespace Fractal;

/// <summary>Snap (N, Ctrl+click), dives, and copying and pasting locations.</summary>
public sealed partial class MainForm
{
    // N finds the exact point the view is circling (Snap.Find, in the background); Ctrl+click
    // finds the one under the cursor (Snap.FindAt). Then it jumps or dives there.
    private const int SnapBits = 1070;       // ~320 digits: enough for any zoom the deep renderer reaches
    private const double SnapBoxPixels = 16; // snap at cursor searches a box this far either side
    private bool _snapping;
    private CancellationTokenSource? _snapCts;
    private int _navigation;                 // bumped when the view moves; drops stale snap results
    private Fractal.Location? _pendingSnap;         // snap asked for before the window was shown

    /// <summary>
    /// Looks for the spiral centre or minibrot the view is circling (or, with
    /// <paramref name="at"/>, the one under that window pixel), in the background, then asks
    /// what to do (<paramref name="ask"/>) or dives straight in to <paramref name="targetZoom"/>
    /// (default: as deep as makes sense for what was found).
    /// </summary>
    private void StartSnap(bool ask, double? targetZoom = null, Point? at = null)
    {
        if (_snapping) { ShowToast("Still looking..."); return; }
        StopDive();
        int generation = _navigation;
        var v = _view.Current();
        var (x, y) = at is { } pt ? _view.PointText(v, pt.X, pt.Y) : _view.CenterText(v);
        var loc = new Fractal.Location(x, y, _view.Zoom, _view.MaxIterations);
        int maxIter = Math.Max(200_000, _view.MaxIterations * 4);
        double boxRadius = SnapBoxPixels * v.Scale;
        var cts = new CancellationTokenSource();
        _snapCts = cts;
        _snapping = true;
        ShowToast(at != null ? "Looking for the minibrot or spiral eye under the cursor... (Esc cancels)"
                             : "Looking for a spiral centre or minibrot... (Esc cancels)", 120);
        Task.Run(() => at != null ? Snap.FindAt(loc, boxRadius, maxIter, SnapBits, cts.Token, out _)
                                  : Snap.Find(loc, maxIter, SnapBits, _ => { }, out _, cts.Token)).ContinueWith(t => Post(() =>
        {
            cts.Dispose();
            if (_snapCts != cts) return; // cancelled (StopDive already cleared the state)
            _snapCts = null;
            _snapping = false;
            if (generation != _navigation) return; // the view moved meanwhile
            if (t.IsFaulted) { ShowToast("Snap failed: " + t.Exception?.GetBaseException().Message, 4); return; }
            if (t.Result is not { } r)
            {
                ShowToast(at != null ? "Nothing found under the cursor: zoom in a little, or try another part of it"
                                     : "Nothing to snap to here: centre a spiral's eye or a minibrot and zoom in a little", 5);
                return;
            }
            OnSnapFound(r, ask, targetZoom);
        }), TaskScheduler.Default);
    }

    private void OnSnapFound(Snap.Result r, bool ask, double? targetZoom)
    {
        double zoom = targetZoom ?? Snap.SuggestedZoom(r);
        var choice = SnapDialog.Choice.Dive;
        if (ask)
        {
            _overlay.ClearToast();
            Invalidate();
            using var dlg = new SnapDialog(Snap.Describe(r), r.DistanceInViews, zoom, _dive.SecondsPerDoubling);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            choice = dlg.Result;
            zoom = dlg.TargetZoom;
            if (choice == SnapDialog.Choice.Dive) _dive.SecondsPerDoubling = dlg.SecondsPerDoubling;
        }
        else
        {
            ShowToast(r.Kind == Snap.Kind.Misiurewicz ? "Found a spiral centre: zooming in (Esc stops)"
                                                      : $"Found a period {r.Period} minibrot: zooming in (Esc stops)", 3);
        }

        // A minibrot needs an iteration limit of some multiple of its period to show its detail.
        if (r.Kind == Snap.Kind.Nucleus && choice != SnapDialog.Choice.Centre)
            _view.EnsureIterationsAt(zoom, (int)Math.Min(20L * r.Period, AutoIterationCap));

        switch (choice)
        {
            case SnapDialog.Choice.Dive:
                _dive.Start(r.Re, r.Im, _view.ScaleForZoom(zoom), _clock.Elapsed.TotalSeconds,
                            _pipeline.Field?.View.Scale ?? _view.Scale);
                Invalidate();
                break;
            case SnapDialog.Choice.Jump:
                _view.GoTo(r.Re, r.Im, _view.ScaleForZoom(zoom));
                RequestRender();
                break;
            case SnapDialog.Choice.Centre:
                _view.GoTo(r.Re, r.Im, _view.Scale);
                RequestRender();
                break;
        }
    }

    private void StepDive(double now, double dt)
    {
        if (_dive.Step(now, dt))
        {
            ShowToast("Arrived");
            RequestRender();
            return;
        }
        // Start the next render as soon as the last one is done; never cancel one mid-dive.
        if (WantedView() is { } want) _pipeline.StartIfIdle(want);
        Invalidate();
    }

    /// <summary>Stops a dive (the view stays where it is) and drops any snap still being looked for.</summary>
    private void StopDive()
    {
        _navigation++;
        if (_dive.Stop())
        {
            ShowToast("Zoom stopped");
            RequestRender();
        }
        else if (_snapping)
        {
            _snapCts?.Cancel();
            _snapCts = null;
            _snapping = false;
            ShowToast("Snap cancelled");
        }
    }

    // =========================================================================
    // Locations
    // =========================================================================

    private void CopyLocation()
    {
        var v = _view.Current();
        var (x, y) = _view.CenterText(v);
        var inv = CultureInfo.InvariantCulture;
        string text = $"Re: {x}\r\nIm: {y}\r\nPixel size: {v.Scale.ToString("R", inv)}\r\nZoom: {_view.Zoom.ToString("G6", inv)}\r\n" +
                      $"Iterations: {_view.MaxIterations}\r\nMapping: {_colours.Mapping}\r\nColour density: {_colours.Density.ToString("0.####", inv)}";
        try
        {
            Clipboard.SetText(text);
            ShowToast("Location copied to clipboard");
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            ShowToast("Couldn't copy: " + ex.Message);
        }
    }

    private void PasteLocation()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            ShowToast("Couldn't read clipboard: " + ex.Message);
            return;
        }
        if (TryGoTo(text, out string error)) { if (!_snapping) ShowToast("Went to pasted location"); }
        else ShowToast("Clipboard isn't a location: " + error, 3);
    }

    /// <summary>
    /// Jumps to a location in the Ctrl+C format ("Re: ...", "Im: ...", "Zoom: ..." lines; the
    /// zoom keeps the same framing whatever the window size), or plain "re im zoom". Mapping and
    /// colour density are applied if present; a "Snap" line then zooms in to the exact spiral
    /// centre or minibrot there (to the zoom given after "Snap:", if any).
    /// </summary>
    private bool TryGoTo(string text, out string error)
    {
        if (!Fractal.Location.TryParse(text, out var loc, out error)) return false;
        StopDive();
        double scale = _view.ScaleForZoom(loc.Zoom);
        int bits = BigFixed.BitsFor(scale);
        _view.GoTo(BigFixed.Parse(loc.Re, bits), BigFixed.Parse(loc.Im, bits), scale);
        if (loc.Iterations is int it) _view.SetIterations(it); // the limit in use when it was copied
        if (loc.Density is double d) _colours.SetDensity(d);
        if (loc.Mapping is ColorMapping mapping) _colours.Mapping = mapping;
        if (loc.Density != null || loc.Mapping != null) Recolor();
        RequestRender();
        if (loc.Snap)
        {
            if (IsHandleCreated) StartSnap(false, loc.SnapZoom);
            else _pendingSnap = loc; // opened from the command line: snap once the window is up
        }
        error = "";
        return true;
    }
}
