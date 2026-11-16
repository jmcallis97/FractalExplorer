using System.Globalization;

namespace Fractal;

/// <summary>
/// A place in the set, as copied with Ctrl+C: centre in full precision (decimal text), zoom
/// (relative to the whole set being 3.5 wide, so framing is the same at any window size), and
/// optionally the iteration limit and colouring that were in use. A "Snap" line asks the app to
/// find the exact spiral centre or minibrot near the location and zoom in to it ("Snap: 1e100"
/// gives the zoom to stop at).
/// </summary>
public sealed record Location(string Re, string Im, double Zoom, int? Iterations,
                              double? Density = null, ColorMapping? Mapping = null,
                              bool Snap = false, double? SnapZoom = null)
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
        string? re = null, im = null, zoomText = null, iterText = null, densityText = null, mappingText = null, snapText = null;
        bool snap = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Equals("snap", StringComparison.OrdinalIgnoreCase)) { snap = true; continue; }
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant(), value = line[(colon + 1)..].Trim();
            if (key is "re" or "real" or "x") re = value;
            else if (key is "im" or "imag" or "y") im = value;
            else if (key == "zoom") zoomText = value.TrimEnd('x', 'X');
            else if (key == "iterations") iterText = value;
            else if (key is "colour density" or "color density" or "density") densityText = value.TrimEnd('x', 'X');
            else if (key == "mapping") mappingText = value;
            else if (key == "snap") { snap = true; snapText = value.TrimEnd('x', 'X'); }
        }
        if (re == null)
        {
            var parts = text.Split((char[])[' ', '\t', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) (re, im) = (parts[0], parts[1]);
            if (parts.Length >= 3) zoomText = parts[2].TrimEnd('x', 'X');
        }
        if (re == null || im == null) { error = "no Re/Im"; return false; }

        double zoom = 1;
        if (zoomText != null && (!double.TryParse(zoomText, NumberStyles.Float, CultureInfo.InvariantCulture, out zoom)
                                 || !double.IsFinite(zoom) || zoom <= 0))
        {
            error = "bad zoom";
            return false;
        }
        var inv = CultureInfo.InvariantCulture;
        int? iterations = int.TryParse(iterText, NumberStyles.Integer, inv, out int it) ? it : null;
        double? density = double.TryParse(densityText, NumberStyles.Float, inv, out double d) && double.IsFinite(d) && d > 0 ? d : null;
        ColorMapping? mapping = Enum.TryParse<ColorMapping>(mappingText, true, out var mp) && Enum.IsDefined(mp) ? mp : null;
        double? snapZoom = double.TryParse(snapText, NumberStyles.Float, inv, out double sz) && double.IsFinite(sz) && sz > 0 ? sz : null;

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

        location = new Location(re, im, Math.Max(zoom, 1e-3), iterations, density, mapping, snap, snapZoom);
        error = "";
        return true;
    }
}
