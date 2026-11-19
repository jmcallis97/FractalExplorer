using System.Text;

namespace Fractal;

/// <summary>
/// One line of the key map: the keys it's bound to, how the help shows it, and what it does.
/// A command with no <see cref="Bindings"/> is a mouse gesture, listed in the help only (the
/// mouse handlers do the work).
/// </summary>
internal sealed record Command(string Group, string Shown, string Text, Keys[] Bindings, Action? Run = null);

/// <summary>
/// The key map: every keyboard command in one table. Keys are looked up here, and the help
/// (H) is generated from it, so the two can't drift apart.
/// </summary>
public sealed partial class MainForm
{
    private const string Navigation = "NAVIGATION", Places = "PLACES", SnapGroup = "SNAP",
        Colour = "COLOUR (instant — no recompute)", Window = "WINDOW", Engine = "ENGINE (F-keys; re-renders)";

    private Command[] _commands = null!;
    private Dictionary<Keys, Command> _keyMap = null!;

    private void BuildKeyMap()
    {
        _commands = Commands();
        _keyMap = new();
        foreach (var c in _commands)
            foreach (var k in c.Bindings)
                if (!_keyMap.TryAdd(k, c)) throw new InvalidOperationException($"{k} is bound twice");
        _overlay.HelpText = HelpText(_commands);
    }

