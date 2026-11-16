namespace Fractal;

/// <summary>
/// Numbers and rules shared by the app, FractalVideo and the test bench, so that a location,
/// a video keyframe and a test render at the same zoom all agree on what they show.
/// </summary>
public static class Mandelbrot
{
    /// <summary>
    /// |z|² above which a point has escaped. Far larger than the minimal 4, which makes the
    /// smooth (fractional) iteration count accurate, so colour bands blend without seams.
    /// (The GPU kernel has its own copy, as a float literal.)
    /// </summary>
    public const double Bailout = 256.0;

    // ---- Framing -----------------------------------------------------------------------------

    /// <summary>Width of the complex plane shown at zoom 1, whatever the window size.</summary>
    public const double ViewWidthAtZoom1 = 3.5;

    /// <summary>Centre of the opening view.</summary>
    public const double InitialCenterX = -0.5, InitialCenterY = 0.0;

    /// <summary>Shallowest zoom allowed: the whole set is a small blob in the middle by then.</summary>
    public const double MinZoom = 0.25;

    /// <summary>
    /// Smallest pixel size: perturbation holds pixel offsets as doubles, which stop at ~1e-308,
    /// and some headroom is needed below the pixel size. About 10²⁸⁷× zoom in a 1920-wide window.
    /// </summary>
    public const double MinScale = 1e-290;

    /// <summary>
    /// Pixel size below which renders switch to perturbation (deep zoom): plain doubles carry
    /// ~16 significant digits, so smaller pixels can't be told apart (about 3×10¹⁰× zoom).
    /// </summary>
    public const double DeepThreshold = 1e-13;

    /// <summary>Zoom at which pixels are <paramref name="scale"/> wide in an image <paramref name="width"/> pixels wide.</summary>
    public static double ZoomFor(double scale, int width) => ViewWidthAtZoom1 / (scale * Math.Max(1, width));

    /// <summary>Pixel size for <paramref name="zoom"/> in an image <paramref name="width"/> pixels wide.</summary>
    public static double ScaleFor(double zoom, int width) => ViewWidthAtZoom1 / (zoom * Math.Max(1, width));

    /// <summary>Pixel size for <paramref name="zoom"/>, kept within the zoom range the renderer supports.</summary>
    public static double ClampedScaleFor(double zoom, int width) =>
        Math.Clamp(ScaleFor(zoom, width), MinScale, ScaleFor(MinZoom, width));

    // ---- Iterations --------------------------------------------------------------------------

    /// <summary>Iteration limit at zoom 1.</summary>
    public const int BaseIterations = 200;

    /// <summary>Extra iterations per doubling of zoom: deeper detail takes longer to escape.</summary>
    public const int IterationsPerDoubling = 100;

    /// <summary>The limit never goes below this, however much it is lowered by hand.</summary>
    public const int MinIterations = 50;

    /// <summary>
    /// The automatic iteration limit at <paramref name="zoom"/>, before any adjustment by hand
    /// (the app's +/-, or the limit saved in a location). The final limit is
    /// <c>Math.Max(MinIterations, AutoIterations(zoom) + adjustment)</c>; see <see cref="Iterations"/>.
    /// </summary>
    public static int AutoIterations(double zoom) =>
        (int)(BaseIterations + IterationsPerDoubling * Math.Log2(Math.Max(1.0, zoom)));

    /// <summary>The iteration limit at <paramref name="zoom"/> with an adjustment by hand.</summary>
    public static int Iterations(double zoom, int adjustment = 0) =>
        Math.Max(MinIterations, AutoIterations(zoom) + adjustment);
}
