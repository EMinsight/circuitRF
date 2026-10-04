// brief-em3d-106 R-em3d106-3g — THE REFERENCE for the realistic view's shading. fs_pbr (scene.wgsl) is this file in WGSL: the same
// BRDF (GGX distribution, height-correlated Smith visibility, Schlick Fresnel), the same spherical-harmonic irradiance, the same
// octahedral environment mapping and split-sum lookup, the same raster approximation of transmission, and every constant written
// once here and once there (RealisticViewTests' scan holds the two equal). Brief 110's CPU mirror shades with Shade() below, and this
// brief's gates check the GPU against it.
//
// LINEAR LIGHT throughout. Base colours arrive linear (AppearanceColour is decoded from sRGB once, by the resolver), so the table the
// shader reads is linear too; ToneCurve.Display is the last step.

using System.Numerics;
using CircuitRF.Design.ThreeD.Appearance;

namespace CircuitRF.Render.Scene3D.Look;

/// <summary>An appearance as the shader reads it: one row of the table (three vec4f).</summary>
public readonly record struct PbrMaterial(
    Vector3 Base, float Metallic, float Roughness, float Transmission, float Ior, float Clearcoat, float ClearcoatRoughness,
    Vector3 Attenuation)
{
    public static PbrMaterial Of(in AppearanceValues v) => new(
        new((float)v.BaseColor.R, (float)v.BaseColor.G, (float)v.BaseColor.B), (float)v.Metallic, (float)v.Roughness,
        (float)v.Transmission, (float)v.Ior, (float)v.Clearcoat, (float)v.ClearcoatRoughness,
        new((float)v.AttenuationColor.R, (float)v.AttenuationColor.G, (float)v.AttenuationColor.B));
}

/// <summary>What the shader samples, in the ENVIRONMENT's frame (the caller rotates world directions into it).</summary>
public interface IPbrEnvironment
{
    /// <summary>Prefiltered radiance toward <paramref name="direction"/> at <paramref name="roughness"/> (0–1).</summary>
    Vector3 Radiance(Vector3 direction, float roughness);
    /// <summary>Irradiance E on a surface facing <paramref name="normal"/> (Lambert's radiance is base·E/π).</summary>
    Vector3 Irradiance(Vector3 normal);
    /// <summary>The split-sum scale and bias at (N·V, roughness).</summary>
    Vector2 Brdf(float nv, float roughness);
}

/// <summary>The lighting a frame's uniforms carry: exposure, the environment's intensity and rotation, the key light.</summary>
public readonly record struct PbrLighting(float Exposure, float Intensity, float RotationDegrees, Vector3 KeyDirectionWorld, Vector3 KeyRadiance)
{
    public float Cos => MathF.Cos(RotationDegrees * MathF.PI / 180);
    public float Sin => MathF.Sin(RotationDegrees * MathF.PI / 180);

    /// <summary>A world direction in the environment's frame: the environment is turned by +rotation about +z, so a lookup turns
    /// by −rotation (scene.wgsl env_dir).</summary>
    public Vector3 ToEnvironment(Vector3 w) { float c = Cos, s = Sin; return new(c * w.X + s * w.Y, -s * w.X + c * w.Y, w.Z); }

    /// <summary>The reverse: an environment direction in the world.</summary>
    public Vector3 ToWorld(Vector3 e) { float c = Cos, s = Sin; return new(c * e.X - s * e.Y, s * e.X + c * e.Y, e.Z); }
}

public static class Pbr
{
    // ── constants (each one is repeated in scene.wgsl; the scan compares them) ───────────────────────────────────────

    /// <summary>A dielectric's reflectance at normal incidence when its IOR is glTF's 1.5 (scene.wgsl DIELECTRIC_F0).</summary>
    public const float DielectricF0 = 0.04f;
    /// <summary>The roughness the KEY LIGHT's lobe never goes below: at 0 the GGX lobe is a delta and a point light has no highlight
    /// to draw. The environment's lookup uses the true roughness.</summary>
    public const float MinRoughness = 0.045f;
    /// <summary>The prefiltered environment's levels: roughness 0, 0.25, 0.5, 0.75, 1 at mip levels 0–4.</summary>
    public const int EnvironmentLevels = 5;
    /// <summary>Level 0's side (octahedral, square).</summary>
    public const int EnvironmentSize = 256;
    /// <summary>The split-sum table's side, and the samples each entry integrates.</summary>
    public const int BrdfSize = 32, BrdfSamples = 512;
    /// <summary>The smallest N·V the shading takes (a grazing fragment's).</summary>
    public const float MinNdotV = 1e-4f;

