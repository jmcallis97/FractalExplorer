using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Fractal.Deep;
using Fractal.Gpu;

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
    private const double MinScale = 1e-290;      // perturbation needs pixel offsets within double range
    /// <summary>Auto mode switches to perturbation (deep zoom) below this pixel size.</summary>
    private const double DeepThreshold = 1e-13;
    private const double KeyPanFraction = 0.1;   // arrow keys pan this fraction of the window
    // ---------------------------------------------------------------------

    private readonly MandelbrotRenderer _renderer = new();
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };

    // ---- View state -----------------------------------------------------
    // The anchor is kept in full precision; _anchorX/Y are its nearest doubles.
    private BigFixed _hx, _hy;
    private DeepAnchor _deepAnchor = null!;
    private double _anchorX, _anchorY, _scale;
    private long _panX, _panY;

    private enum DeepMode { Auto, Always, Off }
    private DeepMode _deepMode = DeepMode.Auto;
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
    private Field? _runningTarget;
    private double _runningStarted;
    private long _lastRenderMs, _lastRenderPixels;
    private double _msPerMegapixel;        // from the last full (non-reuse) render; predicts slow renders

    // ---- Progressive rendering ---------------------------------------------
    // Renders predicted to take longer than this get a quick low-resolution preview first.
    private const double PreviewThresholdMs = 100;
    private const double PartialRefreshSeconds = 0.1;
    private bool _progressive = true;
    private double _lastPartialRefresh;

    // ---- Display: drawn bottom to top ------------------------------------
    private readonly DisplayLayer _baseLayer = new();                            // last completed render
    private readonly DisplayLayer _previewLayer = new() { SmoothUpscale = true }; // low-res preview
    private readonly DisplayLayer _partialLayer = new();                         // in-progress render

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
    private bool _showFilled; // tint pixels that interior fill skipped
    private bool _forceFull; // next render ignores the previous field (for honest V-toggle timing)
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

    /// <param name="startLocation">Optional location text (as copied with Ctrl+C) to open at.</param>
    public MainForm(string? startLocation = null)
    {
        Text = "Mandelbrot";
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _scale = InitialViewWidth / ClientSize.Width;
        SetAnchor(BigFixed.FromDouble(InitialCenterX, 64), BigFixed.FromDouble(InitialCenterY, 64));
        LoadUserPalettes();
        if (startLocation != null && !TryGoTo(startLocation, out string error))
            ShowToast("Couldn't open location: " + error, 4);

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

    private bool UseDeep => _deepMode == DeepMode.Always || (_deepMode == DeepMode.Auto && _scale < DeepThreshold);

    private Viewport CurrentView() =>
        new(_anchorX, _anchorY, _scale, _panX, _panY, ClientSize.Width, ClientSize.Height, MaxIterations,
            UseDeep ? _deepAnchor : null);

    private void SetAnchor(BigFixed x, BigFixed y)
    {
        _hx = x;
        _hy = y;
        _deepAnchor = new DeepAnchor(x, y);
        _anchorX = x.ToDouble();
        _anchorY = y.ToDouble();
    }

    private Palette ActivePalette => _palettes[_hoverIndex >= 0 ? _hoverIndex : _paletteIndex];

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RequestRender();

        // Find and self-test the GPU in the background (takes a few hundred ms); deep renders
        // use the CPU until it's ready, and for good if there isn't a usable one.
        Task.Run(() =>
        {
            var gpu = GpuDeep.TryCreate(out string reason);
            Post(() =>
            {
                if (IsDisposed) { gpu?.Dispose(); return; }
                _renderer.Gpu = gpu;
                _gpuStatus = gpu != null ? gpu.Name : $"none ({reason})";
                if (gpu != null && UseDeep) ForceFullRender();
                Invalidate();
            });
        });
    }

    private string _gpuStatus = "starting…";

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
        // Resize fires from the constructor before the window exists. A render started then could
        // never marshal its result back, leaving _running set forever, so wait for the handle.
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
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

