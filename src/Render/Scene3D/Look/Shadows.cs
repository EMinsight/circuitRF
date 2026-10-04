// brief-em3d-107 R-em3d107-1/2/3 — how objects sit on each other in the realistic view: the key light's SHADOW MAP, screen-space
// AMBIENT OCCLUSION (contact shading) and the shadow-catching GROUND. Every constant the shader shares is written once here and once
// in scene.wgsl (the realistic section), and the gates hold the two equal, as Pbr.cs's are.
//
// The shadow map is orthographic along the key light, fitted to the casters' bounds, and re-rendered only when what it depends on
// changes (Scene3DFramePlan.ShadowKey): an orbit renders none. Occlusion is computed per frame from a depth prepass, so it follows
// the camera at no cost to anything else. Neither is a CPU computation: the mirror brief 110 builds uses these same numbers.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Look;

/// <summary>The key light's shadow: map sizes, the filter, the bias, and the light's frame fitted to the casters.</summary>
public static class Shadows
{
    /// <summary>R-em3d107-1a — the map's side in the live view, and in a picture (R-em3d107-5c).</summary>
    public const int LiveMapSize = 2048, ExportMapSize = 4096;

    /// <summary>R-em3d107-1d / owner decision D1 — an object whose resolved transmission is at least this casts no shadow: glass lets
    /// the light through, and a full shadow from it looks wrong.</summary>
    public const double CasterTransmissionLimit = 0.5;

    /// <summary>The key light's angular size where no softbox states one (an <c>.hdr</c>, whose light is all in its map and whose key
    /// therefore casts nothing), degrees.</summary>
    public const float DefaultKeyAngleDeg = 10;

    /// <summary>R-em3d107-1c — how far from its caster a shadow is judged, as a fraction of the scene's bounding radius: the filter's
    /// radius is this distance times tan(half the key's angle). A wide softbox (High key) gives a wide, soft edge and a small one (Dark)
    /// a crisp one; the radius is in WORLD units, so the edge is the same at every zoom.</summary>
    public const float PenumbraReach = 0.06f;

    /// <summary>The map's window is the casters' bounds grown by this fraction on each side (and by the filter's radius), so no tap of
    /// an edge receiver falls off the map.</summary>
    public const float WindowMargin = 0.02f;

    /// <summary>The smallest |cos| between a receiver's normal and the light the receiver-plane slope uses: steeper surfaces are lit
    /// so little by the key that their slope is clamped rather than let run to infinity.</summary>
    public const float MinSlopeCos = 0.2f;

    /// <summary>How far a receiver's lookup point is moved along its normal, in texels of the map.</summary>
    public const float NormalOffsetTexels = 1.5f;

    /// <summary>The filter's taps: a FIXED Poisson disc (overview §1e: no per-frame noise), scaled by the filter's radius. Each tap is
    /// itself a bilinear comparison (the comparison sampler's linear filter), which smooths the steps between taps.</summary>
    public static readonly Vector2[] Poisson =
    [
        new(-0.94201624f, -0.39906216f), new(0.94558609f, -0.76890725f), new(-0.094184101f, -0.92938870f), new(0.34495938f, 0.29387760f),
        new(-0.91588581f, 0.45771432f), new(-0.81544232f, -0.87912464f), new(-0.38277543f, 0.27676845f), new(0.97484398f, 0.75648379f),
        new(0.44323325f, -0.97511554f), new(0.53742981f, -0.47373420f), new(-0.26496911f, -0.41893023f), new(0.79197514f, 0.19090188f),
        new(-0.24188840f, 0.99706507f), new(-0.81409955f, 0.91437590f), new(0.19984126f, 0.78641367f), new(0.14383161f, -0.14100790f),
    ];

    /// <summary>The filter's radius in world units for a key of <paramref name="keyAngleDeg"/> over a scene of bounding radius
    /// <paramref name="sceneRadius"/>.</summary>
    public static float KernelRadius(float keyAngleDeg, float sceneRadius)
        => PenumbraReach * sceneRadius * MathF.Tan(Math.Clamp(keyAngleDeg, 0.5f, 120f) * MathF.PI / 360f);

    /// <summary>The light's frame: right, up and forward (from the light into the scene), orthonormal and right-handed.</summary>
    public static (Vector3 Right, Vector3 Up, Vector3 Forward) Frame(Vector3 towardLight)
    {
        var f = -Vector3.Normalize(towardLight);
        var helper = MathF.Abs(f.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX;
        var r = Vector3.Normalize(Vector3.Cross(f, helper));
        var u = Vector3.Cross(r, f);
        return (r, u, f);
    }
}

/// <summary>The light's window over the casters, in its own frame: a square (the map's texels are square) and a depth range.</summary>
public readonly record struct ShadowWindow(Vector3 Right, Vector3 Up, Vector3 Forward, float CentreX, float CentreY, float Half,
                                           float Near, float Far)
{
    /// <summary>World to the map's clip space as a ROW-vector matrix (p · M): x and y over [−1, 1] across the window, z the depth over
    /// [0, 1] from <see cref="Near"/> to <see cref="Far"/> along <see cref="Forward"/>.</summary>
    public Matrix4x4 Matrix
    {
        get
        {
            float s = 1 / Half, dz = 1 / MathF.Max(Far - Near, 1e-30f);
            return new Matrix4x4(
                Right.X * s, Up.X * s, Forward.X * dz, 0,
                Right.Y * s, Up.Y * s, Forward.Y * dz, 0,
                Right.Z * s, Up.Z * s, Forward.Z * dz, 0,
                -CentreX * s, -CentreY * s, -Near * dz, 1);
        }
    }

    /// <summary>The map's uv per world unit (the window is 2·Half across).</summary>
    public float UvPerWorld => 0.5f / Half;

    /// <summary>The depth per world unit.</summary>
    public float DepthPerWorld => 1 / MathF.Max(Far - Near, 1e-30f);
}

/// <summary>Screen-space ambient occlusion (overview D10): constants shared with the shader.</summary>
public static class Occlusion
{
    /// <summary>R-em3d107-2b — the occlusion's reach in world units, as a fraction of the scene's bounding radius: so the contact
    /// darkening looks the same at every zoom.</summary>
    public const float RadiusFraction = 0.08f;

    /// <summary>The horizon kernel: this many directions around each pixel, each walked in this many steps out to the radius. The
    /// directions are turned per pixel by a FIXED 4 × 4 interleave (never per frame), which the 4 × 4 blur then removes.</summary>
    public const int Directions = 8, Steps = 4;

    /// <summary>The horizon's sine below which nothing occludes (a flat neighbour never darkens its plane).</summary>
    public const float Bias = 0.1f;

    /// <summary>The largest radius in pixels the kernel walks (a near camera's world radius covers half the screen).</summary>
    public const float MaxPixels = 64f;

    /// <summary>The blur's depth test: a neighbour farther than this fraction of the radius from the pixel's point is another surface
    /// and does not mix in.</summary>
    public const float BlurReach = 0.25f;

    /// <summary>What the prepass target holds where nothing is drawn (no depth there).</summary>
    public const float Empty = 3.0e38f;
}

/// <summary>The shadow catcher (R-em3d107-3).</summary>
public static class Ground
{
    /// <summary>Its radius, in scene bounding radii.</summary>
    public const float RadiusScale = 4f;

    /// <summary>Where its fade to nothing begins, as a fraction of its radius.</summary>
    public const float FadeStart = 0.35f;

    /// <summary>How far below the model's lowest point it lies, in scene bounding radii: a model's bottom face never fights it.</summary>
    public const float Drop = 1e-4f;
}
