using System.Buffers;
using System.Runtime.Intrinsics;

namespace Fractal;

/// <summary>
/// Computes smooth iteration counts in parallel using (cores - 2) worker threads, minimum 1.
/// Output is colour-independent; see <see cref="Colorizer"/> for turning it into pixels.
/// </summary>
public sealed class MandelbrotRenderer
{
    /// <summary>Pixel was iterated and found to be inside the set.</summary>
    public const float Inside = -1f;

    /// <summary>Pixel is inside the set, inferred by interior fill rather than iterated.</summary>
    public const float FilledInside = -2f;

    private const double Bailout = 256.0; // large bailout gives smoother colouring
    private static readonly double Ln2 = Math.Log(2.0);

    /// <summary>Work unit handed to threads; aligned to the global pixel grid.</summary>
    private const int TileSize = 64;

    /// <summary>Rectangles this thin or thinner are just computed, not subdivided further.</summary>
    private const int MinFillSize = 6;

    public int WorkerCount { get; } = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>True if the CPU can run 4 doubles per instruction (AVX2).</summary>
    public static bool SimdSupported => Vector256.IsHardwareAccelerated;

    /// <summary>Use the 4-wide SIMD kernel. Produces bit-identical results to the scalar kernel.</summary>
    public bool UseSimd { get; set; } = SimdSupported;

    /// <summary>
    /// Interior fill (Mariani–Silver). The Mandelbrot set is connected and has no holes, so if
    /// the whole border of a rectangle is inside the set, so is everything within it. Each tile's
    /// border is computed first; if it is entirely inside, the interior is filled without
    /// iterating; otherwise the tile is split into quarters and the test repeats.
    /// Interior pixels are the expensive ones (they run to the iteration limit), so this pays
    /// off most exactly where rendering is slowest.
    /// </summary>
    public bool UseInteriorFill { get; set; } = true;

    private readonly struct Job(float[] dst, Viewport view, bool simd, bool fill, RenderProgress progress)
    {
        public readonly float[] Dst = dst;
        public readonly Viewport View = view;
        public readonly bool Simd = simd;
        public readonly bool Fill = fill;
        public readonly RenderProgress Progress = progress;
    }

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
        var job = new Job(dst, v, UseSimd && SimdSupported, UseInteriorFill, progress);

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

        // Pass 1: copy reusable pixels; mark everything else unknown (NaN).
        Parallel.For(0, h, options, py =>
        {
            int row = py * w;
            long gy = v.OriginY + py;
            if (reuse && gy >= oy0 && gy < oy1)
            {
                var s = source!.View;
                int a = (int)(ox0 - v.OriginX);
                int b = (int)(ox1 - v.OriginX);
                int srcIndex = (int)(gy - s.OriginY) * s.Width + (int)(ox0 - s.OriginX);
                dst.AsSpan(row, a).Fill(float.NaN);
                Array.Copy(source.Values, srcIndex, dst, row + a, b - a);
                dst.AsSpan(row + b, w - b).Fill(float.NaN);
            }
            else
            {
                dst.AsSpan(row, w).Fill(float.NaN);
            }
        });

        // Pass 2: tiles aligned to the global grid, so a pan sees the same tile boundaries.
        long tx0 = FloorDiv(v.OriginX, TileSize), tx1 = FloorDiv(v.OriginX + w - 1, TileSize);
        long ty0 = FloorDiv(v.OriginY, TileSize), ty1 = FloorDiv(v.OriginY + h - 1, TileSize);
        int tilesX = (int)(tx1 - tx0 + 1), tilesY = (int)(ty1 - ty0 + 1);

