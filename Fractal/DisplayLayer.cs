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

    public void Colorize(Palette palette, ColorMapping mapping, double density, double offset,
                         int workers, bool showFilled)
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

        Colorizer.Colorize(Field, palette, mapping, density, offset, _buffer, workers, showFilled);

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
            double ratio = f.Scale / cur.Scale;
            double x0 = (f.AnchorX + f.OriginX * f.Scale - cur.AnchorX) / cur.Scale - cur.OriginX;
            double y0 = (cur.AnchorY - f.AnchorY + f.OriginY * f.Scale) / cur.Scale - cur.OriginY;
            var dest = new RectangleF((float)x0, (float)y0, (float)(f.Width * ratio), (float)(f.Height * ratio));

            if (ratio > 1 && SmoothUpscale)
            {
                // TileFlipXY stops bilinear filtering from fading the image edges to transparent.
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_bitmap, Rectangle.Round(dest), 0, 0, f.Width, f.Height, GraphicsUnit.Pixel, attrs);
            }
            else
            {
                g.InterpolationMode = ratio > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                g.DrawImage(_bitmap, dest);
            }
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    public void Dispose() => _bitmap?.Dispose();
}
