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

    // Preview of where it ends: rendered in the background, again whenever the zoom is changed.
    private const int PreviewWidth = 560;
    private readonly Func<double, CancellationToken, Bitmap?>? _render;
    private readonly PictureBox _picture;
    private readonly Label _previewText;
    private readonly System.Windows.Forms.Timer _rerender = new() { Interval = 600 };
    private CancellationTokenSource? _previewCts;
    private double _previewZoom = double.NaN;

    /// <param name="render">Renders the view at a given zoom (on a worker thread), or null for no preview.</param>
    /// <param name="aspect">Height / width of the window, for the preview's shape.</param>
    public SnapDialog(string description, double distanceInViews, double targetZoom, double secondsPerDoubling,
                      Func<double, CancellationToken, Bitmap?>? render = null, double aspect = 9.0 / 16)
    {
        _render = render;
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

        _picture = new PictureBox
        {
            Size = new Size(PreviewWidth, (int)Math.Round(PreviewWidth * Math.Clamp(aspect, 0.3, 1.5))),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Margin = new Padding(0, 0, 0, 4),
            Visible = render != null,
        };
        _previewText = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 10), Visible = render != null };
        layout.Controls.Add(_picture, 0, 1);
        layout.SetColumnSpan(_picture, 2);
        layout.Controls.Add(_previewText, 0, 2);
        layout.SetColumnSpan(_previewText, 2);

        _zoom = new TextBox { Text = targetZoom.ToString("0.###e0", inv), Width = 120 };
        _speed = new TextBox { Text = secondsPerDoubling.ToString("0.###", inv), Width = 120 };
        layout.Controls.Add(new Label { Text = "Zoom to:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(_zoom, 1, 3);
        layout.Controls.Add(new Label { Text = "Seconds per doubling:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        layout.Controls.Add(_speed, 1, 4);

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
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);

        if (render != null)
        {
            _rerender.Tick += (_, _) => { _rerender.Stop(); StartPreview(); };
            _zoom.TextChanged += (_, _) => { _rerender.Stop(); _rerender.Start(); };
            Shown += (_, _) => StartPreview();
        }
    }

    private static bool TryParseZoom(string text, out double zoom) =>
        double.TryParse(text.Trim().TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out zoom) && zoom > 0;

    /// <summary>Renders the view at the zoom in the box, unless that's already shown or on its way.</summary>
    private void StartPreview()
    {
        if (_render == null || !TryParseZoom(_zoom.Text, out double zoom) || zoom == _previewZoom) return;
        _previewZoom = zoom;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        _previewText.Text = $"Rendering where it ends, at {zoom:G3}x...";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var render = _render;
        Task.Run(() => render(zoom, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || IsDisposed) { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); return; }
            if (t.IsFaulted)
            {
                _previewText.Text = "Couldn't render a preview: " + t.Exception?.GetBaseException().Message;
                return;
            }
            if (t.IsCanceled || t.Result is not { } bmp) return;
            var old = _picture.Image;
            _picture.Image = bmp;
            old?.Dispose();
            _previewText.Text = $"Where it ends, at {zoom:G3}x (rendered in {sw.Elapsed.TotalSeconds:0.0} s)";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _previewCts?.Cancel();
        _rerender.Dispose();
        _picture.Image?.Dispose();
        base.OnFormClosed(e);
    }

    private void Finish(Choice choice)
    {
        var inv = CultureInfo.InvariantCulture;
        if (choice is Choice.Dive or Choice.Jump)
        {
            if (!TryParseZoom(_zoom.Text, out double z))
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
