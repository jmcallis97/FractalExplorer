using System.Numerics;

namespace Fractal.Deep;

/// <summary>
/// Finds the exact point a deep view is centred on, so a zoom can carry on to any depth.
///
/// Endless spirals are centred on Misiurewicz points: c whose orbit 0, c, c²+c, ... lands
/// exactly on a repelling cycle after m steps (the preperiod) and then repeats every p steps.
/// Near such a point the set is self-similar: zooming in by |λ| (the cycle's multiplier)
/// shows the same picture turned by arg λ, forever. Minibrots are centred on nuclei: c whose
/// orbit returns exactly to 0 after p steps.
///
/// Both are found the same way: run the orbit of the current centre, read off the pattern
/// (where it settles into a cycle, or the period at which it swings closest to 0), then solve
/// the matching equation with Newton's method at full precision. The view centre only has to
/// be close; Newton converges quadratically, doubling the correct digits every step.
/// </summary>
public static class Snap
{
    public enum Kind { Misiurewicz, Nucleus }

    public sealed record Result(
        Kind Kind,
        int Preperiod,              // m (0 for a nucleus)
        int Period,                 // p
        BigFixed Re, BigFixed Im,
        double DistanceInViews,     // how far the exact point is from the given centre, in view widths
        double MultiplierAbs,       // Misiurewicz: zoom factor of one self-similar step
        double TurnDegrees,         //              and the rotation that comes with it
        double MinibrotSize,        // Nucleus: rough size of the minibrot (in the units of c)
        int NewtonSteps);

    /// <summary>Zoom to stop at: as deep as the maths goes for a spiral, framing the minibrot otherwise.</summary>
    public static double SuggestedZoom(Result r) =>
        r.Kind == Kind.Misiurewicz ? 1e285 : Location.ViewWidthAtZoom1 / (r.MinibrotSize * 6);

    /// <summary>One or two lines saying what was found.</summary>
    public static string Describe(Result r) => r.Kind == Kind.Misiurewicz
        ? $"The eye of an endless spiral (Misiurewicz point, preperiod {r.Preperiod}, period {r.Period}).\n" +
          $"The picture repeats every {r.MultiplierAbs:G4}x of zoom, turned {r.TurnDegrees:0} degrees."
        : $"A minibrot of period {r.Period}, about {r.MinibrotSize:G3} across.";

    /// <param name="targetBits">Fractional bits of the answer (about 3.32 per decimal digit).</param>
    public static Result? Find(Location loc, int maxIter, int targetBits, Action<string> log, out string why,
                               CancellationToken token = default)
    {
        double viewWidth = Location.ViewWidthAtZoom1 / loc.Zoom;
        int inBits = BigFixed.BitsFor(viewWidth / 4096);
        var c0 = new BC(BigFixed.Parse(loc.Re, inBits), BigFixed.Parse(loc.Im, inBits));

        // 1. The orbit of the given centre.
        var orbit = OrbitOf(c0, maxIter, token, out bool escaped);
        log(escaped ? $"Orbit escapes after {orbit.Count - 1} iterations."
                    : $"Orbit stays bounded for {maxIter} iterations (inside the set).");

        // 2a. Does it settle onto a cycle? (Misiurewicz if repelling, interior if attracting.)
        foreach (var (m, p) in CycleCandidates(orbit, token))
        {
            double lamLog = 0;
            for (int k = m; k < m + p; k++) lamLog += Math.Log((2 * orbit[k]).Magnitude);
            if (lamLog < 0)
            {
                log($"Orbit is drawn into an attracting cycle of period {p}: the centre is inside a minibrot.");
                var nuc = SolveNucleus(c0, p, targetBits, viewWidth, log, token);
                if (nuc != null) { why = ""; return nuc; }
                continue;
            }
            log($"Orbit lands on a cycle of period {p} after about {m} steps.");
            var mis = SolveMisiurewicz(c0, m, p, targetBits, viewWidth, log, token);
            if (mis != null && mis.DistanceInViews < 50) { why = ""; return mis; }
        }

        // 2b. Otherwise look for a minibrot: the periods at which the orbit comes closest to 0.
        var periods = new List<int>();
        double best = double.MaxValue;
        for (int n = 1; n < orbit.Count; n++)
            if (orbit[n].Magnitude < best) { best = orbit[n].Magnitude; periods.Add(n); }
        Result? nearest = null;
        foreach (int p in periods.AsEnumerable().Reverse().Take(4))
        {
            token.ThrowIfCancellationRequested();
            log($"Trying a minibrot of period {p}...");
            var nuc = SolveNucleus(c0, p, targetBits, viewWidth, log, token);
            log(nuc == null ? "  Newton did not settle." : $"  found one {nuc.DistanceInViews:G3} view widths away, size {nuc.MinibrotSize:G3}.");
            if (nuc != null && (nearest == null || nuc.DistanceInViews < nearest.DistanceInViews)) nearest = nuc;
        }
        if (nearest != null && nearest.DistanceInViews < 50) { why = ""; return nearest; }

        why = "No repeating pattern found near the centre. Centre the view on the spiral's eye " +
              "(or the minibrot) and zoom in a little further, then try again.";
        return null;
    }

