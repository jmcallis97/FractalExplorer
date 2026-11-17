using Fractal.Deep;

namespace Fractal;

/// <summary>
/// Extra numbers per pixel of one view, for the effects that need more than the iteration
/// count (stripes, angle, binary, boundary lines, atom domains). Computed on request by
/// <see cref="ExtraRenderer"/>, since it takes about as long as a CPU render of the view again.
/// </summary>
public sealed class ExtraField(Viewport view, bool hasStripes)
{
    public Viewport View { get; } = view;

    /// <summary>True if <see cref="Stripe"/> was computed (it costs the most, so only when asked for).</summary>
    public bool HasStripes { get; } = hasStripes;

    /// <summary>Angle of z when it escaped, -π to π (NaN inside).</summary>
    public float[] Angle { get; } = new float[view.Width * view.Height];

    /// <summary>Stripe average, 0 to 1 (NaN inside, or when not computed).</summary>
    public float[] Stripe { get; } = new float[view.Width * view.Height];

    /// <summary>Distance estimate to the set, in pixels (0 inside).</summary>
    public float[] Distance { get; } = new float[view.Width * view.Height];

    /// <summary>
    /// Inside the set: the period of the cycle the orbit settles on (the period of the bulb or
    /// minibrot the point belongs to), or 0 if it hadn't settled. Outside: 0.
    /// </summary>
    public float[] Period { get; } = new float[view.Width * view.Height];

    /// <summary>A copy sampled at <paramref name="view"/>'s size (nearest pixel), for small previews.</summary>
    public ExtraField Sample(Viewport view)
    {
        var s = new ExtraField(view, HasStripes);
        int fw = View.Width, fh = View.Height, w = view.Width, h = view.Height;
        for (int y = 0; y < h; y++)
        {
            int row = (int)((long)y * fh / h) * fw;
            for (int x = 0; x < w; x++)
            {
                int from = row + (int)((long)x * fw / w), to = y * w + x;
                s.Angle[to] = Angle[from];
                s.Stripe[to] = Stripe[from];
                s.Distance[to] = Distance[from] * w / fw; // in the sample's (bigger) pixels
                s.Period[to] = Period[from];
            }
        }
        return s;
    }
}

/// <summary>
/// Computes an <see cref="ExtraField"/>: every pixel iterated again in double precision on the
/// CPU (by perturbation against the view's reference orbit when deep), keeping track of the
/// derivative (for the distance estimate), the angle at escape and the stripe average; inside
/// the set, the period of the cycle the orbit settles on.
/// </summary>
public static class ExtraRenderer
{
    private const double Bailout = Mandelbrot.Bailout;

    /// <summary>
    /// Pixels inside the set run this many iterations to settle onto their cycle (not the full
    /// limit, which deep down can be in the millions), then up to this many more to find its period.
    /// </summary>
    private const int InsideCap = 20_000;

    /// <summary>Two points of the settled cycle count as the same within this (relative) distance.</summary>
    private const double SameRelative = 1e-9;

