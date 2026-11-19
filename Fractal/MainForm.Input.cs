namespace Fractal;

/// <summary>Mouse, panning, palettes and fullscreen (keys: MainForm.Commands.cs).</summary>
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

        if (_dive.Paused)
        {
            _dive.MoveAlong(Math.Pow(ZoomPerNotch, notches));
            RequestRender();
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
            if ((ModifierKeys & Keys.Alt) != 0)
            {
                ShowProfile(e.Location);
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
    // Panning (keys: the key map in MainForm.Commands.cs)
    // =========================================================================

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
