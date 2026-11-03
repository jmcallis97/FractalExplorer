using System.Globalization;

namespace Fractal;

/// <summary>
/// A place in the set, as copied with Ctrl+C: centre in full precision (decimal text), zoom
/// (relative to the whole set being 3.5 wide, so framing is the same at any window size), and
/// optionally the iteration limit that was in use.
/// </summary>
public sealed record Location(string Re, string Im, double Zoom, int? Iterations)
{
    /// <summary>Width of the complex plane shown at zoom 1.</summary>
    public const double ViewWidthAtZoom1 = 3.5;

    /// <summary>
    /// Parses the Ctrl+C format ("Re: ...", "Im: ...", "Zoom: ...", "Iterations: ..." lines),
    /// or plain "re im [zoom]".
    /// </summary>
    public static bool TryParse(string text, out Location location, out string error)
    {
        location = null!;
        string? re = null, im = null, zoomText = null, iterText = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant(), value = line[(colon + 1)..].Trim();
            if (key is "re" or "real" or "x") re = value;
            else if (key is "im" or "imag" or "y") im = value;
            else if (key == "zoom") zoomText = value.TrimEnd('x', 'X');
            else if (key == "iterations") iterText = value;
        }
        if (re == null)
        {
            var parts = text.Split((char[])[' ', '\t', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) (re, im) = (parts[0], parts[1]);
            if (parts.Length >= 3) zoomText = parts[2].TrimEnd('x', 'X');
        }
        if (re == null || im == null) { error = "no Re/Im"; return false; }

        double zoom = 1;
        if (zoomText != null && !double.TryParse(zoomText, NumberStyles.Float, CultureInfo.InvariantCulture, out zoom))
        {
            error = "bad zoom";
            return false;
        }
        int? iterations = int.TryParse(iterText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int it) ? it : null;

        // Validate the numbers now, so callers can rely on them parsing.
        try
        {
            Deep.BigFixed.Parse(re, 64);
            Deep.BigFixed.Parse(im, 64);
        }
        catch (FormatException)
        {
            error = "bad number";
            return false;
        }

        location = new Location(re, im, Math.Max(zoom, 1e-3), iterations);
        error = "";
        return true;
    }
}
