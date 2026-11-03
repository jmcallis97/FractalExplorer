using System.Diagnostics;
using ComputeSharp;
using Fractal.Deep;

namespace Fractal.Gpu;

/// <summary>
/// Runs <see cref="DeepKernel"/> through DirectX 12 (ComputeSharp), which works with any
/// DX12-capable GPU on Windows 10/11 — NVIDIA, AMD or Intel, discrete or integrated — using
/// just the normal graphics driver. If there is no such GPU, or it fails its self-test,
/// <see cref="TryCreate"/> returns null and the CPU renderer is used instead.
/// </summary>
public sealed class GpuDeep : IDisposable
{
    /// <summary>
    /// Target time per dispatch. Keeps each GPU job far below Windows' ~2 s watchdog (which
    /// resets drivers that hang), keeps the desktop responsive, and lets renders cancel quickly.
    /// </summary>
    private const double TargetDispatchMs = 100;

    private readonly GraphicsDevice _device;
    private readonly Lock _lock = new();
    private ReadOnlyBuffer<int>? _indices;
    private ReadWriteBuffer<float>? _results;
    private ReadOnlyBuffer<Float2>? _orbitBuffer;
    private ReferenceOrbit? _orbitUploaded;
    // Persistent CPU-visible transfer buffers (allocating them per copy costs milliseconds).
    private UploadBuffer<int>? _upload;
    private ReadBackBuffer<float>? _readBack;

    /// <summary>Estimated cost of one pixel-iteration, in ms; adapts to the GPU.</summary>
    private double _msPerIteration = 1e-6;

    public string Name => _device.Name;

    /// <summary>Running totals, for diagnostics: dispatches, and ms spent waiting on them.</summary>
    public long Dispatches { get; private set; }
    public double DispatchMs { get; private set; }
    public double MsPerIteration => _msPerIteration;

    private GpuDeep(GraphicsDevice device) => _device = device;

