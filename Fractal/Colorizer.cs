namespace Fractal;

/// <summary>How smooth iteration counts are mapped onto the palette.</summary>
public enum ColorMapping
{
    /// <summary>Evenly spaced bands. Great when shallow; can get noisy very deep.</summary>
    Linear,
    /// <summary>Bands widen with depth. A good all-rounder.</summary>
    Sqrt,
    /// <summary>Bands widen a lot with depth. Calms down very high iteration areas.</summary>
    Log,
}

/// <summary>
/// Turns a computed <see cref="Field"/> into pixels. This is cheap (a few ms for a full screen),
/// which is what makes palette preview and colour cycling instant.
/// </summary>
public static class Colorizer
{
    private const int InsideColor = unchecked((int)0xFF000000);
    private const int FilledColor = unchecked((int)0xFF0E3A1A); // dark green: "skipped by interior fill"

    public static void Colorize(Field field, Palette palette, ColorMapping mapping,
                                double density, double offset, int[] dest, int workers,
                                bool showFilled = false)
    {
        var values = field.Values;
        var lut = palette.Lut;
        int n = lut.Length;
        int w = field.View.Width, h = field.View.Height;

        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = workers }, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float s = values[row + x];
                if (s < 0)
                {
                    dest[row + x] = showFilled && s == MandelbrotRenderer.FilledInside ? FilledColor : InsideColor;
                    continue;
                }

                double m = mapping switch
                {
                    ColorMapping.Sqrt => Math.Sqrt(s) * 0.25,
                    ColorMapping.Log => Math.Log(s + 1) * 0.75,
                    _ => s * (1.0 / 64),
                };
                double t = m * density + offset;
                t -= Math.Floor(t);
                int idx = (int)(t * n);
                dest[row + x] = lut[idx >= n ? n - 1 : idx];
            }
        });
    }
}