/// <summary>Re-renders everything from scratch, e.g. to time a kernel change.</summary>
    private void ForceFullRender()
    {
        if (!IsHandleCreated) return;
        _forceFull = true;
        if (_running != null) _runningCts!.Cancel();
        else StartRender(CurrentView());
        Invalidate();
    }

    private void StartRender(Viewport view)
    {
        var source = _forceFull ? null : _field;
        _forceFull = false;
        int n = view.Width * view.Height;
        var values = _spare?.Length == n ? _spare : new float[n];
        _spare = null;

        var target = new Field(view, values);
        var cts = new CancellationTokenSource();
        var progress = new RenderProgress();
        _runningCts = cts;
        _runningView = view;
        _runningProgress = progress;
        _runningTarget = target;
        _runningStarted = _clock.Elapsed.TotalSeconds;

        bool reuse = source != null && source.View.CanReuse(view);
        int[] previewFactors = reuse ? [] : ChoosePreviewFactors(view);

        var sw = Stopwatch.StartNew();
        _running = Task.Run(() =>
        {
            // Coarse previews first: tiny cost (1/64 of the pixels at factor 8) but something
            // sharp-ish on screen almost immediately.
            foreach (int factor in previewFactors)
            {
                var pv = PreviewView(view, factor);
                var pf = new Field(pv, new float[pv.Width * pv.Height]);
                _renderer.Render(pf, null, new RenderProgress(), cts.Token);
                Post(() => OnPreviewReady(target, pf));
            }
            _renderer.Render(target, source, progress, cts.Token);
        }, cts.Token);
        _running.ContinueWith(t =>
        {
            long ms = sw.ElapsedMilliseconds;
            Post(() => OnRenderFinished(t, target, progress, ms, reuse));
        }, TaskScheduler.Default);
    }

    private void Post(Action action)
    {
        if (IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* form closing */ }
    }

    private int[] ChoosePreviewFactors(Viewport view)
    {
        if (!_progressive) return [];
        double estimateMs = _msPerMegapixel * view.Width * view.Height / 1e6;
        if (estimateMs < PreviewThresholdMs) return [];
        return estimateMs < 1500 ? [8] : [16, 4];
    }

    /// <summary>
    /// Same anchor and iteration limit, <paramref name="factor"/>× coarser pixels, centred to
    /// within one coarse pixel. Sharing the anchor means a deep preview reuses the reference orbit.
    /// </summary>
    private static Viewport PreviewView(Viewport v, int factor) =>
        new(v.AnchorX, v.AnchorY, v.Scale * factor,
            (long)Math.Round((double)v.PanX / factor), (long)Math.Round((double)v.PanY / factor),
            (v.Width + factor - 1) / factor, (v.Height + factor - 1) / factor, v.MaxIterations, v.Deep);

    private void OnPreviewReady(Field target, Field preview)
    {
        if (_runningTarget != target) return; // a newer render has started
        _previewLayer.Field = preview;
        _partialLayer.Field = null;           // older partial would sit on top of the newer preview
        ColorizeLayer(_previewLayer);
        Invalidate();
    }

    private void OnRenderFinished(Task task, Field target, RenderProgress progress, long ms, bool reuse)
    {
        _running = null;
        _runningCts?.Dispose();
        _runningCts = null;
        _runningProgress = null;
        _runningTarget = null;

        if (task.IsCompletedSuccessfully)
        {
            _spare = _field?.Values;
            _field = target;
            _lastRenderMs = ms;
            _lastRenderPixels = progress.Total;
            if (!reuse) _msPerMegapixel = ms / (target.View.Width * (double)target.View.Height / 1e6);
            _baseLayer.Field = target;
            _previewLayer.Field = null;
            _partialLayer.Field = null;
            Recolor();
        }
        else
        {
            // Keep a cancelled render's partial image on screen (it's still the newest imagery),
            // so don't recycle its buffer while that layer shows it.
            if (_partialLayer.Field != target) _spare = target.Values;
            if (task.IsFaulted) ShowToast("Render failed: " + task.Exception?.GetBaseException().Message);
        }

        // Chase whatever the user has done since this render started.
        if (ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            var want = CurrentView();
            if (_field == null || _field.View != want || _forceFull) StartRender(want);
        }
        Invalidate();
    }

    /// <summary>Re-applies the palette to every visible layer. No fractal maths involved.</summary>
    private void Recolor()
    {
        ColorizeLayer(_baseLayer);
        ColorizeLayer(_previewLayer);
        ColorizeLayer(_partialLayer);
        Invalidate();
    }

    private void ColorizeLayer(DisplayLayer layer) =>
        layer.Colorize(ActivePalette, _mapping, _density, _offset, _renderer.WorkerCount, _showFilled);

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastTick, 0.1);
        _lastTick = now;

        // Show the in-progress render as it fills in (only for renders slow enough to notice).
        bool refreshPartial = _progressive && _runningTarget != null
                              && now - _runningStarted > 0.12
                              && now - _lastPartialRefresh >= PartialRefreshSeconds;
        if (refreshPartial)
        {
            _partialLayer.Field = _runningTarget;
            _lastPartialRefresh = now;
        }

        if (_cycling && _field != null)
        {
            _offset += _cycleDirection * _cycleSpeed * dt;
            _offset -= Math.Floor(_offset);
            Recolor();
        }
        else if (refreshPartial)
        {
            ColorizeLayer(_partialLayer);
            Invalidate();
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

        // Zoom, keeping the complex point under the cursor fixed on screen. Done in full
        // precision: new anchor = anchor + (cursor's global pixel) × old scale
        //                              - (cursor's offset from centre) × new scale.
        var v = CurrentView();
        double maxScale = InitialViewWidth * 4 / Math.Max(1, v.Width);
        double newScale = Math.Clamp(v.Scale / Math.Pow(ZoomPerNotch, notches), MinScale, maxScale);
        bool wasDeep = UseDeep;

        int bits = BigFixed.BitsFor(Math.Min(newScale, v.Scale));
        long gx = v.OriginX + e.X, gy = v.OriginY + e.Y;
        SetAnchor(
            _hx.WithBits(bits) + BigFixed.FromDouble(gx * v.Scale, bits) - BigFixed.FromDouble((e.X - v.Width / 2) * newScale, bits),
            _hy.WithBits(bits) - BigFixed.FromDouble(gy * v.Scale, bits) + BigFixed.FromDouble((e.Y - v.Height / 2) * newScale, bits));
        _scale = newScale;
        _panX = _panY = 0;

        if (UseDeep != wasDeep)
            ShowToast(UseDeep ? "Deep zoom: perturbation on" : "Deep zoom off (plain doubles)");

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
case Keys.V:
                if (!MandelbrotRenderer.SimdSupported) { ShowToast("SIMD not supported on this CPU"); return true; }
                _renderer.UseSimd = !_renderer.UseSimd;
                ShowToast(_renderer.UseSimd ? "SIMD on (AVX2, 8 px/step)" : "SIMD off (scalar)");
                ForceFullRender();
                return true;
            case Keys.Q:
                _progressive = !_progressive;
                if (!_progressive) { _previewLayer.Field = null; _partialLayer.Field = null; }
                ShowToast(_progressive ? "Progressive rendering on" : "Progressive rendering off");
                Invalidate();
                return true;
            case Keys.B:
                _renderer.UseInteriorFill = !_renderer.UseInteriorFill;
                ShowToast(_renderer.UseInteriorFill ? "Interior fill on" : "Interior fill off");
                ForceFullRender();
                return true;
            case Keys.D:
                _showFilled = !_showFilled;
                ShowToast(_showFilled ? "Showing filled (skipped) areas in green" : "Filled areas shown normally");
                Recolor();
                return true;
            case Keys.X:
                _deepMode = (DeepMode)(((int)_deepMode + 1) % 3);
                ShowToast(_deepMode switch
                {
                    DeepMode.Auto => $"Deep zoom: auto (perturbation below {DeepThreshold:0e0} per pixel)",
                    DeepMode.Always => "Deep zoom: always perturbation",
                    _ => "Deep zoom: off (plain doubles - blocky when deep)",
                }, 2.5);
                RequestRender();
                return true;
            case Keys.Control | Keys.C:
                CopyLocation();
                return true;
            case Keys.Control | Keys.V:
                PasteLocation();
                return true;
            case Keys.U:
                if (_renderer.Gpu == null) { ShowToast($"No GPU in use: {_renderer.GpuFailure ?? _gpuStatus}", 3); return true; }
                _renderer.UseGpu = !_renderer.UseGpu;
                ShowToast(_renderer.UseGpu ? $"GPU on ({_renderer.Gpu.Name}) for deep zoom" : "GPU off (CPU for deep zoom)");
                if (UseDeep) ForceFullRender();
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

    /// <summary>View centre in full precision, with enough digits to pin down a pixel.</summary>
    private (string X, string Y) CenterText(Viewport v)
    {
        int bits = Math.Max(_hx.Bits, BigFixed.BitsFor(v.Scale));
        var cx = _hx.WithBits(bits) + BigFixed.FromDouble(v.PanX * v.Scale, bits);
        var cy = _hy.WithBits(bits) - BigFixed.FromDouble(v.PanY * v.Scale, bits);
        int digits = Math.Max(16, (int)Math.Ceiling(-Math.Log10(v.Scale)) + 2);
        return (cx.ToDecimalString(digits), cy.ToDecimalString(digits));
    }

    private void CopyLocation()
    {
        var v = CurrentView();
        var (x, y) = CenterText(v);
        string text = $"Re: {x}\r\nIm: {y}\r\nPixel size: {v.Scale:R}\r\nZoom: {Zoom:G6}\r\nIterations: {MaxIterations}";
        try
        {
            Clipboard.SetText(text);
            ShowToast("Location copied to clipboard");
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            ShowToast("Couldn't copy: " + ex.Message);
        }
    }

    private void PasteLocation()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException)
        {
            ShowToast("Couldn't read clipboard: " + ex.Message);
            return;
        }
        if (TryGoTo(text, out string error)) ShowToast("Went to pasted location");
        else ShowToast("Clipboard isn't a location: " + error, 3);
    }

    /// <summary>
    /// Jumps to a location in the Ctrl+C format ("Re: ...", "Im: ...", "Zoom: ..." lines; the
    /// zoom keeps the same framing whatever the window size), or plain "re im zoom".
    /// </summary>
    private bool TryGoTo(string text, out string error)
    {
        string? re = null, im = null, zoomText = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant(), value = line[(colon + 1)..].Trim();
            if (key is "re" or "real" or "x") re = value;
            else if (key is "im" or "imag" or "y") im = value;
            else if (key == "zoom") zoomText = value.TrimEnd('x', 'X');
        }
        if (re == null)
        {
            var parts = text.Split((char[])[' ', '\t', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) (re, im) = (parts[0], parts[1]);
            if (parts.Length >= 3) zoomText = parts[2].TrimEnd('x', 'X');
        }
        if (re == null || im == null) { error = "no Re/Im"; return false; }

        double zoom = 1;
        if (zoomText != null && !double.TryParse(zoomText, System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out zoom))
        {
            error = "bad zoom";
            return false;
        }
        double scale = Math.Clamp(InitialViewWidth / (Math.Max(zoom, 1e-3) * Math.Max(1, ClientSize.Width)), MinScale, 1);
        try
        {
            int bits = BigFixed.BitsFor(scale);
            var x = BigFixed.Parse(re, bits);
            var y = BigFixed.Parse(im, bits);
            SetAnchor(x, y);
        }
        catch (FormatException)
        {
            error = "bad number";
            return false;
        }
        _panX = _panY = 0;
        _scale = scale;
        RequestRender();
        error = "";
        return true;
    }

    private void ResetView()
    {
        SetAnchor(BigFixed.FromDouble(InitialCenterX, 64), BigFixed.FromDouble(InitialCenterY, 64));
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

        // Each layer is drawn transformed into the current view, so while panning or zooming the
        // newest imagery follows the mouse until the exact render replaces it.
        // A GDI+ error escaping OnPaint makes WinForms give up on the window for good (red X),
        // so a layer that can't be drawn is skipped instead.
        var cur = CurrentView();
        foreach (var layer in (ReadOnlySpan<DisplayLayer>)[_baseLayer, _previewLayer, _partialLayer])
        {
            try { layer.Draw(g, cur); }
            catch (Exception ex) when (ex is OverflowException or ArgumentException or ExternalException) { }
        }

        DrawProgress(g);
        if (_showInfo) DrawInfo(g);
        if (_pickerOpen) DrawPicker(g);
        if (_showHelp) DrawHelp(g);
        if (_toast != null) DrawToast(g);
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
        var (cx, cy) = CenterText(v);
        const int maxShown = 40;
        string Clip(string s) => s.Length <= maxShown ? s : s[..maxShown] + "…";
        string precision = v.Deep == null
            ? $"Precision: double{(_deepMode == DeepMode.Off ? " (deep zoom off)" : "")}"
            : $"Precision: perturbation, {v.Deep.X.Bits}-bit reference" +
              (_renderer.LastOrbit is { } o ? $" ({o.Length - 1:N0} iters, {o.ComputeTime.TotalMilliseconds:0} ms)" : "");
        string text =
            $"Center: {Clip(cx)}, {Clip(cy)}\n" +
            $"Zoom: {Zoom:G4}x   Iterations: {MaxIterations}   {precision}\n" +
            $"{EngineText(v)}   {(_renderer.UseSimd ? "SIMD" : "Scalar")}{(_renderer.UseInteriorFill ? "+Fill" : "")}{(_progressive ? "+Prog" : "")}   {status} ({_lastRenderPixels:N0} px rendered)\n" +
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

    private string EngineText(in Viewport v)
    {
        string cpu = $"Threads: {_renderer.WorkerCount} of {Environment.ProcessorCount} cores";
        string gpu = _renderer.GpuFailure != null ? $"GPU failed: {_renderer.GpuFailure}"
            : _renderer.Gpu == null ? $"GPU: {_gpuStatus}"
            : !_renderer.UseGpu ? "GPU: off (U)"
            : v.Deep != null && _renderer.LastRenderUsedGpu ? $"GPU: {_renderer.Gpu.Name}"
            : $"GPU: {_renderer.Gpu.Name} (deep zoom only)";
        return $"{cpu}   {gpu}";
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
            "  X                   Deep zoom: auto / always / off\n" +
            "  Ctrl+C / Ctrl+V     Copy / go to location (full precision)\n" +
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
            "  V                   SIMD on/off (compare render times)\n" +
            "  U                   GPU on/off for deep zoom (DirectX 12)\n" +
            "  B / D               Interior fill on/off / show filled areas\n" +
            "  Q                   Progressive rendering (preview + live fill-in) on/off\n" +
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
        _baseLayer.Dispose();
        _previewLayer.Dispose();
        _partialLayer.Dispose();
        foreach (var b in _stripCache.Values) b.Dispose();
        _infoFont.Dispose();
        _toastFont.Dispose();
        _labelFont.Dispose();
        base.OnFormClosed(e);
    }
}