    /// <summary>
    /// The GPU with the most dedicated memory (the discrete one, on a laptop with two), if it
    /// passes the self-test; otherwise null, with the reason.
    /// </summary>
    public static GpuDeep? TryCreate(out string reason)
    {
        try
        {
            var device = GraphicsDevice.QueryDevices(d => d.IsHardwareAccelerated)
                .OrderByDescending(d => d.DedicatedMemorySize)
                .FirstOrDefault();
            if (device == null)
            {
                reason = "no DirectX 12 GPU";
                return null;
            }

            var gpu = new GpuDeep(device);
            if (!gpu.SelfTest(out reason))
            {
                gpu.Dispose();
                return null;
            }
            reason = "";
            return gpu;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Renders a small deep view on the GPU and the CPU and checks they agree, which catches a
    /// broken driver or shader compiler before it can show garbage.
    /// </summary>
    private bool SelfTest(out string reason)
    {
        const int w = 48, h = 32, maxIter = 1000;
        var cx = BigFixed.Parse("-0.743643887037158704752191506114774", 128);
        var cy = BigFixed.Parse("0.131825904205311970493132056385139", 128);
        double scale = 1e-11;
        var orbit = ReferenceOrbit.Compute(cx, cy, maxIter);
        var view = new Viewport(0, 0, scale, 0, 0, w, h, maxIter, new DeepAnchor(cx, cy));

        var indices = Enumerable.Range(0, w * h).ToArray();
        var gpu = new float[w * h];
        Compute(orbit, view, indices, gpu, null, CancellationToken.None);

        int bad = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            int py = i / w, px = i % w;
            float cpu = Perturbation.Iterate(orbit, (view.OriginX + px) * scale, -((view.OriginY + py) * scale), maxIter);
            if ((cpu < 0) != (gpu[i] < 0) || Math.Abs(cpu - gpu[i]) > 0.5f) bad++;
        }
        reason = bad <= indices.Length / 50 ? "" : $"self-test failed ({bad} of {indices.Length} pixels differ)";
        if (reason.Length == 0) Calibrate();
        return reason.Length == 0;
    }

    /// <summary>
    /// Measures this GPU's speed on a known workload: a patch inside the main cardioid, where
    /// every pixel runs to the limit. Dispatch sizes are planned from this.
    /// </summary>
    private void Calibrate()
    {
        const int w = 256, h = 256, maxIter = 2000;
        var c = BigFixed.FromDouble(-0.1, 64);
        var zero = BigFixed.Zero(64);
        var orbit = ReferenceOrbit.Compute(c, zero, maxIter);
        var view = new Viewport(-0.1, 0, 1e-6, 0, 0, w, h, maxIter, new DeepAnchor(c, zero));
        var indices = Enumerable.Range(0, w * h).ToArray();
        var dst = new float[w * h];
        for (int pass = 0; pass < 2; pass++)
        {
            _msPerIteration = 1e-9; // one dispatch for the whole patch
            var sw = Stopwatch.StartNew();
            Compute(orbit, view, indices, dst, null, CancellationToken.None);
            _msPerIteration = Math.Max(1e-12, sw.Elapsed.TotalMilliseconds / ((double)w * h * maxIter));
        }
    }

    /// <summary>
    /// Iterates the given pixels of <paramref name="view"/> and writes their smooth iteration
    /// counts (or -1 inside) into <paramref name="dst"/> at the same indices. Work is split
    /// into short dispatches; cancellation is checked between them.
    /// </summary>
    public void Compute(ReferenceOrbit orbit, in Viewport view, ReadOnlySpan<int> indices, float[] dst,
                        RenderProgress? progress, CancellationToken token)
    {
        if (indices.IsEmpty) return;
        if (!CanRender(view)) throw new NotSupportedException("Panned too far from the reference for the GPU");

        int se = Math.ILogB(view.Scale);
        float sm = (float)Math.ScaleB(view.Scale, -se);

        lock (_lock)
        {
            if (!ReferenceEquals(orbit, _orbitUploaded))
            {
                var z = new Float2[orbit.Length];
                for (int i = 0; i < z.Length; i++) z[i] = new Float2((float)orbit.Zr[i], (float)orbit.Zi[i]);
                _orbitBuffer?.Dispose();
                _orbitBuffer = _device.AllocateReadOnlyBuffer(z);
                _orbitUploaded = orbit;
            }

            int done = 0;
            while (done < indices.Length)
            {
                token.ThrowIfCancellationRequested();

                // Size the dispatch from the measured speed, assuming the worst case (every
                // pixel runs to the limit) so a dispatch never runs much over target.
                int n = (int)Math.Clamp(TargetDispatchMs / (_msPerIteration * view.MaxIterations), 1024, 1 << 21);
                n = Math.Min(n, indices.Length - done);
                EnsureCapacity(n);

                indices.Slice(done, n).CopyTo(_upload!.Span);
                _upload.CopyTo(_indices!, 0, 0, n);

                var sw = Stopwatch.StartNew();
                _device.For(n, new DeepKernel(_indices!, _orbitBuffer!, _results!, n, view.Width,
                    (int)view.OriginX, (int)view.OriginY, sm, se, view.MaxIterations, orbit.Length - 1));
                _results!.CopyTo(_readBack!, 0, 0, n);
                var results = _readBack!.Span;
                double ms = sw.Elapsed.TotalMilliseconds;
                Dispatches++;
                DispatchMs += ms;

                // Count the iterations actually run (escape count, or the limit if inside) to
                // learn the true cost per iteration.
                double iterations = 0;
                for (int i = 0; i < n; i++)
                {
                    float r = results[i];
                    dst[indices[done + i]] = r;
                    iterations += r < 0 ? view.MaxIterations : r;
                }
                // Each dispatch has a fixed overhead (upload, launch, read back), so only learn
                // from dispatches long enough for that to be negligible.
                if (ms > 10) _msPerIteration = Math.Max(1e-12, ms / Math.Max(1, iterations));

                done += n;
                progress?.Add(n);
            }
        }
    }

    /// <summary>
    /// Pixel offsets from the reference are formed in floats, exact for global pixel indices
    /// up to 2^24 — about 4000 screen widths of panning without zooming. Past that, use the CPU.
    /// </summary>
    public static bool CanRender(in Viewport view) =>
        Math.Abs(view.OriginX) + view.Width < 1 << 24 && Math.Abs(view.OriginY) + view.Height < 1 << 24;

    private void EnsureCapacity(int n)
    {
        if (_upload != null && _upload.Length >= n) return;
        int cap = Math.Max(n, 1 << 16);
        _indices?.Dispose();
        _results?.Dispose();
        _upload?.Dispose();
        _readBack?.Dispose();
        _indices = _device.AllocateReadOnlyBuffer<int>(cap);
        _results = _device.AllocateReadWriteBuffer<float>(cap);
        _upload = _device.AllocateUploadBuffer<int>(cap);
        _readBack = _device.AllocateReadBackBuffer<float>(cap);
    }

    public void Dispose()
    {
        _indices?.Dispose();
        _results?.Dispose();
        _upload?.Dispose();
        _readBack?.Dispose();
        _orbitBuffer?.Dispose();
        _device.Dispose();
    }
}
