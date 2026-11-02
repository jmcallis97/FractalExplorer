using System.Globalization;
using System.Numerics;

namespace Fractal.Deep;

/// <summary>
/// Arbitrary-precision fixed-point number: <c>Raw / 2^Bits</c>. Slow compared to double, so it
/// is only used for the handful of things that genuinely need it: the view centre and the
/// single reference orbit. Every pixel is then computed in ordinary doubles relative to that.
/// </summary>
public readonly struct BigFixed
{
    public BigInteger Raw { get; }
    public int Bits { get; }

    public BigFixed(BigInteger raw, int bits)
    {
        Raw = raw;
        Bits = bits;
    }

    /// <summary>Fractional bits needed to hold coordinates at this pixel size, with headroom.</summary>
    public static int BitsFor(double pixelSize) => Math.Max(64, -Math.ILogB(pixelSize) + 64);

    public static BigFixed Zero(int bits) => new(BigInteger.Zero, bits);

    /// <summary>Exact conversion (as long as <paramref name="bits"/> is large enough).</summary>
    public static BigFixed FromDouble(double d, int bits)
    {
        if (d == 0) return Zero(bits);
        int exp = Math.ILogB(d);                       // d = m * 2^exp, 1 <= |m| < 2
        long mant = (long)Math.ScaleB(d, 52 - exp);    // 53-bit integer mantissa, exact
        int shift = bits + exp - 52;
        var big = new BigInteger(mant);
        return new BigFixed(shift >= 0 ? big << shift : big >> -shift, bits);
    }

    /// <summary>Parses decimal text such as "-0.7436438870371587" or "1.5e-40", to any length.</summary>
    public static BigFixed Parse(string text, int bits)
    {
        string s = text.Trim();
        int exp10 = 0;
        int e = s.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exp10 = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
            s = s[..e];
        }
        bool negative = s.StartsWith('-');
        s = s.TrimStart('-', '+');
        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            exp10 -= s.Length - dot - 1;
            s = s.Remove(dot, 1);
        }

        var digits = BigInteger.Parse(s.Length == 0 ? "0" : s, CultureInfo.InvariantCulture);
        BigInteger raw;
        if (exp10 >= 0)
        {
            raw = (digits * BigInteger.Pow(10, exp10)) << bits;
        }
        else
        {
            var div = BigInteger.Pow(10, -exp10);
            raw = ((digits << bits) + div / 2) / div; // round to nearest
        }
        return new BigFixed(negative ? -raw : raw, bits);
    }

    /// <summary>Nearest double (the top 63 bits are kept, so this is accurate to the last bit or so).</summary>
    public double ToDouble()
    {
        if (Raw.IsZero) return 0;
        long len = (long)BigInteger.Abs(Raw).GetBitLength();
        int sh = (int)Math.Max(0, len - 63);
        return Math.ScaleB((double)(Raw >> sh), sh - Bits);
    }

    public static BigFixed operator +(BigFixed a, BigFixed b) => new(a.Raw + Align(b, a.Bits), a.Bits);
    public static BigFixed operator -(BigFixed a, BigFixed b) => new(a.Raw - Align(b, a.Bits), a.Bits);
    public static BigFixed operator -(BigFixed a) => new(-a.Raw, a.Bits);
    public static BigFixed operator *(BigFixed a, BigFixed b) => new((a.Raw * b.Raw) >> b.Bits, a.Bits);

    public BigFixed Twice() => new(Raw << 1, Bits);
    public BigFixed WithBits(int bits) => new(Align(this, bits), bits);

    private static BigInteger Align(BigFixed v, int bits) =>
        v.Bits == bits ? v.Raw : v.Bits < bits ? v.Raw << (bits - v.Bits) : v.Raw >> (v.Bits - bits);

    public override string ToString() => ToDouble().ToString("R", CultureInfo.InvariantCulture);
}
