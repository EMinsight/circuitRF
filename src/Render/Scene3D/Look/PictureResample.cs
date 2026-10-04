// brief-em3d-107 R-em3d107-5a/b — a realistic picture is drawn at k × the size asked for and brought down here, on the CPU, with a
// separable filter; and a transparent one is read back PREMULTIPLIED (the GPU's blend makes it so) and straightened here before the
// PNG is encoded. Below the firewall, so brief 110's headless `render --look realistic` uses the same two steps.

namespace CircuitRF.Render.Scene3D.Look;

public static class PictureResample
{
    /// <summary>The supersampling factors a picture offers (1, 2, 4); 2 is the default (owner decision D3).</summary>
    public static readonly int[] Factors = [1, 2, 4];
    public const int DefaultFactor = 2;

    /// <summary>
    /// <paramref name="rgba"/> (<paramref name="width"/> × <paramref name="height"/>, RGBA8 rows top first, premultiplied or opaque)
    /// brought down by <paramref name="k"/> in each direction: a TENT filter of radius k source pixels (2k taps a side), run along x
    /// then along y in float, rounded once. Its weights are a partition of unity at every output pixel, so a flat colour stays exactly
    /// itself, and averaging premultiplied values is what makes a soft alpha edge come out right. The size must divide by k.
    /// </summary>
    public static byte[] Downsample(byte[] rgba, int width, int height, int k)
    {
        if (k <= 1) return rgba;
        if (width % k != 0 || height % k != 0) throw new ArgumentException($"{width} × {height} does not divide by {k}.");
        int w = width / k, h = height / k;
        var weights = Weights(k);
        int taps = weights.Length;
        // along x: (width × height) → (w × height), four channels in float
        var mid = new float[w * height * 4];
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                int first = x * k - k / 2;
                for (int t = 0; t < taps; t++)
                {
                    int sx = Math.Clamp(first + t, 0, width - 1);
                    int s = 4 * (row + sx);
                    float wt = weights[t];
                    r += wt * rgba[s]; g += wt * rgba[s + 1]; b += wt * rgba[s + 2]; a += wt * rgba[s + 3];
                }
                int d = 4 * (y * w + x);
                mid[d] = r; mid[d + 1] = g; mid[d + 2] = b; mid[d + 3] = a;
            }
        });
        var outp = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            int first = y * k - k / 2;
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int t = 0; t < taps; t++)
                {
                    int sy = Math.Clamp(first + t, 0, height - 1);
                    int s = 4 * (sy * w + x);
                    float wt = weights[t];
                    r += wt * mid[s]; g += wt * mid[s + 1]; b += wt * mid[s + 2]; a += wt * mid[s + 3];
                }
                int d = 4 * (y * w + x);
                outp[d] = Byte(r); outp[d + 1] = Byte(g); outp[d + 2] = Byte(b); outp[d + 3] = Byte(a);
            }
        });
        return outp;
    }

    /// <summary>The tent's 2k weights for output pixel 0, whose centre is at source x = k/2: taps at source pixels −k/2 … 3k/2 − 1,
    /// weight max(0, 1 − |centre distance| / k), normalised to sum to 1.</summary>
    public static float[] Weights(int k)
    {
        var w = new float[2 * k];
        double sum = 0;
        for (int t = 0; t < w.Length; t++)
        {
            double centre = t - k / 2 + 0.5;                 // source pixel centre, relative to the output pixel's start
            double d = Math.Abs(centre - k / 2.0) / k;
            w[t] = (float)Math.Max(0, 1 - d);
            sum += w[t];
        }
        for (int t = 0; t < w.Length; t++) w[t] = (float)(w[t] / sum);
        return w;
    }

    /// <summary>
    /// R-em3d107-5b — premultiplied RGBA8 made straight, in place: each colour channel divided by its alpha (rounded), and a pixel of
    /// alpha 0 made (0, 0, 0, 0). What a transparent picture goes through before its PNG is encoded, since PNG stores straight alpha.
    /// </summary>
    public static void Unpremultiply(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;
            if (a == 0) { rgba[i] = rgba[i + 1] = rgba[i + 2] = 0; continue; }
            for (int c = 0; c < 3; c++) rgba[i + c] = (byte)Math.Min(255, (rgba[i + c] * 255 + a / 2) / a);
        }
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
