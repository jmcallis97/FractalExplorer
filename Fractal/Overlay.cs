using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Fractal;

/// <summary>
/// Everything drawn over the fractal: progress bar, info panel, palette picker, help and toast
/// messages. Owns its fonts and the palette strips it draws.
/// </summary>
internal sealed class Overlay(ColourState colours) : IDisposable
{
    private readonly Font _infoFont = new("Consolas", 9f);
    private readonly Font _toastFont = new("Segoe UI", 14f, FontStyle.Bold);
    private readonly Font _labelFont = new("Segoe UI", 8.5f);
    private readonly Dictionary<Palette, Bitmap> _stripCache = new();
    private Bitmap? _infoStrip;
    private (Palette, double) _infoStripKey;

    private string? _toast;
    private double _toastUntil;

    public bool PickerOpen { get; set; }

    // ---- Toast ---------------------------------------------------------------------------------

    public void ShowToast(string text, double until)
    {
        _toast = text;
        _toastUntil = until;
    }

    public void ClearToast() => _toast = null;

    /// <summary>Removes the toast once its time is up. True if it was removed (repaint).</summary>
    public bool ExpireToast(double now)
    {
        if (_toast == null || now <= _toastUntil) return false;
        _toast = null;
        return true;
    }

    // ---- Picker layout ---------------------------------------------------------------------

    private const int TileW = 168, StripH = 22, LabelH = 18, Gap = 10, Pad = 14, HeaderH = 26;

    private static int PickerColumns(Size client) => Math.Max(1, (client.Width - 2 * Pad + Gap) / (TileW + Gap));

    public Rectangle PickerBounds(Size client)
    {
        int cols = PickerColumns(client);
        int rows = (colours.Palettes.Count + cols - 1) / cols;
        int height = Pad * 2 + HeaderH + rows * (StripH + LabelH + Gap) - Gap;
        return new Rectangle(0, client.Height - height, client.Width, height);
    }

    /// <summary>True if the picker is open and covers <paramref name="p"/>.</summary>
    public bool PickerContains(Point p, Size client) => PickerOpen && PickerBounds(client).Contains(p);

    private Rectangle TileRect(int i, Size client)
    {
        int top = PickerBounds(client).Top;
        int cols = PickerColumns(client);
        int col = i % cols, row = i / cols;
        return new Rectangle(Pad + col * (TileW + Gap), top + Pad + HeaderH + row * (StripH + LabelH + Gap),
                             TileW, StripH + LabelH);
    }

    /// <summary>Index of the palette tile at <paramref name="p"/>, or -1.</summary>
    public int HitTile(Point p, Size client)
    {
        if (!PickerOpen) return -1;
        for (int i = 0; i < colours.Palettes.Count; i++)
            if (TileRect(i, client).Contains(p)) return i;
        return -1;
    }

    // ---- Drawing -------------------------------------------------------------------------------

    public void Draw(Graphics g, Size client, double? progress, string? info, bool help)
    {
        if (progress is double fraction) DrawProgress(g, client, fraction);
        if (info != null) DrawInfo(g, info);
        if (PickerOpen) DrawPicker(g, client);
        if (help) DrawHelp(g, client);
        if (_toast != null) DrawToast(g, client);
    }

    private static void DrawProgress(Graphics g, Size client, double fraction)
    {
        using var b = new SolidBrush(Color.FromArgb(220, 255, 200, 60));
        g.FillRectangle(b, 0, 0, (int)(client.Width * fraction), 3);
    }

    private void DrawInfo(Graphics g, string text)
    {
        var size = g.MeasureString(text, _infoFont);
        int stripY = 12 + (int)size.Height + 4;
        using var bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        g.FillRectangle(bg, 8, 8, Math.Max(size.Width, 208) + 8, size.Height + 26);
        g.DrawString(text, _infoFont, Brushes.White, 12, 12);

        // Current palette, rotated by the cycle offset, so you can see what's happening.
        var key = (colours.Active, colours.Offset);
        if (_infoStrip == null || _infoStripKey != key)
        {
            _infoStrip?.Dispose();
            _infoStrip = CreateStrip(colours.Active, 200, colours.Offset);
            _infoStripKey = key;
        }
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_infoStrip, new Rectangle(12, stripY, 200, 10));
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private void DrawPicker(Graphics g, Size client)
    {
        var bounds = PickerBounds(client);
        using var bg = new SolidBrush(Color.FromArgb(200, 10, 10, 14));
        g.FillRectangle(bg, bounds);
        g.DrawString("Palettes — hover to preview, click to select.   G: random   S: save   Esc/P: close",
                     _labelFont, Brushes.Gainsboro, Pad, bounds.Top + Pad);

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        var palettes = colours.Palettes;
        for (int i = 0; i < palettes.Count; i++)
        {
            var r = TileRect(i, client);
            var stripRect = new Rectangle(r.X, r.Y, TileW, StripH);
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(StripFor(palettes[i]), stripRect);
            g.PixelOffsetMode = PixelOffsetMode.Default;

            if (i == colours.PaletteIndex)
                using (var pen = new Pen(Color.White, 2)) g.DrawRectangle(pen, Rectangle.Inflate(stripRect, 2, 2));
            else if (i == colours.HoverIndex)
                using (var pen = new Pen(Color.Gold, 2)) g.DrawRectangle(pen, Rectangle.Inflate(stripRect, 2, 2));

            string label = i < 9 ? $"{i + 1}  {palettes[i].Name}" : palettes[i].Name;
            g.DrawString(label, _labelFont, i == colours.PaletteIndex ? Brushes.White : Brushes.Silver, r.X, r.Y + StripH + 2);
        }
    }

