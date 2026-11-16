using Fractal.Deep;

namespace Fractal;

internal enum DeepMode { Auto, Always, Off }

/// <summary>
/// Where the window is looking: an anchor point in full precision, a whole-pixel pan away from
/// it (panning keeps the pixel grid, so a render can reuse the last one), the pixel size, and
/// the iteration limit. The window size comes from <paramref name="clientSize"/>.
/// </summary>
internal sealed class ViewState(Func<Size> clientSize)
{
    private DeepAnchor _deepAnchor = null!;
    private double _anchorX, _anchorY; // nearest doubles to the anchor

    public BigFixed AnchorX { get; private set; }
    public BigFixed AnchorY { get; private set; }
    public double Scale { get; set; }
    public long PanX { get; set; }
    public long PanY { get; set; }
    public DeepMode DeepMode { get; set; } = DeepMode.Auto;

    /// <summary>Adjustment to the automatic iteration limit: +/-, a pasted location, auto iterations.</summary>
    public int IterationBoost { get; set; }

    public double Zoom => Mandelbrot.ZoomFor(Scale, clientSize().Width);

    public int MaxIterations => Mandelbrot.Iterations(Zoom, IterationBoost);

    public bool UseDeep =>
        DeepMode == DeepMode.Always || (DeepMode == DeepMode.Auto && Scale < Mandelbrot.DeepThreshold);

    public Viewport Current()
    {
        var size = clientSize();
        return new(_anchorX, _anchorY, Scale, PanX, PanY, size.Width, size.Height, MaxIterations,
                   UseDeep ? _deepAnchor : null);
    }

    public void SetAnchor(BigFixed x, BigFixed y)
    {
        AnchorX = x;
        AnchorY = y;
        _deepAnchor = new DeepAnchor(x, y);
        _anchorX = x.ToDouble();
        _anchorY = y.ToDouble();
    }

    /// <summary>The whole set, default iterations.</summary>
    public void Reset()
    {
        SetAnchor(BigFixed.FromDouble(Mandelbrot.InitialCenterX, 64), BigFixed.FromDouble(Mandelbrot.InitialCenterY, 64));
        PanX = PanY = 0;
        Scale = Mandelbrot.ScaleFor(1, clientSize().Width);
        IterationBoost = 0;
    }

    /// <summary>Centres on (<paramref name="x"/>, <paramref name="y"/>) with pixels <paramref name="scale"/> wide.</summary>
    public void GoTo(BigFixed x, BigFixed y, double scale)
    {
        int bits = BigFixed.BitsFor(scale);
        SetAnchor(x.WithBits(bits), y.WithBits(bits));
        PanX = PanY = 0;
        Scale = scale;
    }

    /// <summary>Pixel size for <paramref name="zoom"/> in this window, within the supported range.</summary>
    public double ScaleForZoom(double zoom) => Mandelbrot.ClampedScaleFor(zoom, clientSize().Width);

    /// <summary>
    /// Zooms in by <paramref name="factor"/> (below 1 zooms out), keeping the point under window
    /// pixel <paramref name="p"/> where it is. Done in full precision:
    /// new anchor = anchor + (p's global pixel) × old scale - (p's offset from centre) × new scale.
    /// </summary>
    public void ZoomAt(Point p, double factor)
    {
        var v = Current();
        double newScale = Math.Clamp(v.Scale / factor, Mandelbrot.MinScale, Mandelbrot.ScaleFor(Mandelbrot.MinZoom, v.Width));
        int bits = BigFixed.BitsFor(Math.Min(newScale, v.Scale));
        long gx = v.OriginX + p.X, gy = v.OriginY + p.Y;
        SetAnchor(
            AnchorX.WithBits(bits) + BigFixed.FromDouble(gx * v.Scale, bits) - BigFixed.FromDouble((p.X - v.Width / 2) * newScale, bits),
            AnchorY.WithBits(bits) - BigFixed.FromDouble(gy * v.Scale, bits) + BigFixed.FromDouble((p.Y - v.Height / 2) * newScale, bits));
        Scale = newScale;
        PanX = PanY = 0;
    }

    /// <summary>View centre in full precision, with enough digits to pin down a pixel.</summary>
    public (string X, string Y) CenterText(in Viewport v) => PointText(v, v.Width / 2, v.Height / 2);

    /// <summary>The point under window pixel (<paramref name="sx"/>, <paramref name="sy"/>), in full precision.</summary>
    public (string X, string Y) PointText(in Viewport v, int sx, int sy)
    {
        int bits = Math.Max(AnchorX.Bits, BigFixed.BitsFor(v.Scale));
        var cx = AnchorX.WithBits(bits) + BigFixed.FromDouble((v.PanX + sx - v.Width / 2) * v.Scale, bits);
        var cy = AnchorY.WithBits(bits) - BigFixed.FromDouble((v.PanY + sy - v.Height / 2) * v.Scale, bits);
        int digits = Math.Max(16, (int)Math.Ceiling(-Math.Log10(v.Scale)) + 2);
        return (cx.ToDecimalString(digits), cy.ToDecimalString(digits));
    }

    // ---- Iteration limit -------------------------------------------------------------------

    /// <summary>Sets the limit at the current zoom to <paramref name="limit"/> (it then grows with zoom as usual).</summary>
    public void SetIterations(int limit) => IterationBoost = limit - Mandelbrot.AutoIterations(Zoom);

    /// <summary>Lowers the limit by <paramref name="step"/>, not below <see cref="Mandelbrot.MinIterations"/>.</summary>
    public void LowerIterations(int step) =>
        IterationBoost = Math.Max(IterationBoost - step, Mandelbrot.MinIterations - Mandelbrot.AutoIterations(Zoom));

    /// <summary>Raises the limit, if needed, so that it is at least <paramref name="need"/> once at <paramref name="zoom"/>.</summary>
    public void EnsureIterationsAt(double zoom, int need)
    {
        int auto = Mandelbrot.AutoIterations(zoom);
        if (auto + IterationBoost < need) IterationBoost = need - auto;
    }
}
