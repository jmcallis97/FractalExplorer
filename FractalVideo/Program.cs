// Zoom video renderer: a straight dive into a location copied with Ctrl+C in the app.
//
//   FractalVideo <location.txt> <out.mp4> [options]     (run with no arguments for the options)
//
// How it works: one keyframe is rendered at every doubling of zoom, at twice the video's
// resolution. Each video frame is a zoomed-in crop of the keyframe at or below its zoom, with
// the next keyframe (twice as deep, so sharper) blended over the middle, where it fits.
// Every frame is therefore built from 1-4 samples per pixel (anti-aliased), and only one
// fractal render is needed per doubling instead of one per frame. Frames are streamed to
// ffmpeg, so nothing large is written to disk.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Fractal;
using Fractal.Deep;
using Fractal.Gpu;

var opt = Options.Parse(args);
if (opt == null) return 1;

if (!Location.TryParse(File.ReadAllText(opt.LocationFile), out var loc, out string locError))
{
    Console.Error.WriteLine($"Can't read location from {opt.LocationFile}: {locError}");
    return 1;
}

string? ffmpeg = FindFfmpeg(opt.Ffmpeg);
if (ffmpeg == null)
{
    Console.Error.WriteLine("ffmpeg not found. Install it (winget install Gyan.FFmpeg) or pass --ffmpeg <path>.");
    return 1;
}

var palette = LoadPalette(opt.Palette);
if (palette == null) return 1;

int W = opt.Width, H = opt.Height;  // video
int KW = 2 * W, KH = 2 * H;         // keyframes
double zStart = Math.Log2(opt.StartZoom);
double zEnd = Math.Log2(opt.EndZoom ?? loc.Zoom);
int lastKey = (int)Math.Floor(zEnd) + 1;           // keyframes 0..lastKey (zoom 2^k)
int firstKey = Math.Max(0, (int)Math.Floor(zStart));

// Iteration limit: the app's rule (grows 100 per doubling), shifted so the final frame gets
// the limit that was in use when the location was copied.
const int BaseIterations = 200;
int boost = loc.Iterations is int li ? li - (int)(BaseIterations + 100 * zEnd) : 0;
int MaxIterFor(int k) => Math.Max(50, BaseIterations + 100 * k + boost);

// All deep keyframes share one anchor (the target, at the precision the deepest one needs).
double deepestScale = Location.ViewWidthAtZoom1 / (Math.Pow(2, lastKey) * KW);
int bits = BigFixed.BitsFor(deepestScale);
var anchor = new DeepAnchor(BigFixed.Parse(loc.Re, bits), BigFixed.Parse(loc.Im, bits));
double ax = anchor.X.ToDouble(), ay = anchor.Y.ToDouble();
const double DeepThreshold = 1e-13; // same switch-over as the app

var gpu = GpuDeep.TryCreate(out string gpuWhy);
var renderer = new MandelbrotRenderer { Gpu = gpu };
int workers = renderer.WorkerCount;

int holdStart = (int)Math.Round(opt.HoldStart * opt.Fps);
int zoomFrames = Math.Max(1, (int)Math.Round((zEnd - zStart) * opt.SecondsPerDoubling * opt.Fps));
int holdEnd = (int)Math.Round(opt.HoldEnd * opt.Fps);
int mainFrames = holdStart + zoomFrames + 1 + holdEnd;
int finaleFrames = (int)Math.Round(opt.FinaleSeconds * opt.Fps);
int totalFrames = mainFrames + finaleFrames;

Console.WriteLine($"Target: zoom {Math.Pow(2, zEnd):G4} (2^{zEnd:0.0}), max iterations {MaxIterFor(lastKey)}");
Console.WriteLine($"Video:  {W}x{H} @ {opt.Fps} fps, {totalFrames} frames ({totalFrames / (double)opt.Fps:0.0} s), " +
                  $"{opt.SecondsPerDoubling} s per doubling");
Console.WriteLine($"Keyframes: {lastKey - firstKey + 1} at {KW}x{KH}, on {(gpu != null ? gpu.Name : $"CPU ({gpuWhy})")}");
Console.WriteLine($"Palette: {palette.Name}, {opt.Mapping}, density {opt.Density}, offset {opt.Offset}");
Console.WriteLine($"Encoder: {ffmpeg} ({opt.Encoder})\n");

