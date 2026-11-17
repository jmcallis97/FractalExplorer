using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Fractal;

/// <summary>One way of colouring a view: everything the colour explorer varies.</summary>
internal sealed record ColourChoice(Palette Palette, ColorMapping Mapping, double Density, double Offset, Effects Effects)
{
    public string Label =>
        $"{Palette.Name} · {Mapping} · {Density:G3}x" + (Offset > 0.001 ? $" · offset {Offset:0.##}" : "") +
        (Effects.Any ? $" · {Effects}" : "");
}

/// <summary>
/// The colour explorer (V): the view on screen in a grid of different colourings around the
/// current one (other mappings fitted to the view, more and less density, a turned palette,
/// effects on or off, other palettes and new random ones). Click one to use it; right-click
/// one for more like it. Only recolours the finished render, so it is instant.
/// </summary>
internal sealed class ColourExplorer : Form
{
    private const int Columns = 4, Rows = 4, Gap = 10, LabelHeight = 22, HeaderHeight = 30;

    public ColourChoice? Chosen { get; private set; }

    private readonly Field _field;           // the view, sampled down to about twice the tile size
    private readonly ColourScale? _scale;
    private readonly IReadOnlyList<Palette> _palettes;
    private readonly Func<Palette> _makeRandom;
    private readonly int _workers;
    private readonly int _tileW, _tileH;
    private readonly Random _rng = new();
    private readonly Font _font = new("Segoe UI", 9f);

    private ColourChoice _base;
    private readonly List<(ColourChoice Choice, Bitmap Image)> _tiles = [];
    private int _hover = -1;

    public ColourExplorer(Field field, ColourScale? scale, ColourChoice current, IReadOnlyList<Palette> palettes,
                          Func<Palette> makeRandom, Rectangle ownerBounds, int workers)
    {
        _scale = scale;
        _palettes = palettes;
        _makeRandom = makeRandom;
        _workers = workers;
        _base = current;

        Text = "Colour explorer";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(24, 24, 28);
        DoubleBuffered = true;
        KeyPreview = true;

        // Tiles as big as fit in 90% of the main window, in the view's shape.
        double aspect = (double)field.View.Height / field.View.Width;
        int availW = (int)(ownerBounds.Width * 0.9), availH = (int)(ownerBounds.Height * 0.9) - 40;
        _tileW = Math.Max(120, Math.Min((availW - (Columns + 1) * Gap) / Columns,
                                       (int)(((availH - HeaderHeight - (Rows + 1) * Gap) / (double)Rows - LabelHeight) / aspect)));
        _tileH = Math.Max(1, (int)Math.Round(_tileW * aspect));
        ClientSize = new Size(Columns * _tileW + (Columns + 1) * Gap,
                              HeaderHeight + Rows * (_tileH + LabelHeight) + (Rows + 1) * Gap);
        Location = new Point(ownerBounds.X + (ownerBounds.Width - Width) / 2, ownerBounds.Y + (ownerBounds.Height - Height) / 2);

        _field = Sample(field, Math.Min(field.View.Width, 2 * _tileW));
        Generate();
    }