    private Command[] Commands()
    {
        var palettes = _colours.Palettes;
        int Step() => (int)(Math.Min(ClientSize.Width, ClientSize.Height) * KeyPanFraction);

        Command C(string group, string shown, string text, Action run, params Keys[] keys) => new(group, shown, text, keys, run);
        Command Mouse(string group, string shown, string text) => new(group, shown, text, []);

        return
        [
            Mouse(Navigation, "Wheel", "Zoom at cursor"),
            Mouse(Navigation, "Left-drag", "Pan (only new edges are computed)"),
            Mouse(Navigation, "Middle-click", "Centre on point"),
            Mouse(Navigation, "Alt+click", "Profile: the side view of that row, height = iterations to escape"),
            new(Navigation, "Arrows / Shift", "Pan a step / a big step", [], null),
            C(Navigation, "", "", () => Pan(-Step(), 0), Keys.Left),
            C(Navigation, "", "", () => Pan(Step(), 0), Keys.Right),
            C(Navigation, "", "", () => Pan(0, -Step()), Keys.Up),
            C(Navigation, "", "", () => Pan(0, Step()), Keys.Down),
            C(Navigation, "", "", () => Pan(-4 * Step(), 0), Keys.Shift | Keys.Left),
            C(Navigation, "", "", () => Pan(4 * Step(), 0), Keys.Shift | Keys.Right),
            C(Navigation, "", "", () => Pan(0, -4 * Step()), Keys.Shift | Keys.Up),
            C(Navigation, "", "", () => Pan(0, 4 * Step()), Keys.Shift | Keys.Down),
            C(Navigation, "Home", "Reset view", ResetView, Keys.Home),
            C(Navigation, "+ / -", $"{IterationStep} more / fewer iterations", MoreIterations, Keys.Oemplus, Keys.Add, Keys.Shift | Keys.Oemplus),
            C(Navigation, "", "", FewerIterations, Keys.OemMinus, Keys.Subtract),
            C(Navigation, "A", "Auto iterations on/off (raises the limit when detail is cut off)", ToggleAutoIterations, Keys.A),
            C(Navigation, "Ctrl+C / Ctrl+V", "Copy / go to location (full precision, with mapping and density;\n" +
                                             "a pasted \"Snap\" line zooms in to the spiral centre or minibrot there)", CopyLocation, Keys.Control | Keys.C),
            C(Navigation, "", "", PasteLocation, Keys.Control | Keys.V),

            C(SnapGroup, "N / Shift+N", "Find the spiral centre or minibrot here (ask, with a preview / zoom straight in)",
              () => StartSnap(ask: true), Keys.N),
            C(SnapGroup, "", "", () => StartSnap(ask: false), Keys.Shift | Keys.N),
            Mouse(SnapGroup, "Ctrl+click", "Snap at the cursor: works on any structure (Ctrl+Shift+click zooms straight in)"),
            C(SnapGroup, "Space", "Pause / resume a snap zoom (while paused, the wheel moves along it)", PauseOrResumeDive, Keys.Space),
            C(SnapGroup, "Esc", "Stop a snap zoom (or close the picker, help, fullscreen)", Escape, Keys.Escape),

            C(Places, "L", "Places you've been (click one to go back; kept between sessions)", ShowPlaces, Keys.L),
            C(Places, "Ctrl+S", "Save this place now (otherwise saved after 5 s here, when you move on)", SavePlaceNow, Keys.Control | Keys.S),
            C(Places, "Backspace", "Back to the last place", GoBack, Keys.Back),
            C(Places, "Delete", "Forget this place (removed from the list, not saved when you leave)", ForgetPlace, Keys.Delete),

            C(Colour, "P", "Palette picker (hover previews)", TogglePicker, Keys.P),
            C(Colour, "PgUp / PgDn / 1-9", "Previous / next / pick palette",
              () => SelectPalette((_colours.PaletteIndex - 1 + palettes.Count) % palettes.Count), Keys.PageUp),
            C(Colour, "", "", () => SelectPalette((_colours.PaletteIndex + 1) % palettes.Count), Keys.PageDown),
            .. Enumerable.Range(0, 9).Select(i => C(Colour, "", "", () => { if (i < palettes.Count) SelectPalette(i); }, Keys.D1 + i)),
            C(Colour, "G", "Generate a random palette", GeneratePalette, Keys.G),
            C(Colour, "Ctrl+P", "Save the palette as .map (Documents\\Fractal\\Palettes)", SaveSelectedPalette, Keys.Control | Keys.P),
            C(Colour, "C / Shift+C", "Cycling on/off / reverse direction", ToggleCycling, Keys.C),
            C(Colour, "", "", ReverseCycling, Keys.Shift | Keys.C),
            C(Colour, ", / .", "Cycle slower / faster", () => ChangeCycleSpeed(1 / 1.25), Keys.Oemcomma),
            C(Colour, "", "", () => ChangeCycleSpeed(1.25), Keys.OemPeriod),
            Mouse(Colour, "Shift+Wheel", "Rotate the palette by hand"),
            C(Colour, "Ctrl+Wheel or [ ]", "Colour density", () => ChangeDensity(1 / 1.25), Keys.OemOpenBrackets),
            C(Colour, "", "", () => ChangeDensity(1.25), Keys.OemCloseBrackets),
            C(Colour, "M", "Mapping: Linear / Sqrt / Log (from the view's floor) / Equalize (equal share of pixels)", NextMapping, Keys.M),
            C(Colour, "F", "Fit: density so the palette runs twice across this view", FitColours, Keys.F),
            C(Colour, "V", "Colour explorer: this view in other colourings (click one to use it)", ShowColourExplorer, Keys.V),
            C(Colour, "E", "Effects for this image: relief, mirror; stripes, binary, boundary, inside by period\n" +
                         "(the last four compute extra numbers for the view when ticked)", ToggleEffectsPanel, Keys.E),

            C(Window, "F11 / Alt+Enter", "Fullscreen (or double-click; Esc exits)", ToggleFullScreen, Keys.F11, Keys.Alt | Keys.Enter),
            C(Window, "I", "Info on/off", () => { _showInfo = !_showInfo; Invalidate(); }, Keys.I),
            C(Window, "H / F1", "Help on/off", () => { _showHelp = !_showHelp; Invalidate(); }, Keys.H, Keys.F1),

            C(Engine, "F5", "GPU on/off for deep zoom (DirectX 12)", ToggleGpu, Keys.F5),
            C(Engine, "F6", "SIMD on/off (compare render times)", ToggleSimd, Keys.F6),
            C(Engine, "F7 / Shift+F7", "Interior fill on/off / show filled areas in green", ToggleInteriorFill, Keys.F7),
            C(Engine, "", "", ToggleShowFilled, Keys.Shift | Keys.F7),
            C(Engine, "F8", "Progressive rendering (preview + live fill-in) on/off", ToggleProgressive, Keys.F8),
            C(Engine, "F9", "Deep zoom: auto / always / off", NextDeepMode, Keys.F9),
        ];
    }