// Keep the machine awake: a long render left alone must not stop when Windows goes to sleep.
NativeMethods.SetThreadExecutionState(NativeMethods.EsContinuous | NativeMethods.EsSystemRequired);

// ---- Keyframe producer (renders ahead while frames are being composed and encoded) ----------

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping..."); };
var keyQueue = new BlockingCollection<Keyframe>(boundedCapacity: 2);
var clock = Stopwatch.StartNew();
double renderSeconds = 0;

var producer = Task.Run(() =>
{
    try
    {
        for (int k = firstKey; k <= lastKey && !cts.IsCancellationRequested; k++)
        {
            var sw = Stopwatch.StartNew();
            double scale = Location.ViewWidthAtZoom1 / (Math.Pow(2, k) * KW);
            var view = new Viewport(ax, ay, scale, 0, 0, KW, KH, MaxIterFor(k), scale < DeepThreshold ? anchor : null);
            var field = new Field(view, new float[KW * KH]);
            renderer.Render(field, null, new RenderProgress(), cts.Token);
            var full = new int[KW * KH];
            Colorizer.Colorize(field, palette, opt.Mapping, opt.Density, opt.Offset, full, workers);
            // The last two keep their iteration data, so the finale can recolour them.
            var key = new Keyframe(k, full, HalfSize(full, KW, KH), k >= lastKey - 1 ? field : null);
            renderSeconds += sw.Elapsed.TotalSeconds;
            keyQueue.Add(key, cts.Token);
        }
    }
    catch (OperationCanceledException) { }
    finally { keyQueue.CompleteAdding(); }
});

// ---- ffmpeg -------------------------------------------------------------------------------

string codec = opt.Encoder == "nvenc"
    ? $"-c:v h264_nvenc -preset p6 -rc vbr -cq {opt.Crf} -b:v 0"
    : $"-c:v libx264 -preset {opt.Preset} -crf {opt.Crf}";
