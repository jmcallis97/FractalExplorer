namespace Fractal;

/// <summary>
/// How iteration counts become colours: the palettes (built-in, saved, generated), which one is
/// in use (or previewed under the mouse in the picker), mapping, density and the cycling offset.
/// Changing any of it only recolours; nothing is re-rendered.
/// </summary>
internal sealed class ColourState
{
    private static readonly string PaletteFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fractal", "Palettes");

    private readonly List<Palette> _palettes = Palette.BuiltIns();
    private readonly Random _rng = new();
    private int _randomCount;

    public IReadOnlyList<Palette> Palettes => _palettes;
    public int PaletteIndex { get; private set; }

    /// <summary>Palette under the mouse in the picker (shown instead), or -1.</summary>
    public int HoverIndex { get; set; } = -1;

    public Palette Active => _palettes[HoverIndex >= 0 ? HoverIndex : PaletteIndex];

    public ColorMapping Mapping { get; set; } = ColorMapping.Linear;
    public double Density { get; private set; } = 1.0;

    /// <summary>Palette rotation, in cycles (0 to 1).</summary>
    public double Offset { get; private set; }

    public bool Cycling { get; set; }
    public double CycleSpeed { get; private set; } = 0.25; // cycles per second
    public int CycleDirection { get; private set; } = 1;

    public void Select(int index)
    {
        PaletteIndex = index;
        HoverIndex = -1;
    }

    /// <summary>Adds a new random palette and selects it.</summary>
    public Palette AddRandom()
    {
        _palettes.Add(Palette.Random(_rng, ++_randomCount));
        Select(_palettes.Count - 1);
        return _palettes[^1];
    }

    public void SetDensity(double density) => Density = Math.Clamp(density, 0.01, 100);

    public void Rotate(double cycles)
    {
        Offset += cycles;
        Offset -= Math.Floor(Offset);
    }

    /// <summary>Advances cycling by <paramref name="dt"/> seconds. False when not cycling.</summary>
    public bool Advance(double dt)
    {
        if (!Cycling) return false;
        Rotate(CycleDirection * CycleSpeed * dt);
        return true;
    }

    public void ReverseCycling()
    {
        CycleDirection = -CycleDirection;
        Cycling = true;
    }

    public void ChangeCycleSpeed(double factor) => CycleSpeed = Math.Clamp(CycleSpeed * factor, 0.01, 10);

    public void NextMapping() =>
        Mapping = (ColorMapping)(((int)Mapping + 1) % Enum.GetValues<ColorMapping>().Length);

    /// <summary>Adds the .map files in Documents\Fractal\Palettes. Ones that can't be read are skipped.</summary>
    public void LoadUserPalettes()
    {
        try
        {
            if (!Directory.Exists(PaletteFolder)) return;
            foreach (var file in Directory.EnumerateFiles(PaletteFolder, "*.map").Order())
            {
                try
                {
                    var p = Palette.FromMapFile(file);
                    if (p != null) _palettes.Add(p);
                }
                catch (IOException) { /* skip unreadable file */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Palette folder not accessible; built-ins still work.
        }
    }

    /// <summary>Saves the selected palette as a new .map file and returns its path. Throws IOException or UnauthorizedAccessException.</summary>
    public string SaveSelected()
    {
        var p = _palettes[PaletteIndex];
        Directory.CreateDirectory(PaletteFolder);
        string baseName = string.Concat(p.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))
            .Replace('#', '_').Replace(' ', '_');
        string path = Path.Combine(PaletteFolder, baseName + ".map");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(PaletteFolder, $"{baseName}_{i}.map");
        p.SaveMapFile(path);
        return path;
    }
}
