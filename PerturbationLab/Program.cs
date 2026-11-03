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

if (args.Length > 1 && args[1] == "app")
{
    // The app's renderer in deep mode.
    bool ok = true;
    foreach (var (name, x, y, scale) in new[]
    {
        ("seahorse 1e-15", SeahorseX, SeahorseY, 1e-15),
        ("c = i 1e-100  ", "0", "1", 1e-100),
        ("c = i 1e-280  ", "0", "1", 1e-280),
    })
    {
        int w = 1280, h = 720, maxIter = MaxIterFor(scale, w);
        int bits = BigFixed.BitsFor(scale);
        var anchor = new DeepAnchor(BigFixed.Parse(x, bits), BigFixed.Parse(y, bits));
        var view = new Viewport(anchor.X.ToDouble(), anchor.Y.ToDouble(), scale, 0, 0, w, h, maxIter, anchor);
        float[] RenderWith(Viewport v, bool simd, bool fill, Field? source = null)
        {
            var f = new Field(v, new float[v.Width * v.Height]);
            new MandelbrotRenderer { UseSimd = simd, UseInteriorFill = fill }.Render(f, source, new RenderProgress(), CancellationToken.None);
            return f.Values;
        }
        int Diff(float[] a, float[] b) => a.Zip(b).Count(p => BitConverter.SingleToInt32Bits(p.First) != BitConverter.SingleToInt32Bits(p.Second));

        var scalar = RenderWith(view, false, false);
        var simd = RenderWith(view, true, false);
        var standalone = new float[w * h];
        Perturbation.Render(new DeepView(anchor.X, anchor.Y, scale, w, h, maxIter),
                            ReferenceOrbit.Compute(anchor.X, anchor.Y, maxIter), standalone, workers);
        var filled = RenderWith(view, true, true);
        int fillDiff = filled.Zip(simd).Count(p => !(p.First < 0 && p.Second < 0) && p.First != p.Second);

        // Pan: build on the first render, compare with a fresh render of the panned view.
        var panned = view with { PanX = 137, PanY = -59 };
        var src = new Field(view, simd);
        var reused = RenderWith(panned, true, false, src);
        var fresh = RenderWith(panned, true, false);

        int d1 = Diff(scalar, simd), d2 = Diff(simd, standalone), d3 = Diff(reused, fresh);
        Console.WriteLine($"{name}: simd vs scalar {d1}, vs standalone {d2}, pan reuse vs fresh {d3}, fill changed {fillDiff} px, inside {simd.Count(f => f < 0)}");
        ok &= d1 == 0 && d2 == 0 && d3 == 0 && fillDiff < w * h / 1000;
    }

    // Speed at 4K.
    {
        int w = 3840, h = 2160;
        void Time(string label, Viewport v, bool simd)
        {
            var r = new MandelbrotRenderer { UseSimd = simd };
            var f = new Field(v, new float[w * h]);
            r.Render(f, null, new RenderProgress(), CancellationToken.None); // warm (orbit cached after)
            var sw = Stopwatch.StartNew();
            r.Render(f, null, new RenderProgress(), CancellationToken.None);
            Console.WriteLine($"4K {label}: {sw.ElapsedMilliseconds} ms");
        }
        double s = 1e-12;
        int bits = BigFixed.BitsFor(s), mi = MaxIterFor(s, w);
        var a = new DeepAnchor(BigFixed.Parse(SeahorseX, bits), BigFixed.Parse(SeahorseY, bits));
        var plain = new Viewport(a.X.ToDouble(), a.Y.ToDouble(), s, 0, 0, w, h, mi);
        Time("seahorse 1e-12 plain SIMD       ", plain, true);
        Time("seahorse 1e-12 perturbation SIMD", plain with { Deep = a }, true);
        Time("seahorse 1e-12 perturbation scalar", plain with { Deep = a }, false);
        s = 1e-15; bits = BigFixed.BitsFor(s); mi = MaxIterFor(s, w);
        a = new DeepAnchor(BigFixed.Parse(SeahorseX, bits), BigFixed.Parse(SeahorseY, bits));
        Time("seahorse 1e-15 perturbation SIMD", new Viewport(a.X.ToDouble(), a.Y.ToDouble(), s, 0, 0, w, h, mi, a), true);
        s = 1e-100; bits = BigFixed.BitsFor(s); mi = MaxIterFor(s, w);
        a = new DeepAnchor(BigFixed.Parse("0", bits), BigFixed.Parse("1", bits));
        Time("c = i 1e-100 perturbation SIMD  ", new Viewport(0, 1, s, 0, 0, w, h, mi, a), true);
    }
    Console.WriteLine(ok ? "APP CHECKS PASSED" : "APP CHECKS FAILED");
    return ok ? 0 : 1;
}

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
if (args.Length > 1 && args[1] == "gputime")
{
    // gputime <x> <y> <scale> [w h]: time the app renderer on GPU vs CPU for one view.
    var gpu = Fractal.Gpu.GpuDeep.TryCreate(out string why) ?? throw new Exception(why);
    double s = double.Parse(args[4]);
    int W = args.Length > 6 ? int.Parse(args[5]) : 1024, H = args.Length > 6 ? int.Parse(args[6]) : 640;
    int bits = BigFixed.BitsFor(s), mi = MaxIterFor(s, W);
    var a = new DeepAnchor(BigFixed.Parse(args[2], bits), BigFixed.Parse(args[3], bits));
    var v = new Viewport(a.X.ToDouble(), a.Y.ToDouble(), s, 0, 0, W, H, mi, a);
    foreach (bool useGpu in new[] { true, true, false })
    {
        var r = new MandelbrotRenderer { Gpu = gpu, UseGpu = useGpu };
        var f = new Field(v, new float[W * H]);
        long d0 = gpu.Dispatches; double ms0 = gpu.DispatchMs;
        var sw = Stopwatch.StartNew();
        r.Render(f, null, new RenderProgress(), CancellationToken.None);
        if (useGpu) Console.Write($"[{gpu.Dispatches - d0} dispatches, {gpu.DispatchMs - ms0:0.0} ms in them, {gpu.MsPerIteration:0.00e0} ms/iter] ");
        Console.WriteLine($"{(r.LastRenderUsedGpu ? "GPU" : "CPU")}: {sw.ElapsedMilliseconds} ms (orbit {r.LastOrbit!.Length} long, {r.LastOrbit.ComputeTime.TotalMilliseconds:0} ms), maxIter {mi}, inside {f.Values.Count(x => x < 0)}, filled {f.Values.Count(x => x == MandelbrotRenderer.FilledInside)}");
    }
    return 0;
}

