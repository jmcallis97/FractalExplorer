using System.Drawing.Imaging;
using System.Globalization;

namespace Fractal;

/// <summary>
/// A cyclic colour gradient baked into a lookup table. Every palette wraps seamlessly
/// (the end blends back into the start) so colour cycling never shows a seam.
/// </summary>
public sealed class Palette
{
    public const int Size = 2048;
    private const int Opaque = unchecked((int)0xFF000000);

    public string Name { get; }
    public int[] Lut { get; }

    private Palette(string name, int[] lut)
    {
        Name = name;
        Lut = lut;
    }

    // ---- Construction ---------------------------------------------------

    /// <summary>Gradient through colour stops at positions in [0,1). Wraps around.</summary>
    public static Palette FromStops(string name, params (double Pos, int Rgb)[] stops)
    {
        var sorted = stops.OrderBy(s => s.Pos).ToList();
        var ext = new List<(double Pos, int Rgb)>(sorted.Count + 2) { (sorted[^1].Pos - 1, sorted[^1].Rgb) };
        ext.AddRange(sorted);
        ext.Add((sorted[0].Pos + 1, sorted[0].Rgb));

        var lut = new int[Size];
        int seg = 0;
        for (int i = 0; i < Size; i++)
        {
            double t = (double)i / Size;
            while (t > ext[seg + 1].Pos) seg++;
            double span = ext[seg + 1].Pos - ext[seg].Pos;
            double f = span <= 0 ? 0 : (t - ext[seg].Pos) / span;
            lut[i] = Lerp(ext[seg].Rgb, ext[seg + 1].Rgb, f) | Opaque;
        }
        return new Palette(name, lut);
    }

    /// <summary>
    /// Procedural palette: colour(t) = a + b·cos(2π(c·t + d)) per channel.
    /// Integer c values keep the palette seamless.
    /// </summary>
    public static Palette Cosine(string name,
        (double R, double G, double B) a, (double R, double G, double B) b,
        (double R, double G, double B) c, (double R, double G, double B) d)
    {
        var lut = new int[Size];
        for (int i = 0; i < Size; i++)
        {
            double t = (double)i / Size;
            int r = Channel(a.R + b.R * Math.Cos(2 * Math.PI * (c.R * t + d.R)));
            int g = Channel(a.G + b.G * Math.Cos(2 * Math.PI * (c.G * t + d.G)));
            int bl = Channel(a.B + b.B * Math.Cos(2 * Math.PI * (c.B * t + d.B)));
            lut[i] = Opaque | (r << 16) | (g << 8) | bl;
        }
        return new Palette(name, lut);
    }

    public static Palette Random(Random rng, int number)
    {
        string name = $"Random #{number}";
        double R(double lo, double hi) => lo + rng.NextDouble() * (hi - lo);

        if (rng.NextDouble() < 0.5)
        {
            int[] freqs = [1, 1, 1, 2];
            return Cosine(name,
                (R(0.35, 0.65), R(0.35, 0.65), R(0.35, 0.65)),
                (R(0.3, 0.55), R(0.3, 0.55), R(0.3, 0.55)),
                (freqs[rng.Next(4)], freqs[rng.Next(4)], freqs[rng.Next(4)]),
                (rng.NextDouble(), rng.NextDouble(), rng.NextDouble()));
        }

        // Alternating dark/bright stops with random hues: tends to give strong contrast.
        int n = rng.Next(3, 7) * 2;
        double baseHue = rng.NextDouble() * 360;
        double hueStep = R(20, 140);
        var stops = new (double, int)[n];
        for (int k = 0; k < n; k++)
        {
            double pos = (k + R(-0.2, 0.2)) / n;
            double hue = (baseHue + k * hueStep) % 360;
            double val = k % 2 == 0 ? R(0.03, 0.3) : R(0.75, 1.0);
            stops[k] = ((pos + 1) % 1, HsvToRgb(hue, R(0.35, 1.0), val));
        }
        return FromStops(name, stops);
    }

