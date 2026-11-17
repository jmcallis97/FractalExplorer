using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Fractal;

/// <summary>
/// The places you've been, as a gallery of thumbnails, newest first: as many columns as fit,
/// scrolling down. Click a place to go back there (<see cref="Chosen"/>, DialogResult.OK).
/// Hovering shows a × that deletes it (Ctrl+Z undoes); right-click for pin, copy and delete;
/// Ctrl+wheel or +/- resizes the thumbnails.
/// </summary>
internal sealed class PlacesDialog : Form
{
    private const int MinTileW = 240, MaxTileW = 1100;
    private static int s_tileWidth = 520; // remembered for the session

    private readonly PlaceStore _store;
    private readonly Grid _grid;
    private readonly Label _header;
    private readonly Stack<(int Index, Place Place, byte[]? Png)> _deleted = new();

    public Place? Chosen { get; private set; }

    /// <param name="ownerBounds">The main window: the gallery covers most of it.</param>
    public PlacesDialog(PlaceStore store, Rectangle ownerBounds)
    {
        _store = store;
        Text = "Places you've been";
        BackColor = Color.FromArgb(14, 14, 18);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = false;
        MinimizeBox = false;
        KeyPreview = true;
        MinimumSize = new Size(500, 360);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.FromRectangle(ownerBounds).WorkingArea;
        var bounds = Rectangle.Inflate(ownerBounds, -ownerBounds.Width / 20, -ownerBounds.Height / 20);
        bounds.Intersect(area);
        Bounds = bounds.Width >= MinimumSize.Width && bounds.Height >= MinimumSize.Height
            ? bounds
            : new Rectangle(area.X + area.Width / 10, area.Y + area.Height / 10, area.Width * 8 / 10, area.Height * 8 / 10);

        _header = new Label { Dock = DockStyle.Top, Height = 32, Padding = new Padding(14, 9, 0, 0), ForeColor = Color.Silver };
        _grid = new Grid(this) { Dock = DockStyle.Fill };
        Controls.Add(_grid);
        Controls.Add(_header);
        UpdateHeader();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _grid.Focus();
    }

    private void UpdateHeader() =>
        _header.Text = _store.Places.Count == 0
            ? "No places yet. Stay at a view for a few seconds, or change its colours, and it's added when you move on."
            : $"{_store.Places.Count} places.   Click one to go back.   × or Delete removes it (Ctrl+Z undoes).   " +
              "Right-click: pin, copy location.   Ctrl+wheel or +/-: thumbnail size.   Esc closes.";

    private void Choose(Place place)
    {
        Chosen = place;
        DialogResult = DialogResult.OK;
    }

