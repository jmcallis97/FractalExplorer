// Deep zoom test bench. Run: dotnet run -c Release --project PerturbationLab -- [output folder] [diag|probe]
//
// 1. Shallow: perturbation must agree with the normal double renderer where doubles still work.
// 2. Deep: perturbation must agree with brute-force full-precision iteration (the ground truth).
// 3. Show why: plain doubles vs perturbation at 1e-20, plus a timed render far past double range.

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Fractal;
using Fractal.Deep;

string outDir = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "deep-out");
Directory.CreateDirectory(outDir);
int workers = Math.Max(1, Environment.ProcessorCount - 2);
bool allPassed = true;

// Well-known deep zoom target in Seahorse Valley; and c = i, a Misiurewicz point (spirals forever).
const string SeahorseX = "-0.743643887037158704752191506114774";
const string SeahorseY = "0.131825904205311970493132056385139";

Console.WriteLine($"Workers: {workers}\n");

if (args.Length > 1 && args[1] == "diag")
{
    // The bad pixel at seahorse 1e-15, with references at various distances from it.
    int w = 1920, h = 1080, px = 1381, py = 671;
    double scale = 1e-15;
    int maxIter = MaxIterFor(scale, w), bits = BigFixed.BitsFor(scale) + 64;
    var cx = BigFixed.Parse(SeahorseX, bits);
    var cy = BigFixed.Parse(SeahorseY, bits);
    double dcr = (px - w / 2) * scale, dci = -(py - h / 2) * scale;
    Console.WriteLine($"exact: {BruteForce(cx + BigFixed.FromDouble(dcr, bits), cy + BigFixed.FromDouble(dci, bits), maxIter):0.000}");
    foreach (int dist in new[] { 0, 1, 10, 100, 491 })
    {
        // Reference 'dist' pixels left of the bad pixel (491 = the view centre).
        double rr = dcr - dist * scale;
        var orbit = ReferenceOrbit.Compute(cx + BigFixed.FromDouble(rr, bits), cy + BigFixed.FromDouble(dci, bits), maxIter);
        float v = Perturbation.Iterate(orbit, dist * scale, 0, maxIter);
        Console.WriteLine($"reference {dist,3} px away (escapes: {orbit.Escaped}, len {orbit.Length}): {v:0.000}");
    }

    // Error rate over many pixels.
    var rng = new Random(1);
    var refOrbit = ReferenceOrbit.Compute(cx, cy, maxIter);
    int n = 3000, tiny = 0, small = 0, big = 0;
    var errs = new float[n];
    Parallel.For(0, n, k =>
    {
        int qx, qy;
        lock (rng) { qx = rng.Next(w); qy = rng.Next(h); }
        double ar = (qx - w / 2) * scale, ai = -(qy - h / 2) * scale;
        float p = Perturbation.Iterate(refOrbit, ar, ai, maxIter);
        float t = BruteForce(cx + BigFixed.FromDouble(ar, bits), cy + BigFixed.FromDouble(ai, bits), maxIter);
        errs[k] = (p < 0) != (t < 0) ? float.PositiveInfinity : Math.Abs(p - t);
    });
    foreach (var e in errs) { if (e <= 0.01) tiny++; else if (e <= 1) small++; else big++; }
    Console.WriteLine($"{n} random pixels: {tiny} exact (<0.01), {small} slightly off (<1 iter), {big} wrong (>1 iter)");
    return 0;
}

