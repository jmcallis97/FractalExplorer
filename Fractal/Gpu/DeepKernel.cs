using ComputeSharp;

namespace Fractal.Gpu;

/// <summary>
/// Perturbation iteration on the GPU, in single precision (fast on every GPU, unlike doubles,
/// which consumer cards run at 1/32-1/64 speed and some don't support at all).
///
/// Floats only reach ~1e-38, far short of deep zoom offsets, so each pixel's offset is held
/// "rescaled": dz = w · 2^e, with w a float pair kept near 1 and e an integer exponent. In terms
/// of w the perturbation step dz' = (2Z + dz)·dz + dc becomes
///
///     w' = (2Z + s·w)·w + dc / 2^e,      s = 2^e
///
/// Terms that underflow to zero here (s·w, or dc/2^e) are below float precision relative to
/// the terms that survive, so dropping them costs nothing. Rebasing works as on the CPU:
/// when |z| &lt; |dz| or the reference runs out, continue from its start with dz = z.
///
/// Output matches <see cref="Deep.Perturbation.Iterate"/>: smooth iteration count, or -1 inside.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct DeepKernel(
    ReadOnlyBuffer<int> indices,     // pixel index into the field (row-major)
    ReadOnlyBuffer<Float2> orbit,    // reference orbit Z(0..last), Z(0) = 0
    ReadWriteBuffer<float> results,
    int count,
    int width,
    int originX,                     // global pixel index of the field's (0,0)
    int originY,
    float scaleMantissa,             // pixel size = scaleMantissa · 2^scaleExponent
    int scaleExponent,
    int maxIterations,
    int last) : IComputeShader
{
    /// <summary>2^e exactly (built from the bits), 0 below the normal float range.</summary>
    private static float Pow2(int e)
    {
        if (e < -126) return 0f;
        if (e > 127) e = 127;
        return Hlsl.AsFloat((uint)(e + 127) << 23);
    }

    public void Execute()
    {
        int t = ThreadIds.X;
        if (t >= count) return;

        int idx = indices[t];
        int py = idx / width;
        int px = idx - py * width;

        // dc = (dcr, dci) · 2^edc
        float dcr = (float)(originX + px) * scaleMantissa;
        float dci = -(float)(originY + py) * scaleMantissa;
        int edc = scaleExponent;

        float wr = 0, wi = 0;
        int e = edc;
        int m = 0;
        float zRefR = 0, zRefI = 0;
        float result = -1f;

        for (int i = 0; i < maxIterations; i++)
        {
            float s = Pow2(e);
            float d = Pow2(edc - e);

            // w = (2Z + dz)·w + dc/2^e
            float tr = 2 * zRefR + s * wr;
            float ti = 2 * zRefI + s * wi;
            float nr = tr * wr - ti * wi + dcr * d;
            wi = tr * wi + ti * wr + dci * d;
            wr = nr;
            m++;

            Float2 zm = orbit[m];
            zRefR = zm.X;
            zRefI = zm.Y;

            float zr = zRefR + s * wr;
            float zi = zRefI + s * wi;
            float mag = zr * zr + zi * zi;
            if (mag > 256f)
            {
                float nu = Hlsl.Log2(Hlsl.Log2(mag) * 0.5f);
                result = Hlsl.Max(0f, (float)i + 2f - nu);
                break;
            }

            // Rebase. |dz|² = s²·|w|²; when s² underflows, dz is far too small to matter.
            float ww = wr * wr + wi * wi;
            if (m == last || (e > -60 && mag < s * s * ww))
            {
                wr = zr;
                wi = zi;
                e = 0;
                m = 0;
                zRefR = 0;
                zRefI = 0;
                ww = mag;
            }

            // Keep w near 1 so it neither overflows nor loses precision as dz grows or shrinks.
            if (ww > 256f || (ww < 0.00390625f && ww > 0))
            {
                float mx = Hlsl.Max(Hlsl.Abs(wr), Hlsl.Abs(wi));
                int k = (int)((Hlsl.AsUInt(mx) >> 23) & 0xFF) - 127;
                float f = Pow2(-k);
                wr *= f;
                wi *= f;
                e += k;
            }
        }

        results[t] = result;
    }
}
