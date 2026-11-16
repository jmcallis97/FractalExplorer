using System.Globalization;
using System.Runtime.InteropServices;

namespace Fractal;

/// <summary>
/// The places you've been, as thumbnails, newest first. Click one to go back there; right-click
/// to pin, copy or delete it. <see cref="Chosen"/> is the place picked (DialogResult.OK).
/// </summary>
internal sealed class PlacesDialog : Form
{
    private const int ThumbW = 384, ThumbH = 216, TextH = 40, TileGap = 12;

    private readonly PlaceStore _store;
    private readonly FlowLayoutPanel _flow;
    private readonly Label _header;
    private readonly List<Image> _images = [];

    public Place? Chosen { get; private set; }

    public PlacesDialog(PlaceStore store)
    {
        _store = store;
        Text = "Places you've been";
        BackColor = Color.FromArgb(18, 18, 22);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        KeyPreview = true;

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(TileGap, 8, 0, 0),
            ForeColor = Color.Silver,
        };
        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(TileGap / 2),
        };
        Controls.Add(_flow);
        Controls.Add(_header);

        // Up to 6 across and 4 down fit on screen (scrolls beyond that).
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int tileW = ThumbW + TileGap, tileH = ThumbH + TextH + TileGap;
        int cols = Math.Clamp((area.Width * 9 / 10 - TileGap) / tileW, 1, 6);
        int rows = Math.Clamp((area.Height * 85 / 100 - 30 - TileGap) / tileH, 1, 4);
        int count = Math.Max(1, store.Places.Count);
        cols = Math.Min(cols, count);
        rows = Math.Min(rows, (count + cols - 1) / cols);
        ClientSize = new Size(cols * tileW + TileGap + SystemInformation.VerticalScrollBarWidth,
                              _header.Height + rows * tileH + TileGap);

        Fill();
    }

    private void Fill()
    {
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls)
        {
            c.ContextMenuStrip?.Dispose();
            c.Dispose();
        }
        _flow.Controls.Clear();
        foreach (var img in _images) img.Dispose();
        _images.Clear();

        _header.Text = _store.Places.Count == 0
            ? "No places yet. Stay at a view for a few seconds, or change its colours, and it's added when you move on."
            : "Click a place to go back to it.   Right-click: pin, copy location, delete.   Esc closes.";

        foreach (var place in _store.Places)
        {
            var thumb = LoadThumbnail(place);
            if (thumb != null) _images.Add(thumb);
            var tile = new PlaceTile(place, thumb)
            {
                Size = new Size(ThumbW, ThumbH + TextH),
                Margin = new Padding(TileGap / 2),
            };
            tile.Click += (_, _) => Choose(place);
            tile.ContextMenuStrip = MenuFor(place);
            _flow.Controls.Add(tile);
        }
        _flow.ResumeLayout();
    }

    private Image? LoadThumbnail(Place p)
    {
        try
        {
            // Copied out of the file so the file isn't kept locked (it may be deleted from here).
            using var fs = File.OpenRead(_store.ThumbnailPath(p));
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    private ContextMenuStrip MenuFor(Place place)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Go there", null, (_, _) => Choose(place));
        menu.Items.Add(place.Pinned ? "Unpin" : "Pin (never dropped from the list)", null, (_, _) =>
        {
            Try(() => _store.SetPinned(place, !place.Pinned));
            BeginInvoke(Fill); // not from inside the menu being disposed
        });
        menu.Items.Add("Copy location", null, (_, _) =>
        {
            try { Clipboard.SetText(place.ToLocation().ToText()); }
            catch (Exception ex) when (ex is ExternalException or ThreadStateException) { Fail(ex); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) =>
        {
            Try(() => _store.Remove(place));
            BeginInvoke(Fill); // not from inside the menu being disposed
        });
        return menu;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); }
    }

    private void Fail(Exception ex) =>
        MessageBox.Show(this, ex.Message, "Places", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void Choose(Place place)
    {
        Chosen = place;
        DialogResult = DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.L)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var img in _images) img.Dispose();
            Font.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>One place: its thumbnail (letterboxed), with zoom, colouring and when below.</summary>
    private sealed class PlaceTile : Control
    {
        private readonly Place _place;
        private readonly Image? _thumb;
        private bool _hover;

        public PlaceTile(Place place, Image? thumb)
        {
            _place = place;
            _thumb = thumb;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Color.Black);
            var box = new Rectangle(0, 0, ThumbW, ThumbH);
            using (var bg = new SolidBrush(Color.Black)) g.FillRectangle(bg, box);
            if (_thumb != null)
            {
                double s = Math.Min((double)ThumbW / _thumb.Width, (double)ThumbH / _thumb.Height);
                int w = (int)Math.Round(_thumb.Width * s), h = (int)Math.Round(_thumb.Height * s);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(_thumb, new Rectangle((ThumbW - w) / 2, (ThumbH - h) / 2, w, h));
            }
            else
            {
                TextRenderer.DrawText(g, "(no picture)", Font, box, Color.Gray);
            }
            if (_hover)
                using (var pen = new Pen(Color.Gold, 3)) g.DrawRectangle(pen, 1, 1, ThumbW - 3, ThumbH - 3);

            var inv = CultureInfo.InvariantCulture;
            string line1 = $"Zoom {_place.Zoom.ToString(_place.Zoom < 1e5 ? "0.##" : "0.00e0", inv)}x   {_place.Iterations:N0} iterations" +
                           (_place.Pinned ? "   (pinned)" : "");
            string line2 = $"{_place.Palette}, {_place.Mapping}, density {_place.Density:0.##}   {_place.Visited.ToLocalTime():d MMM HH:mm}";
            TextRenderer.DrawText(g, line1, Font, new Point(2, ThumbH + 3), _place.Pinned ? Color.Gold : Color.White);
            TextRenderer.DrawText(g, line2, Font, new Point(2, ThumbH + 21), Color.Silver);
        }
    }
}