if (args.Length > 1 && args[1] == "probe")
{
    for (int e = 12; e <= 33; e += 3)
    {
        double s = Math.Pow(10, -e);
        var v = Deep(SeahorseX, SeahorseY, s, 192, 108, MaxIterFor(s, 1920) , out _);
        Console.WriteLine($"1e-{e}: inside {v.Count(f => f < 0) * 100.0 / v.Length:0}%, max escape iter {v.Max():0}");
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== 1. Shallow: perturbation vs normal double renderer (scale 1e-12) ==");
{
    int w = 480, h = 270, maxIter = 6000;
    double scale = 1e-12;
    var view = new Viewport(double.Parse(SeahorseX), double.Parse(SeahorseY), scale, 0, 0, w, h, maxIter);
    var plain = new Field(view, new float[w * h]);
    new MandelbrotRenderer { UseInteriorFill = false, UseSimd = false }
        .Render(plain, null, new RenderProgress(), CancellationToken.None);

    var deep = Deep(SeahorseX, SeahorseY, scale, w, h, maxIter, out _);
    // Not a pass/fail on its own: plain doubles already drift here on long orbits.
    Report(plain.Values, deep, 0.01, minMatch: 0);

    // Where they disagree, ask the ground truth who is right.
    int bits = BigFixed.BitsFor(scale);
    var cx = BigFixed.Parse(SeahorseX, bits);
    var cy = BigFixed.Parse(SeahorseY, bits);
    int plainRight = 0, deepRight = 0, neither = 0, checkedCount = 0;
    var lines = new List<string>();
    for (int i = 0; i < w * h && checkedCount < 40; i += 97)
    {
        float a = plain.Values[i], b = deep[i];
        bool same = (a < 0 && b < 0) || (a >= 0 && b >= 0 && Math.Abs(a - b) <= 0.01);
        if (same) continue;
        checkedCount++;
        int px = i % w, py = i / w;
        float t = BruteForce(cx + BigFixed.FromDouble((px - w / 2) * scale, bits + 64).WithBits(bits),
                             cy + BigFixed.FromDouble(-(py - h / 2) * scale, bits + 64).WithBits(bits), maxIter);
        bool pa = Math.Abs(a - t) <= 0.01 || (a < 0 && t < 0);
        bool pb = Math.Abs(b - t) <= 0.01 || (b < 0 && t < 0);
        if (pb && !pa) deepRight++; else if (pa && !pb) plainRight++; else neither++;
        if (lines.Count < 6) lines.Add($"   ({px},{py}) plain {a:0.00}  perturbation {b:0.00}  exact {t:0.00}");
    }
    Console.WriteLine($"   Disagreements checked against exact: perturbation right {deepRight}, plain right {plainRight}, neither {neither}");
    lines.ForEach(Console.WriteLine);
    bool pass = deepRight >= 0.9 * checkedCount;
    Console.WriteLine($"   -> {(pass ? "PASS" : "FAIL")} (perturbation must be right in 90%+ of disagreements)");
    allPassed &= pass;
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n== 2. Deep: perturbation vs brute-force full precision (ground truth) ==");
foreach (var (name, x, y, scale) in new[]
{
    ("seahorse", SeahorseX, SeahorseY, 1e-15),
    ("c = i   ", "0", "1", 1e-60),
    ("c = i   ", "0", "1", 1e-250),
})
{
    int w = 1920, h = 1080;
    int maxIter = MaxIterFor(scale, w);
    int bits = BigFixed.BitsFor(scale);
    var cx = BigFixed.Parse(x, bits);
    var cy = BigFixed.Parse(y, bits);
    var orbit = ReferenceOrbit.Compute(cx, cy, maxIter);

    // 10x10 grid of sample pixels across the screen (exact iteration is far too slow for all).
    var samples = new List<(int px, int py)>();
    for (int j = 0; j < 10; j++)
        for (int i = 0; i < 10; i++)
            samples.Add((i * w / 10 + 37, j * h / 10 + 23));

    var pert = new float[samples.Count];
    var exact = new float[samples.Count];
    var exactHi = new float[samples.Count];
    var sw = Stopwatch.StartNew();
    Parallel.For(0, samples.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
    {
        var (px, py) = samples[k];
        double dcr = (px - w / 2) * scale, dci = -(py - h / 2) * scale;
        pert[k] = Perturbation.Iterate(orbit, dcr, dci, maxIter);
        exact[k] = BruteForce(cx + BigFixed.FromDouble(dcr, bits + 64).WithBits(bits),
                              cy + BigFixed.FromDouble(dci, bits + 64).WithBits(bits), maxIter);
        // Same again with 128 more bits, to check the ground truth itself is converged.
        int hi = bits + 128;
        exactHi[k] = BruteForce(BigFixed.Parse(x, hi) + BigFixed.FromDouble(dcr, hi),
                                BigFixed.Parse(y, hi) + BigFixed.FromDouble(dci, hi), maxIter);
    });
    Console.Write($"{name} scale {scale:0e0} ({bits} bits, max iter {maxIter}, brute force {sw.ElapsedMilliseconds} ms): ");
    // Tolerance 0.01 iterations is invisible. Double-precision perturbation can still miss a few
// pixels on chaotic minibrot edges (~0.3% at seahorse 1e-15; run with "diag" to measure).
    allPassed &= Report(exactHi, pert, 0.01, minMatch: 0.97);
    for (int k = 0; k < samples.Count; k++)
        if (Math.Abs(pert[k] - exactHi[k]) > 0.01 || Math.Abs(exact[k] - exactHi[k]) > 0.01)
            Console.WriteLine($"   pixel {samples[k]}: perturbation {pert[k]:0.000}, exact@{bits} bits {exact[k]:0.000}, exact@{bits + 128} bits {exactHi[k]:0.000}");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n== 3. Pictures ==");
{
    // Plain doubles at 1e-20 around c = i: pixel spacing is below double resolution (~1e-16 at
    // |c| = 1), so neighbouring pixels collapse onto the same value -> big flat blocks.
    int w = 1280, h = 720;
    double scale = 1e-20;
    int maxIter = MaxIterFor(scale, w);
    var view = new Viewport(0, 1, scale, 0, 0, w, h, maxIter);
    var plain = new Field(view, new float[w * h]);
    new MandelbrotRenderer().Render(plain, null, new RenderProgress(), CancellationToken.None);
    Save(plain.Values, w, h, Path.Combine(outDir, "1_plain_doubles_1e-20.png"));

    var deep = Deep("0", "1", scale, w, h, maxIter, out var info);
    Save(deep, w, h, Path.Combine(outDir, "2_perturbation_1e-20.png"));
    Console.WriteLine($"c = i at 1e-20, 1280x720: {info}");

    // Seahorse at 1e-15: the edge of the minibrot this coordinate sits in (it is ~1e-15 across;
    // any deeper is all interior, i.e. black, and slow since every pixel hits the limit).
    w = 1920; h = 1080; scale = 1e-15;
    deep = Deep(SeahorseX, SeahorseY, scale, w, h, MaxIterFor(scale, w), out info);
    Save(deep, w, h, Path.Combine(outDir, "3_seahorse_1e-15.png"));
    Console.WriteLine($"Seahorse at 1e-15, 1920x1080: {info}");

    // Far past where doubles die. c = i is self-similar, so there is always structure.
    scale = 1e-100;
    deep = Deep("0", "1", scale, w, h, MaxIterFor(scale, w), out info);
    Save(deep, w, h, Path.Combine(outDir, "4_c=i_1e-100.png"));
    Console.WriteLine($"c = i at 1e-100, 1920x1080: {info}");
}

Console.WriteLine($"\nImages: {outDir}");
Console.WriteLine(allPassed ? "\nALL PASSED" : "\nSOME CHECKS FAILED");
return allPassed ? 0 : 1;

// =============================================================================================

static int MaxIterFor(double scale, int width)
{
    double zoom = 3.5 / width / scale; // same rule as the app
    return (int)(200 + 100 * Math.Log2(Math.Max(1, zoom)));
}

static float[] Deep(string x, string y, double scale, int w, int h, int maxIter, out string info)
{
    int bits = BigFixed.BitsFor(scale);
    var view = new DeepView(BigFixed.Parse(x, bits), BigFixed.Parse(y, bits), scale, w, h, maxIter);
    var orbit = ReferenceOrbit.Compute(view.CenterX, view.CenterY, maxIter);
    var dst = new float[w * h];
    var sw = Stopwatch.StartNew();
    Perturbation.Render(view, orbit, dst, Math.Max(1, Environment.ProcessorCount - 2));
    info = $"reference {orbit.ComputeTime.TotalMilliseconds:0} ms ({orbit.Length - 1} iters, {bits} bits), " +
           $"pixels {sw.ElapsedMilliseconds} ms, max iter {maxIter}";
    return dst;
}

/// <summary>Ground truth: every pixel iterated entirely in full precision.</summary>
static float BruteForce(BigFixed cr, BigFixed ci, int maxIter)
{
    int bits = cr.Bits;
    var x = BigFixed.Zero(bits); var y = x; var x2 = x; var y2 = x;
    var limit = BigFixed.FromDouble(256, bits).Raw;
    for (int i = 0; i < maxIter; i++)
    {
        y = (x * y).Twice() + ci;
        x = x2 - y2 + cr;
        x2 = x * x;
        y2 = y * y;
        var mag = x2 + y2;
        if (mag.Raw > limit) return MandelbrotRenderer.Smooth(i, mag.ToDouble());
    }
    return MandelbrotRenderer.Inside;
}

static bool Report(float[] expected, float[] actual, double tolerance, double minMatch)
{
    int match = 0, n = expected.Length;
    double worst = 0;
    for (int i = 0; i < n; i++)
    {
        float a = expected[i], b = actual[i];
        bool ok = (a < 0 && b < 0) || (a >= 0 && b >= 0 && Math.Abs(a - b) <= tolerance);
        if (ok) match++;
        else worst = Math.Max(worst, a < 0 || b < 0 ? double.PositiveInfinity : Math.Abs(a - b));
    }
    double frac = (double)match / n;
    bool pass = frac >= minMatch;
    int inside = expected.Count(f => f < 0);
    string verdict = minMatch <= 0 ? "(info only)" : pass ? "-> PASS" : "-> FAIL";
    Console.WriteLine($"{match}/{n} match ({frac:P2}){(match < n ? $", worst diff {worst:0.###}" : "")}, {inside} inside {verdict}");
    return pass;
}

static void Save(float[] values, int w, int h, string path)
{
    var palette = Palette.BuiltIns()[0];
    var field = new Field(new Viewport(0, 0, 1, 0, 0, w, h, 0), values);
    var px = new int[w * h];
    Colorizer.Colorize(field, palette, ColorMapping.Sqrt, 1.0, 0.0, px, Environment.ProcessorCount);
    using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
    Marshal.Copy(px, 0, data.Scan0, px.Length);
    bmp.UnlockBits(data);
    bmp.Save(path, ImageFormat.Png);
}