    private const string HelpText =
        "NAVIGATION\n" +
        "  Wheel               Zoom at cursor\n" +
        "  Left-drag           Pan (only new edges are computed)\n" +
        "  Arrows / Shift      Pan a step / a big step\n" +
        "  Middle-click        Center on point\n" +
        "  + / -               1000 more / fewer iterations\n" +
        "  A                   Auto iterations on/off (raises the limit when detail is cut off)\n" +
        "  R / Home            Reset view\n" +
        "  X                   Deep zoom: auto / always / off\n" +
        "  Ctrl+C / Ctrl+V     Copy / go to location (full precision, with mapping and density;\n" +
        "                      a pasted \"Snap\" line zooms in to the spiral centre or minibrot there)\n" +
        "  N / Shift+N         Snap: find the spiral centre or minibrot here (ask / zoom straight in)\n" +
        "  Ctrl+click          Snap at the cursor: works on any structure (Ctrl+Shift+click zooms straight in)\n" +
        "  Esc                 Stop a snap zoom\n" +
        "  L                   Places you've been (click one to go back; kept between sessions)\n" +
        "  Ctrl+S              Save this place now (otherwise saved after 5 s here, when you move on)\n" +
        "  Backspace           Back to the last place\n" +
        "  Delete              Forget this place (removed from the list, not saved when you leave)\n" +
        "\n" +
        "COLOUR (instant — no recompute)\n" +
        "  P                   Palette picker (hover previews)\n" +
        "  PgUp / PgDn / 1-9   Previous / next / pick palette\n" +
        "  G                   Generate a random palette\n" +
        "  S                   Save palette as .map (Documents\\Fractal\\Palettes)\n" +
        "  C / Shift+C         Toggle cycling / reverse direction\n" +
        "  , / .               Cycle slower / faster\n" +
        "  Shift+Wheel         Rotate palette by hand\n" +
        "  Ctrl+Wheel or [ ]   Colour density\n" +
        "  M                   Mapping: Linear / Sqrt / Log\n" +
        "\n" +
        "ENGINE (re-renders)\n" +
        "  V                   SIMD on/off (compare render times)\n" +
        "  U                   GPU on/off for deep zoom (DirectX 12)\n" +
        "  B / D               Interior fill on/off / show filled areas\n" +
        "  Q                   Progressive rendering (preview + live fill-in) on/off\n" +
        "\n" +
        "WINDOW\n" +
        "  F11 / Alt+Enter / double-click   Fullscreen (Esc exits)\n" +
        "  I                   Toggle info      H / F1   Toggle help";

    private void DrawHelp(Graphics g, Size client)
    {
        var size = g.MeasureString(HelpText, _infoFont);
        float x = (client.Width - size.Width) / 2, y = (client.Height - size.Height) / 2;
        using var bg = new SolidBrush(Color.FromArgb(215, 0, 0, 0));
        g.FillRectangle(bg, x - 16, y - 16, size.Width + 32, size.Height + 32);
        g.DrawString(HelpText, _infoFont, Brushes.White, x, y);
    }

    private void DrawToast(Graphics g, Size client)
    {
        var size = g.MeasureString(_toast, _toastFont);
        float x = (client.Width - size.Width) / 2, y = 24;
        using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        g.FillRectangle(bg, x - 12, y - 6, size.Width + 24, size.Height + 12);
        g.DrawString(_toast, _toastFont, Brushes.White, x, y);
    }

    private Bitmap StripFor(Palette p)
    {
        if (!_stripCache.TryGetValue(p, out var bmp))
            _stripCache[p] = bmp = CreateStrip(p, TileW);
        return bmp;
    }

    /// <summary>A width×1 strip of the palette, rotated by <paramref name="offset"/> cycles.</summary>
    private static Bitmap CreateStrip(Palette p, int width, double offset = 0)
    {
        var bmp = new Bitmap(width, 1, PixelFormat.Format32bppArgb);
        for (int x = 0; x < width; x++)
            bmp.SetPixel(x, 0, Color.FromArgb(p.ColorAt((double)x / width + offset)));
        return bmp;
    }

    public void Dispose()
    {
        foreach (var b in _stripCache.Values) b.Dispose();
        _infoStrip?.Dispose();
        _infoFont.Dispose();
        _toastFont.Dispose();
        _labelFont.Dispose();
    }
}