var psi = new ProcessStartInfo(ffmpeg,
    $"-y -loglevel error -f rawvideo -pix_fmt bgra -s {W}x{H} -r {opt.Fps} -i - " +
    $"{codec} -pix_fmt yuv420p -movflags +faststart \"{opt.Output}\"")
{
    RedirectStandardInput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
using var enc = Process.Start(psi)!;
var errors = new System.Text.StringBuilder();
enc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
enc.BeginErrorReadLine();
var encIn = enc.StandardInput.BaseStream;

// ---- Compose frames -----------------------------------------------------------------------

var keys = new Dictionary<int, Keyframe>();
var frame = new int[W * H];
long lastReport = 0;

Keyframe? GetKey(int k)
{
    while (!keys.ContainsKey(k))
    {
        if (!keyQueue.TryTake(out var next, Timeout.Infinite)) return null;
        keys[next.K] = next;
    }
    foreach (int old in keys.Keys.Where(x => x < k - 1).ToList()) keys.Remove(old);
    return keys[k];
}

void Report(int f, string where)
{
    if (clock.ElapsedMilliseconds - lastReport < 2000 && f != totalFrames - 1) return;
    lastReport = clock.ElapsedMilliseconds;
    double done = (f + 1.0) / totalFrames;
    double elapsed = clock.Elapsed.TotalSeconds;
    var eta = TimeSpan.FromSeconds(elapsed / done - elapsed);
    Console.Write($"\rFrame {f + 1}/{totalFrames} ({done:P0})  {where}  " +
                  $"elapsed {TimeSpan.FromSeconds(elapsed):hh\\:mm\\:ss}  left ~{eta:hh\\:mm\\:ss}   ");
}

bool ok = true;
try
{
    for (int f = 0; f < mainFrames; f++)
    {
        double z = f < holdStart ? zStart
                 : f <= holdStart + zoomFrames ? zStart + (zEnd - zStart) * (f - holdStart) / zoomFrames
                 : zEnd;
        int k = Math.Clamp((int)Math.Floor(z), firstKey, lastKey - 1);
        var a = GetKey(k);
        var b = GetKey(k + 1);
        if (a == null || b == null) { ok = false; break; } // cancelled
        Compose(frame, W, H, a, b, z - k);
        encIn.Write(MemoryMarshal.AsBytes(frame.AsSpan()));

        Report(f, $"zoom 2^{z:0.0}  keyframe {k}/{lastKey}");
    }

    // Finale: the last view held still while random palettes fade from one to the next.
    if (ok && finaleFrames > 0)
    {
        int k = Math.Clamp((int)Math.Floor(zEnd), firstKey, lastKey - 1);
        var a = GetKey(k)!;
        var b = GetKey(k + 1)!;
        var rng = new Random(opt.Seed);
        int perPalette = Math.Max(1, (int)Math.Round(opt.FinalePaletteSeconds * opt.Fps));
        int fade = Math.Clamp((int)Math.Round(opt.FinaleFadeSeconds * opt.Fps), 1, perPalette);
        var from = palette;
        var to = Palette.Random(rng, 1);
        int number = 1;
        for (int i = 0; i < finaleFrames && !cts.IsCancellationRequested; i++)
        {
            int phase = i % perPalette;
            if (i > 0 && phase == 0)
            {
                from = to;
                to = Palette.Random(rng, ++number);
            }
            // Fade, then hold. Held frames are identical, so only fades need recomputing.
            if (phase < fade)
            {
                var mixed = Palette.Blend(from, to, SmoothStep((phase + 1.0) / fade));
                Colorizer.Colorize(a.Values!, mixed, opt.Mapping, opt.Density, opt.Offset, a.Full, workers);
                Colorizer.Colorize(b.Values!, mixed, opt.Mapping, opt.Density, opt.Offset, b.Full, workers);
                Compose(frame, W, H, a, b with { Half = HalfSize(b.Full, KW, KH) }, zEnd - k);
            }
            encIn.Write(MemoryMarshal.AsBytes(frame.AsSpan()));
            Report(mainFrames + i, $"finale palette {number}");
        }
        if (cts.IsCancellationRequested) ok = false;
    }
}
catch (IOException ex)
{
    ok = false;
    Console.Error.WriteLine($"\nffmpeg stopped accepting frames: {ex.Message}");
}

cts.Cancel(); // stops the producer if we finished early
try { producer.Wait(); } catch (AggregateException) { }
encIn.Close();
enc.WaitForExit();
Console.WriteLine();
if (errors.Length > 0) Console.Error.WriteLine("ffmpeg: " + errors.ToString().Trim());
if (!ok || enc.ExitCode != 0)
{
    Console.Error.WriteLine("Video incomplete.");
    return 1;
}
Console.WriteLine($"Done: {opt.Output} in {clock.Elapsed:hh\\:mm\\:ss} (keyframe rendering {TimeSpan.FromSeconds(renderSeconds):hh\\:mm\\:ss})");
gpu?.Dispose();
return 0;

// =============================================================================================

/// <summary>
/// Builds the frame at zoom 2^(k+t) from keyframe k (full size, 2W×2H) and keyframe k+1
/// (its half-size copy, W×H). Both then have the same pixel size: k+1's half-size copy is
/// exactly the middle half of keyframe k, but averaged from 4× the samples, so it is used
/// wherever it reaches, feathered at its edge.
/// </summary>
static void Compose(int[] dst, int w, int h, Keyframe a, Keyframe b, double t)
{
    int kw = 2 * w, kh = 2 * h;
    double r = 2 / Math.Pow(2, t);      // keyframe pixels per video pixel, in (1, 2]
    double d = r / 4;                    // 2×2 sample offsets: a box filter about r wide
    double feather = 40 * (1 - t) + 1;   // video pixels; shrinks to nothing as b fills the frame

    Parallel.For(0, h, y =>
    {
        double oy = (y + 0.5 - h / 2.0) * r;
        for (int x = 0; x < w; x++)
        {
            double ox = (x + 0.5 - w / 2.0) * r;
            double bx = w / 2.0 + ox, by = h / 2.0 + oy;
            double edge = Math.Min(Math.Min(bx, w - bx), Math.Min(by, h - by)) / r;
            double wb = edge <= 0 ? 0 : edge >= feather ? 1 : SmoothStep(edge / feather);

            double cr = 0, cg = 0, cb = 0;
            if (wb > 0) Box(b.Half, w, h, bx, by, d, wb, ref cr, ref cg, ref cb);
            if (wb < 1) Box(a.Full, kw, kh, kw / 2.0 + ox, kh / 2.0 + oy, d, 1 - wb, ref cr, ref cg, ref cb);
            dst[y * w + x] = unchecked((int)0xFF000000) | ((int)(cr + 0.5) << 16) | ((int)(cg + 0.5) << 8) | (int)(cb + 0.5);
        }
    });
}

static double SmoothStep(double x) => x * x * (3 - 2 * x);

/// <summary>Adds weight × (average of 4 bilinear samples around (x, y)) to the colour sums.</summary>
static void Box(int[] img, int w, int h, double x, double y, double d, double weight,
                ref double r, ref double g, ref double b)
{
    double sr = 0, sg = 0, sb = 0;
    Bilinear(img, w, h, x - d, y - d, ref sr, ref sg, ref sb);
    Bilinear(img, w, h, x + d, y - d, ref sr, ref sg, ref sb);
    Bilinear(img, w, h, x - d, y + d, ref sr, ref sg, ref sb);
    Bilinear(img, w, h, x + d, y + d, ref sr, ref sg, ref sb);
    double k = weight / 4;
    r += sr * k; g += sg * k; b += sb * k;
}

/// <summary>Bilinear sample at continuous position (x, y); pixel centres are at i + 0.5.</summary>
static void Bilinear(int[] img, int w, int h, double x, double y, ref double r, ref double g, ref double b)
{
    x -= 0.5; y -= 0.5;
    int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
    double fx = x - x0, fy = y - y0;
    int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1);
    int ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
    int p00 = img[ya * w + xa], p10 = img[ya * w + xb], p01 = img[yb * w + xa], p11 = img[yb * w + xb];
    double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
    r += ((p00 >> 16) & 255) * w00 + ((p10 >> 16) & 255) * w10 + ((p01 >> 16) & 255) * w01 + ((p11 >> 16) & 255) * w11;
    g += ((p00 >> 8) & 255) * w00 + ((p10 >> 8) & 255) * w10 + ((p01 >> 8) & 255) * w01 + ((p11 >> 8) & 255) * w11;
    b += (p00 & 255) * w00 + (p10 & 255) * w10 + (p01 & 255) * w01 + (p11 & 255) * w11;
}

