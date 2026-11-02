namespace Fractal.Deep;

/// <summary>A deep-zoom view: centre in full precision, everything else in doubles.</summary>
public sealed record DeepView(BigFixed CenterX, BigFixed CenterY, double Scale, int Width, int Height, int MaxIterations);

/// <summary>
/// Perturbation-theory iteration. With z = Z + dz (Z = reference orbit, dz = this pixel's
/// offset from it), z² + c expands to
///
///     dz(n+1) = (2·Z(n) + dz(n))·dz(n) + dc
///
/// where dc is the pixel's offset from the reference point. dz and dc are tiny, but doubles
/// store tiny numbers just as precisely as big ones; only the huge Z + dz sum loses digits,
/// and that sum is never formed until it is large enough not to matter.
///
/// Glitches (where dz stops being small relative to Z) are handled by rebasing
/// (Zhuoran, 2021): whenever |z| &lt; |dz|, or the reference orbit runs out, restart from the
/// beginning of the reference orbit with dz = z. That needs no extra references or glitch
/// detection.
///
/// Limit for now: dc must stay within double range (pixel size above ~1e-300).
/// </summary>
public static class Perturbation
{
    private const double Bailout = 256.0;

    public static float Iterate(ReferenceOrbit orbit, double dcr, double dci, int maxIterations)
    {
        var refR = orbit.Zr;
        var refI = orbit.Zi;
        int last = orbit.Length - 1;

        double dzr = 0, dzi = 0;
        int m = 0;

        for (int i = 0; i < maxIterations; i++)
        {
            double zr = refR[m], zi = refI[m];
            double tr = 2 * zr + dzr, ti = 2 * zi + dzi;
            double nr = tr * dzr - ti * dzi + dcr;
            dzi = tr * dzi + ti * dzr + dci;
            dzr = nr;
            m++;

            zr = refR[m] + dzr;
            zi = refI[m] + dzi;
            double mag = zr * zr + zi * zi;
            if (mag > Bailout) return MandelbrotRenderer.Smooth(i, mag);

            if (mag < dzr * dzr + dzi * dzi || m == last)
            {
                dzr = zr;
                dzi = zi;
                m = 0;
            }
        }
        return MandelbrotRenderer.Inside;
    }

    /// <summary>Renders a whole view (scalar, multi-threaded). The reference must be at the view centre.</summary>
    public static void Render(DeepView view, ReferenceOrbit orbit, float[] dst, int workers,
                              CancellationToken token = default)
    {
        int w = view.Width, h = view.Height;
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token };
        Parallel.For(0, h, options, py =>
        {
            double dci = -(py - h / 2) * view.Scale;
            int row = py * w;
            for (int px = 0; px < w; px++)
                dst[row + px] = Iterate(orbit, (px - w / 2) * view.Scale, dci, view.MaxIterations);
        });
    }
}
