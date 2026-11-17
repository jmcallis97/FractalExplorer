namespace Fractal;

/// <summary>
/// The effects for the image on screen (E): relief lighting, outlines and a mirrored palette,
/// each with its settings. A small window that stays open beside the fractal; every change
/// shows at once (effects only recolour, nothing is re-rendered).
/// </summary>
internal sealed class EffectsPanel : Form
{
    /// <summary>Raised on every change, with the effects as they now are.</summary>
    public event Action<Effects>? Changed;

    private readonly CheckBox _relief, _outline, _mirror;
    private readonly TrackBar _reliefStrength, _light, _outlineStrength;
    private readonly Label _reliefValue, _lightValue, _outlineValue;
    private bool _setting; // true while controls are being set from code: don't raise Changed

    public EffectsPanel(Effects current)
    {
        Text = "Effects";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        KeyPreview = true;

        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        int row = 0;

        CheckBox Check(string text, string tip)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, row == 0 ? 0 : 10, 0, 0) };
            new ToolTip().SetToolTip(c, tip);
            c.CheckedChanged += (_, _) => Raise();
            layout.Controls.Add(c, 0, row);
            layout.SetColumnSpan(c, 3);
            row++;
            return c;
        }
        (TrackBar, Label) Slider(string name, int min, int max, int tick)
        {
            var bar = new TrackBar { Minimum = min, Maximum = max, TickFrequency = tick, Width = 240, AutoSize = true };
            var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(48, 0) };
            bar.ValueChanged += (_, _) => Raise();
            layout.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(20, 0, 6, 0) }, 0, row);
            layout.Controls.Add(bar, 1, row);
            layout.Controls.Add(value, 2, row);
            row++;
            return (bar, value);
        }

        _relief = Check("&Relief", "Light the image as if the colours were the height of a landscape");
        (_reliefStrength, _reliefValue) = Slider("Strength", 1, 40, 5);
        (_light, _lightValue) = Slider("Light from", 0, 23, 2); // in steps of 15 degrees
        _outline = Check("&Outline", "Darken where the values change fast: filaments and edges");
        (_outlineStrength, _outlineValue) = Slider("Strength", 1, 40, 5);
        _mirror = Check("&Mirror palette", "Run the palette forward then back, so it has no seam where it repeats");

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        var none = new Button { Text = "&None", AutoSize = true };
        none.Click += (_, _) => { SetEffects(Effects.None); Raise(); };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(none);
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 3);

        Controls.Add(layout);
        SetEffects(current);
    }

    /// <summary>Shows <paramref name="e"/> (e.g. after going to a place) without raising <see cref="Changed"/>.</summary>
    public void SetEffects(Effects e)
    {
        _setting = true;
        _relief.Checked = e.Relief;
        _reliefStrength.Value = Math.Clamp((int)Math.Round(e.ReliefStrength * 10), _reliefStrength.Minimum, _reliefStrength.Maximum);
        _light.Value = Math.Clamp((int)Math.Round(((e.LightAngle % 360 + 360) % 360) / 15), _light.Minimum, _light.Maximum);
        _outline.Checked = e.Outline;
        _outlineStrength.Value = Math.Clamp((int)Math.Round(e.OutlineStrength * 10), _outlineStrength.Minimum, _outlineStrength.Maximum);
        _mirror.Checked = e.Mirror;
        _setting = false;
        UpdateLabels();
    }

    private Effects Current() => new(
        _relief.Checked, _reliefStrength.Value / 10.0, _light.Value * 15.0,
        _outline.Checked, _outlineStrength.Value / 10.0,
        _mirror.Checked);

    private void Raise()
    {
        UpdateLabels();
        if (!_setting) Changed?.Invoke(Current());
    }

    private void UpdateLabels()
    {
        _reliefValue.Text = $"{_reliefStrength.Value / 10.0:0.0}";
        _outlineValue.Text = $"{_outlineStrength.Value / 10.0:0.0}";
        int deg = _light.Value * 15;
        string[] names = ["right", "top right", "top", "top left", "left", "bottom left", "bottom", "bottom right"];
        _lightValue.Text = deg % 45 == 0 ? names[deg / 45] : $"{deg}°";
        bool relief = _relief.Checked, outline = _outline.Checked;
        _reliefStrength.Enabled = _light.Enabled = relief;
        _outlineStrength.Enabled = outline;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.E) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
