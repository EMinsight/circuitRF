// brief-em3d-106 R-em3d106-4b/c/d — an environment turned into what the realistic shader samples, ON THE CPU and DETERMINISTICALLY:
//
//   * an OCTAHEDRAL map of radiance (+z at the centre) at five roughness levels, 0, 0.25, 0.5, 0.75 and 1, held as the five MIP LEVELS
//     of one 2D texture (256², 128², 64², 32², 16²), so the shader's explicit-lod sample interpolates between roughness levels for
//     free and the upload is brief 101's ordinary mipmapped 2D path. 2D only, because only 2D upload exists on all three backends;
//     no cube map. Level 0 is the radiance itself; levels 1–4 are GGX-prefiltered by importance sampling with a FIXED Hammersley
//     sequence, reading a box-filtered pyramid of level 0 at the sample's footprint (filtered importance sampling), so 128 samples
//     a texel do what thousands would otherwise;
//   * nine spherical-harmonic irradiance coefficients, integrated EXACTLY over each texel of a 512 × 256 latitude–longitude image
//     (each texel's radiance is constant, and every basis function's integral over its patch is closed-form), so a uniform white
//     environment's irradiance is constant to rounding: the white-furnace gate;
//   * the 32 × 32 split-sum table, shared by every environment.
//
// Every environment — a studio or a user's .hdr — goes through the SAME path: it is first drawn into the 512 × 256 lat–long image
// (a studio sampled 2 × 2 a texel; an .hdr box-averaged down, or bilinearly up), and both the SH and level 0 are made from that.
// Rows are computed in parallel, each by itself, so the bytes do not depend on the thread count or the scheduling (gate 4).
// Results are cached per environment; ROTATION is the shader's (it turns its lookups), so a rotation drag recomputes nothing.

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Render.Scene3D.Look;

/// <summary>One mip level of the environment texture: RGBA16F, rows top first (v down).</summary>
public sealed record EnvironmentLevel(int Width, int Height, Half[] Rgba)
{
    public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(Rgba.AsSpan());
}

/// <summary>R-em3d106-4b — what the shader samples for one environment, and the CPU's own sampler of the same bytes.</summary>
public sealed class PrefilteredEnvironment : IPbrEnvironment
{
    public required string Key { get; init; }
    /// <summary>What the status line names: <c>Studio</c>, <c>High key</c>, <c>Dark</c>, or the <c>.hdr</c>'s file name.</summary>
    public required string Label { get; init; }
    /// <summary>Why a user's <c>.hdr</c> was not used (the view fell back to Studio), or null.</summary>
    public string? Fallback { get; init; }
    public required EnvironmentLevel[] Levels { get; init; }
    /// <summary>The split-sum table as a texture: RGBA16F, (scale, bias, 0, 1), <see cref="Pbr.BrdfSize"/>².</summary>
    public required Half[] BrdfTable { get; init; }
    /// <summary>The nine irradiance coefficients (<see cref="Pbr.Irradiance"/>), in the environment's frame.</summary>
    public required Vector3[] Sh { get; init; }
    /// <summary>The key light, in the environment's frame, and its direct irradiance (zero for an <c>.hdr</c>: its light is all in the
    /// map; the direction is then its dominant one, for brief 107).</summary>
    public required Vector3 KeyDirection { get; init; }
    public required Vector3 KeyRadiance { get; init; }

    public long Bytes => Levels.Sum(l => (long)l.Rgba.Length * 2) + BrdfTable.Length * 2L;

    // ── the CPU's sampler: what the GPU's linear, clamp-to-edge, linear-between-levels sampler does with these bytes ───────

    public Vector3 Radiance(Vector3 direction, float roughness)
        => SampleLevel(Pbr.OctEncode(Vector3.Normalize(direction)), Math.Clamp(roughness, 0, 1) * (Pbr.EnvironmentLevels - 1));

    public Vector3 Irradiance(Vector3 normal) => Pbr.Irradiance(Sh, normal);

    public Vector2 Brdf(float nv, float roughness)
    {
        var c = Bilinear(BrdfTable, Pbr.BrdfSize, Pbr.BrdfSize, new(Math.Clamp(nv, 0, 1), Math.Clamp(roughness, 0, 1)));
        return new(c.X, c.Y);
    }