    /// <param name="values">The view's rendered values (tells which pixels are inside, to cap them).</param>
    /// <param name="orbit">The view's reference orbit for a deep view, else null.</param>
    public static ExtraField Compute(Viewport v, float[] values, ReferenceOrbit? orbit, bool stripes, int workers,
                                     RenderProgress progress, CancellationToken token)
    {
        int w = v.Width, h = v.Height;
        var x = new ExtraField(v, stripes);
        progress.Total = h;
        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, py =>
        {
            for (int px = 0; px < w; px++)
            {
                int i = py * w + px;
                float s = values[i];
                if (float.IsNaN(s)) { x.Angle[i] = x.Stripe[i] = float.NaN; continue; }
                int limit = s < 0 ? Math.Min(v.MaxIterations, InsideCap) : v.MaxIterations;
                Pixel(v, orbit, px, py, limit, stripes, out x.Angle[i], out x.Stripe[i], out x.Distance[i], out x.Period[i]);
            }
            progress.Add(1);
        });
        return x;
    }

    private static void Pixel(in Viewport v, ReferenceOrbit? orbit, int px, int py, int maxIter, bool stripes,
                              out float angle, out float stripe, out float distance, out float period)
    {
        double dcr = (v.OriginX + px) * v.Scale, dci = -((v.OriginY + py) * v.Scale);
        double cr = v.AnchorX + dcr, ci = v.AnchorY + dci; // plain (not deep) views only
        double[]? refR = orbit?.Zr, refI = orbit?.Zi;
        int last = orbit != null ? orbit.Length - 1 : 0;

        double zr = 0, zi = 0;   // z (plain), or z = Z[m] + dz (deep)
        double dzr = 0, dzi = 0; // deep: offset from the reference
        int m = 0;
        // dz/dc times the pixel size, so it stays in range at any depth; then the distance
        // estimate |z| ln|z| / |dz/dc| comes out directly in pixels.
        double der = 0, dei = 0;
        double sum = 0, lastAdded = 0;
        int count = 0;
        // Period search (inside): after maxIter iterations remember z, then look for its return.
        bool inside = false;
        double cycleR = 0, cycleI = 0;

        for (int n = 0; n < 2 * maxIter; n++)
        {
            if (n == maxIter)
            {
                inside = true;
                cycleR = zr;
                cycleI = zi;
            }
            // Derivative first: d(n+1) = 2·z(n)·d(n) + pixel size.
            double t = 2 * (zr * der - zi * dei) + v.Scale;
            dei = 2 * (zr * dei + zi * der);
            der = t;

            if (refR != null)
            {
                double Zr = refR[m], Zi = refI![m];
                double tr = 2 * Zr + dzr, ti = 2 * Zi + dzi;
                double nr = tr * dzr - ti * dzi + dcr;
                dzi = tr * dzi + ti * dzr + dci;
                dzr = nr;
                m++;
                zr = refR[m] + dzr;
                zi = refI[m] + dzi;
            }
            else
            {
                double nr = zr * zr - zi * zi + cr;
                zi = 2 * zr * zi + ci;
                zr = nr;
            }

            double mag = zr * zr + zi * zi;
            if (inside)
            {
                double ddr = zr - cycleR, ddi = zi - cycleI;
                if (ddr * ddr + ddi * ddi <= SameRelative * SameRelative * Math.Max(mag, 1e-300))
                {
                    angle = stripe = float.NaN;
                    distance = 0;
                    period = n + 1 - maxIter;
                    return;
                }
            }
            else if (mag > Bailout)
            {
                angle = (float)Math.Atan2(zi, zr);
                double r = Math.Sqrt(mag), lr = Math.Log(r);
                double dmag = Math.Sqrt(der * der + dei * dei);
                distance = dmag > 0 && double.IsFinite(dmag) ? (float)(r * lr / dmag) : 0;
                if (stripes && count > 0)
                {
                    // Smooth between the averages with and without the last term (Härkönen).
                    double avg = sum / count, prev = count > 1 ? (sum - lastAdded) / (count - 1) : avg;
                    double frac = Math.Clamp(1 + Math.Log2(Math.Log(Math.Sqrt(Bailout)) / lr), 0, 1);
                    stripe = (float)(prev + (avg - prev) * frac);
                }
                else stripe = float.NaN;
                period = 0;
                return;
            }

            if (stripes && n > 0 && !inside)
            {
                // 0.5 + 0.5·sin(k·arg z), as the imaginary part of (z/|z|)^4.
                double inv = 1 / Math.Sqrt(mag), ur = zr * inv, ui = zi * inv;
                double a = ur * ur - ui * ui, b = 2 * ur * ui;
                lastAdded = 0.5 + 0.5 * (2 * a * b);
                sum += lastAdded;
                count++;
            }

            if (refR != null && (mag < dzr * dzr + dzi * dzi || m == last))
            {
                dzr = zr;
                dzi = zi;
                m = 0;
            }
        }
        angle = float.NaN;
        stripe = float.NaN;
        distance = 0;
        period = 0; // never settled (or escaped very late): left black
    }
}
