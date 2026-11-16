using System.Globalization;

namespace Fractal;

/// <summary>
/// Asks what to do with a point found by snapping: zoom in to it (animated), jump straight
/// there, or just centre on it at the current zoom.
/// </summary>
public sealed class SnapDialog : Form
{
    public enum Choice { Cancel, Dive, Jump, Centre }

    public Choice Result { get; private set; } = Choice.Cancel;
    public double TargetZoom { get; private set; }
    public double SecondsPerDoubling { get; private set; }

    private readonly TextBox _zoom, _speed;

    public SnapDialog(string description, double distanceInViews, double targetZoom, double secondsPerDoubling)
    {
        Text = "Snap";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        var inv = CultureInfo.InvariantCulture;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        string where = distanceInViews < 0.01 ? "right at the centre of the view"
                     : $"{distanceInViews:G2} view widths from the centre";
        var info = new Label { Text = $"{description}\nIt is {where}.", AutoSize = true, MaximumSize = new Size(460, 0),
                               Margin = new Padding(0, 0, 0, 10) };
        layout.Controls.Add(info, 0, 0);
        layout.SetColumnSpan(info, 2);

        _zoom = new TextBox { Text = targetZoom.ToString("0.###e0", inv), Width = 120 };
        _speed = new TextBox { Text = secondsPerDoubling.ToString("0.###", inv), Width = 120 };
        layout.Controls.Add(new Label { Text = "Zoom to:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_zoom, 1, 1);
        layout.Controls.Add(new Label { Text = "Seconds per doubling:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_speed, 1, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0) };
        Button Add(string text, Choice choice)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => Finish(choice);
            buttons.Controls.Add(b);
            return b;
        }
        AcceptButton = Add("&Zoom in to it", Choice.Dive);
        Add("&Jump there", Choice.Jump);
        Add("&Centre on it", Choice.Centre);
        var cancel = Add("Cancel", Choice.Cancel);
        CancelButton = cancel;
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
    }

    private void Finish(Choice choice)
    {
        var inv = CultureInfo.InvariantCulture;
        if (choice is Choice.Dive or Choice.Jump)
        {
            if (!double.TryParse(_zoom.Text.Trim().TrimEnd('x', 'X'), NumberStyles.Float, inv, out double z) || z <= 0)
            {
                MessageBox.Show(this, "Zoom should be a number such as 1e100.", "Snap");
                return;
            }
            TargetZoom = z;
        }
        if (choice == Choice.Dive)
        {
            if (!double.TryParse(_speed.Text.Trim(), NumberStyles.Float, inv, out double s) || s <= 0)
            {
                MessageBox.Show(this, "Seconds per doubling should be a positive number such as 0.5.", "Snap");
                return;
            }
            SecondsPerDoubling = s;
        }
        Result = choice;
        DialogResult = choice == Choice.Cancel ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }
}
