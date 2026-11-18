namespace Fractal;

/// <summary>
/// The effects for the image on screen (E), each with what it does and what it looks like. A
/// small window that stays open beside the fractal. The first group shows at once (it only
/// recolours). The second needs extra numbers per pixel, which take about as long as a CPU
/// render of the view: ticking one of them computes them for the view on screen.
/// </summary>
internal sealed class EffectsPanel : Form
{
    private const int TextWidth = 380;

    /// <summary>Raised on every change, with the effects as they now are.</summary>
    public event Action<Effects>? Changed;

    /// <summary>Compute (true) or Cancel (false) pressed.</summary>
    public event Action<bool>? ComputeClicked;

    private readonly CheckBox _relief, _mirror, _stripes, _binary, _boundary, _inside;
    private readonly TrackBar _reliefStrength, _light, _stripeStrength, _boundaryWidth;
    private readonly Label _reliefValue, _lightValue, _stripeValue, _boundaryValue;
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
        var small = new Font(Font.FontFamily, Font.Size * 0.95f);

        void Span(Control c)
        {
            layout.Controls.Add(c, 0, row);
            layout.SetColumnSpan(c, 3);
            row++;
        }
        void Heading(string text, int top) =>
            Span(new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, top, 0, 2) });
        CheckBox Effect(string name, string does, string looks)
        {
            var c = new CheckBox { Text = name, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 0) };
            c.CheckedChanged += (_, _) => Raise();
            Span(c);
            Span(new Label
            {
                Text = $"Does: {does}\nLooks: {looks}",
                AutoSize = true, MaximumSize = new Size(TextWidth, 0), ForeColor = SystemColors.GrayText, Font = small,
                Margin = new Padding(18, 0, 0, 2),
            });
            return c;
        }
        (TrackBar, Label) Slider(string name, int min, int max, int tick)
        {
            var bar = new TrackBar { Minimum = min, Maximum = max, TickFrequency = tick, Width = 240, AutoSize = true };
            var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(60, 0) };
            bar.ValueChanged += (_, _) => Raise();
            layout.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(18, 0, 6, 0) }, 0, row);
            layout.Controls.Add(bar, 1, row);
            layout.Controls.Add(value, 2, row);
            row++;
            return (bar, value);
        }

        Heading("INSTANT", 0);
        _relief = Effect("&Relief",
            "treats each pixel's place on the palette (its mapped iteration count) as a height, and lights that " +
            "landscape from one side: slopes facing the light are brightened, slopes facing away darkened. Shading " +
            "appears where the count changes quickly, so most of it rises towards the set.",
            "embossed, 3-D bands; the detail near the set stands on high ground and throws a shadow on its far side.");
        (_reliefStrength, _reliefValue) = Slider("Strength", 1, 40, 5);
        (_light, _lightValue) = Slider("Light from", 0, 23, 2); // in steps of 15 degrees
        _mirror = Effect("&Mirror palette",
            "runs the palette start → end → start, instead of jumping from its end straight back to its start " +
            "each time it repeats. (Not a reversal: the order of colours is kept, then retraced.)",
            "no hard seam where the palette repeats; bands come in mirrored pairs and look softer.");

        Heading("COMPUTED WHEN TICKED (about as long as a CPU render of the view)", 18);
        _stripes = Effect("&Stripes",
            "averages sin(4 × the angle of z) over every step of each point's orbit (stripe-average colouring) " +
            "and shades by it.",
            "silky ribbons flowing along the filaments and through the calm ground between them.");
        (_stripeStrength, _stripeValue) = Slider("Strength", 1, 40, 5);
        _binary = Effect("&Binary",
            "darkens the points whose z escaped into the lower half-plane (binary decomposition).",
            "a checkerboard of cells following the bands: tidy in calm ground, chaotic in the dust.");
        _boundary = Effect("Bo&undary",
            "estimates each point's distance to the set from the derivative of its orbit, and darkens the points " +
            "closer than the line width.",
            "crisp dark lines tracing the set's edge. Best in shallow or calm views: in very dusty deep ones, " +
            "almost everything is near the edge.");
        (_boundaryWidth, _boundaryValue) = Slider("Width", 1, 24, 2); // quarter pixels
        _inside = Effect("&Inside by period",
            "for points inside the set (normally black), finds the period of the cycle the orbit settles into, " +
            "and gives each period its own colour.",
            "each bulb and minibrot a flat colour of its own. Only shows where the view has black inside parts.");

        var computeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0), WrapContents = false };
        _compute = new Button { Text = "&Compute", AutoSize = true };
        _compute.Click += (_, _) => ComputeClicked?.Invoke(!_computing);
        _status = new Label { AutoSize = true, MaximumSize = new Size(TextWidth - 90, 0), Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 0, 0) };
        computeRow.Controls.Add(_compute);
        computeRow.Controls.Add(_status);
        Span(computeRow);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        var none = new Button { Text = "&None", AutoSize = true };
        none.Click += (_, _) => { SetEffects(Effects.None); Raise(); };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(none);
        buttons.Controls.Add(close);
        Span(buttons);

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
        _mirror.Checked = e.Mirror;
        _stripes.Checked = e.Stripes;
        _stripeStrength.Value = Math.Clamp((int)Math.Round(e.StripeStrength * 10), _stripeStrength.Minimum, _stripeStrength.Maximum);
        _binary.Checked = e.Binary;
        _boundary.Checked = e.Boundary;
        _boundaryWidth.Value = Math.Clamp((int)Math.Round(e.BoundaryWidth * 4), _boundaryWidth.Minimum, _boundaryWidth.Maximum);
        _inside.Checked = e.InsidePeriod;
        _setting = false;
        UpdateLabels();
    }

    private Effects Current() => new(
        _relief.Checked, _reliefStrength.Value / 10.0, _light.Value * 15.0,
        _mirror.Checked,
        _stripes.Checked, _stripeStrength.Value / 10.0,
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
        _stripeValue.Text = $"{_stripeStrength.Value / 10.0:0.0}";
        _boundaryValue.Text = $"{_boundaryWidth.Value / 4.0:0.##} px";
        int deg = _light.Value * 15;
        string[] names = ["right", "top right", "top", "top left", "left", "bottom left", "bottom", "bottom right"];
        _lightValue.Text = deg % 45 == 0 ? names[deg / 45] : $"{deg}°";
        _reliefStrength.Enabled = _light.Enabled = _relief.Checked;
        _stripeStrength.Enabled = _stripes.Checked;
        _boundaryWidth.Enabled = _boundary.Checked;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.E) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
