using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fractal.Deep;

namespace Fractal;

/// <summary>
/// A place you've been: the view and colouring as they were when you left it, plus a
/// thumbnail of what it looked like (a PNG next to the list, named after <see cref="Id"/>).
/// </summary>
internal sealed class Place
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Re { get; set; } = "";
    public string Im { get; set; } = "";
    public double Zoom { get; set; }
    public int Iterations { get; set; }
    public string Palette { get; set; } = "";

    /// <summary>The palette's colours (.map text), for one that was generated and isn't on disk.</summary>
    public string? PaletteMap { get; set; }

    public ColorMapping Mapping { get; set; }
    public double Density { get; set; } = 1;
    public double Offset { get; set; }
    public DateTime Visited { get; set; }

    /// <summary>Pinned places are never dropped to make room.</summary>
    public bool Pinned { get; set; }

    public Location ToLocation() => new(Re, Im, Zoom, Iterations, Density, Mapping);
}

/// <summary>
/// The places you've been, newest first, kept in %LocalAppData%\Fractal\Places (places.json
/// plus one thumbnail PNG per place). Every change is saved straight away. Coming back to a
/// place that's already listed (near the same centre at about the same zoom) moves it to the
/// top instead of adding it twice. Beyond <see cref="Capacity"/>, the oldest unpinned place goes.
/// </summary>
internal sealed class PlaceStore
{
    public const int Capacity = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _folder;
    private readonly List<Place> _places;

    private PlaceStore(string folder, List<Place> places)
    {
        _folder = folder;
        _places = places;
    }

    public IReadOnlyList<Place> Places => _places;

    /// <summary>%LocalAppData%\Fractal\Places, or the FRACTAL_PLACES folder if that environment variable is set.</summary>
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("FRACTAL_PLACES") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fractal", "Places");

    private string ListPath => Path.Combine(_folder, "places.json");

    public string ThumbnailPath(Place p) => Path.Combine(_folder, p.Id + ".png");

    /// <summary>Reads the saved list. A missing or unreadable list gives an empty one (<paramref name="error"/> says why, if it was unreadable).</summary>
    public static PlaceStore Load(string folder, out string? error)
    {
        error = null;
        var places = new List<Place>();
        try
        {
            string path = Path.Combine(folder, "places.json");
            if (File.Exists(path))
                places = JsonSerializer.Deserialize<List<Place>>(File.ReadAllText(path), Json) ?? [];
            // Drop anything that wouldn't parse as a location (a hand-edited or damaged file).
            places.RemoveAll(p => !Location.TryParse($"Re: {p.Re}\nIm: {p.Im}\nZoom: 1", out _, out _)
                                  || !double.IsFinite(p.Zoom) || p.Zoom <= 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            places = [];
        }
        return new PlaceStore(folder, places);
    }

    /// <summary>
    /// Adds <paramref name="place"/> at the top with its thumbnail, replacing a listed place
    /// near it (keeping that one's pin). Throws IOException or UnauthorizedAccessException if
    /// it can't be saved (the list in memory is updated anyway).
    /// </summary>
    public void Add(Place place, Bitmap? thumbnail)
    {
        int old = _places.FindIndex(p => IsNear(p, place.Re, place.Im, place.Zoom));
        if (old >= 0)
        {
            place.Pinned = _places[old].Pinned;
            DeleteThumbnail(_places[old]);
            _places.RemoveAt(old);
        }
        _places.Insert(0, place);
        while (_places.Count > Capacity)
        {
            int i = _places.FindLastIndex(p => !p.Pinned);
            if (i < 0) break; // all pinned: the user's choice to keep them
            DeleteThumbnail(_places[i]);
            _places.RemoveAt(i);
        }

        Directory.CreateDirectory(_folder);
        thumbnail?.Save(ThumbnailPath(place), ImageFormat.Png);
        Save();
    }

    public void Remove(Place place)
    {
        _places.Remove(place);
        DeleteThumbnail(place);
        Save();
    }

    /// <summary>Puts a removed place back at <paramref name="index"/> with its thumbnail file's bytes (undo).</summary>
    public void Insert(int index, Place place, byte[]? png)
    {
        _places.Insert(Math.Clamp(index, 0, _places.Count), place);
        Directory.CreateDirectory(_folder);
        if (png != null) File.WriteAllBytes(ThumbnailPath(place), png);
        Save();
    }

    /// <summary>Removes every listed place near the given view. False if there were none.</summary>
    public bool RemoveNear(string re, string im, double zoom)
    {
        var near = _places.Where(p => IsNear(p, re, im, zoom)).ToList();
        foreach (var p in near)
        {
            _places.Remove(p);
            DeleteThumbnail(p);
        }
        if (near.Count > 0) Save();
        return near.Count > 0;
    }

    public void SetPinned(Place place, bool pinned)
    {
        place.Pinned = pinned;
        Save();
    }

    /// <summary>
    /// True if <paramref name="p"/> is at about the same zoom (within 2×) and its centre is
    /// within half a view width of (<paramref name="re"/>, <paramref name="im"/>).
    /// </summary>
    public static bool IsNear(Place p, string re, string im, double zoom)
    {
        double ratio = p.Zoom / zoom;
        if (!(ratio > 0.5 && ratio < 2)) return false;
        double viewWidth = Mandelbrot.ViewWidthAtZoom1 / Math.Max(p.Zoom, zoom);
        int bits = BigFixed.BitsFor(viewWidth / 4096);
        try
        {
            double dx = (BigFixed.Parse(p.Re, bits) - BigFixed.Parse(re, bits)).ToDouble();
            double dy = (BigFixed.Parse(p.Im, bits) - BigFixed.Parse(im, bits)).ToDouble();
            return Math.Abs(dx) < viewWidth / 2 && Math.Abs(dy) < viewWidth / 2;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_folder);
        string temp = ListPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_places, Json));
        File.Move(temp, ListPath, overwrite: true);
    }

    private void DeleteThumbnail(Place p)
    {
        try { File.Delete(ThumbnailPath(p)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left behind; harmless */ }
    }
}
