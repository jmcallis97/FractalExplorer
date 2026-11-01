namespace Fractal;

/// <summary>
/// Computes Mandelbrot images in parallel. Uses (cores - 2) worker threads, minimum 1.
/// </summary>
public sealed class MandelbrotRenderer
{
    public int WorkerCount { get; } = Math.Max(1, Environment.ProcessorCount - 2);

    private readonly int[] _palette = BuildPalette(1024);

    /// <summary>
    /// Renders into an ARGB pixel buffer (width * height).
    /// centerX/centerY is the complex-plane point at the image center;
    /// scale is the complex-plane width of one pixel.
    /// </summary>
    public void Render(int[] pixels, int width, int height,
                       double centerX, double centerY, double scale,
                       int maxIterations, CancellationToken token)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = WorkerCount,
            CancellationToken = token
        };

        double left = centerX - width / 2.0 * scale;
        double top = centerY - height / 2.0 * scale;
        const double bailout = 256.0; // large bailout gives smoother coloring
        double log2 = Math.Log(2.0);

        // Rows are handed out dynamically, which balances load well since
        // rows near the set take far longer than rows outside it.
        Parallel.For(0, height, options, (py, state) =>
        {
            if (token.IsCancellationRequested) { state.Stop(); return; }

            double ci = top + py * scale;
            int rowOffset = py * width;

            for (int px = 0; px < width; px++)
            {
                double cr = left + px * scale;

                // Quick reject: main cardioid and period-2 bulb are always inside.
                double q = (cr - 0.25) * (cr - 0.25) + ci * ci;
                if (q * (q + (cr - 0.25)) <= 0.25 * ci * ci ||
                    (cr + 1) * (cr + 1) + ci * ci <= 0.0625)
                {
                    pixels[rowOffset + px] = unchecked((int)0xFF000000);
                    continue;
                }

                double zr = 0, zi = 0, zr2 = 0, zi2 = 0;
                int i = 0;
                while (i < maxIterations && zr2 + zi2 <= bailout)
                {
                    zi = 2 * zr * zi + ci;
                    zr = zr2 - zi2 + cr;
                    zr2 = zr * zr;
                    zi2 = zi * zi;
                    i++;
                }

                if (i >= maxIterations)
                {
                    pixels[rowOffset + px] = unchecked((int)0xFF000000);
                }
                else
                {
                    // Smooth (continuous) iteration count to avoid banding.
                    double logZn = Math.Log(zr2 + zi2) / 2;
                    double nu = Math.Log(logZn / log2) / log2;
                    double smooth = i + 1 - nu;
                    pixels[rowOffset + px] = ColorFor(smooth);
                }
            }
        });
    }

    private int ColorFor(double smooth)
    {
        double t = smooth * 8.0; // palette stretch; tweak for different looks
        int n = _palette.Length;
        int idx = (int)t;
        double frac = t - idx;
        int c1 = _palette[((idx % n) + n) % n];
        int c2 = _palette[(((idx + 1) % n) + n) % n];
        return Lerp(c1, c2, frac);
    }

    private static int Lerp(int a, int b, double t)
    {
        int r = (int)(((a >> 16) & 0xFF) * (1 - t) + ((b >> 16) & 0xFF) * t);
        int g = (int)(((a >> 8) & 0xFF) * (1 - t) + ((b >> 8) & 0xFF) * t);
        int bl = (int)((a & 0xFF) * (1 - t) + (b & 0xFF) * t);
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
    }

    /// <summary>Classic "Ultra Fractal"-style gradient, cycled.</summary>
    private static int[] BuildPalette(int size)
    {
        (double pos, int r, int g, int b)[] stops =
        [
            (0.0,    0,   7, 100),
            (0.16,  32, 107, 203),
            (0.42, 237, 255, 255),
            (0.6425, 255, 170,  0),
            (0.8575,  0,   2,   0),
            (1.0,    0,   7, 100),
        ];

        var palette = new int[size];
        for (int i = 0; i < size; i++)
        {
            double p = (double)i / size;
            int s = 0;
            while (s < stops.Length - 2 && p > stops[s + 1].pos) s++;
            var (p0, r0, g0, b0) = stops[s];
            var (p1, r1, g1, b1) = stops[s + 1];
            double t = (p - p0) / (p1 - p0);
            int r = (int)(r0 + (r1 - r0) * t);
            int g = (int)(g0 + (g1 - g0) * t);
            int b = (int)(b0 + (b1 - b0) * t);
            palette[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        return palette;
    }
}
