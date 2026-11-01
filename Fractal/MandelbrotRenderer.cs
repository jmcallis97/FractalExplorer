using System.Runtime.Intrinsics;

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

    /// <summary>True if the CPU can run 4 doubles per instruction (AVX2).</summary>
    public static bool SimdSupported => Vector256.IsHardwareAccelerated;

    /// <summary>
    /// Use the 4-wide SIMD kernel. The two kernels round slightly differently, so switching
    /// requires a full re-render (no pan reuse across kernels).
    /// </summary>
    public bool UseSimd { get; set; } = SimdSupported;

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
        bool simd = UseSimd && SimdSupported;

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
        // take far longer than rows outside it (and suits the i9's mix of P- and E-cores).
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
                ComputeSpan(dst, row, 0, a, v, ci, simd);
                ComputeSpan(dst, row, b, w, v, ci, simd);
                progress.Add(w - (b - a));
            }
            else
            {
                ComputeSpan(dst, row, 0, w, v, ci, simd);
                progress.Add(w);
            }
        });

        token.ThrowIfCancellationRequested();
    }

    private static void ComputeSpan(float[] dst, int row, int from, int to, in Viewport v, double ci, bool simd)
    {
        if (from >= to) return;
        if (simd) ComputeSpanSimd(dst, row, from, to, v, ci);
        else ComputeSpanScalar(dst, row, from, to, v, ci);
    }

    // =========================================================================
    // Scalar kernel (one pixel at a time)
    // =========================================================================

    private static void ComputeSpanScalar(float[] dst, int row, int from, int to, in Viewport v, double ci)
    {
        int maxIter = v.MaxIterations;
        for (int px = from; px < to; px++)
        {
            double cr = v.AnchorX + (v.OriginX + px) * v.Scale;
            dst[row + px] = IterateScalar(cr, ci, maxIter);
        }
    }

    private static float IterateScalar(double cr, double ci, int maxIter)
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

            if (zr2 + zi2 > Bailout) return Smooth(i, zr2 + zi2);

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

    // =========================================================================
    // SIMD kernel (4 pixels at a time with AVX2)
    // =========================================================================

    /// <summary>
    /// Every pixel goes through the same 4-lane kernel, including the ragged end of a span
    /// (spare lanes are padded and discarded). Lanes never influence each other, so a pixel's
    /// value is identical however the span is split, which keeps pan reuse seamless.
    /// </summary>
    private static void ComputeSpanSimd(float[] dst, int row, int from, int to, in Viewport v, double ci)
    {
        int maxIter = v.MaxIterations;
        var vci = Vector256.Create(ci);
        Span<double> crs = stackalloc double[8];

        // 8 pixels per step as two interleaved 4-lane vectors: while one vector waits on its
        // multiply latency the CPU works on the other.
        for (int px = from; px < to; px += 8)
        {
            int n = Math.Min(8, to - px);
            for (int k = 0; k < 8; k++)
                crs[k] = v.AnchorX + (v.OriginX + px + Math.Min(k, n - 1)) * v.Scale;

            var a = Vector256.Create((ReadOnlySpan<double>)crs[..4]);
            var b = Vector256.Create((ReadOnlySpan<double>)crs[4..]);
            var (iterA, magA, iterB, magB) = IterateSimdX2(a, b, vci, maxIter);

            for (int k = 0; k < n; k++)
            {
                double it = k < 4 ? iterA.GetElement(k) : iterB.GetElement(k - 4);
                double mag = k < 4 ? magA.GetElement(k) : magB.GetElement(k - 4);
                dst[row + px + k] = it < 0 ? Inside : Smooth((int)it, mag);
            }
        }
    }

    private static Vector256<double> NotInCardioidOrBulb(Vector256<double> cr, Vector256<double> ci)
    {
        var ci2 = ci * ci;
        var xq = cr - Vector256.Create(0.25);
        var q = xq * xq + ci2;
        var inCardioid = Vector256.LessThanOrEqual(q * (q + xq), Vector256.Create(0.25) * ci2);
        var cr1 = cr + Vector256<double>.One;
        var inBulb = Vector256.LessThanOrEqual(cr1 * cr1 + ci2, Vector256.Create(0.0625));
        return ~(inCardioid | inBulb);
    }

    /// <summary>Two independent 4-lane iterations in one loop. Per-lane maths is identical to the scalar kernel. Returns escape iteration (-1 = inside) and |z|² at escape.</summary>
    private static (Vector256<double>, Vector256<double>, Vector256<double>, Vector256<double>) IterateSimdX2(
        Vector256<double> crA, Vector256<double> crB, Vector256<double> ci, int maxIter)
    {
        var escIterA = Vector256.Create(-1.0);
        var escIterB = escIterA;
        var escMagA = Vector256<double>.Zero;
        var escMagB = escMagA;

        var activeA = NotInCardioidOrBulb(crA, ci);
        var activeB = NotInCardioidOrBulb(crB, ci);
        if ((activeA | activeB).ExtractMostSignificantBits() == 0) return (escIterA, escMagA, escIterB, escMagB);

        var bailout = Vector256.Create(Bailout);
        Vector256<double> zrA = default, ziA = default, zr2A = default, zi2A = default, prA = default, piA = default;
        Vector256<double> zrB = default, ziB = default, zr2B = default, zi2B = default, prB = default, piB = default;
        int check = 8, counter = 0;

        for (int i = 0; i < maxIter; i++)
        {
            ziA = (zrA + zrA) * ziA + ci;
            ziB = (zrB + zrB) * ziB + ci;
            zrA = zr2A - zi2A + crA;
            zrB = zr2B - zi2B + crB;
            zr2A = zrA * zrA;
            zr2B = zrB * zrB;
            zi2A = ziA * ziA;
            zi2B = ziB * ziB;
            var magA = zr2A + zi2A;
            var magB = zr2B + zi2B;

            var escA = Vector256.GreaterThan(magA, bailout) & activeA;
            var escB = Vector256.GreaterThan(magB, bailout) & activeB;
            var perA = Vector256.Equals(zrA, prA) & Vector256.Equals(ziA, piA) & activeA;
            var perB = Vector256.Equals(zrB, prB) & Vector256.Equals(ziB, piB) & activeB;

            if ((escA | escB | perA | perB).ExtractMostSignificantBits() != 0)
            {
                escIterA = Vector256.ConditionalSelect(escA, Vector256.Create((double)i), escIterA);
                escIterB = Vector256.ConditionalSelect(escB, Vector256.Create((double)i), escIterB);
                escMagA = Vector256.ConditionalSelect(escA, magA, escMagA);
                escMagB = Vector256.ConditionalSelect(escB, magB, escMagB);
                activeA = Vector256.AndNot(activeA, escA | perA);
                activeB = Vector256.AndNot(activeB, escB | perB);
                if ((activeA | activeB).ExtractMostSignificantBits() == 0) break;
            }

            if (++counter == check)
            {
                counter = 0;
                check <<= 1;
                prA = zrA; piA = ziA;
                prB = zrB; piB = ziB;
            }
        }
        return (escIterA, escMagA, escIterB, escMagB);
    }

    // =========================================================================

    /// <summary>Smooth (continuous) iteration count to avoid colour banding.</summary>
    private static float Smooth(int i, double magSquared)
    {
        double logZn = Math.Log(magSquared) * 0.5;
        double nu = Math.Log(logZn / Ln2) / Ln2;
        double smooth = i + 2 - nu;
        return smooth < 0 ? 0f : (float)smooth;
    }
}