    public Vector3 SampleLevel(Vector2 uv, float lod)
    {
        lod = Math.Clamp(lod, 0, Levels.Length - 1);
        int l0 = (int)MathF.Floor(lod);
        int l1 = Math.Min(l0 + 1, Levels.Length - 1);
        float f = lod - l0;
        var a = Bilinear(Levels[l0].Rgba, Levels[l0].Width, Levels[l0].Height, uv);
        if (f == 0 || l1 == l0) return new(a.X, a.Y, a.Z);
        var b = Bilinear(Levels[l1].Rgba, Levels[l1].Width, Levels[l1].Height, uv);
        var c = Vector4.Lerp(a, b, f);
        return new(c.X, c.Y, c.Z);
    }

    private static Vector4 Bilinear(Half[] px, int w, int h, Vector2 uv)
    {
        float x = uv.X * w - 0.5f, y = uv.Y * h - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 At(int i, int j)
        {
            i = Math.Clamp(i, 0, w - 1); j = Math.Clamp(j, 0, h - 1);
            int k = 4 * (j * w + i);
            return new((float)px[k], (float)px[k + 1], (float)px[k + 2], (float)px[k + 3]);
        }
        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector4.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }
}

public static class EnvironmentPrefilter
{
    /// <summary>The lat–long image every environment is drawn into first: 512 × 256.</summary>
    public const int LatLongWidth = 512, LatLongHeight = 256;
    /// <summary>Importance samples per texel of levels 1–4 (filtered importance sampling).</summary>
    public const int Samples = 128;

    private static readonly ConcurrentDictionary<string, PrefilteredEnvironment> Cache = new(StringComparer.Ordinal);
    private static readonly Lazy<Half[]> BrdfTexture = new(BuildBrdf);
    private static long _runs;

    /// <summary>How many environments have been prefiltered in this process — a cache hit does not count (gate 7).</summary>
    public static long Runs => Interlocked.Read(ref _runs);

    /// <summary>A studio's environment, cached.</summary>
    public static PrefilteredEnvironment Studio(C3dStudio studio)
        => Cache.GetOrAdd("studio:" + studio, _ => FromStudio(StudioEnvironment.Of(studio)));