    /// <summary>Loads a Fractint-style .map file (one "r g b" triple per line).</summary>
    public static Palette? FromMapFile(string path)
    {
        var colors = new List<int>();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
            {
                colors.Add((Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255));
            }
        }
        if (colors.Count < 2) return null;
        var stops = colors.Select((c, i) => ((double)i / colors.Count, c)).ToArray();
        return FromStops(Path.GetFileNameWithoutExtension(path), stops);
    }

    /// <summary>Saves as a 256-entry Fractint-style .map file.</summary>
    public void SaveMapFile(string path)
    {
        using var w = new StreamWriter(path);
        for (int i = 0; i < 256; i++)
        {
            int c = Lut[i * Size / 256];
            w.WriteLine($"{(c >> 16) & 0xFF} {(c >> 8) & 0xFF} {c & 0xFF}");
        }
    }

    /// <summary>A width×1 strip of the palette, rotated by <paramref name="offset"/> cycles.</summary>
    public Bitmap CreateStrip(int width, double offset = 0)
    {
        var bmp = new Bitmap(width, 1, PixelFormat.Format32bppArgb);
        for (int x = 0; x < width; x++)
        {
            double t = (double)x / width + offset;
            t -= Math.Floor(t);
            bmp.SetPixel(x, 0, Color.FromArgb(Lut[Math.Min(Size - 1, (int)(t * Size))]));
        }
        return bmp;
    }

    // ---- Built-ins -------------------------------------------------------

    public static List<Palette> BuiltIns()
    {
        var rainbow = Enumerable.Range(0, 12)
            .Select(i => (i / 12.0, HsvToRgb(i * 30, 0.85, 1.0)))
            .ToArray();

        return
        [
            FromStops("Classic", (0, 0x000764), (0.16, 0x206BCB), (0.42, 0xEDFFFF), (0.6425, 0xFFAA00), (0.8575, 0x000200)),
            FromStops("Fire", (0, 0x000000), (0.2, 0x5A0000), (0.4, 0xD02000), (0.6, 0xFF9A00), (0.78, 0xFFF5A0), (0.9, 0x802000)),
            FromStops("Ocean", (0, 0x000B1E), (0.25, 0x00487A), (0.5, 0x1FB5D6), (0.65, 0xE8FFFF), (0.82, 0x0A6E8C)),
            FromStops("Electric", (0, 0x07000F), (0.18, 0x5B00D6), (0.38, 0x00E5FF), (0.5, 0xFFFFFF), (0.65, 0xFF2BD6), (0.85, 0x22004A)),
            FromStops("Sunset", (0, 0x14062B), (0.25, 0x7A1F5C), (0.5, 0xF2643A), (0.7, 0xFFD36B), (0.88, 0x3B1C5A)),
            FromStops("Forest", (0, 0x061206), (0.28, 0x2C6B2F), (0.5, 0x9ED36A), (0.65, 0xF4EBC1), (0.82, 0x5C3B1E)),
            FromStops("Moonlight", (0, 0x000000), (0.45, 0x8FA6D9), (0.55, 0xFFFFFF), (0.7, 0x30406A)),
            FromStops("Rainbow", rainbow),
            Cosine("Psychedelic", (.5, .5, .5), (.5, .5, .5), (1, 1, 1), (0, .33, .67)),
            Cosine("Ember", (.5, .5, .5), (.5, .5, .5), (1, 1, 1), (0, .10, .20)),
            Cosine("Candy", (.8, .5, .4), (.2, .4, .2), (2, 1, 1), (0, .25, .25)),
            FromStops("Zebra", (0, 0x000000), (0.47, 0x000000), (0.5, 0xFFFFFF), (0.97, 0xFFFFFF)),
        ];
    }

    // ---- Helpers ---------------------------------------------------------

    private static int Channel(double v) => (int)(Math.Clamp(v, 0, 1) * 255 + 0.5);

    private static int Lerp(int a, int b, double t)
    {
        int r = (int)(((a >> 16) & 0xFF) * (1 - t) + ((b >> 16) & 0xFF) * t + 0.5);
        int g = (int)(((a >> 8) & 0xFF) * (1 - t) + ((b >> 8) & 0xFF) * t + 0.5);
        int bl = (int)((a & 0xFF) * (1 - t) + (b & 0xFF) * t + 0.5);
        return (r << 16) | (g << 8) | bl;
    }

    private static int HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return (Channel(r + m) << 16) | (Channel(g + m) << 8) | Channel(b + m);
    }
}
