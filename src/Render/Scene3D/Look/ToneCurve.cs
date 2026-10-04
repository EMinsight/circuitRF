// brief-em3d-106 R-em3d106-3e / overview D7 — the realistic view's tone curve: the Khronos PBR Neutral curve, applied FORWARD (at the end
// of the PBR fragment, never as a post pass, so a field fragment never meets it — overview rule 2), then the sRGB encode, because
// the swap image is BGRA8 UNORM and not an sRGB format: the shader writes display values itself.
//
// THE REFERENCE. scene.wgsl repeats every constant here (pbr_neutral, srgb_encode); RealisticViewTests' source scan fails when the
// two disagree (R-em3d106-3g). Brief 110's CPU mirror calls these functions, not a copy of them.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Look;

public static class ToneCurve
{
    // ── PBR Neutral (Khronos, 2024) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where highlight compression begins: 0.8 − 0.04.</summary>
    public const float StartCompression = 0.76f;
    /// <summary>How far a compressed highlight is pulled toward white.</summary>
    public const float Desaturation = 0.15f;
    /// <summary>The toe: below <see cref="ToeBreak"/> the darkest channel loses x − <see cref="ToeSlope"/>·x², above it
    /// <see cref="ToeOffset"/>.</summary>
    public const float ToeBreak = 0.08f, ToeSlope = 6.25f, ToeOffset = 0.04f;

    // ── sRGB (IEC 61966-2-1) ─────────────────────────────────────────────────────────────────────────────────────

    public const float SrgbBreak = 0.0031308f, SrgbLinearSlope = 12.92f, SrgbScale = 1.055f, SrgbOffset = 0.055f, SrgbGamma = 2.4f;
    /// <summary>The decode's breakpoint (the encode's, in display values).</summary>
    public const float SrgbDecodeBreak = 0.04045f;

    /// <summary>The PBR Neutral curve on a linear colour (≥ 0): faithful base colours below the knee, highlights compressed and
    /// desaturated above it. 0 maps to 0, and it is monotonic along the grey axis.</summary>
    public static Vector3 Neutral(Vector3 c)
    {
        float x = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float offset = x < ToeBreak ? x - ToeSlope * x * x : ToeOffset;
        c -= new Vector3(offset);
        float peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (peak < StartCompression) return c;
        const float d = 1 - StartCompression;
        float newPeak = 1 - d * d / (peak + d - StartCompression);
        c *= newPeak / peak;
        float g = 1 - 1 / (Desaturation * (peak - newPeak) + 1);
        return Vector3.Lerp(c, new Vector3(newPeak), g);
    }

    /// <summary>A linear channel (0–1) to its display value.</summary>
    public static float SrgbEncode(float c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= SrgbBreak ? c * SrgbLinearSlope : SrgbScale * MathF.Pow(c, 1 / SrgbGamma) - SrgbOffset;
    }

    /// <summary>A display channel (0–1) to linear.</summary>
    public static float SrgbDecode(float c)
        => c <= SrgbDecodeBreak ? c / SrgbLinearSlope : MathF.Pow((c + SrgbOffset) / SrgbScale, SrgbGamma);

    public static Vector3 SrgbEncode(Vector3 c) => new(SrgbEncode(c.X), SrgbEncode(c.Y), SrgbEncode(c.Z));

    /// <summary>Exposure, then the curve, then the encode: what fs_pbr does last, before hover and selection tint the result.</summary>
    public static Vector3 Display(Vector3 linear, float exposure) => SrgbEncode(Neutral(Vector3.Max(linear * exposure, Vector3.Zero)));
}
