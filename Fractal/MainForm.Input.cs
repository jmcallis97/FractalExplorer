namespace Fractal;

/// <summary>Mouse, keyboard and fullscreen.</summary>
public sealed partial class MainForm
{
    /// <summary>Zoom multiplier per scroll-wheel notch. 2.0 = each notch halves the view width.</summary>
    private const double ZoomPerNotch = 1.5;
    private const double KeyPanFraction = 0.1; // arrow keys pan this fraction of the window
    private const int IterationStep = 1000;    // + and -

    private bool _dragging;
    private Point _dragStart;
    private long _dragPanX0, _dragPanY0;

    private bool _fullScreen;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
    private Rectangle _savedBounds;

    // =========================================================================
    // Mouse
    // =========================================================================

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_overlay.PickerContains(e.Location, ClientSize)) return;

        double notches = e.Delta / 120.0; // positive = wheel forward

        if ((ModifierKeys & Keys.Control) != 0)
        {
            ChangeDensity(Math.Pow(1.15, notches));
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            _colours.Rotate(0.02 * notches);
            TouchPlace();
            Recolor();
            return;
        }

        StopDive();
        bool wasDeep = _view.UseDeep;
        _view.ZoomAt(e.Location, Math.Pow(ZoomPerNotch, notches));
        if (_view.UseDeep != wasDeep)
            ShowToast(_view.UseDeep ? "Deep zoom: perturbation on" : "Deep zoom off (plain doubles)");

        if (_dragging) BeginDrag(e.Location); // keep dragging smoothly after a zoom
        RequestRender();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            if (_overlay.PickerContains(e.Location, ClientSize))
            {
                int hit = _overlay.HitTile(e.Location, ClientSize);
                if (hit >= 0) SelectPalette(hit);
                return;
            }
            StopDive();
            if ((ModifierKeys & Keys.Control) != 0)
            {
                StartSnap(ask: (ModifierKeys & Keys.Shift) == 0, at: e.Location);
                return;
            }
            BeginDrag(e.Location);
            Cursor = Cursors.SizeAll;
        }
        else if (e.Button == MouseButtons.Middle)
        {
            // Center the view on the clicked point.
            StopDive();
            _view.PanX += e.X - ClientSize.Width / 2;
            _view.PanY += e.Y - ClientSize.Height / 2;
            RequestRender();
        }
    }

    private void BeginDrag(Point p)
    {
        _dragging = true;
        _dragStart = p;
        _dragPanX0 = _view.PanX;
        _dragPanY0 = _view.PanY;
    }

    private void EndDrag()
    {
        _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _view.PanX = _dragPanX0 - (e.X - _dragStart.X);
            _view.PanY = _dragPanY0 - (e.Y - _dragStart.Y);
            RequestRender();
            return;
        }

        int hit = _overlay.HitTile(e.Location, ClientSize);
        if (hit != _colours.HoverIndex)
        {
            _colours.HoverIndex = hit; // live preview of the hovered palette
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Recolor();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging) EndDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_dragging && !Capture) EndDrag();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && !_overlay.PickerContains(e.Location, ClientSize))
            ToggleFullScreen();
    }

    // =========================================================================
    // Keyboard
    // =========================================================================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int panStep = (int)(Math.Min(ClientSize.Width, ClientSize.Height) * KeyPanFraction);
        int bigStep = panStep * 4;
        var palettes = _colours.Palettes;

        switch (keyData)
        {
            // --- Window
            case Keys.F11:
            case Keys.Alt | Keys.Enter:
                ToggleFullScreen();
                return true;
            case Keys.Escape:
                if (_dive.Active || _snapping) StopDive();
                else if (_overlay.PickerOpen) { _overlay.PickerOpen = false; ClearHover(); }
                else if (_showHelp) _showHelp = false;
                else if (_fullScreen) ToggleFullScreen();
                Invalidate();
                return true;
            case Keys.A:
                _autoIterations = !_autoIterations;
                ShowToast(_autoIterations ? "Auto iterations on" : "Auto iterations off");
                if (_autoIterations && !_pipeline.IsRunning && _pipeline.Field != null && MaybeRaiseIterations(_pipeline.Field))
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
                StopDive();
                _view.Reset();
                RequestRender();
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
                _view.IterationBoost += IterationStep;
                ShowToast($"Iterations: {_view.MaxIterations}");
                RequestRender();
                return true;
            case Keys.OemMinus:
            case Keys.Subtract:
                _view.LowerIterations(IterationStep);
                // Lowering by hand means "I want fewer": don't let auto push it straight back up.
                bool wasAuto = _autoIterations;
                _autoIterations = false;
                ShowToast($"Iterations: {_view.MaxIterations}" + (wasAuto ? "  (auto iterations off; A turns it on)" : ""));
                RequestRender();
                return true;
            case Keys.X:
                _view.DeepMode = (DeepMode)(((int)_view.DeepMode + 1) % 3);
                ShowToast(_view.DeepMode switch
                {
                    DeepMode.Auto => $"Deep zoom: auto (perturbation below {Mandelbrot.DeepThreshold:0e0} per pixel)",
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
            case Keys.L:
                ShowPlaces();
                return true;
            case Keys.Back:
                GoBack();
                return true;
            case Keys.Delete:
                ForgetPlace();
                return true;
            case Keys.Shift | Keys.N:
                StartSnap(ask: false);
                return true;

            // --- Colour
            case Keys.P:
                _overlay.PickerOpen = !_overlay.PickerOpen;
                if (!_overlay.PickerOpen) ClearHover();
                Invalidate();
                return true;
            case Keys.PageDown:
                SelectPalette((_colours.PaletteIndex + 1) % palettes.Count);
                return true;
            case Keys.PageUp:
                SelectPalette((_colours.PaletteIndex - 1 + palettes.Count) % palettes.Count);
                return true;
            case >= Keys.D1 and <= Keys.D9:
                int idx = keyData - Keys.D1;
                if (idx < palettes.Count) SelectPalette(idx);
                return true;
            case Keys.G:
                var random = _colours.AddRandom();
                TouchPlace();
                ShowToast($"Palette: {random.Name} — S to save it");
                Recolor();
                return true;
            case Keys.S:
                SaveSelectedPalette();
                return true;
            case Keys.C:
                _colours.Cycling = !_colours.Cycling;
                TouchPlace();
                ShowToast(_colours.Cycling ? "Colour cycling on" : "Colour cycling off");
                return true;
            case Keys.Shift | Keys.C:
                _colours.ReverseCycling();
                ShowToast(_colours.CycleDirection > 0 ? "Cycling forward" : "Cycling backward");
                return true;
            case Keys.OemPeriod:
                _colours.ChangeCycleSpeed(1.25);
                ShowToast($"Cycle speed: {_colours.CycleSpeed:0.##}/s");
                return true;
            case Keys.Oemcomma:
                _colours.ChangeCycleSpeed(1 / 1.25);
                ShowToast($"Cycle speed: {_colours.CycleSpeed:0.##}/s");
                return true;
            case Keys.OemCloseBrackets:
                ChangeDensity(1.25);
                return true;
            case Keys.OemOpenBrackets:
                ChangeDensity(1 / 1.25);
                return true;
            case Keys.M:
                _colours.NextMapping();
                TouchPlace();
                ShowToast($"Colour mapping: {_colours.Mapping}");
                Recolor();
                return true;
            case Keys.D:
                _showFilled = !_showFilled;
                ShowToast(_showFilled ? "Showing filled (skipped) areas in green" : "Filled areas shown normally");
                Recolor();
                return true;

            // --- Engine
            case Keys.V:
                if (!MandelbrotRenderer.SimdSupported) { ShowToast("SIMD not supported on this CPU"); return true; }
                _renderer.UseSimd = !_renderer.UseSimd;
                ShowToast(_renderer.UseSimd ? "SIMD on (AVX2, 8 px/step)" : "SIMD off (scalar)");
                ForceFullRender();
                return true;
            case Keys.Q:
                _pipeline.Progressive = !_pipeline.Progressive;
                if (!_pipeline.Progressive) { _previewLayer.Field = null; _partialLayer.Field = null; }
                ShowToast(_pipeline.Progressive ? "Progressive rendering on" : "Progressive rendering off");
                Invalidate();
                return true;
            case Keys.B:
                _renderer.UseInteriorFill = !_renderer.UseInteriorFill;
                ShowToast(_renderer.UseInteriorFill ? "Interior fill on" : "Interior fill off");
                ForceFullRender();
                return true;
            case Keys.U:
                if (_renderer.Gpu == null) { ShowToast($"No GPU in use: {_renderer.GpuFailure ?? _gpuStatus}", 3); return true; }
                _renderer.UseGpu = !_renderer.UseGpu;
                ShowToast(_renderer.UseGpu ? $"GPU on ({_renderer.Gpu.Name}) for deep zoom" : "GPU off (CPU for deep zoom)");
                if (_view.UseDeep) ForceFullRender();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Pan(int dx, int dy)
    {
        StopDive();
        _view.PanX += dx;
        _view.PanY += dy;
        RequestRender();
    }

    // =========================================================================
    // Palettes
    // =========================================================================

    private void SelectPalette(int index)
    {
        _colours.Select(index);
        TouchPlace();
        ShowToast($"Palette: {_colours.Palettes[index].Name}");
        Recolor();
    }

    private void ClearHover()
    {
        if (_colours.HoverIndex < 0) return;
        _colours.HoverIndex = -1;
        Recolor();
    }

    private void ChangeDensity(double factor)
    {
        _colours.SetDensity(_colours.Density * factor);
        TouchPlace();
        ShowToast($"Colour density: {_colours.Density:0.##}x");
        Recolor();
    }

    private void SaveSelectedPalette()
    {
        try
        {
            ShowToast($"Saved {_colours.SaveSelected()}", 4);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Save failed: " + ex.Message, 4);
        }
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
}
