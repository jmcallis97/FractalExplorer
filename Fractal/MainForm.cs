using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Fractal;

public sealed class MainForm : Form
{
    // ---- Tunables -------------------------------------------------------
    /// <summary>Zoom multiplier per scroll-wheel notch. 2.0 = each notch halves the view width.</summary>
    private const double ZoomPerNotch = 1.5;
    private const int BaseIterations = 200;
    // ---------------------------------------------------------------------

    private static readonly double InitialCenterX = -0.5;
    private static readonly double InitialCenterY = 0.0;
    private static readonly double InitialViewWidth = 3.5;

    private readonly MandelbrotRenderer _renderer = new();

    private double _centerX = InitialCenterX;
    private double _centerY = InitialCenterY;
    private double _scale; // complex units per pixel
    private int _iterationBoost; // user adjustment via +/- keys

    private Bitmap? _image;
    private CancellationTokenSource? _renderCts;
    private int _renderGeneration;
    private long _lastRenderMs;
    private bool _showInfo = true;

    // Fullscreen state
    private bool _fullScreen;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
    private Rectangle _savedBounds;

    public MainForm()
    {
        Text = "Mandelbrot";
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _scale = InitialViewWidth / ClientSize.Width;
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RequestRender();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ClientSize.Width > 0 && ClientSize.Height > 0)
            RequestRender();
    }

    // ---- Input ----------------------------------------------------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        double notches = e.Delta / 120.0; // positive = wheel forward = zoom in
        double factor = Math.Pow(ZoomPerNotch, notches);

        // Keep the complex point under the cursor fixed on screen.
        double mx = e.X - ClientSize.Width / 2.0;
        double my = e.Y - ClientSize.Height / 2.0;
        double pointX = _centerX + mx * _scale;
        double pointY = _centerY + my * _scale;

        double newScale = _scale / factor;
        if (newScale < 1e-16) newScale = 1e-16; // double precision floor

        _centerX = pointX - mx * newScale;
        _centerY = pointY - my * newScale;

        // Instant feedback: scale the current bitmap around the cursor while the real render runs.
        ZoomPreview(e.Location, _scale / newScale);

        _scale = newScale;
        RequestRender();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Middle)
        {
            // Center the view on the clicked point.
            _centerX += (e.X - ClientSize.Width / 2.0) * _scale;
            _centerY += (e.Y - ClientSize.Height / 2.0) * _scale;
            RequestRender();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) ToggleFullScreen();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F11:
            case Keys.Alt | Keys.Enter:
                ToggleFullScreen();
                return true;
            case Keys.Escape:
                if (_fullScreen) ToggleFullScreen();
                return true;
            case Keys.R:
            case Keys.Home:
                ResetView();
                return true;
            case Keys.Oemplus:
            case Keys.Add:
            case Keys.Shift | Keys.Oemplus:
                _iterationBoost += 100;
                RequestRender();
                return true;
            case Keys.OemMinus:
            case Keys.Subtract:
                _iterationBoost -= 100;
                RequestRender();
                return true;
            case Keys.I:
                _showInfo = !_showInfo;
                Invalidate();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ResetView()
    {
        _centerX = InitialCenterX;
        _centerY = InitialCenterY;
        _scale = InitialViewWidth / Math.Max(1, ClientSize.Width);
        _iterationBoost = 0;
        RequestRender();
    }

    // ---- Fullscreen -----------------------------------------------------

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedBorder = FormBorderStyle;
            _savedState = WindowState;
            _savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

            // Must go Normal first so the maximize covers the taskbar once borderless.
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

    // ---- Rendering ------------------------------------------------------

    private void RequestRender()
    {
        int width = ClientSize.Width, height = ClientSize.Height;
        if (width <= 0 || height <= 0) return;

        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        int generation = ++_renderGeneration;

        double cx = _centerX, cy = _centerY, scale = _scale;
        int maxIter = MaxIterations;

        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var pixels = new int[width * height];
            try
            {
                _renderer.Render(pixels, width, height, cx, cy, scale, maxIter, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            sw.Stop();

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                if (data.Stride == width * 4)
                {
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(pixels, y * width, data.Scan0 + y * data.Stride, width);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            if (IsDisposed) { bmp.Dispose(); return; }
            try
            {
                BeginInvoke(() =>
                {
                    if (generation != _renderGeneration) { bmp.Dispose(); return; } // stale
                    var old = _image;
                    _image = bmp;
                    old?.Dispose();
                    _lastRenderMs = sw.ElapsedMilliseconds;
                    Invalidate();
                });
            }
            catch (InvalidOperationException)
            {
                bmp.Dispose(); // form closing
            }
        });
    }

    /// <summary>Scales the existing image around a point so zooming feels immediate.</summary>
    private void ZoomPreview(Point anchor, double factor)
    {
        if (_image == null) return;
        var preview = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(preview))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            float w = (float)(_image.Width * factor);
            float h = (float)(_image.Height * factor);
            float x = (float)(anchor.X - anchor.X * factor);
            float y = (float)(anchor.Y - anchor.Y * factor);
            g.DrawImage(_image, x, y, w, h);
        }
        _image.Dispose();
        _image = preview;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_image != null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(_image, 0, 0);
        }
        else
        {
            g.Clear(Color.Black);
        }

        if (_showInfo) DrawInfo(g);
    }

    private void DrawInfo(Graphics g)
    {
        string text =
            $"Center: {_centerX:R}, {_centerY:R}\n" +
            $"Zoom: {Zoom:G4}x   Iterations: {MaxIterations}\n" +
            $"Threads: {_renderer.WorkerCount} of {Environment.ProcessorCount} cores   Render: {_lastRenderMs} ms\n" +
            "Wheel: zoom   Middle-click: center   F11/dbl-click: fullscreen   +/-: iterations   R: reset   I: hide info";

        using var font = new Font("Consolas", 9f);
        var size = g.MeasureString(text, font);
        using var bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        g.FillRectangle(bg, 8, 8, size.Width + 8, size.Height + 8);
        g.DrawString(text, font, Brushes.White, 12, 12);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _renderCts?.Cancel();
        _image?.Dispose();
        base.OnFormClosed(e);
    }
}