    /// <summary>The real spherical harmonics' normalisations: Y00, the l = 1 band, then xy/yz/xz, (3z² − 1) and (x² − y²).</summary>
    public const float Sh0 = 0.282095f, Sh1 = 0.488603f, Sh2 = 1.092548f, Sh3 = 0.315392f, Sh4 = 0.546274f;

    // ── the appearance table (binding 2: 256 rows, three vec4f each — overview D16) ────────────────────────────────────

    public const int TableRows = Scene3DBuilder.AppearanceSlots, RowFloats = 12;
    public const int TableFloats = TableRows * RowFloats, TableBytes = TableFloats * 4;

    /// <summary>The scene's appearance table as the shader's uniform: base colour + metallic; roughness, transmission, IOR,
    /// clear coat; clear-coat roughness + attenuation colour. Rows past the scene's are zero.</summary>
    public static float[] Table(IReadOnlyList<AppearanceValues> rows)
    {
        var t = new float[TableFloats];
        for (int k = 0; k < Math.Min(rows.Count, TableRows); k++)
        {
            var m = PbrMaterial.Of(rows[k]);
            var r = t.AsSpan(k * RowFloats, RowFloats);
            r[0] = m.Base.X; r[1] = m.Base.Y; r[2] = m.Base.Z; r[3] = m.Metallic;
            r[4] = m.Roughness; r[5] = m.Transmission; r[6] = m.Ior; r[7] = m.Clearcoat;
            r[8] = m.ClearcoatRoughness; r[9] = m.Attenuation.X; r[10] = m.Attenuation.Y; r[11] = m.Attenuation.Z;
        }
        return t;
    }

    // ── the octahedral map (+z at the centre) ──────────────────────────────────────────────────────────────────────