    /// <summary>
    /// The environment a Look names: a studio, or the <c>.hdr</c> at <paramref name="hdrPath"/> (already resolved). An <c>.hdr</c>
    /// that cannot be read gives Studio, with <see cref="PrefilteredEnvironment.Fallback"/> saying why. Cached on the file's path,
    /// size and time, so an edited file is read again.
    /// </summary>
    public static PrefilteredEnvironment For(C3dStudio studio, string? hdrPath)
    {
        if (hdrPath is null) return Studio(studio);
        FileInfo info;
        try { info = new FileInfo(hdrPath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return FellBack(e.Message); }
        if (!info.Exists) return FellBack("the file does not exist");
        string key = $"hdr:{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var image = Design.ThreeD.RadianceHdr.Read(info.FullName, out string? why);
        if (image is null) return FellBack(why ?? "it does not read");
        var env = FromImage(image, key, Path.GetFileName(hdrPath));
        if (Cache.Count > 16) Cache.Clear();
        return Cache.GetOrAdd(key, env);

        // The file's own key (no size or time: there is none), so the view model's KeyOf recognises it and a rotation or exposure
        // edit does not make — and upload — a new fallback every time.
        PrefilteredEnvironment FellBack(string reason)
        {
            var s = Studio(C3dStudio.Studio);
            string full;
            try { full = Path.GetFullPath(hdrPath); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { full = hdrPath; }
            return new PrefilteredEnvironment
            {
                Key = $"hdr:{full}|unread", Label = s.Label, Fallback = $"{Path.GetFileName(hdrPath)} cannot be read ({reason}); lit with Studio",
                Levels = s.Levels, BrdfTable = s.BrdfTable, Sh = s.Sh, KeyDirection = s.KeyDirection, KeyRadiance = s.KeyRadiance,
            };
        }
    }

    public static PrefilteredEnvironment FromStudio(StudioPreset preset)
    {
        var latlong = DrawLatLong(d => StudioEnvironment.Radiance(preset, d));
        var (dir, radiance) = StudioEnvironment.Key(preset);
        return Build(latlong, "studio:" + preset.Studio, C3dLook.StudioLabel(preset.Studio), dir, radiance);
    }

    /// <summary>A user's lat–long image (rows top first, +z up at the top, +x at the centre column).</summary>
    public static PrefilteredEnvironment FromImage(RadianceImage image, string key, string label)
    {
        var latlong = Resample(image);
        var sh = ProjectSh(latlong);
        // An .hdr's light is all in its map: no direct key, and its dominant direction (the l = 1 band's) for brief 107.
        var dominant = new Vector3(Lum(sh[3]), Lum(sh[1]), Lum(sh[2]));
        var dir = dominant.LengthSquared() > 1e-12f ? Vector3.Normalize(dominant) : Vector3.UnitZ;
        return Build(latlong, key, label, dir, Vector3.Zero, sh);
    }

    /// <summary>A constant radiance everywhere — the white furnace (tests), and what an empty source would be.</summary>
    public static PrefilteredEnvironment Uniform(Vector3 radiance)
        => Build(DrawLatLong(_ => radiance), $"uniform:{radiance}", "uniform", Vector3.UnitZ, Vector3.Zero);

    private static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private static PrefilteredEnvironment Build(float[] latlong, string key, string label, Vector3 keyDir, Vector3 keyRadiance,
                                                Vector3[]? sh = null)
    {
        Interlocked.Increment(ref _runs);
        sh ??= ProjectSh(latlong);
        var level0 = DrawOctahedral(latlong, Pbr.EnvironmentSize);
        var pyramid = Pyramid(level0, Pbr.EnvironmentSize);
        var levels = new EnvironmentLevel[Pbr.EnvironmentLevels];
        levels[0] = ToHalf(level0, Pbr.EnvironmentSize);
        for (int k = 1; k < Pbr.EnvironmentLevels; k++)
        {
            int size = Pbr.EnvironmentSize >> k;
            levels[k] = ToHalf(PrefilterLevel(pyramid, size, k / (float)(Pbr.EnvironmentLevels - 1)), size);
        }
        return new PrefilteredEnvironment
        {
            Key = key, Label = label, Levels = levels, BrdfTable = BrdfTexture.Value, Sh = sh, KeyDirection = keyDir, KeyRadiance = keyRadiance,
        };
    }

    // ── the lat–long image ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The direction at lat–long (u, v): azimuth φ = π − 2πu (so +x is the centre column and the image is not mirrored seen
    /// from inside), polar angle θ = πv from +z.</summary>
    public static Vector3 LatLongDirection(float u, float v)
    {
        float phi = MathF.PI - 2 * MathF.PI * u, theta = MathF.PI * v;
        float s = MathF.Sin(theta);
        return new(s * MathF.Cos(phi), s * MathF.Sin(phi), MathF.Cos(theta));
    }

    private static float[] DrawLatLong(Func<Vector3, Vector3> radiance)
    {
        var img = new float[LatLongWidth * LatLongHeight * 3];
        Parallel.For(0, LatLongHeight, j =>
        {
            for (int i = 0; i < LatLongWidth; i++)
            {
                var sum = Vector3.Zero;
                for (int sy = 0; sy < 2; sy++)
                    for (int sx = 0; sx < 2; sx++)
                        sum += radiance(LatLongDirection((i + 0.25f + 0.5f * sx) / LatLongWidth, (j + 0.25f + 0.5f * sy) / LatLongHeight));
                Put(img, j * LatLongWidth + i, sum / 4);
            }
        });
        return img;
    }

    /// <summary>A user's image to 512 × 256: each output texel the AVERAGE of the input texels it covers when the image is larger (a
    /// small bright sun is kept, not skipped between samples), bilinear when it is smaller.</summary>
    private static float[] Resample(RadianceImage image)
    {
        var img = new float[LatLongWidth * LatLongHeight * 3];
        int w = image.Width, h = image.Height;
        bool down = w >= LatLongWidth && h >= LatLongHeight;
        Parallel.For(0, LatLongHeight, j =>
        {
            for (int i = 0; i < LatLongWidth; i++)
            {
                Vector3 c;
                if (down)
                {
                    int x0 = i * w / LatLongWidth, x1 = Math.Max(x0 + 1, (i + 1) * w / LatLongWidth);
                    int y0 = j * h / LatLongHeight, y1 = Math.Max(y0 + 1, (j + 1) * h / LatLongHeight);
                    c = Vector3.Zero;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            long k = 3L * ((long)y * w + x);
                            c += new Vector3(image.Rgb[k], image.Rgb[k + 1], image.Rgb[k + 2]);
                        }
                    c /= (x1 - x0) * (y1 - y0);
                }
                else c = LatLongBilinear(image.Rgb, w, h, (i + 0.5f) / LatLongWidth, (j + 0.5f) / LatLongHeight);
                Put(img, j * LatLongWidth + i, c);
            }
        });
        return img;
    }

    /// <summary>Bilinear in a lat–long image, wrapping in u and clamping in v.</summary>
    private static Vector3 LatLongBilinear(float[] rgb, int w, int h, float u, float v)
    {
        float x = u * w - 0.5f, y = v * h - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector3 At(int i, int j)
        {
            i = ((i % w) + w) % w; j = Math.Clamp(j, 0, h - 1);
            long k = 3L * ((long)j * w + i);
            return new(rgb[k], rgb[k + 1], rgb[k + 2]);
        }
        return Vector3.Lerp(Vector3.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector3.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }

    private static Vector3 LatLongAt(float[] latlong, Vector3 d)
    {
        float phi = MathF.Atan2(d.Y, d.X), theta = MathF.Acos(Math.Clamp(d.Z, -1, 1));
        float u = 0.5f - phi / (2 * MathF.PI);
        return LatLongBilinear(latlong, LatLongWidth, LatLongHeight, u - MathF.Floor(u), theta / MathF.PI);
    }

    // ── spherical harmonics: exact per-texel integrals ───────────────────────────────────────────────────────────────

    /// <summary>The nine irradiance coefficients of a lat–long image whose texels are constant: each basis function integrated in
    /// closed form over each texel's patch (θ band × φ span), then the band's convolution weight (π, 2π/3, π/4) and the basis's
    /// normalisation folded in, as <see cref="Pbr.Irradiance"/> reads them.</summary>
    public static Vector3[] ProjectSh(float[] latlong)
    {
        const int W = LatLongWidth, H = LatLongHeight;
        // θ parts, ∫ f(θ) sin θ dθ over each row: 1, sin θ (the l=1 x/y), cos θ, sin² θ (xy, x²−y²), sin θ cos θ (xz, yz), 3cos²θ − 1.
        var rows = new double[H, 6];
        for (int j = 0; j < H; j++)
        {
            double a = Math.PI * j / H, b = Math.PI * (j + 1) / H;
            double ca = Math.Cos(a), cb = Math.Cos(b), sa = Math.Sin(a), sb = Math.Sin(b);
            rows[j, 0] = ca - cb;
            rows[j, 1] = (b - a) / 2 - (Math.Sin(2 * b) - Math.Sin(2 * a)) / 4;            // ∫ sin²θ
            rows[j, 2] = (sb * sb - sa * sa) / 2;                                            // ∫ cosθ sinθ
            rows[j, 3] = (-cb + cb * cb * cb / 3) - (-ca + ca * ca * ca / 3);                 // ∫ sin³θ
            rows[j, 4] = (sb * sb * sb - sa * sa * sa) / 3;                                  // ∫ sin²θ cosθ
            rows[j, 5] = (-cb * cb * cb + cb) - (-ca * ca * ca + ca);                         // ∫ (3cos²θ − 1) sinθ
        }
        // φ parts over each column: 1, sin φ, cos φ, sin φ cos φ, cos 2φ. Column i spans φ from π − 2π(i+1)/W to π − 2πi/W.
        var cols = new double[W, 5];
        for (int i = 0; i < W; i++)
        {
            double a = Math.PI - 2 * Math.PI * (i + 1) / W, b = Math.PI - 2 * Math.PI * i / W;
            cols[i, 0] = b - a;
            cols[i, 1] = Math.Cos(a) - Math.Cos(b);
            cols[i, 2] = Math.Sin(b) - Math.Sin(a);
            cols[i, 3] = (Math.Cos(2 * a) - Math.Cos(2 * b)) / 4;
            cols[i, 4] = (Math.Sin(2 * b) - Math.Sin(2 * a)) / 2;
        }
        // Per basis: (θ part, φ part) — y = sinθ sinφ, z = cosθ, x = sinθ cosφ, xy = sin²θ sinφcosφ, yz = sinθcosθ sinφ,
        // 3z²−1, xz = sinθcosθ cosφ, x²−y² = sin²θ cos2φ.
        (int T, int P)[] parts = [(0, 0), (1, 1), (2, 0), (1, 2), (3, 3), (4, 1), (5, 0), (4, 2), (3, 4)];
        var acc = new double[9, 3];
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                int k = 3 * (j * W + i);
                double r = latlong[k], g = latlong[k + 1], bl = latlong[k + 2];
                for (int s = 0; s < 9; s++)
                {
                    double wgt = rows[j, parts[s].T] * cols[i, parts[s].P];
                    acc[s, 0] += r * wgt; acc[s, 1] += g * wgt; acc[s, 2] += bl * wgt;
                }
            }
        // Projection onto Y_lm = K·poly: L_lm = K·∫poly; irradiance coefficient = Â_l·K·L_lm = Â_l·K²·∫poly.
        double[] k2 =
        [
            Pbr.Sh0 * Pbr.Sh0 * Math.PI,
            Pbr.Sh1 * Pbr.Sh1 * 2 * Math.PI / 3, Pbr.Sh1 * Pbr.Sh1 * 2 * Math.PI / 3, Pbr.Sh1 * Pbr.Sh1 * 2 * Math.PI / 3,
            Pbr.Sh2 * Pbr.Sh2 * Math.PI / 4, Pbr.Sh2 * Pbr.Sh2 * Math.PI / 4, Pbr.Sh3 * Pbr.Sh3 * Math.PI / 4,
            Pbr.Sh2 * Pbr.Sh2 * Math.PI / 4, Pbr.Sh4 * Pbr.Sh4 * Math.PI / 4,
        ];
        var c = new Vector3[9];
        for (int s = 0; s < 9; s++) c[s] = new((float)(acc[s, 0] * k2[s]), (float)(acc[s, 1] * k2[s]), (float)(acc[s, 2] * k2[s]));
        return c;
    }

    // ── the octahedral levels ────────────────────────────────────────────────────────────────────────────────────────

    private static float[] DrawOctahedral(float[] latlong, int size)
    {
        var px = new float[size * size * 3];
        Parallel.For(0, size, j =>
        {
            for (int i = 0; i < size; i++)
            {
                var sum = Vector3.Zero;
                for (int sy = 0; sy < 2; sy++)
                    for (int sx = 0; sx < 2; sx++)
                        sum += LatLongAt(latlong, Pbr.OctDecode(new((i + 0.25f + 0.5f * sx) / size, (j + 0.25f + 0.5f * sy) / size)));
                Put(px, j * size + i, sum / 4);
            }
        });
        return px;
    }

    /// <summary>Level 0 and its 2 × 2 box reductions down to 1 × 1: what a filtered importance sample reads at its footprint.</summary>
    private static List<float[]> Pyramid(float[] level0, int size)
    {
        var p = new List<float[]> { level0 };
        for (int s = size / 2; s >= 1; s /= 2)
        {
            var prev = p[^1];
            int ps = s * 2;
            var next = new float[s * s * 3];
            for (int j = 0; j < s; j++)
                for (int i = 0; i < s; i++)
                    for (int c = 0; c < 3; c++)
                        next[3 * (j * s + i) + c] = 0.25f * (prev[3 * ((2 * j) * ps + 2 * i) + c] + prev[3 * ((2 * j) * ps + 2 * i + 1) + c]
                                                          + prev[3 * ((2 * j + 1) * ps + 2 * i) + c] + prev[3 * ((2 * j + 1) * ps + 2 * i + 1) + c]);
            p.Add(next);
        }
        return p;
    }

    private static Vector3 PyramidSample(List<float[]> p, Vector3 d, float lod)
    {
        lod = Math.Clamp(lod, 0, p.Count - 1);
        int l0 = (int)MathF.Floor(lod), l1 = Math.Min(l0 + 1, p.Count - 1);
        var uv = Pbr.OctEncode(d);
        var a = Bilinear3(p[l0], Pbr.EnvironmentSize >> l0, uv);
        if (l1 == l0) return a;
        return Vector3.Lerp(a, Bilinear3(p[l1], Pbr.EnvironmentSize >> l1, uv), lod - l0);
    }

    private static Vector3 Bilinear3(float[] px, int size, Vector2 uv)
    {
        float x = uv.X * size - 0.5f, y = uv.Y * size - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector3 At(int i, int j)
        {
            i = Math.Clamp(i, 0, size - 1); j = Math.Clamp(j, 0, size - 1);
            int k = 3 * (j * size + i);
            return new(px[k], px[k + 1], px[k + 2]);
        }
        return Vector3.Lerp(Vector3.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector3.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }

    /// <summary>One GGX level: per texel (N = V = R), <see cref="Samples"/> half vectors from the fixed sequence, each reading the
    /// pyramid at its sample's solid angle against a texel's, weighted by N·L.</summary>
    private static float[] PrefilterLevel(List<float[]> pyramid, int size, float roughness)
    {
        float alpha = roughness * roughness;
        float texel = 4 * MathF.PI / (Pbr.EnvironmentSize * Pbr.EnvironmentSize);
        var hs = new Vector3[Samples];
        for (int s = 0; s < Samples; s++) hs[s] = Pbr.SampleGgx(Pbr.Hammersley((uint)s, Samples), alpha);
        var px = new float[size * size * 3];
        Parallel.For(0, size, j =>
        {
            for (int i = 0; i < size; i++)
            {
                var n = Pbr.OctDecode(new((i + 0.5f) / size, (j + 0.5f) / size));
                var up = MathF.Abs(n.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
                var t = Vector3.Normalize(Vector3.Cross(up, n));
                var b = Vector3.Cross(n, t);
                var sum = Vector3.Zero;
                float weight = 0;
                foreach (var hl in hs)
                {
                    var h = t * hl.X + b * hl.Y + n * hl.Z;
                    float nh = hl.Z;
                    var l = 2 * nh * h - n;
                    float nl = Vector3.Dot(n, l);
                    if (nl <= 0) continue;
                    float pdf = Pbr.D(nh, alpha) / 4;                                   // D·(N·H)/(4 V·H), V = N
                    float omega = 1 / (Samples * MathF.Max(pdf, 1e-8f));
                    float lod = MathF.Max(0.5f * MathF.Log2(omega / texel) + 1, 0);
                    sum += PyramidSample(pyramid, Vector3.Normalize(l), lod) * nl;
                    weight += nl;
                }
                Put(px, j * size + i, weight > 0 ? sum / weight : Vector3.Zero);
            }
        });
        return px;
    }

    /// <summary>The largest finite half: an <c>.hdr</c> sun can be brighter, and a texel converted to +∞ turns every filtered
    /// lookup that touches it (∞ · 0) into NaN — a black or speckled highlight on the GPU.</summary>
    private const float HalfMax = 65504f;

    private static EnvironmentLevel ToHalf(float[] rgb, int size)
    {
        var h = new Half[size * size * 4];
        for (int p = 0; p < size * size; p++)
        {
            h[4 * p] = (Half)MathF.Min(rgb[3 * p], HalfMax);
            h[4 * p + 1] = (Half)MathF.Min(rgb[3 * p + 1], HalfMax);
            h[4 * p + 2] = (Half)MathF.Min(rgb[3 * p + 2], HalfMax);
            h[4 * p + 3] = (Half)1f;
        }
        return new EnvironmentLevel(size, size, h);
    }

    private static Half[] BuildBrdf()
    {
        var t = Pbr.BrdfTable();
        var h = new Half[t.Length * 4];
        for (int k = 0; k < t.Length; k++)
        {
            h[4 * k] = (Half)t[k].X;
            h[4 * k + 1] = (Half)t[k].Y;
            h[4 * k + 2] = (Half)0f;
            h[4 * k + 3] = (Half)1f;
        }
        return h;
    }

    private static void Put(float[] img, int p, Vector3 c)
    {
        img[3 * p] = c.X; img[3 * p + 1] = c.Y; img[3 * p + 2] = c.Z;
    }
}