/// <summary>2×2 box downsample.</summary>
static int[] HalfSize(int[] src, int w, int h)
{
    int hw = w / 2, hh = h / 2;
    var dst = new int[hw * hh];
    Parallel.For(0, hh, y =>
    {
        for (int x = 0; x < hw; x++)
        {
            int i = 2 * y * w + 2 * x;
            int p0 = src[i], p1 = src[i + 1], p2 = src[i + w], p3 = src[i + w + 1];
            int r = (((p0 >> 16) & 255) + ((p1 >> 16) & 255) + ((p2 >> 16) & 255) + ((p3 >> 16) & 255) + 2) >> 2;
            int g = (((p0 >> 8) & 255) + ((p1 >> 8) & 255) + ((p2 >> 8) & 255) + ((p3 >> 8) & 255) + 2) >> 2;
            int b = ((p0 & 255) + (p1 & 255) + (p2 & 255) + (p3 & 255) + 2) >> 2;
            dst[y * hw + x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
    });
    return dst;
}

static string? FindFfmpeg(string? given)
{
    if (given != null) return File.Exists(given) ? given : null;
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        if (dir.Length > 0 && File.Exists(Path.Combine(dir, "ffmpeg.exe"))) return Path.Combine(dir, "ffmpeg.exe");
    // winget installs update PATH only for new shells; look where it puts things.
    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
    if (File.Exists(link)) return link;
    string pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
    if (Directory.Exists(pkgs))
        foreach (var d in Directory.GetDirectories(pkgs, "Gyan.FFmpeg*"))
            foreach (var exe in Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
                return exe;
    return null;
}

static Palette? LoadPalette(string name)
{
    if (name.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
    {
        var p = File.Exists(name) ? Palette.FromMapFile(name) : null;
        if (p == null) Console.Error.WriteLine($"Can't load palette file {name}");
        return p;
    }
    var all = Palette.BuiltIns();
    string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fractal", "Palettes");
    if (Directory.Exists(userDir))
        foreach (var f in Directory.GetFiles(userDir, "*.map"))
            if (Palette.FromMapFile(f) is { } up) all.Add(up);
    var match = all.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (match == null) Console.Error.WriteLine($"No palette called '{name}'. Available: {string.Join(", ", all.Select(p => p.Name))}");
    return match;
}

sealed record Keyframe(int K, int[] Full, int[] Half, Field? Values);

static class NativeMethods
{
    public const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);
}

sealed class Options
{
    public string LocationFile = "", Output = "";
    public int Width = 1920, Height = 1080, Fps = 60, Crf = 18;
    public double SecondsPerDoubling = 1, StartZoom = 1, HoldStart = 1, HoldEnd = 3;
    public double? EndZoom;
    public string Palette = "Classic", Preset = "medium", Encoder = "x264";
    public ColorMapping Mapping = ColorMapping.Linear;
    public double Density = 1, Offset = 0;
    public double FinaleSeconds = 0, FinalePaletteSeconds = 6, FinaleFadeSeconds = 2;
    public int Seed = Environment.TickCount;
    public string? Ffmpeg;

    public static Options? Parse(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("""
                Usage: FractalVideo <location.txt> <out.mp4> [options]
                  location.txt         text copied with Ctrl+C in the app (Re, Im, Zoom, Iterations)
                  --size 3840x2160     video size (default 1920x1080); keyframes are rendered at 2x this
                  --fps 60
                  --speed 0.5          seconds per doubling of zoom (default 1)
                  --start-zoom 1       zoom of the first frame (default 1: whole set)
                  --end-zoom 1e40      stop short of the location's zoom
                  --hold-start 1 --hold-end 3   seconds to pause on the first / last frame
                  --palette Classic    built-in or saved palette name, or a .map file
                  --mapping Linear     Linear | Sqrt | Log
                  --density 1 --offset 0
                  --finale 120         seconds of random palettes on the last view, after the hold
                  --finale-palette 6   seconds per palette, including its fade
                  --finale-fade 2      seconds each fade takes
                  --seed 1             repeat the same random palettes
                  --encoder x264       x264 (best quality per MB) | nvenc (NVIDIA hardware, faster)
                  --crf 18             quality: lower = better and bigger (x264 ~16-23)
                  --preset medium      x264 speed/size trade-off: faster | fast | medium | slow
                  --ffmpeg <path>      if ffmpeg isn't on PATH
                """);
            return null;
        }
        var o = new Options { LocationFile = args[0], Output = args[1] };
        var inv = CultureInfo.InvariantCulture;
        for (int i = 2; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            double D() => double.Parse(Next(), NumberStyles.Float, inv);
            switch (a)
            {
                case "--size":
                    var wh = Next().ToLowerInvariant().Split('x');
                    o.Width = int.Parse(wh[0]) / 2 * 2; o.Height = int.Parse(wh[1]) / 2 * 2;
                    break;
                case "--fps": o.Fps = int.Parse(Next()); break;
                case "--speed": o.SecondsPerDoubling = D(); break;
                case "--start-zoom": o.StartZoom = D(); break;
                case "--end-zoom": o.EndZoom = D(); break;
                case "--hold-start": o.HoldStart = D(); break;
                case "--hold-end": o.HoldEnd = D(); break;
                case "--palette": o.Palette = Next(); break;
                case "--mapping": o.Mapping = Enum.Parse<ColorMapping>(Next(), true); break;
                case "--density": o.Density = D(); break;
                case "--finale": o.FinaleSeconds = D(); break;
                case "--finale-palette": o.FinalePaletteSeconds = D(); break;
                case "--finale-fade": o.FinaleFadeSeconds = D(); break;
                case "--seed": o.Seed = int.Parse(Next()); break;
                case "--offset": o.Offset = D(); break;
                case "--encoder": o.Encoder = Next().ToLowerInvariant(); break;
                case "--crf": o.Crf = int.Parse(Next()); break;
                case "--preset": o.Preset = Next(); break;
                case "--ffmpeg": o.Ffmpeg = Next(); break;
                default: Console.Error.WriteLine($"Unknown option {a}"); return null;
            }
        }
        return o;
    }
}