    /// <summary>A unit direction to (u, v) in [0, 1]²: the upper hemisphere is the centre diamond, the lower folded into the corners.</summary>
    public static Vector2 OctEncode(Vector3 d)
    {
        float s = MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z);
        float x = d.X / s, y = d.Y / s;
        if (d.Z < 0)
        {
            float fx = (1 - MathF.Abs(y)) * (x >= 0 ? 1 : -1);
            float fy = (1 - MathF.Abs(x)) * (y >= 0 ? 1 : -1);
            (x, y) = (fx, fy);
        }
        return new(x * 0.5f + 0.5f, y * 0.5f + 0.5f);
    }

    public static Vector3 OctDecode(Vector2 uv)
    {
        float x = uv.X * 2 - 1, y = uv.Y * 2 - 1;
        float z = 1 - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0)
        {
            float fx = (1 - MathF.Abs(y)) * (x >= 0 ? 1 : -1);
            float fy = (1 - MathF.Abs(x)) * (y >= 0 ? 1 : -1);
            (x, y) = (fx, fy);
        }
        return Vector3.Normalize(new(x, y, z));
    }

    // ── the microfacet BRDF ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Trowbridge-Reitz (GGX) with α = roughness².</summary>
    public static float D(float nh, float alpha)
    {
        float a2 = alpha * alpha;
        float f = nh * nh * (a2 - 1) + 1;
        return a2 / (MathF.PI * f * f);
    }

    /// <summary>Height-correlated Smith visibility, G / (4 N·L N·V).</summary>
    public static float V(float nv, float nl, float alpha)
    {
        float a2 = alpha * alpha;
        float gv = nl * MathF.Sqrt(nv * nv * (1 - a2) + a2);
        float gl = nv * MathF.Sqrt(nl * nl * (1 - a2) + a2);
        return 0.5f / MathF.Max(gv + gl, 1e-7f);
    }

    public static Vector3 F(Vector3 f0, float vh)
    {
        float k = MathF.Pow(1 - Math.Clamp(vh, 0, 1), 5);
        return f0 + (Vector3.One - f0) * k;
    }

    public static float F(float f0, float vh) => f0 + (1 - f0) * MathF.Pow(1 - Math.Clamp(vh, 0, 1), 5);

    /// <summary>A dielectric's F0 from its optical index: ((n − 1)/(n + 1))², which is <see cref="DielectricF0"/> at 1.5.</summary>
    public static float F0Of(float ior)
    {
        float r = (ior - 1) / (ior + 1);
        return r * r;
    }

    // ── importance sampling (a FIXED sequence: the same bytes every run, on every thread) ───────────────────────────

    /// <summary>The Hammersley point <paramref name="i"/> of <paramref name="n"/>: (i/n, the radical inverse of i in base 2).</summary>
    public static Vector2 Hammersley(uint i, uint n)
    {
        uint b = i;
        b = (b << 16) | (b >> 16);
        b = ((b & 0x55555555u) << 1) | ((b & 0xAAAAAAAAu) >> 1);
        b = ((b & 0x33333333u) << 2) | ((b & 0xCCCCCCCCu) >> 2);
        b = ((b & 0x0F0F0F0Fu) << 4) | ((b & 0xF0F0F0F0u) >> 4);
        b = ((b & 0x00FF00FFu) << 8) | ((b & 0xFF00FF00u) >> 8);
        return new((float)i / n, b * 2.3283064365386963e-10f);
    }

    /// <summary>A half vector about +z drawn from GGX's D·(N·H) for α, at the sample point <paramref name="xi"/>.</summary>
    public static Vector3 SampleGgx(Vector2 xi, float alpha)
    {
        float phi = 2 * MathF.PI * xi.X;
        float cos = MathF.Sqrt((1 - xi.Y) / (1 + (alpha * alpha - 1) * xi.Y));
        float sin = MathF.Sqrt(MathF.Max(0, 1 - cos * cos));
        return new(sin * MathF.Cos(phi), sin * MathF.Sin(phi), cos);
    }

    /// <summary>The split-sum pair (scale, bias) for F0 at (N·V, roughness): ∫ f·N·L with F = F0·scale + bias.</summary>
    public static Vector2 IntegrateBrdf(float nv, float roughness, int samples = BrdfSamples)
    {
        nv = MathF.Max(nv, MinNdotV);
        var v = new Vector3(MathF.Sqrt(MathF.Max(0, 1 - nv * nv)), 0, nv);
        float alpha = roughness * roughness, a = 0, b = 0;
        for (uint i = 0; i < samples; i++)
        {
            var h = SampleGgx(Hammersley(i, (uint)samples), alpha);
            float vh = Vector3.Dot(v, h);
            var l = 2 * vh * h - v;
            float nl = l.Z, nh = h.Z;
            if (nl <= 0 || nh <= 0) continue;
            float g = V(nv, nl, alpha) * 4 * nl * vh / nh;
            float fc = MathF.Pow(1 - Math.Clamp(vh, 0, 1), 5);
            a += (1 - fc) * g;
            b += fc * g;
        }
        return new(a / samples, b / samples);
    }

    /// <summary>The 32 × 32 split-sum table, (scale, bias) per entry: u = N·V, v = roughness, each at its texel centre.</summary>
    public static Vector2[] BrdfTable()
    {
        var t = new Vector2[BrdfSize * BrdfSize];
        for (int j = 0; j < BrdfSize; j++)
            for (int i = 0; i < BrdfSize; i++)
                t[j * BrdfSize + i] = IntegrateBrdf((i + 0.5f) / BrdfSize, (j + 0.5f) / BrdfSize);
        return t;
    }

    // ── spherical harmonics (nine coefficients, pre-multiplied into irradiance) ───────────────────────────────────────

    /// <summary>Irradiance from the nine coefficients the uniforms carry: E(n) = c0 + c1 y + c2 z + c3 x + c4 xy + c5 yz + c6 (3z² − 1)
    /// + c7 xz + c8 (x² − y²) — each c already the band's convolution weight × the basis's normalisation × the projection.</summary>
    public static Vector3 Irradiance(ReadOnlySpan<Vector3> c, Vector3 n)
        => c[0] + c[1] * n.Y + c[2] * n.Z + c[3] * n.X + c[4] * (n.X * n.Y) + c[5] * (n.Y * n.Z)
         + c[6] * (3 * n.Z * n.Z - 1) + c[7] * (n.X * n.Z) + c[8] * (n.X * n.X - n.Y * n.Y);

    // ── one fragment ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// fs_pbr for one fragment, before hover and selection: the PREMULTIPLIED display colour and its coverage. <paramref name="n"/> is
    /// the (already flipped, two-sided) unit normal, <paramref name="v"/> the unit vector to the eye, both in the world;
    /// <paramref name="coverage"/> a stated transparency's opacity (1 otherwise). An opaque material returns coverage 1.
    /// </summary>
    public static Vector4 Shade(in PbrMaterial m, Vector3 n, Vector3 v, IPbrEnvironment env, in PbrLighting light, float coverage = 1)
    {
        var lin = Radiance(m, n, v, env, light, out float alpha);
        alpha *= coverage;
        var display = ToneCurve.Display(lin, light.Exposure) * coverage;
        return new(display, alpha);
    }

    /// <summary>The fragment's linear (premultiplied) radiance and its coverage, before exposure and the curve.</summary>
    public static Vector3 Radiance(in PbrMaterial m, Vector3 n, Vector3 v, IPbrEnvironment env, in PbrLighting light, out float alpha)
    {
        float nv = MathF.Max(Vector3.Dot(n, v), MinNdotV);
        float rough = Math.Clamp(m.Roughness, 0, 1), metal = Math.Clamp(m.Metallic, 0, 1);
        var f0 = Vector3.Lerp(new Vector3(F0Of(m.Ior)), m.Base, metal);
        var lut = env.Brdf(nv, rough);
        var specAlbedo = f0 * lut.X + new Vector3(lut.Y);
        var r = 2 * Vector3.Dot(n, v) * n - v;
        var reflected = env.Radiance(light.ToEnvironment(r), rough) * light.Intensity;
        var irradiance = env.Irradiance(light.ToEnvironment(n)) * light.Intensity;
        float t = Math.Clamp(m.Transmission, 0, 1);

        var spec = reflected * specAlbedo;
        var diffuse = (Vector3.One - specAlbedo) * (1 - metal) * (1 - t) * m.Base * irradiance / MathF.PI;

        // the key light, direct (shadowed in brief 107)
        var l = light.KeyDirectionWorld;
        float nl = Vector3.Dot(n, l);
        var direct = Vector3.Zero;
        if (nl > 0 && light.KeyRadiance != Vector3.Zero)
        {
            var h = Vector3.Normalize(l + v);
            float nh = MathF.Max(Vector3.Dot(n, h), 0), vh = MathF.Max(Vector3.Dot(v, h), 0);
            float ad = MathF.Max(rough, MinRoughness);
            ad *= ad;
            var fr = F(f0, vh);
            var lobe = fr * (D(nh, ad) * V(nv, nl, ad));
            var body = (Vector3.One - fr) * (1 - metal) * (1 - t) * m.Base / MathF.PI;
            direct = (lobe + body) * light.KeyRadiance * light.Intensity * nl;
        }

        var surface = spec + diffuse + direct;

        // the clear coat: a dielectric lobe (F0 0.04) over the body, its energy taken from it
        float cc = Math.Clamp(m.Clearcoat, 0, 1);
        if (cc > 0)
        {
            float ccr = Math.Clamp(m.ClearcoatRoughness, 0, 1);
            var lc = env.Brdf(nv, ccr);
            float coatAlbedo = DielectricF0 * lc.X + lc.Y;
            var coat = env.Radiance(light.ToEnvironment(r), ccr) * light.Intensity * coatAlbedo;
            if (nl > 0 && light.KeyRadiance != Vector3.Zero)
            {
                var h = Vector3.Normalize(l + v);
                float nh = MathF.Max(Vector3.Dot(n, h), 0), vh = MathF.Max(Vector3.Dot(v, h), 0);
                float ac = MathF.Max(ccr, MinRoughness);
                ac *= ac;
                coat += light.KeyRadiance * light.Intensity * (F(DielectricF0, vh) * D(nh, ac) * V(nv, nl, ac) * nl);
            }
            surface = surface * (1 - cc * coatAlbedo) + coat * cc;
        }

        // Transmission, a raster approximation (R-em3d106-3d): coverage 1 − T·(1 − F)·τ̄ with τ̄ the attenuation colour's mean, so a
        // clear body lets the background through untinted and a coloured one dims it; the absorbed share glows in the attenuation
        // colour, lit by the environment, which is how the tint reads. The reflection is added on top. Real refraction and the
        // distance term are glTF export's (111).
        alpha = 1;
        if (t > 0)
        {
            float fs = (specAlbedo.X + specAlbedo.Y + specAlbedo.Z) / 3;
            float tau = (m.Attenuation.X + m.Attenuation.Y + m.Attenuation.Z) / 3;
            float through = t * (1 - fs);
            alpha = 1 - through * tau;
            surface += through * (1 - tau) * m.Attenuation * irradiance / MathF.PI;
        }
        return surface;
    }
}