    private void Delete(Place place)
    {
        int index = _store.Places.ToList().IndexOf(place);
        if (index < 0) return;
        byte[]? png = null;
        try { png = File.ReadAllBytes(_store.ThumbnailPath(place)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!Try(() => _store.Remove(place))) return;
        _deleted.Push((index, place, png));
        Changed();
    }

    private void Undo()
    {
        if (_deleted.Count == 0) return;
        var (index, place, png) = _deleted.Pop();
        Try(() => _store.Insert(index, place, png));
        Changed();
    }

    private void Changed()
    {
        UpdateHeader();
        _grid.Refresh(reloadImages: false);
    }

    private bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ThreadStateException)
        {
            MessageBox.Show(this, ex.Message, "Places", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void ResizeTiles(double factor)
    {
        int w = Math.Clamp((int)Math.Round(s_tileWidth * factor), MinTileW, MaxTileW);
        if (w == s_tileWidth) return;
        s_tileWidth = w;
        _grid.Refresh(reloadImages: true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
            case Keys.L:
                Close();
                return true;
            case Keys.Delete:
            case Keys.Back:
                if (_grid.Hovered is { } h) Delete(h);
                return true;
            case Keys.Control | Keys.Z:
                Undo();
                return true;
            case Keys.Enter:
                if (_grid.Hovered is { } g) Choose(g);
                return true;
            case Keys.Oemplus:
            case Keys.Add:
            case Keys.Shift | Keys.Oemplus:
                ResizeTiles(1.2);
                return true;
            case Keys.OemMinus:
            case Keys.Subtract:
                ResizeTiles(1 / 1.2);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Font.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The thumbnails, drawn directly (only the visible ones; pictures load as they come into view).</summary>
    private sealed class Grid : Panel
    {
        private const int Gap = 16, TextH = 42, CloseSize = 30;

        private readonly PlacesDialog _owner;
        private readonly Dictionary<string, Image?> _images = new(); // by place id, scaled to the tile size
        private readonly Font _small = new("Segoe UI", 8.5f);
        private int _hover = -1;
        private bool _hoverClose;

        public Grid(PlacesDialog owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            AutoScroll = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.UserMouse, true);
            ResizeRedraw = true;
        }

        private IReadOnlyList<Place> Places => _owner._store.Places;
        private static int TileW => s_tileWidth;
        private static int ThumbH => TileW * 9 / 16;
        private static int TileH => ThumbH + TextH;

        public Place? Hovered => _hover >= 0 && _hover < Places.Count ? Places[_hover] : null;

        private int Columns => Math.Max(1, (Width - SystemInformation.VerticalScrollBarWidth - Gap) / (TileW + Gap));

        private int LeftMargin
        {
            get
            {
                int used = Columns * (TileW + Gap) - Gap;
                return Math.Max(Gap, (Width - SystemInformation.VerticalScrollBarWidth - used) / 2);
            }
        }

        /// <summary>Tile <paramref name="i"/> in content coordinates (before scrolling).</summary>
        private Rectangle TileRect(int i) =>
            new(LeftMargin + i % Columns * (TileW + Gap), Gap + i / Columns * (TileH + Gap), TileW, TileH);

        private static Rectangle CloseRect(Rectangle tile) =>
            new(tile.Right - CloseSize - 6, tile.Top + 6, CloseSize, CloseSize);

        public void Refresh(bool reloadImages)
        {
            if (reloadImages) ClearImages();
            UpdateExtent();
            UpdateHover(PointToClient(Cursor.Position));
            Invalidate();
        }

        private void UpdateExtent()
        {
            int rows = (Places.Count + Columns - 1) / Columns;
            AutoScrollMinSize = new Size(0, Gap + rows * (TileH + Gap));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateExtent();
        }

        private void UpdateHover(Point client)
        {
            var p = new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);
            int hit = -1;
            bool close = false;
            if (ClientRectangle.Contains(client))
            {
                for (int i = 0; i < Places.Count; i++)
                {
                    var r = TileRect(i);
                    if (!r.Contains(p)) continue;
                    hit = i;
                    close = CloseRect(r).Contains(p);
                    break;
                }
            }
            if (hit == _hover && close == _hoverClose) return;
            _hover = hit;
            _hoverClose = close;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateHover(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            UpdateHover(new Point(-1, -1));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                _owner.ResizeTiles(Math.Pow(1.1, e.Delta / 120.0));
                return;
            }
            base.OnMouseWheel(e);
            UpdateHover(e.Location);
        }

        private (int Tile, bool Close) _pressed = (-1, false);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            UpdateHover(e.Location);
            _pressed = (_hover, _hoverClose);
        }

        // A click is a press and release on the same tile (and the same part of it): sliding off cancels.
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            UpdateHover(e.Location);
            var pressed = _pressed;
            _pressed = (-1, false);
            if (pressed != (_hover, _hoverClose) || Hovered is not { } place) return;
            if (e.Button == MouseButtons.Left)
            {
                if (_hoverClose) _owner.Delete(place);
                else _owner.Choose(place);
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowMenu(place, e.Location);
            }
        }

        private void ShowMenu(Place place, Point at)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Go there", null, (_, _) => _owner.Choose(place));
            menu.Items.Add(place.Pinned ? "Unpin" : "Pin (never dropped from the list)", null, (_, _) =>
            {
                _owner.Try(() => _owner._store.SetPinned(place, !place.Pinned));
                Invalidate();
            });
            menu.Items.Add("Copy location", null, (_, _) => _owner.Try(() => Clipboard.SetText(place.ToLocation().ToText())));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Delete", null, (_, _) => _owner.Delete(place));
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose); // after the clicked item has run
            menu.Show(this, at);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            var visible = new Rectangle(-AutoScrollPosition.X, -AutoScrollPosition.Y, Width, Height);
            var inv = CultureInfo.InvariantCulture;