if (args.Length > 1 && args[1] == "gpu")
{
    // GPU (single precision, rescaled) vs CPU (double) perturbation, along an automatic zoom
    // path that follows the boundary down to 1e-120.
    var gpu = Fractal.Gpu.GpuDeep.TryCreate(out string why);
    if (gpu == null) { Console.WriteLine("No GPU: " + why); return 1; }
    Console.WriteLine($"GPU: {gpu.Name} (self-test passed)");

    const int w = 480, h = 270;
    double scale = 1e-6;
    var cx = BigFixed.Parse(SeahorseX, 512);
    var cy = BigFixed.Parse("0.1318259", 512);
    bool ok = true;
    int step = 0;
    while (scale > 1e-120)
    {
        int bits = BigFixed.BitsFor(scale);
        var anchor = new DeepAnchor(cx.WithBits(bits), cy.WithBits(bits));
        int maxIter = MaxIterFor(scale, w);
        var view = new Viewport(anchor.X.ToDouble(), anchor.Y.ToDouble(), scale, 0, 0, w, h, maxIter, anchor);
        var orbit = ReferenceOrbit.Compute(anchor.X, anchor.Y, maxIter);

        var cpu = new float[w * h];
        Parallel.For(0, w * h, i =>
        {
            int py = i / w, px = i % w;
            cpu[i] = Perturbation.Iterate(orbit, (view.OriginX + px) * scale, -((view.OriginY + py) * scale), maxIter);
        });
        var g = new float[w * h];
        gpu.Compute(orbit, view, Enumerable.Range(0, w * h).ToArray(), g, null, CancellationToken.None);

        // "Invisible" error: within 1 iteration, or within the spread between this pixel and its
        // neighbours (in chaotic dust neighbours differ by hundreds; that detail is noise anyway).
        int close = 0, invisible = 0, insideMismatch = 0;
        for (int i = 0; i < g.Length; i++)
        {
            if ((cpu[i] < 0) != (g[i] < 0)) { insideMismatch++; continue; }
            float diff = cpu[i] < 0 ? 0 : Math.Abs(cpu[i] - g[i]);
            if (diff <= 1) { close++; invisible++; continue; }
            int px = i % w, py = i / w;
            float spread = 0;
            foreach (int n in new[] { px > 0 ? i - 1 : i, px < w - 1 ? i + 1 : i, py > 0 ? i - w : i, py < h - 1 ? i + w : i })
                if (cpu[n] >= 0) spread = Math.Max(spread, Math.Abs(cpu[n] - cpu[i]));
            if (diff <= spread) invisible++;
        }
        double frac = (double)invisible / g.Length;
        Console.WriteLine($"1e{Math.Log10(scale):0}: maxIter {maxIter,6}, within 1 iter {(double)close / g.Length:P1}, invisible {frac:P2}, inside mismatch {insideMismatch}, inside {cpu.Count(v => v < 0)}");
        ok &= frac > 0.99;
        if (step % 4 == 0)
        {
            Save(cpu, w, h, Path.Combine(outDir, $"gpu_{step:00}_cpu.png"));
            Save(g, w, h, Path.Combine(outDir, $"gpu_{step:00}_gpu.png"));
        }

        // Next centre: the slowest-escaping pixel near the middle (it lies closest to the set's
        // boundary, so following it keeps the view full of detail).
        int best = -1;
        for (int py = h / 4; py < 3 * h / 4; py++)
            for (int px = w / 4; px < 3 * w / 4; px++)
            {
                int i = py * w + px;
                if (cpu[i] >= 0 && (best < 0 || cpu[i] > cpu[best])) best = i;
            }
        if (best < 0) { Console.WriteLine("Lost the boundary"); break; }
        int nb = BigFixed.BitsFor(scale / 1e5);
        var sc = BigFixed.FromDouble(scale, nb);
        cx = cx.WithBits(nb) + BigFixed.FromDouble(view.OriginX + best % w, nb) * sc;
        cy = cy.WithBits(nb) - BigFixed.FromDouble(view.OriginY + best / w, nb) * sc;
        scale /= 1e5;
        step++;
    }
    Console.WriteLine($"Path end: {cx.ToDecimalString(130)}, {cy.ToDecimalString(130)}");

    // The app renderer with the GPU: interior fill and pan reuse, on a view with a minibrot
    // (seahorse 1e-14) so fill has interior to work on.
    {
        double s = 1e-14;
        int W = 1280, H = 720, bits = BigFixed.BitsFor(s), mi = MaxIterFor(s, W);
        var a = new DeepAnchor(BigFixed.Parse(SeahorseX, bits), BigFixed.Parse(SeahorseY, bits));
        var v = new Viewport(a.X.ToDouble(), a.Y.ToDouble(), s, 0, 0, W, H, mi, a);
        float[] R(Viewport vv, bool useGpu, bool fill, Field? src = null)
        {
            var r = new MandelbrotRenderer { Gpu = gpu, UseGpu = useGpu, UseInteriorFill = fill };
            var f = new Field(vv, new float[vv.Width * vv.Height]);
            var prog = new RenderProgress();
            r.Render(f, src, prog, CancellationToken.None);
            if (useGpu && !r.LastRenderUsedGpu) throw new Exception("GPU not used: " + r.GpuFailure);
            if (prog.Done != prog.Total) Console.WriteLine($"  progress {prog.Done} of {prog.Total}");
            return f.Values;
        }
        int Diff(float[] x, float[] y) => x.Zip(y).Count(p => BitConverter.SingleToInt32Bits(p.First) != BitConverter.SingleToInt32Bits(p.Second));
        var plain = new float[W * H];
        gpu.Compute(ReferenceOrbit.Compute(a.X, a.Y, mi), v, Enumerable.Range(0, W * H).ToArray(), plain, null, CancellationToken.None);
        var noFill = R(v, true, false);
        var fill = R(v, true, true);
        var cpuFill = R(v, false, true);
        int fillChanged = fill.Zip(noFill).Count(p => !(p.First < 0 && p.Second < 0) && p.First != p.Second);
        var panned = v with { PanX = 137, PanY = -59 };
        var reused = R(panned, true, true, new Field(v, fill));
        var fresh = R(panned, true, true);
        int panDiff = reused.Zip(fresh).Count(p => !(p.First < 0 && p.Second < 0) && p.First != p.Second);
        Console.WriteLine($"Renderer on GPU: no-fill vs direct {Diff(noFill, plain)}, fill changed {fillChanged} px, " +
                          $"filled {fill.Count(x => x == MandelbrotRenderer.FilledInside)} px (CPU filled {cpuFill.Count(x => x == MandelbrotRenderer.FilledInside)}), " +
                          $"pan reuse vs fresh {panDiff}, inside {noFill.Count(x => x < 0)}");
        ok &= Diff(noFill, plain) == 0 && fillChanged < W * H / 1000 && panDiff < W * H / 1000;
        Save(fill, W, H, Path.Combine(outDir, "gpu_renderer_fill.png"));
    }

    // Speed at 4K: the app renderer on the GPU vs on the CPU (both with interior fill).
    foreach (double s in new[] { 1e-14, 1e-20, 1e-50, 1e-64 })
    {
        int W = 3840, H = 2160, bits = BigFixed.BitsFor(s), mi = MaxIterFor(s, W);
        var a = s > 1e-15 ? new DeepAnchor(BigFixed.Parse(SeahorseX, bits), BigFixed.Parse(SeahorseY, bits))
                          : new DeepAnchor(cx.WithBits(bits), cy.WithBits(bits));
        var v = new Viewport(a.X.ToDouble(), a.Y.ToDouble(), s, 0, 0, W, H, mi, a);
        var r = new MandelbrotRenderer();
        var f = new Field(v, new float[W * H]);
        r.Render(f, null, new RenderProgress(), CancellationToken.None); // warm, caches orbit
        var sw = Stopwatch.StartNew();
        r.Render(f, null, new RenderProgress(), CancellationToken.None);
        long cpuMs = sw.ElapsedMilliseconds;
        r.Gpu = gpu;
        var gf = new Field(v, new float[W * H]);
        r.Render(gf, null, new RenderProgress(), CancellationToken.None);
        sw.Restart();
        r.Render(gf, null, new RenderProgress(), CancellationToken.None);
        var gv = gf.Values;
        Console.WriteLine($"4K 1e{Math.Log10(s):0} (maxIter {mi}): CPU {cpuMs} ms, GPU {sw.ElapsedMilliseconds} ms, inside {gv.Count(x => x < 0) * 100.0 / gv.Length:0}%");
        Save(gv, W, H, Path.Combine(outDir, $"gpu4k_1e{-Math.Log10(s):0}.png"));
    }
    gpu.Dispose();
    Console.WriteLine(ok ? "GPU CHECKS PASSED" : "GPU CHECKS FAILED");
    return ok ? 0 : 1;
}

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
