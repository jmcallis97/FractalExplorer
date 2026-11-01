namespace Fractal;

/// <summary>
/// Computes smooth iteration counts in parallel using (cores - 2) worker threads, minimum 1.
/// Output is colour-independent; see <see cref="Colorizer"/> for turning it into pixels.
/// </summary>
public sealed class MandelbrotRenderer
{
    public const float Inside = -1f;
    private const double Bailout = 256.0; // large bailout gives smoother colouring
    private static readonly double Ln2 = Math.Log(2.0);

    public int WorkerCount { get; } = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>
    /// Fills <paramref name="target"/>. If <paramref name="source"/> shares the same pixel grid
    /// (i.e. the view was only panned or resized), overlapping pixels are copied rather than
    /// recomputed, so only newly exposed areas cost anything.
    /// </summary>
    public void Render(Field target, Field? source, RenderProgress progress, CancellationToken token)
    {
        var v = target.View;
        int w = v.Width, h = v.Height;
        var dst = target.Values;

        // Overlap rectangle, in global grid coordinates.
        long ox0 = 0, ox1 = 0, oy0 = 0, oy1 = 0;
        bool reuse = false;
        if (source != null && source.View.CanReuse(v))
        {
            var s = source.View;
            ox0 = Math.Max(v.OriginX, s.OriginX);
            ox1 = Math.Min(v.OriginX + w, s.OriginX + s.Width);
            oy0 = Math.Max(v.OriginY, s.OriginY);
            oy1 = Math.Min(v.OriginY + h, s.OriginY + s.Height);
            reuse = ox0 < ox1 && oy0 < oy1;
        }

        progress.Total = (long)w * h - (reuse ? (ox1 - ox0) * (oy1 - oy0) : 0);

        var options = new ParallelOptions { MaxDegreeOfParallelism = WorkerCount, CancellationToken = token };

        // Rows are handed out dynamically, which balances load well since rows near the set
        // take far longer than rows outside it.
        Parallel.For(0, h, options, (py, state) =>
        {
            if (token.IsCancellationRequested) { state.Stop(); return; }

            long gy = v.OriginY + py;
            double ci = v.AnchorY - gy * v.Scale;
            int row = py * w;

            if (reuse && gy >= oy0 && gy < oy1)
            {
                var s = source!.View;
                int a = (int)(ox0 - v.OriginX);
                int b = (int)(ox1 - v.OriginX);
                int srcIndex = (int)(gy - s.OriginY) * s.Width + (int)(ox0 - s.OriginX);
                Array.Copy(source.Values, srcIndex, dst, row + a, b - a);
                ComputeSpan(dst, row, 0, a, v, ci);
                ComputeSpan(dst, row, b, w, v, ci);
                progress.Add(w - (b - a));
            }
            else
            {
                ComputeSpan(dst, row, 0, w, v, ci);
                progress.Add(w);
            }
        });

        token.ThrowIfCancellationRequested();
    }

    private static void ComputeSpan(float[] dst, int row, int from, int to, in Viewport v, double ci)
    {
        int maxIter = v.MaxIterations;
        for (int px = from; px < to; px++)
        {
            double cr = v.AnchorX + (v.OriginX + px) * v.Scale;
            dst[row + px] = Iterate(cr, ci, maxIter);
        }
    }

    private static float Iterate(double cr, double ci, int maxIter)
    {
        // Quick reject: main cardioid and period-2 bulb are always inside.
        double xq = cr - 0.25;
        double q = xq * xq + ci * ci;
        if (q * (q + xq) <= 0.25 * ci * ci) return Inside;
        if ((cr + 1) * (cr + 1) + ci * ci <= 0.0625) return Inside;

        double zr = 0, zi = 0, zr2 = 0, zi2 = 0;
        double pr = 0, pi = 0; // periodicity check (Brent): bails early on interior points
        int check = 8, counter = 0;

        for (int i = 0; i < maxIter; i++)
        {
            zi = 2 * zr * zi + ci;
            zr = zr2 - zi2 + cr;
            zr2 = zr * zr;
            zi2 = zi * zi;

            if (zr2 + zi2 > Bailout)
            {
                // Smooth (continuous) iteration count to avoid banding.
                double logZn = Math.Log(zr2 + zi2) * 0.5;
                double nu = Math.Log(logZn / Ln2) / Ln2;
                double smooth = i + 2 - nu;
                return smooth < 0 ? 0f : (float)smooth;
            }

            if (zr == pr && zi == pi) return Inside;
            if (++counter == check)
            {
                counter = 0;
                check <<= 1;
                pr = zr;
                pi = zi;
            }
        }
        return Inside;
    }
}
