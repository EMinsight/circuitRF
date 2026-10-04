// brief-em3d-106 R-em3d106-2b / R-em3d106-5 — the Look as the frame plan reads it: parsed once when the document's Look changes, so a
// frame allocates nothing. And THE TABLE of what the realistic view hides (overview D11): one row per kind of CAD chrome, each with
// the Look key that shows it again. Adding a kind of chrome later is one row here and one case in Scene3DFramePlan.ChromeOf; the
// gate iterates this table.

using System.Numerics;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Render.Scene3D.Look;

/// <summary>R-em3d106-2b — a kind of CAD chrome the realistic view hides by default.</summary>
public enum Scene3DChrome
{
    /// <summary>The general feature-edge lines (the scene's line batches) of everything not in another row.</summary>
    Edges,
    /// <summary>The drawing grid (the Grid pipeline).</summary>
    Grid,
    /// <summary>The mesh, FDTD-grid and section overlays (overlay buffers 0–2).</summary>
    Overlays,
    /// <summary>The air box's faces and edges, and air.</summary>
    AirBox,
    /// <summary>Ports: their surfaces and arrows.</summary>
    Ports,
    /// <summary>Boundaries and face tints.</summary>
    Boundaries,
    /// <summary>Reference images: image sheets and images on faces.</summary>
    Images,
}

/// <summary>The parsed Look: what a realistic frame needs, with no string in sight.</summary>
public sealed class RealisticLook
{
    /// <summary>The table: each chrome row and the Look key that shows it again. Read by the plan, the schema and the gates.</summary>
    public static readonly IReadOnlyList<(Scene3DChrome Row, string Key)> Chrome =
    [
        (Scene3DChrome.Edges, nameof(C3dLook.ShowEdges)),
        (Scene3DChrome.Grid, nameof(C3dLook.ShowGrid)),
        (Scene3DChrome.Overlays, nameof(C3dLook.ShowOverlays)),
        (Scene3DChrome.AirBox, nameof(C3dLook.ShowAirBox)),
        (Scene3DChrome.Ports, nameof(C3dLook.ShowPorts)),
        (Scene3DChrome.Boundaries, nameof(C3dLook.ShowBoundaries)),
        (Scene3DChrome.Images, nameof(C3dLook.ShowImages)),
    ];

    private readonly bool[] _shows = new bool[Chrome.Count];

    public C3dStudio Studio { get; private init; } = C3dLook.DefaultStudio;
    /// <summary>The <c>.hdr</c>'s path as the Look writes it (relative to the <c>.c3d</c>), or null for a studio.</summary>
    public string? HdrPath { get; private init; }
    public float RotationDegrees { get; private init; } = (float)C3dLook.DefaultRotation;
    public float Intensity { get; private init; } = (float)C3dLook.DefaultIntensity;
    public float ExposureEv { get; private init; } = (float)C3dLook.DefaultExposure;
    public C3dBackgroundKind Background { get; private init; }
    /// <summary>A solid's colour (Top) or a gradient's two, display values 0–1.</summary>
    public Vector3 Top { get; private init; }
    public Vector3 Bottom { get; private init; }

    /// <summary>2^EV: what the shader multiplies by before the tone curve.</summary>
    public float Exposure => MathF.Pow(2, ExposureEv);

    /// <summary>brief-em3d-107 R-em3d107-4 — the key light's shadows, contact shading, and the shadow-catching ground (each on unless the
    /// Look says false).</summary>
    public bool Shadows { get; private init; } = true;
    public bool AmbientOcclusion { get; private init; } = true;
    public bool Ground { get; private init; } = true;

    /// <summary>brief-em3d-109 R-em3d109-4a — how a field plot is drawn here, and its opacity over the material under it (0–1).</summary>
    public C3dFieldStyle FieldStyle { get; private init; }
    public float FieldOpacity { get; private init; } = 1;

    /// <summary>R-em3d109-3b — whether a field lets what is under it show through (its colours are then blends).</summary>
    public bool FieldBlends => FieldOpacity < 1;

