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
