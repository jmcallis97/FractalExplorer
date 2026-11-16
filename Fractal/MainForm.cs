using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
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

    // Auto iterations: after each render, if a noticeable number of pixels only escaped near the
    // limit, others were probably cut off by it (painted as "inside" while really escaping later,
    // which happens a lot near cusps such as -0.75 and +0.25). Then the limit is doubled and the
    // view rendered again, until the detail stops changing or a render gets too slow.
    private const int IterationStep = 1000;
    private const int AutoIterationCap = 1 << 24;
    private const double AutoIterationMaxSeconds = 20;
    private bool _autoIterations; // off by default; A toggles

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

    // ---- Snap and dive --------------------------------------------------------
    // N finds the exact point the view is circling (Snap.Find, in the background) and then jumps
    // or "dives" to it: the view centre glides onto the point while the zoom runs at a steady
    // rate, never more than DiveMaxLead ahead of the newest render on screen (layers are drawn
    // stretched until the render catches up). Renders are never cancelled during a dive; each one
    // starts when the previous finishes, at whatever the view is by then.
    private sealed record Dive(BigFixed X, BigFixed Y, double TargetScale, double SecondsPerDoubling,
                               double PanX0, double PanY0, double Started);
    private const double DivePanSeconds = 1.5;
    private const double DiveMaxLead = 4;
    private const int SnapBits = 1070; // ~320 digits: enough for any zoom the deep renderer reaches
    private Dive? _dive;
    private double _divePacedScale;          // scale of the newest render on screen (preview or full)
    private double _diveSecondsPerDoubling = 0.5;
    private bool _snapping;
    private int _navigation;                 // bumped when the view moves; drops stale snap results
    private Fractal.Location? _pendingSnap;  // snap asked for before the window was shown

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
        if (_pendingSnap != null)
        {
            StartSnap(false, _pendingSnap.SnapZoom);
            _pendingSnap = null;
        }

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
        _divePacedScale = target.View.Scale;
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
            _divePacedScale = target.View.Scale;
            _lastRenderMs = ms;
            _lastRenderPixels = progress.Total;
            if (!reuse) _msPerMegapixel = ms / (target.View.Width * (double)target.View.Height / 1e6);
            _baseLayer.Field = target;
            _previewLayer.Field = null;
            _partialLayer.Field = null;
            Recolor();
            if (_autoIterations) MaybeRaiseIterations(target);
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

    /// <summary>
    /// Doubles the iteration limit when the finished render shows signs of being cut off by it:
    /// some pixels inside, and at least 0.5% of them escaping in the top fifth of the limit.
    /// Only changes the limit; the caller starts the new render (OnRenderFinished's "chase"
    /// step does, and must be the only one, or two renders would run at once).
    /// </summary>
    private bool MaybeRaiseIterations(Field field)
    {
        int limit = field.View.MaxIterations;
        if (limit != MaxIterations || limit >= AutoIterationCap) return false;
        // Don't start a render that will take too long: doubling the limit roughly doubles the
        // time of a full render at this window size (measured on the last full render).
        double nextMs = 2 * _msPerMegapixel * field.View.Width * (double)field.View.Height / 1e6;
        if (nextMs > AutoIterationMaxSeconds * 1000) return false;
        float high = 0.8f * limit;
        int inside = 0, nearLimit = 0;
        foreach (float v in field.Values)
        {
            if (v < 0) inside++;
            else if (v > high) nearLimit++;
        }
        if (inside == 0 || nearLimit < Math.Max(16, field.Values.Length / 200)) return false;
        _iterationBoost += Math.Min(limit, AutoIterationCap - limit);
        ShowToast($"Iterations raised to {MaxIterations:N0} (detail was cut off; A turns this off)");
        return true;
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

        if (_dive != null) StepDive(now, dt);

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

        StopDive();

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
            StopDive();
            BeginDrag(e.Location);
            Cursor = Cursors.SizeAll;
        }
        else if (e.Button == MouseButtons.Middle)
        {
            // Center the view on the clicked point.
            StopDive();
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
                if (_dive != null || _snapping) StopDive();
                else if (_pickerOpen) { _pickerOpen = false; _hoverIndex = -1; Recolor(); }
                else if (_showHelp) _showHelp = false;
                else if (_fullScreen) ToggleFullScreen();
                Invalidate();
                return true;
            case Keys.A:
                _autoIterations = !_autoIterations;
                ShowToast(_autoIterations ? "Auto iterations on" : "Auto iterations off");
                if (_autoIterations && _running == null && _field != null && MaybeRaiseIterations(_field))
                    RequestRender();
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
                _iterationBoost += IterationStep;
                ShowToast($"Iterations: {MaxIterations}");
                RequestRender();
                return true;
            case Keys.OemMinus:
            case Keys.Subtract:
                _iterationBoost = Math.Max(_iterationBoost - IterationStep, 50 - (MaxIterations - _iterationBoost));
                // Lowering by hand means "I want fewer": don't let auto push it straight back up.
                bool wasAuto = _autoIterations;
                _autoIterations = false;
                ShowToast($"Iterations: {MaxIterations}" + (wasAuto ? "  (auto iterations off; A turns it on)" : ""));
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
            case Keys.N:
                StartSnap(ask: true);
                return true;
            case Keys.Shift | Keys.N:
                StartSnap(ask: false);
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
        StopDive();
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
        var inv = CultureInfo.InvariantCulture;
        string text = $"Re: {x}\r\nIm: {y}\r\nPixel size: {v.Scale.ToString("R", inv)}\r\nZoom: {Zoom.ToString("G6", inv)}\r\n" +
                      $"Iterations: {MaxIterations}\r\nMapping: {_mapping}\r\nColour density: {_density.ToString("0.####", inv)}";
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
        if (TryGoTo(text, out string error)) { if (!_snapping) ShowToast("Went to pasted location"); }
        else ShowToast("Clipboard isn't a location: " + error, 3);
    }

    /// <summary>
    /// Jumps to a location in the Ctrl+C format ("Re: ...", "Im: ...", "Zoom: ..." lines; the
    /// zoom keeps the same framing whatever the window size), or plain "re im zoom". Mapping and
    /// colour density are applied if present; a "Snap" line then zooms in to the exact spiral
    /// centre or minibrot there (to the zoom given after "Snap:", if any).
    /// </summary>
    private bool TryGoTo(string text, out string error)
    {
        if (!Fractal.Location.TryParse(text, out var loc, out error)) return false;
        StopDive();
        double scale = ScaleForZoom(loc.Zoom);
        int bits = BigFixed.BitsFor(scale);
        SetAnchor(BigFixed.Parse(loc.Re, bits), BigFixed.Parse(loc.Im, bits));
        _panX = _panY = 0;
        _scale = scale;
        if (loc.Iterations is int it)
        {
            _iterationBoost = 0;
            _iterationBoost = it - MaxIterations; // the limit that was in use when it was copied
        }
        if (loc.Density is double d) _density = Math.Clamp(d, 0.01, 100);
        if (loc.Mapping is ColorMapping mapping) _mapping = mapping;
        if (loc.Density != null || loc.Mapping != null) Recolor();
        RequestRender();
        if (loc.Snap)
        {
            if (IsHandleCreated) StartSnap(false, loc.SnapZoom);
            else _pendingSnap = loc; // opened from the command line: snap once the window is up
        }
        error = "";
        return true;
    }

    private double ScaleForZoom(double zoom) =>
        Math.Clamp(InitialViewWidth / (zoom * Math.Max(1, ClientSize.Width)), MinScale, 1);

    // =========================================================================
    // Snap and dive
    // =========================================================================

    /// <summary>
    /// Looks for the spiral centre or minibrot the view is circling, in the background, then asks
    /// what to do (<paramref name="ask"/>) or dives straight in to <paramref name="targetZoom"/>
    /// (default: as deep as makes sense for what was found).
    /// </summary>
    private void StartSnap(bool ask, double? targetZoom = null)
    {
        if (_snapping) { ShowToast("Still looking..."); return; }
        StopDive();
        int generation = _navigation;
        var v = CurrentView();
        var (x, y) = CenterText(v);
        var loc = new Fractal.Location(x, y, Zoom, MaxIterations);
        int maxIter = Math.Max(200_000, MaxIterations * 4);
        _snapping = true;
        ShowToast("Looking for a spiral centre or minibrot... (Esc cancels)", 120);
        Task.Run(() => Snap.Find(loc, maxIter, SnapBits, _ => { }, out _)).ContinueWith(t => Post(() =>
        {
            _snapping = false;
            if (generation != _navigation) return; // cancelled, or the view moved meanwhile
            if (t.IsFaulted) { ShowToast("Snap failed: " + t.Exception?.GetBaseException().Message, 4); return; }
            if (t.Result is not { } r)
            {
                ShowToast("Nothing to snap to here: centre a spiral's eye or a minibrot and zoom in a little", 5);
                return;
            }
            OnSnapFound(r, ask, targetZoom);
        }), TaskScheduler.Default);
    }

    private void OnSnapFound(Snap.Result r, bool ask, double? targetZoom)
    {
        double zoom = targetZoom ?? Snap.SuggestedZoom(r);
        var choice = SnapDialog.Choice.Dive;
        if (ask)
        {
            _toast = null;
            Invalidate();
            using var dlg = new SnapDialog(Snap.Describe(r), r.DistanceInViews, zoom, _diveSecondsPerDoubling);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            choice = dlg.Result;
            zoom = dlg.TargetZoom;
            if (choice == SnapDialog.Choice.Dive) _diveSecondsPerDoubling = dlg.SecondsPerDoubling;
        }
        else
        {
            ShowToast(r.Kind == Snap.Kind.Misiurewicz ? "Found a spiral centre: zooming in (Esc stops)"
                                                      : $"Found a period {r.Period} minibrot: zooming in (Esc stops)", 3);
        }

        switch (choice)
        {
            case SnapDialog.Choice.Dive:
                StartDive(r.Re, r.Im, ScaleForZoom(zoom));
                break;
            case SnapDialog.Choice.Jump:
                GoToPoint(r.Re, r.Im, ScaleForZoom(zoom));
                break;
            case SnapDialog.Choice.Centre:
                GoToPoint(r.Re, r.Im, _scale);
                break;
        }
    }

    private void GoToPoint(BigFixed x, BigFixed y, double scale)
    {
        int bits = BigFixed.BitsFor(scale);
        SetAnchor(x.WithBits(bits), y.WithBits(bits));
        _panX = _panY = 0;
        _scale = scale;
        RequestRender();
    }

    private void StartDive(BigFixed x, BigFixed y, double targetScale)
    {
        var v = CurrentView();
        int bits = Math.Max(BigFixed.BitsFor(v.Scale), _hx.Bits);
        // Where the view centre is now, relative to the point, in pixels (y down).
        double px = (_hx.WithBits(bits) - x.WithBits(bits)).ToDouble() / v.Scale + v.PanX;
        double py = (y.WithBits(bits) - _hy.WithBits(bits)).ToDouble() / v.Scale + v.PanY;
        _dive = new Dive(x, y, targetScale, _diveSecondsPerDoubling, px, py, _clock.Elapsed.TotalSeconds);
        _divePacedScale = _field?.View.Scale ?? v.Scale;
        _diveAnchorBits = -1;
        ApplyDiveView(v.Scale, px, py);
        Invalidate();
    }

    private int _diveAnchorBits;

    /// <summary>Anchors the view on the dive's point at <paramref name="scale"/>, centre offset by the given pixels.</summary>
    private void ApplyDiveView(double scale, double panX, double panY)
    {
        var d = _dive!;
        int bits = BigFixed.BitsFor(scale);
        if (bits != _diveAnchorBits)
        {
            SetAnchor(d.X.WithBits(bits), d.Y.WithBits(bits));
            _diveAnchorBits = bits;
        }
        _scale = scale;
        _panX = (long)Math.Round(panX);
        _panY = (long)Math.Round(panY);
    }

    private void StepDive(double now, double dt)
    {
        var d = _dive!;
        // The centre glides onto the point (smoothstep), while the zoom runs at a steady rate.
        double e = Math.Clamp((now - d.Started) / DivePanSeconds, 0, 1);
        e = e * e * (3 - 2 * e);

        double logNow = Math.Log2(_scale), logTarget = Math.Log2(d.TargetScale);
        double dir = Math.Sign(logTarget - logNow);
        double step = dt / d.SecondsPerDoubling;
        bool reached = Math.Abs(logTarget - logNow) <= step;
        double next = reached ? logTarget : logNow + dir * step;
        // Don't run more than DiveMaxLead ahead of the newest render on screen.
        double limit = Math.Log2(_divePacedScale) + dir * Math.Log2(DiveMaxLead);
        if (dir * (next - limit) > 0)
        {
            reached = false;
            next = dir > 0 ? Math.Max(logNow, limit) : Math.Min(logNow, limit);
        }

        bool arrived = reached && e >= 1;
        ApplyDiveView(reached ? d.TargetScale : Math.Pow(2, next), d.PanX0 * (1 - e), d.PanY0 * (1 - e));
        if (arrived)
        {
            _dive = null;
            ShowToast("Arrived");
            RequestRender();
            return;
        }
        // Start the next render as soon as the last one is done; never cancel one mid-dive.
        if (_running == null && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            var want = CurrentView();
            if (_field == null || _field.View != want) StartRender(want);
        }
        Invalidate();
    }

    /// <summary>Stops a dive (the view stays where it is) and drops any snap still being looked for.</summary>
    private void StopDive()
    {
        _navigation++;
        if (_dive != null)
        {
            _dive = null;
            ShowToast("Zoom stopped");
            RequestRender();
        }
        else if (_snapping)
        {
            ShowToast("Snap cancelled");
        }
    }

    private void ResetView()
    {
        StopDive();
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
            (_dive != null ? $"Zooming in to {InitialViewWidth / (_dive.TargetScale * Math.Max(1, ClientSize.Width)):G3}x (Esc stops)\n" : "") +
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
            "  + / -               1000 more / fewer iterations\n" +
            "  A                   Auto iterations on/off (raises the limit when detail is cut off)\n" +
            "  R / Home            Reset view\n" +
            "  X                   Deep zoom: auto / always / off\n" +
            "  Ctrl+C / Ctrl+V     Copy / go to location (full precision, with mapping and density;\n" +
            "                      a pasted \"Snap\" line zooms in to the spiral centre or minibrot there)\n" +
            "  N / Shift+N         Snap: find the spiral centre or minibrot here (ask / zoom straight in)\n" +
            "  Esc                 Stop a snap zoom\n" +
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
