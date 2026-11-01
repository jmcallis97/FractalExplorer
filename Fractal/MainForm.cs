using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Fractal;

public sealed class MainForm : Form
{
    // ---- Tunables -------------------------------------------------------
    /// <summary>Zoom multiplier per scroll-wheel notch. 2.0 = each notch halves the view width.</summary>
    private const double ZoomPerNotch = 1.5;
    private const int BaseIterations = 200;
    private const double InitialViewWidth = 3.5;
    private const double InitialCenterX = -0.5;
    private const double InitialCenterY = 0.0;
    private const double MinScale = 1e-16;       // double-precision floor
    private const double KeyPanFraction = 0.1;   // arrow keys pan this fraction of the window
    // ---------------------------------------------------------------------

    private readonly MandelbrotRenderer _renderer = new();
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };

    // ---- View state -----------------------------------------------------
    private double _anchorX = InitialCenterX, _anchorY = InitialCenterY, _scale;
    private long _panX, _panY;
    private int _iterationBoost;

    // ---- Render pipeline -------------------------------------------------
    // At most one render runs at a time. If the view changes meanwhile, the next render starts
    // when this one finishes (pans) or the current one is cancelled (zoom/iteration changes,
    // since its work can't be reused).
    private Field? _field;                 // last completed render
    private float[]? _spare;               // recycled buffer to avoid big allocations while dragging
    private Task? _running;
    private CancellationTokenSource? _runningCts;
    private Viewport _runningView;
    private RenderProgress? _runningProgress;
    private double _runningStarted;
    private long _lastRenderMs, _lastRenderPixels;

    // ---- Display ----------------------------------------------------------
    private Bitmap? _bitmap;
    private int[]? _colorBuffer;

    // ---- Colour -------------------------------------------------------------
    private readonly List<Palette> _palettes = Palette.BuiltIns();
    private readonly Dictionary<Palette, Bitmap> _stripCache = new();
    private int _paletteIndex;
    private int _hoverIndex = -1;
    private int _randomCount;
    private ColorMapping _mapping = ColorMapping.Linear;
    private double _density = 1.0;
    private double _offset;
    private bool _cycling;
    private double _cycleSpeed = 0.25;   // palette cycles per second
    private int _cycleDirection = 1;
    private double _lastTick;

    private static readonly string PaletteFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fractal", "Palettes");

    // ---- UI -------------------------------------------------------------------
    private bool _showInfo = true, _showHelp, _pickerOpen;
    private string? _toast;
    private double _toastUntil;
    private bool _dragging;
    private Point _dragStart;
    private long _dragPanX0, _dragPanY0;

    private readonly Font _infoFont = new("Consolas", 9f);
    private readonly Font _toastFont = new("Segoe UI", 14f, FontStyle.Bold);
    private readonly Font _labelFont = new("Segoe UI", 8.5f);

    // Fullscreen state
    private bool _fullScreen;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
    private Rectangle _savedBounds;

    public MainForm()
    {
        Text = "Mandelbrot";
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _scale = InitialViewWidth / ClientSize.Width;
        LoadUserPalettes();

        _timer.Tick += OnTick;
        _timer.Start();
    }

    private double Zoom => InitialViewWidth / (_scale * Math.Max(1, ClientSize.Width));

    private int MaxIterations
    {
        get
        {
            // Deeper zoom needs more iterations to resolve detail.
            double z = Math.Max(1.0, Zoom);
            int auto = (int)(BaseIterations + 100 * Math.Log2(z));
            return Math.Max(50, auto + _iterationBoost);
        }
    }

    private Viewport CurrentView() =>
        new(_anchorX, _anchorY, _scale, _panX, _panY, ClientSize.Width, ClientSize.Height, MaxIterations);

    private Palette ActivePalette => _palettes[_hoverIndex >= 0 ? _hoverIndex : _paletteIndex];

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RequestRender();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RequestRender(); // overlap with the previous size is reused
    }

    // =========================================================================
    // Render pipeline
    // =========================================================================

    private void RequestRender()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var want = CurrentView();

        if (_running != null)
        {
            // A pan can build on the in-flight render once it lands; a zoom can't, so drop it.
            if (!_runningView.CanReuse(want)) _runningCts!.Cancel();
        }
        else if (_field == null || _field.View != want)
        {
            StartRender(want);
        }
        Invalidate();
    }

    private void StartRender(Viewport view)
    {
        var source = _field;
        int n = view.Width * view.Height;
        var values = _spare?.Length == n ? _spare : new float[n];
        _spare = null;

        var target = new Field(view, values);
        var cts = new CancellationTokenSource();
        var progress = new RenderProgress();
        _runningCts = cts;
        _runningView = view;
        _runningProgress = progress;
        _runningStarted = _clock.Elapsed.TotalSeconds;

        var sw = Stopwatch.StartNew();
        _running = Task.Run(() => _renderer.Render(target, source, progress, cts.Token), cts.Token);
        _running.ContinueWith(t =>
        {
            long ms = sw.ElapsedMilliseconds;
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => OnRenderFinished(t, target, progress, ms)); }
            catch (InvalidOperationException) { /* form closing */ }
        }, TaskScheduler.Default);
    }

    private void OnRenderFinished(Task task, Field target, RenderProgress progress, long ms)
    {
        _running = null;
        _runningCts?.Dispose();
        _runningCts = null;
        _runningProgress = null;

        if (task.IsCompletedSuccessfully)
        {
            _spare = _field?.Values;
            _field = target;
            _lastRenderMs = ms;
            _lastRenderPixels = progress.Total;
            Recolor();
        }
        else
        {
            _spare = target.Values;
            if (task.IsFaulted) ShowToast("Render failed: " + task.Exception?.GetBaseException().Message);
        }

        // Chase whatever the user has done since this render started.
        if (ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            var want = CurrentView();
            if (_field == null || _field.View != want) StartRender(want);
        }
        Invalidate();
    }

    /// <summary>Re-applies the palette to the last computed field. No fractal maths involved.</summary>
    private void Recolor()
    {
        if (_field == null) return;
        var v = _field.View;
        int n = v.Width * v.Height;
        if (_colorBuffer?.Length != n) _colorBuffer = new int[n];
        if (_bitmap == null || _bitmap.Width != v.Width || _bitmap.Height != v.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppPArgb);
        }

        Colorizer.Colorize(_field, ActivePalette, _mapping, _density, _offset, _colorBuffer, _renderer.WorkerCount);

        var data = _bitmap.LockBits(new Rectangle(0, 0, v.Width, v.Height), ImageLockMode.WriteOnly, _bitmap.PixelFormat);
        try
        {
            if (data.Stride == v.Width * 4)
                Marshal.Copy(_colorBuffer, 0, data.Scan0, n);
            else
                for (int y = 0; y < v.Height; y++)
                    Marshal.Copy(_colorBuffer, y * v.Width, data.Scan0 + y * data.Stride, v.Width);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
        Invalidate();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastTick, 0.1);
        _lastTick = now;

        if (_cycling && _field != null)
        {
            _offset += _cycleDirection * _cycleSpeed * dt;
            _offset -= Math.Floor(_offset);
            Recolor();
        }
        else if (_runningProgress != null)
        {
            Invalidate(); // progress bar
        }

        if (_toast != null && now > _toastUntil)
        {
            _toast = null;
            Invalidate();
        }
    }

    // =========================================================================
    // Input
    // =========================================================================

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_pickerOpen && PickerBounds().Contains(e.Location)) return;

        double notches = e.Delta / 120.0; // positive = wheel forward

        if ((ModifierKeys & Keys.Control) != 0)
        {
            ChangeDensity(Math.Pow(1.15, notches));
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            _offset += 0.02 * notches;
            _offset -= Math.Floor(_offset);
            Recolor();
            return;
        }

        // Zoom, keeping the complex point under the cursor fixed on screen.
        var v = CurrentView();
        double pointR = v.PixelToReal(e.X);
        double pointI = v.PixelToImag(e.Y);
        double maxScale = InitialViewWidth * 4 / Math.Max(1, v.Width);
        double newScale = Math.Clamp(v.Scale / Math.Pow(ZoomPerNotch, notches), MinScale, maxScale);

        _scale = newScale;
        _panX = _panY = 0;
        _anchorX = pointR - (e.X - v.Width / 2) * newScale;
        _anchorY = pointI + (e.Y - v.Height / 2) * newScale;

        if (_dragging) BeginDrag(e.Location); // keep dragging smoothly after a zoom
        RequestRender();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            if (_pickerOpen && PickerBounds().Contains(e.Location))
            {
                int hit = HitTile(e.Location);
                if (hit >= 0) SelectPalette(hit);
                return;
            }
            BeginDrag(e.Location);
            Cursor = Cursors.SizeAll;
        }
        else if (e.Button == MouseButtons.Middle)
        {
            // Center the view on the clicked point.
            _panX += e.X - ClientSize.Width / 2;
            _panY += e.Y - ClientSize.Height / 2;
            RequestRender();
        }
    }

    private void BeginDrag(Point p)
    {
        _dragging = true;
        _dragStart = p;
        _dragPanX0 = _panX;
        _dragPanY0 = _panY;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _panX = _dragPanX0 - (e.X - _dragStart.X);
            _panY = _dragPanY0 - (e.Y - _dragStart.Y);
            RequestRender();
            return;
        }

        int hit = _pickerOpen ? HitTile(e.Location) : -1;
        if (hit != _hoverIndex)
        {
            _hoverIndex = hit; // live preview of the hovered palette
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Recolor();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            Cursor = Cursors.Default;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            Recolor();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && !(_pickerOpen && PickerBounds().Contains(e.Location)))
            ToggleFullScreen();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int panStep = (int)(Math.Min(ClientSize.Width, ClientSize.Height) * KeyPanFraction);
        int bigStep = panStep * 4;

        switch (keyData)
        {
            // --- Window
            case Keys.F11:
            case Keys.Alt | Keys.Enter:
                ToggleFullScreen();
                return true;
            case Keys.Escape:
                if (_pickerOpen) { _pickerOpen = false; _hoverIndex = -1; Recolor(); }
                else if (_showHelp) _showHelp = false;
                else if (_fullScreen) ToggleFullScreen();
                Invalidate();
                return true;
            case Keys.I:
                _showInfo = !_showInfo;
                Invalidate();
                return true;
            case Keys.H:
            case Keys.F1:
                _showHelp = !_showHelp;
                Invalidate();
                return true;

            // --- Navigation
            case Keys.R:
            case Keys.Home:
                ResetView();
                return true;
            case Keys.Left: Pan(-panStep, 0); return true;
            case Keys.Right: Pan(panStep, 0); return true;
            case Keys.Up: Pan(0, -panStep); return true;
            case Keys.Down: Pan(0, panStep); return true;
            case Keys.Shift | Keys.Left: Pan(-bigStep, 0); return true;
            case Keys.Shift | Keys.Right: Pan(bigStep, 0); return true;
            case Keys.Shift | Keys.Up: Pan(0, -bigStep); return true;
            case Keys.Shift | Keys.Down: Pan(0, bigStep); return true;
            case Keys.Oemplus:
            case Keys.Add:
            case Keys.Shift | Keys.Oemplus:
                _iterationBoost += 100;
                ShowToast($"Iterations: {MaxIterations}");
                RequestRender();
                return true;
            case Keys.OemMinus:
            case Keys.Subtract:
                _iterationBoost -= 100;
                ShowToast($"Iterations: {MaxIterations}");
                RequestRender();
                return true;

            // --- Colour
            case Keys.P:
                _pickerOpen = !_pickerOpen;
                if (!_pickerOpen && _hoverIndex >= 0) { _hoverIndex = -1; Recolor(); }
                Invalidate();
                return true;
            case Keys.PageDown:
                SelectPalette((_paletteIndex + 1) % _palettes.Count);
                return true;
            case Keys.PageUp:
                SelectPalette((_paletteIndex - 1 + _palettes.Count) % _palettes.Count);
                return true;
            case >= Keys.D1 and <= Keys.D9:
                int idx = keyData - Keys.D1;
                if (idx < _palettes.Count) SelectPalette(idx);
                return true;
            case Keys.G:
                _palettes.Add(Palette.Random(_rng, ++_randomCount));
                SelectPalette(_palettes.Count - 1, " — S to save it");
                return true;
            case Keys.S:
                SaveCurrentPalette();
                return true;
            case Keys.C:
                _cycling = !_cycling;
                ShowToast(_cycling ? "Colour cycling on" : "Colour cycling off");
                return true;
            case Keys.Shift | Keys.C:
                _cycleDirection = -_cycleDirection;
                _cycling = true;
                ShowToast(_cycleDirection > 0 ? "Cycling forward" : "Cycling backward");
                return true;
            case Keys.OemPeriod:
                _cycleSpeed = Math.Min(_cycleSpeed * 1.25, 10);
                ShowToast($"Cycle speed: {_cycleSpeed:0.##}/s");
                return true;
            case Keys.Oemcomma:
                _cycleSpeed = Math.Max(_cycleSpeed / 1.25, 0.01);
                ShowToast($"Cycle speed: {_cycleSpeed:0.##}/s");
                return true;
            case Keys.OemCloseBrackets:
                ChangeDensity(1.25);
                return true;
            case Keys.OemOpenBrackets:
                ChangeDensity(1 / 1.25);
                return true;
            case Keys.M:
                _mapping = (ColorMapping)(((int)_mapping + 1) % Enum.GetValues<ColorMapping>().Length);
                ShowToast($"Colour mapping: {_mapping}");
                Recolor();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Pan(int dx, int dy)
    {
        _panX += dx;
        _panY += dy;
        RequestRender();
    }

    private void ResetView()
    {
        _anchorX = InitialCenterX;
        _anchorY = InitialCenterY;
        _panX = _panY = 0;
        _scale = InitialViewWidth / Math.Max(1, ClientSize.Width);
        _iterationBoost = 0;
        RequestRender();
    }

    // =========================================================================
    // Palettes
    // =========================================================================

    private void SelectPalette(int index, string suffix = "")
    {
        _paletteIndex = index;
        _hoverIndex = -1;
        ShowToast($"Palette: {_palettes[index].Name}{suffix}");
        Recolor();
    }

    private void ChangeDensity(double factor)
    {
        _density = Math.Clamp(_density * factor, 0.01, 100);
        ShowToast($"Colour density: {_density:0.##}x");
        Recolor();
    }

    private void LoadUserPalettes()
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

    private void SaveCurrentPalette()
    {
        var p = _palettes[_paletteIndex];
        try
        {
            Directory.CreateDirectory(PaletteFolder);
            string baseName = string.Concat(p.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))
                .Replace('#', '_').Replace(' ', '_');
            string path = Path.Combine(PaletteFolder, baseName + ".map");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(PaletteFolder, $"{baseName}_{i}.map");
            p.SaveMapFile(path);
            ShowToast($"Saved {path}", 4);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Save failed: " + ex.Message, 4);
        }
    }

    // ---- Picker layout ---------------------------------------------------

    private const int TileW = 168, StripH = 22, LabelH = 18, Gap = 10, Pad = 14, HeaderH = 26;

    private int PickerColumns => Math.Max(1, (ClientSize.Width - 2 * Pad + Gap) / (TileW + Gap));

    private Rectangle PickerBounds()
    {
        int rows = (_palettes.Count + PickerColumns - 1) / PickerColumns;
        int height = Pad * 2 + HeaderH + rows * (StripH + LabelH + Gap) - Gap;
        return new Rectangle(0, ClientSize.Height - height, ClientSize.Width, height);
    }

    private Rectangle TileRect(int i)
    {
        int top = PickerBounds().Top;
        int col = i % PickerColumns, row = i / PickerColumns;
        return new Rectangle(Pad + col * (TileW + Gap), top + Pad + HeaderH + row * (StripH + LabelH + Gap),
                             TileW, StripH + LabelH);
    }

    private int HitTile(Point p)
    {
        for (int i = 0; i < _palettes.Count; i++)
            if (TileRect(i).Contains(p)) return i;
        return -1;
    }

    private Bitmap StripFor(Palette p)
    {
        if (!_stripCache.TryGetValue(p, out var bmp))
            _stripCache[p] = bmp = p.CreateStrip(TileW);
        return bmp;
    }

    // =========================================================================
    // Fullscreen
    // =========================================================================

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedBorder = FormBorderStyle;
            _savedState = WindowState;
            _savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

            // Must go Normal first so the borderless window covers the taskbar.
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _fullScreen = true;
        }
        else
        {
            FormBorderStyle = _savedBorder;
            Bounds = _savedBounds;
            WindowState = _savedState;
            _fullScreen = false;
        }
    }

    // =========================================================================
    // Painting
    // =========================================================================

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        if (_bitmap != null && _field != null)
            DrawFractal(g, _field.View, CurrentView());

        DrawProgress(g);
        if (_showInfo) DrawInfo(g);
        if (_pickerOpen) DrawPicker(g);
        if (_showHelp) DrawHelp(g);
        if (_toast != null) DrawToast(g);
    }

    /// <summary>
    /// Draws the last completed image transformed into the current view. While panning or
    /// zooming this gives instant feedback; the exact render replaces it when ready.
    /// </summary>
    private void DrawFractal(Graphics g, Viewport f, Viewport cur)
    {
        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (f.SameGrid(cur))
        {
            // Pure pan/resize: exact integer offset, no scaling.
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            int x = (int)(f.OriginX - cur.OriginX);
            int y = (int)(f.OriginY - cur.OriginY);
            g.DrawImage(_bitmap!, new Rectangle(x, y, f.Width, f.Height));
        }
        else
        {
            double ratio = f.Scale / cur.Scale;
            double x0 = (f.AnchorX + f.OriginX * f.Scale - cur.AnchorX) / cur.Scale - cur.OriginX;
            double y0 = (cur.AnchorY - f.AnchorY + f.OriginY * f.Scale) / cur.Scale - cur.OriginY;
            g.InterpolationMode = ratio > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.DrawImage(_bitmap!, new RectangleF((float)x0, (float)y0, (float)(f.Width * ratio), (float)(f.Height * ratio)));
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private void DrawProgress(Graphics g)
    {
        // Only show for renders slow enough to notice.
        if (_runningProgress == null || _clock.Elapsed.TotalSeconds - _runningStarted < 0.15) return;
        int w = (int)(ClientSize.Width * _runningProgress.Fraction);
        using var b = new SolidBrush(Color.FromArgb(220, 255, 200, 60));
        g.FillRectangle(b, 0, 0, w, 3);
    }

    private void DrawInfo(Graphics g)
    {
        var v = CurrentView();
        string status = _runningProgress != null ? $"Rendering {_runningProgress.Fraction:P0}" : $"Last render: {_lastRenderMs} ms";
        string text =
            $"Center: {v.CenterX:R}, {v.CenterY:R}\n" +
            $"Zoom: {Zoom:G4}x   Iterations: {MaxIterations}\n" +
            $"Threads: {_renderer.WorkerCount} of {Environment.ProcessorCount} cores   {status} ({_lastRenderPixels:N0} px computed)\n" +
            $"Palette: {ActivePalette.Name}   Mapping: {_mapping}   Density: {_density:0.##}x   Cycling: {(_cycling ? $"{_cycleSpeed:0.##}/s" : "off")}\n" +
            "H: help   P: palettes   I: hide info";

        var size = g.MeasureString(text, _infoFont);
        int stripY = 12 + (int)size.Height + 4;
        using var bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        g.FillRectangle(bg, 8, 8, Math.Max(size.Width, 208) + 8, size.Height + 26);
        g.DrawString(text, _infoFont, Brushes.White, 12, 12);

        // Current palette, rotated by the cycle offset, so you can see what's happening.
        using var strip = ActivePalette.CreateStrip(200, _offset);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(strip, new Rectangle(12, stripY, 200, 10));
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private void DrawPicker(Graphics g)
    {
        var bounds = PickerBounds();
        using var bg = new SolidBrush(Color.FromArgb(200, 10, 10, 14));
        g.FillRectangle(bg, bounds);
        g.DrawString("Palettes — hover to preview, click to select.   G: random   S: save   Esc/P: close",
                     _labelFont, Brushes.Gainsboro, Pad, bounds.Top + Pad);

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        for (int i = 0; i < _palettes.Count; i++)
        {
            var r = TileRect(i);
            var stripRect = new Rectangle(r.X, r.Y, TileW, StripH);
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(StripFor(_palettes[i]), stripRect);
            g.PixelOffsetMode = PixelOffsetMode.Default;

            if (i == _paletteIndex)
                using (var pen = new Pen(Color.White, 2)) g.DrawRectangle(pen, Rectangle.Inflate(stripRect, 2, 2));
            else if (i == _hoverIndex)
                using (var pen = new Pen(Color.Gold, 2)) g.DrawRectangle(pen, Rectangle.Inflate(stripRect, 2, 2));

            string label = i < 9 ? $"{i + 1}  {_palettes[i].Name}" : _palettes[i].Name;
            g.DrawString(label, _labelFont, i == _paletteIndex ? Brushes.White : Brushes.Silver, r.X, r.Y + StripH + 2);
        }
    }

    private void DrawHelp(Graphics g)
    {
        const string help =
            "NAVIGATION\n" +
            "  Wheel               Zoom at cursor\n" +
            "  Left-drag           Pan (only new edges are computed)\n" +
            "  Arrows / Shift      Pan a step / a big step\n" +
            "  Middle-click        Center on point\n" +
            "  + / -               More / fewer iterations\n" +
            "  R / Home            Reset view\n" +
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
            "WINDOW\n" +
            "  F11 / Alt+Enter / double-click   Fullscreen (Esc exits)\n" +
            "  I                   Toggle info      H / F1   Toggle help";

        var size = g.MeasureString(help, _infoFont);
        float x = (ClientSize.Width - size.Width) / 2, y = (ClientSize.Height - size.Height) / 2;
        using var bg = new SolidBrush(Color.FromArgb(215, 0, 0, 0));
        g.FillRectangle(bg, x - 16, y - 16, size.Width + 32, size.Height + 32);
        g.DrawString(help, _infoFont, Brushes.White, x, y);
    }

    private void DrawToast(Graphics g)
    {
        var size = g.MeasureString(_toast, _toastFont);
        float x = (ClientSize.Width - size.Width) / 2, y = 24;
        using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        g.FillRectangle(bg, x - 12, y - 6, size.Width + 24, size.Height + 12);
        g.DrawString(_toast, _toastFont, Brushes.White, x, y);
    }

    private void ShowToast(string text, double seconds = 1.5)
    {
        _toast = text;
        _toastUntil = _clock.Elapsed.TotalSeconds + seconds;
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _runningCts?.Cancel();
        _bitmap?.Dispose();
        foreach (var b in _stripCache.Values) b.Dispose();
        _infoFont.Dispose();
        _toastFont.Dispose();
        _labelFont.Dispose();
        base.OnFormClosed(e);
    }
}
