namespace Fractal;

/// <summary>How smooth iteration counts are mapped onto the palette.</summary>
public enum ColorMapping
{
    /// <summary>Evenly spaced bands. Great when shallow; can get noisy very deep.</summary>
    Linear,
    /// <summary>Bands widen with the iterations above the view's floor (past its typical spread). A good all-rounder.</summary>
    Sqrt,
    /// <summary>Bands widen a lot with the iterations above the view's floor (past its typical spread). Calms down busy areas.</summary>
    Log,
    /// <summary>
    /// By rank: each colour gets an equal share of the pixels on screen, whatever the spread of
    /// iteration counts. Density = how many times the palette runs across the view.
    /// </summary>
    Equalize,
}

/// <summary>
/// Turns a computed <see cref="Field"/> into pixels. This is cheap (a few ms for a full screen),
/// which is what makes palette preview and colour cycling instant.
/// </summary>
public static class Colorizer
{
    private const int InsideColor = unchecked((int)0xFF000000);
    private const int FilledColor = unchecked((int)0xFF0E3A1A); // dark green: "skipped by interior fill"

    /// <summary>
    /// Position on the palette before density and offset, in palette cycles. Sqrt and Log count
    /// from <paramref name="scale"/>'s floor and bend at its knee (without a scale: from 0, knee
    /// 1, as they always did); Equalize needs a scale (and is Linear without one). Linear doesn't
    /// use the floor: subtracting it would only rotate the palette.
    /// </summary>
    public static double Map(ColorMapping mapping, double s, ColourScale? scale)
    {
        double above = Math.Max(0, s - (scale?.Floor ?? 0));
        double knee = scale?.Knee ?? 1;
        return mapping switch
        {
            // Both have a finite slope at the floor (1/(8·√knee) and 0.75/knee cycles per
            // iteration): plain √x and ln x are vertical there, which at a high density turned
            // the calm ground just above the floor into fine moiré stripes.
            ColorMapping.Sqrt => (Math.Sqrt(above + knee) - Math.Sqrt(knee)) * 0.25,
            ColorMapping.Log => Math.Log(1 + above / knee) * 0.75,
            ColorMapping.Equalize when scale != null => scale.Rank(s),
            _ => s * (1.0 / 64),
        };
    }

    /// <summary>
    /// Density that makes the bulk of the view (1st to 99th percentile of its values) span the
    /// palette <paramref name="cycles"/> times. Null if the view has no spread.
    /// </summary>
    public static double? FitDensity(ColorMapping mapping, ColourScale scale, double cycles = 2)
    {
        double span = Map(mapping, scale.Percentile(0.99), scale) - Map(mapping, scale.Percentile(0.01), scale);
        return span > 1e-12 ? cycles / span : null;
    }

    /// <param name="scale">The view's floor and distribution, for Sqrt, Log and Equalize (see <see cref="Map"/>).</param>
    /// <param name="effects">Mirror and inside-by-period are applied here (the rest come in <paramref name="shade"/>).</param>
    /// <param name="shade">Per-pixel brightness factors, or null; see <see cref="Effects.ShadeMap"/>.</param>
    /// <param name="extra">The view's extra numbers, for the inside-by-period effect (null: skipped).</param>
    public static void Colorize(Field field, Palette palette, ColorMapping mapping,
                                double density, double offset, int[] dest, int workers,
                                bool showFilled = false, ColourScale? scale = null, Effects? effects = null,
                                float[]? shade = null, ExtraField? extra = null)
    {
        bool mirror = effects?.Mirror == true;
        bool periods = effects?.InsidePeriod == true && extra != null;
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
                if (float.IsNaN(s)) { dest[row + x] = 0; continue; } // not computed yet: transparent
                if (s < 0)
                {
                    if (periods && extra!.Period[row + x] > 0)
                    {
                        // Inside, coloured by period: each period its own place on the palette (golden-ratio spaced).
                        double a = extra.Period[row + x] * 0.6180339887 + offset;
                        dest[row + x] = lut[(int)((a - Math.Floor(a)) * (n - 1))];
                        continue;
                    }
                    dest[row + x] = showFilled && s == MandelbrotRenderer.FilledInside ? FilledColor : InsideColor;
                    continue;
                }
                double t = Map(mapping, s, scale) * density + offset;
                t -= Math.Floor(t);
                if (mirror) t = t < 0.5 ? 2 * t : 2 - 2 * t;
                int idx = (int)(t * n);
                int c = lut[idx >= n ? n - 1 : idx];
                if (shade != null) c = Shade(c, shade[row + x]);
                dest[row + x] = c;
            }
        });
    }

    private static int Shade(int c, float f)
    {
        int r = Math.Min(255, (int)(((c >> 16) & 0xFF) * f));
        int g = Math.Min(255, (int)(((c >> 8) & 0xFF) * f));
        int b = Math.Min(255, (int)((c & 0xFF) * f));
        return unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
    }
}
