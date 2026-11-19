using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Fractal;

/// <summary>
/// A side view of one row of the image (Alt+click): the cross-section through the escape-time
/// landscape along that line. Across: the row, left to right, as on screen. Up: each point's
/// height, its escape time as coloured (the mapped iteration count, so a band of colour here is
/// the same band on screen). A column is filled from the bottom up to its point's height, each
/// level in the colour of that level: strata. Above it, black - that point had escaped by then.
/// Points inside the set never escape (no height): grey columns off the top.
/// </summary>
internal sealed class ProfileWindow : Form
{
    private const int Footer = 22; // the text line under the picture
    private const int InsideGrey = unchecked((int)0xFF3A3A3A);

    private readonly Viewport _view;
    private readonly int _row;
    private readonly float[] _values; // the row's smooth iteration counts (negative = inside)
    private Palette _palette;
    private ColorMapping _mapping;
    private double _density, _offset;
    private ColourScale? _scale;
    private bool _mirror;

    private double[] _height = [];   // per sample: mapped height in palette cycles (+inf inside)
    private double _lo, _hi;          // the escaped heights' range
    private float _sLo, _sHi;         // ...in iterations
    private int _inside;
    private Bitmap? _bitmap;
    private string _hover = "";

    /// <summary>The global grid row shown, for the marker on the main view.</summary>
    public long GlobalRow => _view.OriginY + _row;
    public Viewport View => _view;

    public ProfileWindow(Field field, int row, Palette palette, ColorMapping mapping, double density, double offset,
                         ColourScale? scale, bool mirror, Rectangle ownerArea)
    {
        _view = field.View;
        _row = row;
        _values = new float[_view.Width];
        Array.Copy(field.Values, row * _view.Width, _values, 0, _view.Width);
        _palette = palette;
        _mapping = mapping;
        _density = density;
        _offset = offset;
        _scale = scale;
        _mirror = mirror;

        Text = "Profile";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.Black;
        ForeColor = Color.Gainsboro;
        KeyPreview = true;
        MinimumSize = new Size(200, 120);
        int w = Math.Min(_view.Width, ownerArea.Width - 40);
        ClientSize = new Size(w, 320 + Footer);
        Location = new Point(ownerArea.Left + (ownerArea.Width - Width) / 2, ownerArea.Bottom - Height - 20);
        Measure();
    }

    /// <summary>The main view's colours changed: follow them.</summary>
    public void SetColours(Palette palette, ColorMapping mapping, double density, double offset, ColourScale? scale, bool mirror)
    {
        if (palette == _palette && mapping == _mapping && density == _density && offset == _offset && scale == _scale && mirror == _mirror) return;
        (_palette, _mapping, _density, _offset, _scale, _mirror) = (palette, mapping, density, offset, scale, mirror);
        Measure();
        Invalidate();
    }

    private void Measure()
    {
        _height = new double[_values.Length];
        _lo = double.MaxValue; _hi = double.MinValue;
        _sLo = float.MaxValue; _sHi = float.MinValue;
        _inside = 0;
        for (int i = 0; i < _values.Length; i++)
        {
            float s = _values[i];
            if (float.IsNaN(s) || s < 0) { _height[i] = double.PositiveInfinity; _inside++; continue; }
            double h = Colorizer.Map(_mapping, s, _scale) * _density;
            _height[i] = h;
            _lo = Math.Min(_lo, h); _hi = Math.Max(_hi, h);
            _sLo = Math.Min(_sLo, s); _sHi = Math.Max(_sHi, s);
        }
        if (_lo > _hi) { _lo = 0; _hi = 1; _sLo = _sHi = 0; } // nothing escaped
        if (_hi - _lo < 1e-9) _hi = _lo + 1e-3;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private Bitmap Draw(int w, int h)
    {
        var px = new int[w * h];
        // A little headroom, so the highest escaped peak doesn't touch the top (inside columns do).
        double top = _hi + (_hi - _lo) * 0.06;
        int n = _values.Length;
        var lut = _palette.Lut;
        var levelColour = new int[h];
        for (int y = 0; y < h; y++)
        {
            double level = _lo + (h - 1 - y) * (top - _lo) / Math.Max(1, h - 1);
            double t = level + _offset;
            t -= Math.Floor(t);
            if (_mirror) t = t < 0.5 ? 2 * t : 2 - 2 * t;
            levelColour[y] = lut[Math.Min(lut.Length - 1, (int)(t * lut.Length))];
        }
        for (int x = 0; x < w; x++)
        {
            // The samples under this column: take the highest, so narrow peaks aren't lost.
            int a = (int)((long)x * n / w), b = Math.Max(a + 1, (int)((long)(x + 1) * n / w));
            double hx = double.NegativeInfinity;
            for (int i = a; i < b && i < n; i++) hx = Math.Max(hx, _height[i]);
            if (double.IsPositiveInfinity(hx))
            {
                // Inside the set: never escapes, so no height - a grey column off the top.
                for (int y = 0; y < h; y++) px[y * w + x] = InsideGrey;
                continue;
            }
            // Filled from the bottom up to this height.
            int filledTo = (int)Math.Ceiling(h - 1 - (hx - _lo) / (top - _lo) * (h - 1));
            for (int y = Math.Max(0, filledTo); y < h; y++) px[y * w + x] = levelColour[y];
            for (int y = 0; y < Math.Min(filledTo, h); y++) px[y * w + x] = unchecked((int)0xFF000000);
        }
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, data.Scan0 + y * data.Stride, w);
        bmp.UnlockBits(data);
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = ClientSize.Width, h = ClientSize.Height - Footer;
        if (w <= 0 || h <= 0) return;
        if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = Draw(w, h);
        }
        g.DrawImageUnscaled(_bitmap, 0, 0);
        using var brush = new SolidBrush(ForeColor);
        string info = _hover.Length > 0 ? _hover
            : $"Height = iterations as coloured ({_mapping}): {_sLo:N0} at the bottom to {_sHi:N0}" +
              (_inside > 0 ? $"   {_inside:N0} points inside the set (grey: they never escape)" : "") + "   (Esc closes)";
        g.DrawString(info, Font, brush, 4, h + 4);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int w = ClientSize.Width;
        if (w <= 0) return;
        int i = Math.Clamp((int)((long)e.X * _values.Length / w), 0, _values.Length - 1);
        float s = _values[i];
        string text = $"Column {i}: " + (s < 0 || float.IsNaN(s) ? "inside the set (never escapes)" : $"{s:N1} iterations to escape");
        if (text != _hover) { _hover = text; Invalidate(new Rectangle(0, ClientSize.Height - Footer, w, Footer)); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = "";
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bitmap?.Dispose();
        base.Dispose(disposing);
    }
}
