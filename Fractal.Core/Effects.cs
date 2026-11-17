using System.Text.Json.Serialization;

namespace Fractal;

/// <summary>
/// Optional effects laid over the colouring of one image (E in the app). They work from the
/// rendered values alone, so turning them on or changing them never re-renders.
/// </summary>
/// <param name="Relief">Light the image as if the iteration counts were the height of a landscape.</param>
/// <param name="ReliefStrength">How steep that landscape is (1 = moderate).</param>
/// <param name="LightAngle">Where the light comes from, in degrees: 0 = right, 90 = top, 135 = top left.</param>
/// <param name="Outline">Darken where the values change fast: traces the filaments and edges.</param>
/// <param name="OutlineStrength">How dark (1 = moderate).</param>
/// <param name="Mirror">Run the palette forward then back, so it has no seam where it repeats.</param>
public sealed record Effects(
    bool Relief = false, double ReliefStrength = 1, double LightAngle = 135,
    bool Outline = false, double OutlineStrength = 1,
    bool Mirror = false)
{
    public static readonly Effects None = new();

    [JsonIgnore] public bool Any => Relief || Outline || Mirror;

    /// <summary>True if <see cref="ShadeMap"/> has anything to do.</summary>
    [JsonIgnore] public bool Shades => Relief || Outline;

    /// <summary>"Relief, Outline", or "none".</summary>
    public override string ToString()
    {
        var on = new List<string>();
        if (Relief) on.Add("Relief");
        if (Outline) on.Add("Outline");
        if (Mirror) on.Add("Mirror");
        return on.Count == 0 ? "none" : string.Join(", ", on);
    }

    private const double LightElevation = 45 * Math.PI / 180;

    /// <summary>
    /// Brightness factor per pixel (1 = unchanged) for relief and outline, into
    /// <paramref name="dest"/>. The "height" is the position on the palette before it wraps
    /// (<see cref="Colorizer.Map"/>), so the landscape follows the colour bands you see. How steep
    /// that is varies enormously between views, so strengths are relative to this view's typical
    /// slope: strength 1 looks about as strong in a calm shallow view as in a busy deep one.
    /// </summary>
    public void ShadeMap(Field field, ColorMapping mapping, ColourScale? scale, float[] dest, int workers)
    {
        double Height(double s) => Colorizer.Map(mapping, s, scale);
        var values = field.Values;
        int w = field.View.Width, h = field.View.Height;
        double a = LightAngle * Math.PI / 180;
        // Towards the light, in screen axes (x right, y down, z out of the screen).
        double lx = Math.Cos(a) * Math.Cos(LightElevation), ly = -Math.Sin(a) * Math.Cos(LightElevation), lz = Math.Sin(LightElevation);

        double typical = TypicalSlope(values, w, h, Height);
        double steep = 0.6 * ReliefStrength / typical;       // the median slope gets a gentle ~30 degrees
        double ink = 0.01 * OutlineStrength;

        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = workers }, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (!(values[row + x] >= 0)) { dest[row + x] = 1; continue; } // inside, or not computed yet
                var (gx, gy) = Gradient(values, w, h, x, y, Height);
                double f = 1;
                if (Relief)
                {
                    // Surface normal of z = steep·height, against the light; flat ground keeps its colour.
                    double nx = -gx * steep, ny = -gy * steep;
                    double lambert = (nx * lx + ny * ly + lz) / Math.Sqrt(nx * nx + ny * ny + 1);
                    f *= Math.Clamp(0.15 + 0.85 * lambert / lz, 0.1, 1.6);
                }
                if (Outline)
                {
                    // Darkens where the slope is well above typical: filaments and edges, not smooth
                    // ground. At most to a quarter, so busy dust is shaded rather than blacked out.
                    double r = Math.Sqrt(gx * gx + gy * gy) / typical;
                    f *= 0.25 + 0.75 / (1 + ink * r * r);
                }
                dest[row + x] = (float)f;
            }
        });
    }

    /// <summary>Central-difference slope of the height at (x, y); a missing neighbour (edge, inside, not computed) counts as level.</summary>
    private static (double Gx, double Gy) Gradient(float[] values, int w, int h, int x, int y, Func<double, double> height)
    {
        int i = y * w + x;
        double c = height(values[i]);
        double At(bool exists, int j) => exists && values[j] >= 0 ? height(values[j]) : c;
        double gx = (At(x + 1 < w, i + 1) - At(x > 0, i - 1)) * 0.5;
        double gy = (At(y + 1 < h, i + w) - At(y > 0, i - w)) * 0.5;
        return (gx, gy);
    }

    /// <summary>A low-ish slope over a sample of the escaped pixels (30th percentile, never 0): the calm parts set the scale.</summary>
    private static double TypicalSlope(float[] values, int w, int h, Func<double, double> height)
    {
        var sample = new List<double>(20_000);
        int step = Math.Max(1, w * h / 20_000) | 1; // odd, so the sample doesn't line up with columns
        for (int i = 0; i < values.Length; i += step)
        {
            if (!(values[i] >= 0)) continue;
            var (gx, gy) = Gradient(values, w, h, i % w, i / w, height);
            double g = Math.Sqrt(gx * gx + gy * gy);
            if (g > 0) sample.Add(g);
        }
        if (sample.Count == 0) return 1;
        sample.Sort();
        return sample[(int)(sample.Count * 0.3)];
    }
}