        // Tiles are handed out dynamically, which balances load well since tiles near the set
        // take far longer than ones outside it (and suits the i9's mix of P- and E-cores).
        Parallel.For(0, tilesX * tilesY, options, (t, state) =>
        {
            if (token.IsCancellationRequested) { state.Stop(); return; }

            long gx = (tx0 + t % tilesX) * TileSize;
            long gy = (ty0 + t / tilesX) * TileSize;
            int x0 = (int)Math.Max(0, gx - v.OriginX), x1 = (int)Math.Min(w, gx + TileSize - v.OriginX);
            int y0 = (int)Math.Max(0, gy - v.OriginY), y1 = (int)Math.Min(h, gy + TileSize - v.OriginY);

            int[] buf = ArrayPool<int>.Shared.Rent(TileSize * TileSize);
            try
            {
                ProcessRect(job, buf, x0, y0, x1, y1);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buf);
            }
        });

        token.ThrowIfCancellationRequested();
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // =========================================================================
    // Interior fill (Mariani–Silver)
    // =========================================================================

    private static void ProcessRect(in Job job, int[] buf, int x0, int y0, int x1, int y1)
    {
        int rw = x1 - x0, rh = y1 - y0;
        if (rw <= 0 || rh <= 0) return;

        if (!job.Fill || rw <= MinFillSize || rh <= MinFillSize)
        {
            ComputeUnknownIn(job, buf, x0, y0, x1, y1);
            return;
        }

        var dst = job.Dst;
        int w = job.View.Width;

        // Compute whatever is still unknown on the border. Each edge is queued as a contiguous
        // run so SIMD lanes get neighbouring pixels (similar iteration counts, little waste).
        int n = 0;
        for (int x = x0; x < x1; x++) AddIfUnknown(dst, buf, ref n, y0 * w + x);
        for (int x = x0; x < x1; x++) AddIfUnknown(dst, buf, ref n, (y1 - 1) * w + x);
        for (int y = y0 + 1; y < y1 - 1; y++) AddIfUnknown(dst, buf, ref n, y * w + x0);
        for (int y = y0 + 1; y < y1 - 1; y++) AddIfUnknown(dst, buf, ref n, y * w + x1 - 1);
        ComputeIndices(job, buf.AsSpan(0, n));

        // Count border pixels inside the set.
        int inside = 0, borderCount = 2 * rw + 2 * (rh - 2);
        for (int x = x0; x < x1; x++)
        {
            if (dst[y0 * w + x] < 0) inside++;
            if (dst[(y1 - 1) * w + x] < 0) inside++;
        }
        for (int y = y0 + 1; y < y1 - 1; y++)
        {
            if (dst[y * w + x0] < 0) inside++;
            if (dst[y * w + x1 - 1] < 0) inside++;
        }

        if (inside == 0)
        {
            // Border touches no interior: subdividing rarely pays off here (it would need an
            // island of the set entirely within this rectangle), so compute it straight.
            ComputeUnknownIn(job, buf, x0, y0, x1, y1);
            return;
        }

        if (inside == borderCount)
        {
            long filled = 0;
            for (int y = y0 + 1; y < y1 - 1; y++)
            {
                var span = dst.AsSpan(y * w + x0 + 1, rw - 2);
                for (int i = 0; i < span.Length; i++)
                {
                    if (float.IsNaN(span[i]))
                    {
                        span[i] = FilledInside;
                        filled++;
                    }
                }
            }
            job.Progress.Add(filled);
            return;
        }

        // Mixed border: split into quarters. Children reuse the border pixels already computed.
        int mx = x0 + rw / 2, my = y0 + rh / 2;
        ProcessRect(job, buf, x0, y0, mx, my);
        ProcessRect(job, buf, mx, y0, x1, my);
        ProcessRect(job, buf, x0, my, mx, y1);
        ProcessRect(job, buf, mx, my, x1, y1);
    }

    private static void AddIfUnknown(float[] dst, int[] buf, ref int n, int index)
    {
        if (float.IsNaN(dst[index])) buf[n++] = index;
    }

    private static void ComputeUnknownIn(in Job job, int[] buf, int x0, int y0, int x1, int y1)
    {
        var dst = job.Dst;
        int w = job.View.Width;
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                AddIfUnknown(dst, buf, ref n, y * w + x);
        ComputeIndices(job, buf.AsSpan(0, n));
    }

    /// <summary>Iterates an arbitrary set of pixels (by index into the field).</summary>
    private static void ComputeIndices(in Job job, ReadOnlySpan<int> indices)
    {
        if (indices.IsEmpty) return;
        if (job.Simd) ComputeIndicesSimd(job, indices);
        else ComputeIndicesScalar(job, indices);
        job.Progress.Add(indices.Length);
    }

    // =========================================================================
    // Scalar kernel (one pixel at a time)
    // =========================================================================

    private static void ComputeIndicesScalar(in Job job, ReadOnlySpan<int> indices)
    {
        var v = job.View;
        int w = v.Width;
        foreach (int idx in indices)
        {
            int py = idx / w, px = idx - py * w;
            double cr = v.AnchorX + (v.OriginX + px) * v.Scale;
            double ci = v.AnchorY - (v.OriginY + py) * v.Scale;
            job.Dst[idx] = IterateScalar(cr, ci, v.MaxIterations);
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
    // SIMD kernel (8 pixels per step with AVX2)
    // =========================================================================

    /// <summary>
    /// Every pixel goes through the same lane maths, including padded groups at the end
    /// (spare lanes repeat the last pixel). Lanes never influence each other, so a pixel's
    /// value is identical however pixels are grouped, which keeps pan reuse seamless.
    /// </summary>
    private static void ComputeIndicesSimd(in Job job, ReadOnlySpan<int> indices)
    {
        var v = job.View;
        int w = v.Width;
        Span<double> cr = stackalloc double[8];
        Span<double> ci = stackalloc double[8];
        Span<int> idx = stackalloc int[8];

        for (int start = 0; start < indices.Length; start += 8)
        {
            int n = Math.Min(8, indices.Length - start);
            for (int k = 0; k < 8; k++)
            {
                int i = indices[start + Math.Min(k, n - 1)];
                int py = i / w, px = i - py * w;
                idx[k] = i;
                cr[k] = v.AnchorX + (v.OriginX + px) * v.Scale;
                ci[k] = v.AnchorY - (v.OriginY + py) * v.Scale;
            }

            // Two interleaved 4-lane vectors: while one waits on multiply latency the CPU
            // works on the other.
            var (iterA, magA, iterB, magB) = IterateSimdX2(
                Vector256.Create((ReadOnlySpan<double>)cr[..4]), Vector256.Create((ReadOnlySpan<double>)ci[..4]),
                Vector256.Create((ReadOnlySpan<double>)cr[4..]), Vector256.Create((ReadOnlySpan<double>)ci[4..]),
                v.MaxIterations);

            for (int k = 0; k < n; k++)
            {
                double it = k < 4 ? iterA.GetElement(k) : iterB.GetElement(k - 4);
                double mag = k < 4 ? magA.GetElement(k) : magB.GetElement(k - 4);
                job.Dst[idx[k]] = it < 0 ? Inside : Smooth((int)it, mag);
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

    /// <summary>
    /// Two independent 4-lane iterations in one loop. Per-lane maths is identical to the scalar
    /// kernel. Returns escape iteration (-1 = inside) and |z|² at escape.
    /// </summary>
    private static (Vector256<double>, Vector256<double>, Vector256<double>, Vector256<double>) IterateSimdX2(
        Vector256<double> crA, Vector256<double> ciA, Vector256<double> crB, Vector256<double> ciB, int maxIter)
    {
        var escIterA = Vector256.Create(-1.0);
        var escIterB = escIterA;
        var escMagA = Vector256<double>.Zero;
        var escMagB = escMagA;

        var activeA = NotInCardioidOrBulb(crA, ciA);
        var activeB = NotInCardioidOrBulb(crB, ciB);
        if ((activeA | activeB).ExtractMostSignificantBits() == 0) return (escIterA, escMagA, escIterB, escMagB);

        var bailout = Vector256.Create(Bailout);
        Vector256<double> zrA = default, ziA = default, zr2A = default, zi2A = default, prA = default, piA = default;
        Vector256<double> zrB = default, ziB = default, zr2B = default, zi2B = default, prB = default, piB = default;
        int check = 8, counter = 0;

        for (int i = 0; i < maxIter; i++)
        {
            ziA = (zrA + zrA) * ziA + ciA;
            ziB = (zrB + zrB) * ziB + ciB;
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