    /// <summary>
    /// Snap at a point picked by hand (the cursor): the eye of a spiral right there if there is
    /// one, otherwise the lowest-period minibrot within <paramref name="boxRadius"/> of it, found
    /// by box period detection, which works for any shape of structure.
    /// </summary>
    public static Result? FindAt(Location loc, double boxRadius, int maxIter, int targetBits,
                                 CancellationToken token, out string why)
    {
        double viewWidth = Location.ViewWidthAtZoom1 / loc.Zoom;
        int inBits = BigFixed.BitsFor(boxRadius / 4096);
        var c0 = new BC(BigFixed.Parse(loc.Re, inBits), BigFixed.Parse(loc.Im, inBits));
        Action<string> log = _ => { };

        // 1. A spiral's eye under the cursor: the orbit lands on a repelling cycle.
        var orbit = OrbitOf(c0, maxIter, token, out _);
        foreach (var (m, p) in CycleCandidates(orbit, token).Take(3))
        {
            token.ThrowIfCancellationRequested();
            double lamLog = 0;
            for (int k = m; k < m + p; k++) lamLog += Math.Log((2 * orbit[k]).Magnitude);
            if (lamLog < 0) break; // inside a minibrot: the box search below finds its nucleus
            var mis = SolveMisiurewicz(c0, m, p, targetBits, viewWidth, log, token);
            if (mis != null && mis.DistanceInViews * viewWidth < 2 * boxRadius) { why = ""; return mis; }
        }

        // 2. The minibrot this structure is built around. If the box's corners escape before it
        //    surrounds one, try smaller boxes, then bigger ones (they hold lower periods).
        foreach (double f in (double[])[1, 0.5, 0.25, 2, 4, 8])
        {
            double r = boxRadius * f;
            int p = BoxPeriod(c0, r, maxIter, inBits, token);
            if (p < 0) continue;
            var nuc = SolveNucleus(c0, p, targetBits, viewWidth, log, token);
            if (nuc != null && nuc.DistanceInViews * viewWidth < 2 * r) { why = ""; return nuc; }
        }
        why = "Nothing found under the cursor: zoom in a little, or point at a different part of the structure.";
        return null;
    }

    /// <summary>
    /// Box period (R. Munafo; C. Heiland-Allen): iterate the four corners of a box of c values
    /// together. The first n at which the image of the box surrounds 0 is the period of the
    /// lowest-period minibrot inside the box. -1 if a corner escapes first (box too big) or
    /// nothing turns up within <paramref name="maxIter"/>.
    /// </summary>
    private static int BoxPeriod(BC c0, double r, int maxIter, int bits, CancellationToken token)
    {
        var rr = BigFixed.FromDouble(r, bits);
        var centre = c0.WithBits(bits);
        int[] sx = [-1, 1, 1, -1], sy = [-1, -1, 1, 1];
        var corners = new BC[4];
        var z = new BC[4];
        for (int k = 0; k < 4; k++)
        {
            corners[k] = new BC(sx[k] < 0 ? centre.R - rr : centre.R + rr, sy[k] < 0 ? centre.I - rr : centre.I + rr);
            z[k] = BC.Zero(bits);
        }
        Span<Complex> pts = stackalloc Complex[4];
        for (int n = 1; n <= maxIter; n++)
        {
            if ((n & 255) == 0) token.ThrowIfCancellationRequested();
            for (int k = 0; k < 4; k++)
            {
                z[k] = z[k].Sq() + corners[k];
                pts[k] = z[k].ToComplex();
                if (pts[k].Magnitude > 1e10) return -1;
            }
            if (SurroundsOrigin(pts)) return n;
        }
        return -1;
    }