    /// <summary>The help screen: each group's commands, in table order. Rows with no text are extra keys for the row above.</summary>
    private static string HelpText(IEnumerable<Command> commands)
    {
        const int keyColumn = 20;
        var sb = new StringBuilder();
        foreach (var group in commands.GroupBy(c => c.Group))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(group.Key).Append('\n');
            foreach (var c in group.Where(c => c.Text.Length > 0))
            {
                var lines = c.Text.Split('\n');
                sb.Append("  ").Append(c.Shown.PadRight(keyColumn)).Append(lines[0]).Append('\n');
                foreach (var more in lines.Skip(1)) sb.Append(' ', 2 + keyColumn).Append(more).Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_keyMap.TryGetValue(keyData, out var c) && c.Run != null)
        {
            c.Run();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // =========================================================================
    // Commands too long for the table
    // =========================================================================

    private void Escape()
    {
        if (_dive.Active || _snapping) StopDive();
        else if (_overlay.PickerOpen) { _overlay.PickerOpen = false; ClearHover(); }
        else if (_showHelp) _showHelp = false;
        else if (_fullScreen) ToggleFullScreen();
        Invalidate();
    }

    private void PauseOrResumeDive()
    {
        if (_dive.Paused) ResumeDive();
        else if (_dive.Active) PauseDive();
    }

    private void ResetView()
    {
        StopDive();
        _view.Reset();
        RequestRender();
    }

    private void ToggleAutoIterations()
    {
        _autoIterations = !_autoIterations;
        ShowToast(_autoIterations ? "Auto iterations on" : "Auto iterations off");
        if (_autoIterations && !_pipeline.IsRunning && _pipeline.Field != null && MaybeRaiseIterations(_pipeline.Field))
            RequestRender();
    }

    private void MoreIterations()
    {
        _view.IterationBoost += IterationStep;
        ShowToast($"Iterations: {_view.MaxIterations}");
        RequestRender();
    }

    private void FewerIterations()
    {
        _view.LowerIterations(IterationStep);
        // Lowering by hand means "I want fewer": don't let auto push it straight back up.
        bool wasAuto = _autoIterations;
        _autoIterations = false;
        ShowToast($"Iterations: {_view.MaxIterations}" + (wasAuto ? "  (auto iterations off; A turns it on)" : ""));
        RequestRender();
    }

    private void TogglePicker()
    {
        _overlay.PickerOpen = !_overlay.PickerOpen;
        if (!_overlay.PickerOpen) ClearHover();
        Invalidate();
    }

    private void GeneratePalette()
    {
        var random = _colours.AddRandom();
        TouchPlace();
        ShowToast($"Palette: {random.Name} — Ctrl+P to save it");
        Recolor();
    }

    private void ToggleCycling()
    {
        _colours.Cycling = !_colours.Cycling;
        TouchPlace();
        ShowToast(_colours.Cycling ? "Colour cycling on" : "Colour cycling off");
    }

    private void ReverseCycling()
    {
        _colours.ReverseCycling();
        ShowToast(_colours.CycleDirection > 0 ? "Cycling forward" : "Cycling backward");
    }

    private void ChangeCycleSpeed(double factor)
    {
        _colours.ChangeCycleSpeed(factor);
        ShowToast($"Cycle speed: {_colours.CycleSpeed:0.##}/s");
    }

    private void NextMapping()
    {
        _colours.NextMapping();
        TouchPlace();
        ShowToast($"Colour mapping: {_colours.Mapping}");
        Recolor();
    }

    private void FitColours()
    {
        if (_colourScale == null) { ShowToast("Nothing to fit yet: wait for the render"); return; }
        if (Colorizer.FitDensity(_colours.Mapping, _colourScale) is not double d) { ShowToast("This view is all one value: nothing to fit"); return; }
        _colours.SetDensity(d);
        TouchPlace();
        ShowToast($"Fitted: density {_colours.Density:G3}x");
        Recolor();
    }

    private void ShowColourExplorer()
    {
        if (_baseLayer.Field is not { } field) { ShowToast("Wait for the render, then V"); return; }
        _overlay.PickerOpen = false;
        ClearHover();
        var current = new ColourChoice(_colours.Selected, _colours.Mapping, _colours.Density, _colours.Offset, _colours.Effects);
        using var dlg = new ColourExplorer(field, _colourScale, _extra, current, _colours.Palettes, _colours.MakeRandom, Bounds, _renderer.WorkerCount);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Chosen is not { } c) return;
        _colours.Adopt(c.Palette);
        _colours.Mapping = c.Mapping;
        _colours.SetDensity(c.Density);
        _colours.SetOffset(c.Offset);
        SetEffects(c.Effects);
        TouchPlace();
        ShowToast(c.Label, 3);
        Recolor();
    }

    private EffectsPanel? _effectsPanel;

    private void ToggleEffectsPanel()
    {
        if (_effectsPanel != null) { _effectsPanel.Close(); return; }
        var panel = new EffectsPanel(_colours.Effects);
        panel.Changed += e =>
        {
            _colours.Effects = e;
            TouchPlace();
            Recolor();
            // Ticking an effect that needs the extra numbers asks for them (with stripes, if ticked).
            if (ExtraMissing(e)) ComputeExtra();
            UpdateExtraStatus();
        };
        panel.ComputeClicked += compute => { if (compute) ComputeExtra(); else CancelExtra(); };
        panel.FormClosed += (_, _) => { _effectsPanel = null; Activate(); };
        _effectsPanel = panel;
        panel.Show(this);
        UpdateExtraStatus();
        // Top right of the window, inside it.
        var area = RectangleToScreen(ClientRectangle);
        panel.Location = new Point(area.Right - panel.Width - 16, area.Top + 16);
    }

    /// <summary>Changes the effects (from a place or the explorer), keeping an open Effects panel in step.</summary>
    private void SetEffects(Effects e)
    {
        _colours.Effects = e;
        _effectsPanel?.SetEffects(e);
    }

    private void ToggleGpu()
    {
        if (_renderer.Gpu == null) { ShowToast($"No GPU in use: {_renderer.GpuFailure ?? _gpuStatus}", 3); return; }
        _renderer.UseGpu = !_renderer.UseGpu;
        ShowToast(_renderer.UseGpu ? $"GPU on ({_renderer.Gpu.Name}) for deep zoom" : "GPU off (CPU for deep zoom)");
        if (_view.UseDeep) ForceFullRender();
    }

    private void ToggleSimd()
    {
        if (!MandelbrotRenderer.SimdSupported) { ShowToast("SIMD not supported on this CPU"); return; }
        _renderer.UseSimd = !_renderer.UseSimd;
        ShowToast(_renderer.UseSimd ? "SIMD on (AVX2, 8 px/step)" : "SIMD off (scalar)");
        ForceFullRender();
    }

    private void ToggleInteriorFill()
    {
        _renderer.UseInteriorFill = !_renderer.UseInteriorFill;
        ShowToast(_renderer.UseInteriorFill ? "Interior fill on" : "Interior fill off");
        ForceFullRender();
    }

    private void ToggleShowFilled()
    {
        _showFilled = !_showFilled;
        ShowToast(_showFilled ? "Showing filled (skipped) areas in green" : "Filled areas shown normally");
        Recolor();
    }

    private void ToggleProgressive()
    {
        _pipeline.Progressive = !_pipeline.Progressive;
        if (!_pipeline.Progressive) { _previewLayer.Field = null; _partialLayer.Field = null; }
        ShowToast(_pipeline.Progressive ? "Progressive rendering on" : "Progressive rendering off");
        Invalidate();
    }

    private void NextDeepMode()
    {
        _view.DeepMode = (DeepMode)(((int)_view.DeepMode + 1) % 3);
        ShowToast(_view.DeepMode switch
        {
            DeepMode.Auto => $"Deep zoom: auto (perturbation below {Mandelbrot.DeepThreshold:0e0} per pixel)",
            DeepMode.Always => "Deep zoom: always perturbation",
            _ => "Deep zoom: off (plain doubles - blocky when deep)",
        }, 2.5);
        RequestRender();
    }
}
