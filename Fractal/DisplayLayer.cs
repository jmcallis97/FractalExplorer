using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Fractal;

/// <summary>
/// A field plus its coloured bitmap, drawn transformed into whatever the current view is.
/// The form stacks three of these: last completed render, low-res preview, in-progress render.
/// Bitmaps and buffers are reused across renders to avoid large allocations.
/// </summary>
internal sealed class DisplayLayer : IDisposable
{
    private Bitmap? _bitmap;
    private int[]? _buffer;

    /// <summary>The field shown by this layer, or null when the layer is hidden.</summary>
    public Field? Field { get; set; }

    /// <summary>Upscale with bilinear filtering (soft) rather than nearest neighbour (blocky).</summary>
    public bool SmoothUpscale { get; init; }

    // Relief and outline shading depends only on the values, mapping, scale and effects, not on
    // palette, density or offset, so it's kept between recolours (colour cycling recolours
    // every frame).
    private float[]? _shade;
    private (Field Field, Viewport View, ColorMapping Mapping, ColourScale? Scale, Effects Effects)? _shadeKey;

    /// <param name="filling">The field is still being rendered (its values change): don't keep its shading.</param>
    public void Colorize(Palette palette, ColorMapping mapping, double density, double offset,
                         ColourScale? scale, Effects effects, int workers, bool showFilled, bool filling = false)
    {
        if (Field == null) return;
        var v = Field.View;
        int n = v.Width * v.Height;
        if (_buffer?.Length != n) _buffer = new int[n];
        if (_bitmap == null || _bitmap.Width != v.Width || _bitmap.Height != v.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppPArgb);
        }

        float[]? shade = null;
        if (effects.Shades)
        {
            var key = (Field, v, mapping, scale, effects);
            if (filling || _shade?.Length != n || _shadeKey != key)
            {
                if (_shade?.Length != n) _shade = new float[n];
                effects.ShadeMap(Field, mapping, scale, _shade, workers);
                _shadeKey = filling ? null : key;
            }
            shade = _shade;
        }

        Colorizer.Colorize(Field, palette, mapping, density, offset, _buffer, workers, showFilled, scale, effects.Mirror, shade);

        var data = _bitmap.LockBits(new Rectangle(0, 0, v.Width, v.Height), ImageLockMode.WriteOnly, _bitmap.PixelFormat);
        try
        {
            if (data.Stride == v.Width * 4)
                Marshal.Copy(_buffer, 0, data.Scan0, n);
            else
                for (int y = 0; y < v.Height; y++)
                    Marshal.Copy(_buffer, y * v.Width, data.Scan0 + y * data.Stride, v.Width);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    /// <summary>Draws this layer's image where its pixels belong in <paramref name="cur"/>.</summary>
    public void Draw(Graphics g, Viewport cur)
    {
        if (Field == null || _bitmap == null) return;
        var f = Field.View;

        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (f.SameGrid(cur))
        {
            // Pure pan/resize: exact integer offset, no scaling.
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            int x = (int)(f.OriginX - cur.OriginX);
            int y = (int)(f.OriginY - cur.OriginY);
            g.DrawImage(_bitmap, new Rectangle(x, y, f.Width, f.Height));
        }
        else
        {
            // On-screen size of one layer pixel. Past these limits the layer is useless (a single
            // pixel filling the screen, or the whole image shrunk to a dot), and GDI+ coordinates
            // would overflow - which happens when many wheel notches arrive before a render lands.
            double ratio = f.Scale / cur.Scale;
            if (!(ratio > 1e-4 && ratio < 1e5)) return;

            // Screen position of the layer's pixel (0,0). Anchor offsets come from the
            // full-precision anchors when deep (plain doubles of two deep anchors can be identical
            // even though they are many pixels apart).
            double x0 = (f.AnchorOffsetX(cur) + f.OriginX * f.Scale) / cur.Scale - cur.OriginX;
            double y0 = (f.OriginY * f.Scale - f.AnchorOffsetY(cur)) / cur.Scale - cur.OriginY;
            if (!double.IsFinite(x0) || !double.IsFinite(y0)) return;

            // Only draw the part that's on screen, so coordinates stay small however far zoomed.
            int sx0 = (int)Math.Max(0, Math.Floor(-x0 / ratio));
            int sy0 = (int)Math.Max(0, Math.Floor(-y0 / ratio));
            int sx1 = (int)Math.Min(f.Width, Math.Ceiling((cur.Width - x0) / ratio));
            int sy1 = (int)Math.Min(f.Height, Math.Ceiling((cur.Height - y0) / ratio));
            if (sx1 <= sx0 || sy1 <= sy0) return;

            var src = new Rectangle(sx0, sy0, sx1 - sx0, sy1 - sy0);
            var dest = new RectangleF((float)(x0 + sx0 * ratio), (float)(y0 + sy0 * ratio),
                                      (float)(src.Width * ratio), (float)(src.Height * ratio));

            if (ratio > 1 && SmoothUpscale)
            {
                // TileFlipXY stops bilinear filtering from fading the image edges to transparent.
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_bitmap, Rectangle.Round(dest), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            else
            {
                g.InterpolationMode = ratio > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                g.DrawImage(_bitmap, dest, src, GraphicsUnit.Pixel);
            }
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    /// <summary>
    /// A copy of the coloured image shrunk to <paramref name="width"/> pixels wide (never
    /// enlarged), each pixel the average of the pixels it covers. Null if nothing is shown.
    /// </summary>
    public Bitmap? CreateThumbnail(int width)
    {
        if (Field == null || _buffer == null) return null;
        int sw = Field.View.Width, sh = Field.View.Height;
        if (_buffer.Length != sw * sh || sw <= 0 || sh <= 0) return null;
        int tw = Math.Min(width, sw);
        int th = Math.Max(1, (int)Math.Round((double)sh * tw / sw));

        var pixels = new int[tw * th];
        for (int ty = 0; ty < th; ty++)
        {
            int y0 = (int)((long)ty * sh / th), y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * sh / th));
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = (int)((long)tx * sw / tw), x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * sw / tw));
                long r = 0, g = 0, b = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * sw;
                    for (int x = x0; x < x1; x++)
                    {
                        int c = _buffer[row + x];
                        r += (c >> 16) & 0xFF;
                        g += (c >> 8) & 0xFF;
                        b += c & 0xFF;
                    }
                }
                long n = (long)(y1 - y0) * (x1 - x0);
                pixels[ty * tw + tx] = unchecked((int)0xFF000000) | (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n);
            }
        }

        var bmp = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < th; y++)
                Marshal.Copy(pixels, y * tw, data.Scan0 + y * data.Stride, tw);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    public void Dispose() => _bitmap?.Dispose();
}
