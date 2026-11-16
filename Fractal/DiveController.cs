using Fractal.Deep;

namespace Fractal;

/// <summary>
/// An animated zoom onto an exact point (found by snap): the view centre glides onto the point
/// while the zoom runs at a steady rate, never more than <see cref="MaxLead"/>× ahead of the
/// newest render on screen (layers are drawn stretched until the render catches up). The dive
/// never cancels a render: each one starts when the previous finishes, at whatever the view is
/// by then.
/// </summary>
internal sealed class DiveController(ViewState view)
{
    private sealed record Dive(BigFixed X, BigFixed Y, double TargetScale, double SecondsPerDoubling,
                               double PanX0, double PanY0, double Started);

    private const double PanSeconds = 1.5;
    private const double MaxLead = 4;

    private Dive? _dive;
    private int _anchorBits;

    public bool Active => _dive != null;

    /// <summary>Pixel size the dive is heading for, or null when not diving.</summary>
    public double? TargetScale => _dive?.TargetScale;

    public double SecondsPerDoubling { get; set; } = 0.5;

    /// <summary>Pixel size of the newest render on screen (preview or full); paces the zoom.</summary>
    public double PacedScale { get; set; }

    /// <summary>Starts diving onto (<paramref name="x"/>, <paramref name="y"/>) from the current view.</summary>
    public void Start(BigFixed x, BigFixed y, double targetScale, double now, double shownScale)
    {
        var v = view.Current();
        int bits = Math.Max(BigFixed.BitsFor(v.Scale), view.AnchorX.Bits);
        // Where the view centre is now, relative to the point, in pixels (y down).
        double px = (view.AnchorX.WithBits(bits) - x.WithBits(bits)).ToDouble() / v.Scale + v.PanX;
        double py = (y.WithBits(bits) - view.AnchorY.WithBits(bits)).ToDouble() / v.Scale + v.PanY;
        _dive = new Dive(x, y, targetScale, SecondsPerDoubling, px, py, now);
        PacedScale = shownScale;
        _anchorBits = -1;
        Apply(v.Scale, px, py);
    }

    /// <summary>Stops the dive where it is. False if there wasn't one.</summary>
    public bool Stop()
    {
        if (_dive == null) return false;
        _dive = null;
        return true;
    }

    /// <summary>Moves the view on by <paramref name="dt"/> seconds. True once it has arrived (the dive is then over).</summary>
    public bool Step(double now, double dt)
    {
        var d = _dive!;
        // The centre glides onto the point (smoothstep), while the zoom runs at a steady rate.
        double e = Math.Clamp((now - d.Started) / PanSeconds, 0, 1);
        e = e * e * (3 - 2 * e);

        double logNow = Math.Log2(view.Scale), logTarget = Math.Log2(d.TargetScale);
        double dir = Math.Sign(logTarget - logNow);
        double step = dt / d.SecondsPerDoubling;
        bool reached = Math.Abs(logTarget - logNow) <= step;
        double next = reached ? logTarget : logNow + dir * step;
        // Don't run more than MaxLead ahead of the newest render on screen.
        double limit = Math.Log2(PacedScale) + dir * Math.Log2(MaxLead);
        if (dir * (next - limit) > 0)
        {
            reached = false;
            next = dir > 0 ? Math.Max(logNow, limit) : Math.Min(logNow, limit);
        }

        bool arrived = reached && e >= 1;
        Apply(reached ? d.TargetScale : Math.Pow(2, next), d.PanX0 * (1 - e), d.PanY0 * (1 - e));
        if (arrived) _dive = null;
        return arrived;
    }

    /// <summary>Anchors the view on the dive's point at <paramref name="scale"/>, centre offset by the given pixels.</summary>
    private void Apply(double scale, double panX, double panY)
    {
        var d = _dive!;
        int bits = BigFixed.BitsFor(scale);
        if (bits != _anchorBits)
        {
            view.SetAnchor(d.X.WithBits(bits), d.Y.WithBits(bits));
            _anchorBits = bits;
        }
        view.Scale = scale;
        view.PanX = (long)Math.Round(panX);
        view.PanY = (long)Math.Round(panY);
    }
}