    /// <summary>Nearest-pixel copy of <paramref name="f"/>, <paramref name="width"/> wide.</summary>
    private static Field Sample(Field f, int width)
    {
        int fw = f.View.Width, fh = f.View.Height;
        int w = Math.Max(1, width), h = Math.Max(1, (int)Math.Round((double)fh * w / fw));
        var values = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = (int)((long)y * fh / h) * fw;
            for (int x = 0; x < w; x++) values[y * w + x] = f.Values[row + (int)((long)x * fw / w)];
        }
        return new Field(f.View with { Width = w, Height = h }, values);
    }

    /// <summary>Colourings around <see cref="_base"/>, starting with it.</summary>
    private void Generate()
    {
        var b = _base;
        double Fit(ColorMapping m) => _scale != null && Colorizer.FitDensity(m, _scale) is double d ? d : b.Density;

        var list = new List<ColourChoice> { b };
        foreach (var m in Enum.GetValues<ColorMapping>())
            if (m != b.Mapping) list.Add(b with { Mapping = m, Density = Fit(m) });
        list.Add(b with { Density = b.Density * 0.5 });
        list.Add(b with { Density = b.Density * 2 });
        list.Add(b with { Offset = (b.Offset + 0.5) % 1 });
        list.Add(b with { Effects = b.Effects with { Relief = !b.Effects.Relief } });
        list.Add(b with { Effects = b.Effects with { Outline = !b.Effects.Outline } });
        list.Add(b with { Effects = b.Effects with { Mirror = !b.Effects.Mirror } });
        for (int i = 0; i < 2; i++) list.Add(b with { Palette = _makeRandom() });
        var others = _palettes.Where(p => p != b.Palette).OrderBy(_ => _rng.Next()).ToList();
        for (int i = 0; list.Count < Columns * Rows && i < others.Count; i++) list.Add(b with { Palette = others[i] });
        while (list.Count < Columns * Rows) list.Add(b with { Palette = _makeRandom() });

        foreach (var t in _tiles) t.Image.Dispose();
        _tiles.Clear();
        foreach (var c in list.Take(Columns * Rows)) _tiles.Add((c, Render(c)));
        Invalidate();
    }

    private Bitmap Render(ColourChoice c)
    {
        int w = _field.View.Width, h = _field.View.Height;
        float[]? shade = null;
        if (c.Effects.Shades)
        {
            shade = new float[w * h];
            c.Effects.ShadeMap(_field, c.Mapping, _scale, shade, _workers);
        }
        var px = new int[w * h];
        Colorizer.Colorize(_field, c.Palette, c.Mapping, c.Density, c.Offset, px, _workers, false, _scale, c.Effects.Mirror, shade);

        var full = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = full.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, full.PixelFormat);
        try { Marshal.Copy(px, 0, data.Scan0, px.Length); }
        finally { full.UnlockBits(data); }
        if (w == _tileW) return full;

        // Shrink with averaging (high-quality bicubic over a 2x source does it well).
        var tile = new Bitmap(_tileW, _tileH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(tile))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, new Rectangle(0, 0, _tileW, _tileH));
        }
        full.Dispose();
        return tile;
    }

    private Rectangle TileRect(int i) => new(
        Gap + i % Columns * (_tileW + Gap),
        HeaderHeight + Gap + i / Columns * (_tileH + LabelHeight + Gap),
        _tileW, _tileH);

    private int HitTile(Point p)
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            var r = TileRect(i);
            r.Height += LabelHeight;
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TextRenderer.DrawText(g, "Click one to use it   ·   Right-click: more like it   ·   Space: shuffle   ·   Esc: close",
                              _font, new Rectangle(Gap, 0, ClientSize.Width - 2 * Gap, HeaderHeight + Gap), Color.Silver,
                              TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        for (int i = 0; i < _tiles.Count; i++)
        {
            var r = TileRect(i);
            g.DrawImage(_tiles[i].Image, r);
            if (i == _hover)
            {
                using var pen = new Pen(Color.Gold, 3);
                g.DrawRectangle(pen, r.X - 2, r.Y - 2, r.Width + 3, r.Height + 3);
            }
            string label = (i == 0 ? "Now: " : "") + _tiles[i].Choice.Label;
            TextRenderer.DrawText(g, label, _font, new Rectangle(r.X, r.Bottom + 2, r.Width, LabelHeight - 2),
                                  i == _hover ? Color.White : Color.Silver, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTile(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int hit = HitTile(e.Location);
        if (hit < 0) return;
        if (e.Button == MouseButtons.Left)
        {
            Chosen = _tiles[hit].Choice;
            DialogResult = DialogResult.OK;
            Close();
        }
        else if (e.Button == MouseButtons.Right)
        {
            _base = _tiles[hit].Choice;
            Generate();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
            case Keys.V:
                Close();
                return true;
            case Keys.Space:
                Generate();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var t in _tiles) t.Image.Dispose();
        _font.Dispose();
        base.OnFormClosed(e);
    }
}