    /// <summary>Whether the quadrilateral through the points winds around 0 (ray-crossing test).</summary>
    private static bool SurroundsOrigin(ReadOnlySpan<Complex> pts)
    {
        bool inside = false;
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            if ((a.Imaginary <= 0) != (b.Imaginary <= 0))
            {
                double x = a.Real + (b.Real - a.Real) * (0 - a.Imaginary) / (b.Imaginary - a.Imaginary);
                if (x > 0) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// The orbit of c in full precision, stored as doubles (the orbit itself is of order 1, so
    /// doubles are plenty for recognising its shape). Stops when it escapes.
    /// </summary>
    private static List<Complex> OrbitOf(BC c, int maxIter, CancellationToken token, out bool escaped)
    {
        var orbit = new List<Complex> { Complex.Zero };
        var z = BC.Zero(c.R.Bits);
        escaped = false;
        for (int n = 1; n <= maxIter; n++)
        {
            if ((n & 1023) == 0) token.ThrowIfCancellationRequested();
            z = z.Sq() + c;
            var d = z.ToComplex();
            orbit.Add(d);
            if (d.Magnitude > 2) { escaped = true; break; }
        }
        return orbit;
    }

    /// <summary>(m, p) where the orbit from step m onwards repeats every p steps, smallest p first.</summary>
    private static IEnumerable<(int m, int p)> CycleCandidates(List<Complex> orbit, CancellationToken token)
    {
        const double Tol2 = 1e-18; // (1e-9)²
        int n = orbit.Count;
        int pMax = Math.Min(4000, n / 3);
        for (int p = 1; p <= pMax; p++)
        {
            if ((p & 63) == 0) token.ThrowIfCancellationRequested();
            int need = Math.Max(24, 3 * p), run = 0;
            for (int k = 1; k + p < n; k++)
            {
                var d = orbit[k + p] - orbit[k];
                if (d.Real * d.Real + d.Imaginary * d.Imaginary < Tol2)
                {
                    if (++run >= need) { yield return (k - run + 1, p); break; }
                }
                else run = 0;
            }
        }
    }

    private static Result? SolveMisiurewicz(BC c0, int m, int p, int targetBits, double viewWidth, Action<string> log,
                                            CancellationToken token)
    {
        while (true)
        {
            var c = Newton(c0, targetBits, log, bits =>
            {
                // G(c) = z_{m+p} - z_m, G'(c) = dz_{m+p} - dz_m
                return cc =>
                {
                    var z = BC.Zero(bits); var dz = BC.Zero(bits); var one = BC.One(bits);
                    BC zm = default, dzm = default;
                    for (int k = 1; k <= m + p; k++)
                    {
                        dz = (z * dz).Twice() + one;
                        z = z.Sq() + cc;
                        if (k == m) { zm = z; dzm = dz; }
                    }
                    return (z - zm, dz - dzm);
                };
            }, token, out int steps);
            if (c == null) return null;

            // The orbit must land at step m, not before: if z_{m-1} already repeats, m is too big.
            var orb = Orbit(c.Value, m + p);
            if (m > 1 && (orb[m - 1 + p] - orb[m - 1]).Magnitude < 1e-12) { m--; continue; }

            double lamLog = 0, lamArg = 0;
            for (int k = m; k < m + p; k++) { lamLog += Math.Log((2 * orb[k]).Magnitude); lamArg += (2 * orb[k]).Phase; }
            // G = z(m+p) - z(m) is also zero at minibrot nuclei (the orbit hits 0 and repeats);
            // Newton can land on one of those. A spiral's eye has a repelling cycle.
            if (!(lamLog > 0) || orb.Skip(1).Take(m + p).Any(z => z.Magnitude < 1e-12)) return null;
            double turn = Math.IEEERemainder(lamArg * 180 / Math.PI, 360);
            return new Result(Kind.Misiurewicz, m, p, c.Value.R, c.Value.I, Distance(c.Value, c0) / viewWidth,
                Math.Exp(lamLog), turn, 0, steps);
        }
    }

    private static Result? SolveNucleus(BC c0, int p, int targetBits, double viewWidth, Action<string> log,
                                        CancellationToken token)
    {
        var c = Newton(c0, targetBits, log, bits => cc =>
        {
            var z = BC.Zero(bits); var dz = BC.Zero(bits); var one = BC.One(bits);
            for (int k = 1; k <= p; k++) { dz = (z * dz).Twice() + one; z = z.Sq() + cc; }
            return (z, dz);
        }, token, out int steps);
        if (c == null) return null;

        // Minibrot size estimate (C. Heiland-Allen): l = Π 2z_j, b = Σ 1/Π 2z_j, size = 1/(b l²).
        var orb = Orbit(c.Value, p);
        double lLog = 0; Complex b = Complex.Zero, prod = Complex.One;
        bool bDone = false;
        for (int j = 1; j < p; j++)
        {
            lLog += Math.Log((2 * orb[j]).Magnitude);
            if (bDone) continue;
            prod *= 2 * orb[j];
            if (prod.Magnitude > 1e200) { bDone = true; continue; } // further terms of b are negligible
            b += 1 / prod;
        }
        double size = p == 1 ? 2.5 : Math.Exp(-(Math.Log(b.Magnitude) + 2 * lLog)); // period 1: the whole set
        return new Result(Kind.Nucleus, 0, p, c.Value.R, c.Value.I, Distance(c.Value, c0) / viewWidth, 0, 0, size, steps);
    }

    /// <summary>
    /// Newton's method c ← c − G/G'. Rounding errors in G grow along the orbit by the same factor
    /// as G' does, so the step is accurate to about the working precision regardless of depth.
    /// </summary>
    private static BC? Newton(BC c0, int targetBits, Action<string> log,
        Func<int, Func<BC, (BC g, BC dg)>> makeG, CancellationToken token, out int steps)
    {
        int bits = targetBits + 64;
        var G = makeG(bits);
        var c = c0.WithBits(bits);
        var tiny = BigInteger.One << (bits - targetBits);
        for (steps = 1; steps <= 60; steps++)
        {
            token.ThrowIfCancellationRequested();
            var (g, dg) = G(c);
            var step = g / dg;
            if (step == null) return null;
            c -= step.Value;
            var mag = BigInteger.Max(BigInteger.Abs(step.Value.R.Raw), BigInteger.Abs(step.Value.I.Raw));
            if (mag <= tiny) return c;
            if (step.Value.ToComplex().Magnitude > 1) return null; // flew off
        }
        log("Newton did not converge.");
        return null;
    }

    private static Complex[] Orbit(BC c, int n)
    {
        var res = new Complex[n + 1];
        var z = BC.Zero(c.R.Bits);
        for (int k = 1; k <= n; k++) { z = z.Sq() + c; res[k] = z.ToComplex(); }
        return res;
    }

    private static double Distance(BC a, BC b) => (a - b.WithBits(a.R.Bits)).ToComplex().Magnitude;

    /// <summary>Complex number with BigFixed parts.</summary>
    private readonly struct BC(BigFixed r, BigFixed i)
    {
        public BigFixed R { get; } = r;
        public BigFixed I { get; } = i;

        public static BC Zero(int bits) => new(BigFixed.Zero(bits), BigFixed.Zero(bits));
        public static BC One(int bits) => new(new BigFixed(BigInteger.One << bits, bits), BigFixed.Zero(bits));
        public BC WithBits(int bits) => new(R.WithBits(bits), I.WithBits(bits));

        public static BC operator +(BC a, BC b) => new(a.R + b.R, a.I + b.I);
        public static BC operator -(BC a, BC b) => new(a.R - b.R, a.I - b.I);
        public static BC operator *(BC a, BC b) => new(a.R * b.R - a.I * b.I, a.R * b.I + a.I * b.R);
        public BC Sq() => new(R * R - I * I, (R * I).Twice());
        public BC Twice() => new(R.Twice(), I.Twice());

        public static BC? operator /(BC a, BC b)
        {
            var den = b.R * b.R + b.I * b.I;
            if (den.Raw.IsZero) return null;
            BigFixed Div(BigFixed x) => new((x.Raw << den.Bits) / den.Raw, den.Bits);
            return new BC(Div(a.R * b.R + a.I * b.I), Div(a.I * b.R - a.R * b.I));
        }

        public Complex ToComplex() => new(R.ToDouble(), I.ToDouble());
    }
}