    /// <summary>R-em3d109-3a — Glow's dimming of everything around the field, in EV (owner decision D2: −2.5, tuned by eye on the shipped
    /// thermal and connector examples). Every lit surface's exposure and the backdrop take it; the field takes none.</summary>
    public const float GlowDim = -2.5f;

    /// <summary>The exposure fs_pbr and the environment backdrop multiply by: 2^EV, and with Glow 2^(EV + <see cref="GlowDim"/>).</summary>
    public float SurfaceExposure => FieldStyle == C3dFieldStyle.Glow ? MathF.Pow(2, ExposureEv + GlowDim) : Exposure;

    /// <summary>R-em3d109-3a — a DISPLAY colour (the theme's, a solid or gradient background) darkened as Glow darkens a lit surface:
    /// decoded, scaled by 2^GlowDim, encoded. Unchanged in any other style.</summary>
    public Vector3 Backdrop(Vector3 display)
    {
        if (FieldStyle != C3dFieldStyle.Glow) return display;
        float k = MathF.Pow(2, GlowDim);
        return new(Dim(display.X), Dim(display.Y), Dim(display.Z));
        float Dim(float c) => ToneCurve.SrgbEncode(ToneCurve.SrgbDecode(c) * k);
    }

    /// <summary>
    /// R-em3d109-4b (owner decision D13) — the label a picture of a field drawn with this Look carries under its legend stack:
    /// <c>Lit Fields</c>, <c>Blended Fields</c>, <c>Lit, Blended Fields</c>, or null for Exact and Glow at full opacity, whose colours are
    /// the legend's. THE one spelling: the live view, Export Picture, Copy and <c>render</c> all ask here.
    /// </summary>
    public string? FieldIndicator => FieldIndicatorText(FieldStyle == C3dFieldStyle.Lit, FieldBlends);

    public const string IndicatorLit = "Lit", IndicatorBlended = "Blended", IndicatorFields = "Fields";

    public static string? FieldIndicatorText(bool lit, bool blended) => (lit, blended) switch
    {
        (true, true) => $"{IndicatorLit}, {IndicatorBlended} {IndicatorFields}",
        (true, false) => $"{IndicatorLit} {IndicatorFields}",
        (false, true) => $"{IndicatorBlended} {IndicatorFields}",
        _ => null,
    };

    public bool Shows(Scene3DChrome row) => _shows[(int)row];

    public static readonly RealisticLook Default = From(null);

    public static RealisticLook From(C3dLook? look)
    {
        look ??= new C3dLook();
        var (studio, path) = look.EnvironmentOf(out _);
        look.TryBackground(out var kind, out var top, out var bottom);
        static Vector3 V((byte R, byte G, byte B) c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
        var r = new RealisticLook
        {
            Studio = studio, HdrPath = path, RotationDegrees = (float)look.RotationDegrees, Intensity = (float)look.IntensityValue,
            ExposureEv = (float)look.ExposureValue, Background = kind, Top = V(top), Bottom = V(bottom),
            Shadows = look.Shadows != false, AmbientOcclusion = look.AmbientOcclusion != false, Ground = look.Ground != false,
            FieldStyle = look.FieldStyleOf(out _), FieldOpacity = (float)(look.FieldOpacityValue / C3dLook.FieldOpacityMax),
        };
        bool?[] shows = [look.ShowEdges, look.ShowGrid, look.ShowOverlays, look.ShowAirBox, look.ShowPorts, look.ShowBoundaries, look.ShowImages];
        for (int k = 0; k < shows.Length; k++) r._shows[k] = shows[k] == true;
        return r;
    }

    /// <summary>The lighting the reference shading and the uniforms take, for <paramref name="env"/>.</summary>
    public PbrLighting Lighting(PrefilteredEnvironment env)
    {
        var l = new PbrLighting(Exposure, Intensity, RotationDegrees, default, env.KeyRadiance);
        return l with { KeyDirectionWorld = l.ToWorld(env.KeyDirection) };
    }
}
