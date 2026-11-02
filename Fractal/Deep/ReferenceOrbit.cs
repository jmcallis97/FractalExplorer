namespace Fractal.Deep;

/// <summary>
/// One orbit Z(n+1) = Z(n)² + C computed in full precision at the view centre, then rounded to
/// doubles. Every other pixel is iterated as a small offset from this orbit (see
/// <see cref="Perturbation"/>), which is what lets doubles keep working at any zoom depth.
/// </summary>
public sealed class ReferenceOrbit
{
    public BigFixed Cr { get; }
    public BigFixed Ci { get; }

    /// <summary>Z(0) = 0 through Z(Length - 1).</summary>
    public double[] Zr { get; }
    public double[] Zi { get; }
    public int Length { get; }

    /// <summary>True if the reference point itself escaped before the iteration limit.</summary>
    public bool Escaped { get; }

    public TimeSpan ComputeTime { get; }

    private ReferenceOrbit(BigFixed cr, BigFixed ci, double[] zr, double[] zi, int length, bool escaped, TimeSpan time)
    {
        Cr = cr; Ci = ci; Zr = zr; Zi = zi; Length = length; Escaped = escaped; ComputeTime = time;
    }

    public static ReferenceOrbit Compute(BigFixed cr, BigFixed ci, int maxIterations,
                                         double bailout = 256, CancellationToken token = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int bits = cr.Bits;
        var zr = new double[maxIterations + 1];
        var zi = new double[maxIterations + 1];

        var x = BigFixed.Zero(bits);
        var y = BigFixed.Zero(bits);
        var x2 = x; var y2 = y;
        var limit = BigFixed.FromDouble(bailout, bits);
        int n = 1;
        bool escaped = false;

        for (int i = 0; i < maxIterations; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            y = (x * y).Twice() + ci;
            x = x2 - y2 + cr;
            x2 = x * x;
            y2 = y * y;
            zr[n] = x.ToDouble();
            zi[n] = y.ToDouble();
            n++;
            if ((x2 + y2).Raw > limit.Raw) { escaped = true; break; }
        }
        return new ReferenceOrbit(cr, ci, zr, zi, n, escaped, sw.Elapsed);
    }
}
