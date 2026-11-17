namespace Fractal;

/// <summary>
/// The effects for the image on screen (E), with their settings. A small window that stays open
/// beside the fractal. The first group shows at once (it only recolours). The second needs
/// extra numbers per pixel, which take about as long as a CPU render of the view, so they are
/// computed only when you press Compute; until then those effects don't show.
/// </summary>
internal sealed class EffectsPanel : Form
{
    /// <summary>Raised on every change, with the effects as they now are.</summary>
    public event Action<Effects>? Changed;

    /// <summary>Compute (true) or Cancel (false) pressed.</summary>
    public event Action<bool>? ComputeClicked;

    private readonly CheckBox _relief, _outline, _mirror, _stripes, _angle, _binary, _boundary, _inside;
    private readonly TrackBar _reliefStrength, _light, _outlineStrength, _stripeStrength, _angleStrength, _boundaryWidth;
    private readonly Label _reliefValue, _lightValue, _outlineValue, _stripeValue, _angleValue, _boundaryValue;
    private readonly Label _status;
    private readonly Button _compute;
    private bool _computing;
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

        var heading = new Label
        {
            Text = "NEED EXTRA NUMBERS (computed when you press Compute)",
            AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 18, 0, 0),
        };
        layout.Controls.Add(heading, 0, row);
        layout.SetColumnSpan(heading, 3);
        row++;
        _stripes = Check("&Stripes", "Silky ribbons along the filaments (stripe average). Computing them takes longest");
        (_stripeStrength, _stripeValue) = Slider("Strength", 1, 40, 5);
        _angle = Check("&Angle", "Turn the palette by the angle each point escaped at: swirls in calm areas");
        (_angleStrength, _angleValue) = Slider("Strength", 1, 40, 5);
        _binary = Check("&Binary", "Two-tone cells by which half-plane each point escaped into");
        _boundary = Check("Bo&undary", "Crisp dark lines along the set's boundary (distance estimate)");
        (_boundaryWidth, _boundaryValue) = Slider("Width", 1, 24, 2); // quarter pixels
        _inside = Check("&Inside by period", "Colour the inside of the set by period: each bulb and minibrot its own colour");

        var computeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
        _compute = new Button { Text = "&Compute", AutoSize = true };
        _compute.Click += (_, _) => ComputeClicked?.Invoke(!_computing);
        _status = new Label { AutoSize = true, MaximumSize = new Size(300, 0), Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 0, 0) };
        computeRow.Controls.Add(_compute);
        computeRow.Controls.Add(_status);
        layout.Controls.Add(computeRow, 0, row);
        layout.SetColumnSpan(computeRow, 3);
        row++;

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
        _stripes.Checked = e.Stripes;
        _stripeStrength.Value = Math.Clamp((int)Math.Round(e.StripeStrength * 10), _stripeStrength.Minimum, _stripeStrength.Maximum);
        _angle.Checked = e.Angle;
        _angleStrength.Value = Math.Clamp((int)Math.Round(e.AngleStrength * 10), _angleStrength.Minimum, _angleStrength.Maximum);
        _binary.Checked = e.Binary;
        _boundary.Checked = e.Boundary;
        _boundaryWidth.Value = Math.Clamp((int)Math.Round(e.BoundaryWidth * 4), _boundaryWidth.Minimum, _boundaryWidth.Maximum);
        _inside.Checked = e.InsidePeriod;
        _setting = false;
        UpdateLabels();
    }

    private Effects Current() => new(
        _relief.Checked, _reliefStrength.Value / 10.0, _light.Value * 15.0,
        _outline.Checked, _outlineStrength.Value / 10.0,
        _mirror.Checked,
        _stripes.Checked, _stripeStrength.Value / 10.0,
        _angle.Checked, _angleStrength.Value / 10.0,
        _binary.Checked,
        _boundary.Checked, _boundaryWidth.Value / 4.0,
        _inside.Checked);

    /// <summary>The line beside Compute; <paramref name="computing"/> turns the button into Cancel.</summary>
    public void SetStatus(string text, bool computing)
    {
        if (_status.Text != text) _status.Text = text;
        _computing = computing;
        string label = computing ? "&Cancel" : "&Compute";
        if (_compute.Text != label) _compute.Text = label;
    }

    private void Raise()
    {
        UpdateLabels();
        if (!_setting) Changed?.Invoke(Current());
    }

    private void UpdateLabels()
    {
        _reliefValue.Text = $"{_reliefStrength.Value / 10.0:0.0}";
        _outlineValue.Text = $"{_outlineStrength.Value / 10.0:0.0}";
        _stripeValue.Text = $"{_stripeStrength.Value / 10.0:0.0}";
        _angleValue.Text = $"{_angleStrength.Value / 10.0:0.0}";
        _boundaryValue.Text = $"{_boundaryWidth.Value / 4.0:0.##} px";
        int deg = _light.Value * 15;
        string[] names = ["right", "top right", "top", "top left", "left", "bottom left", "bottom", "bottom right"];
        _lightValue.Text = deg % 45 == 0 ? names[deg / 45] : $"{deg}°";
        bool relief = _relief.Checked, outline = _outline.Checked;
        _reliefStrength.Enabled = _light.Enabled = relief;
        _outlineStrength.Enabled = outline;
        _stripeStrength.Enabled = _stripes.Checked;
        _angleStrength.Enabled = _angle.Checked;
        _boundaryWidth.Enabled = _boundary.Checked;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.E) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
