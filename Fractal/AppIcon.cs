namespace Fractal;

/// <summary>The app's icon (all sizes), for the windows' title bars and the taskbar.</summary>
internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon? Get()
    {
        if (_icon != null) return _icon;
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Fractal.ico");
        return _icon = stream != null ? new Icon(stream) : null;
    }
}
