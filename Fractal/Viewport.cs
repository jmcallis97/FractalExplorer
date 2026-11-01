namespace Fractal;

/// <summary>
/// Describes exactly which complex-plane pixels an image covers.
///
/// The plane is treated as an infinite integer pixel grid anchored at (AnchorX, AnchorY) with
/// spacing Scale. Panning only changes the integer PanX/PanY, so two views that share a grid
/// (same anchor, scale and iteration limit) produce bit-identical values for the same global
/// pixel. That is what lets a pan reuse every already-computed pixel and only compute the
/// newly exposed strip.
/// </summary>
public readonly record struct Viewport(
    double AnchorX, double AnchorY, double Scale,
    long PanX, long PanY,
    int Width, int Height,
    int MaxIterations)
{
    /// <summary>Global grid column of this view's pixel column 0.</summary>
    public long OriginX => PanX - Width / 2;

    /// <summary>Global grid row of this view's pixel row 0.</summary>
    public long OriginY => PanY - Height / 2;

    public double CenterX => AnchorX + PanX * Scale;
    public double CenterY => AnchorY - PanY * Scale;

    public double PixelToReal(double px) => AnchorX + (OriginX + px) * Scale;
    public double PixelToImag(double py) => AnchorY - (OriginY + py) * Scale;

    public bool SameGrid(in Viewport o) =>
        AnchorX == o.AnchorX && AnchorY == o.AnchorY && Scale == o.Scale;

    public bool CanReuse(in Viewport o) => SameGrid(o) && MaxIterations == o.MaxIterations;
}

/// <summary>A computed image: one smooth iteration count per pixel (negative = inside the set).</summary>
public sealed class Field(Viewport view, float[] values)
{
    public Viewport View { get; } = view;
    public float[] Values { get; } = values;
}

public sealed class RenderProgress
{
    private long _done;
    public long Total { get; set; }
    public long Done => Interlocked.Read(ref _done);
    public void Add(long n) => Interlocked.Add(ref _done, n);
    public double Fraction => Total <= 0 ? 1.0 : (double)Done / Total;
}