            for (int i = 0; i < Places.Count; i++)
            {
                var tile = TileRect(i);
                if (!tile.IntersectsWith(visible)) continue;
                var place = Places[i];
                var box = new Rectangle(tile.X, tile.Y, TileW, ThumbH);

                g.FillRectangle(Brushes.Black, box);
                if (ImageFor(place) is { } img)
                {
                    double s = Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height);
                    int w = (int)Math.Round(img.Width * s), h = (int)Math.Round(img.Height * s);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
                }
                else
                {
                    TextRenderer.DrawText(g, "(no picture)", Font, box, Color.Gray);
                }

                if (i == _hover)
                {
                    using (var pen = new Pen(Color.Gold, 3)) g.DrawRectangle(pen, box.X + 1, box.Y + 1, box.Width - 3, box.Height - 3);
                    var cr = CloseRect(tile);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var bg = new SolidBrush(_hoverClose ? Color.FromArgb(230, 200, 40, 40) : Color.FromArgb(170, 0, 0, 0)))
                        g.FillEllipse(bg, cr);
                    using (var pen = new Pen(Color.White, 2.5f))
                    {
                        int m = CloseSize / 3;
                        g.DrawLine(pen, cr.Left + m, cr.Top + m, cr.Right - m, cr.Bottom - m);
                        g.DrawLine(pen, cr.Right - m, cr.Top + m, cr.Left + m, cr.Bottom - m);
                    }
                    g.SmoothingMode = SmoothingMode.Default;
                }
                if (place.Pinned)
                    TextRenderer.DrawText(g, "★ pinned", _small, new Point(box.X + 8, box.Y + 6), Color.Gold, Color.FromArgb(0, 0, 0));

                string zoom = place.Zoom.ToString(place.Zoom < 1e5 ? "0.##" : "0.00e0", inv);
                string line1 = $"Zoom {zoom}x   {place.Iterations:N0} iterations";
                string line2 = $"{place.Palette}, {place.Mapping}, density {place.Density:0.##}   {place.Visited.ToLocalTime():d MMM HH:mm}";
                var text1 = new Rectangle(tile.X, box.Bottom + 4, TileW, 20);
                var text2 = new Rectangle(tile.X, box.Bottom + 23, TileW, 18);
                const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(g, line1, Font, text1, i == _hover ? Color.Gold : Color.White, flags);
                TextRenderer.DrawText(g, line2, _small, text2, Color.Silver, flags);
            }
        }

        /// <summary>The place's thumbnail at about the tile size, loaded the first time it is drawn.</summary>
        private Image? ImageFor(Place p)
        {
            if (_images.TryGetValue(p.Id, out var cached)) return cached;
            Image? result = null;
            try
            {
                using var fs = File.OpenRead(_owner._store.ThumbnailPath(p));
                using var img = Image.FromStream(fs);
                double s = Math.Min(1.0, Math.Min((double)TileW / img.Width, (double)ThumbH / img.Height));
                int w = Math.Max(1, (int)Math.Round(img.Width * s)), h = Math.Max(1, (int)Math.Round(img.Height * s));
                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, 0, 0, w, h);
                }
                result = bmp;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
            }
            _images[p.Id] = result;
            return result;
        }

        private void ClearImages()
        {
            foreach (var img in _images.Values) img?.Dispose();
            _images.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearImages();
                _small.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
