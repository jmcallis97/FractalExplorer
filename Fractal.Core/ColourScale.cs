namespace Fractal;

/// <summary>
/// What the view-relative colour mappings need to know about a rendered view: its floor (the
/// fewest iterations any pixel took; deep down every pixel takes tens of thousands, so Sqrt
/// and Log count from here rather than from 0) and the distribution of its values (Equalize
/// gives each colour an equal share of the pixels).
///
/// Taken from one finished render and then kept while you pan, so colours don't shift as new
/// edges come in. Values outside what was sampled still map sensibly (below the floor counts
/// as the floor; beyond the sample, the ends of the distribution).
/// </summary>
public sealed class ColourScale
{
    private const int MaxSamples = 1 << 16;

    /// <summary>Sqrt and Log count iterations from here.</summary>
    public double Floor { get; }

    private readonly float[] _sorted; // a sample of the escaped values, ascending

    private ColourScale(double floor, float[] sorted)
    {
        Floor = floor;
        _sorted = sorted;
    }

    /// <summary>
    /// The scale of <paramref name="field"/>, with its floor replaced by <paramref name="floor"/>
    /// if given (a place saved with its floor looks the same when you come back). Null if no
    /// pixel escaped.
    /// </summary>
    public static ColourScale? From(Field field, double? floor = null)
    {
        var values = field.Values;
        int escaped = 0;
        float min = float.MaxValue;
        foreach (float v in values)
            if (v >= 0) { escaped++; if (v < min) min = v; }
        if (escaped == 0) return null;

        int stride = Math.Max(1, escaped / MaxSamples);
        var sample = new List<float>(Math.Min(escaped, MaxSamples + 1));
        int k = 0;
        foreach (float v in values)
            if (v >= 0 && k++ % stride == 0) sample.Add(v);
        var sorted = sample.ToArray();
        Array.Sort(sorted);
        return new ColourScale(floor ?? min, sorted);
    }

    /// <summary>Fraction of the view's escaped pixels below <paramref name="s"/> (0 to 1), interpolated between samples.</summary>
    public double Rank(double s)
    {
        var a = _sorted;
        if (s <= a[0]) return 0;
        if (s >= a[^1]) return 1;
        int lo = 0, hi = a.Length - 1; // a[lo] < s <= a[hi]
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] < s) lo = mid; else hi = mid;
        }
        double span = a[hi] - a[lo];
        return (lo + (span > 0 ? (s - a[lo]) / span : 0)) / (a.Length - 1);
    }

    /// <summary>The value below which a fraction <paramref name="q"/> of the view's escaped pixels lie.</summary>
    public double Percentile(double q) => _sorted[(int)Math.Clamp(Math.Round(q * (_sorted.Length - 1)), 0, _sorted.Length - 1)];
}
