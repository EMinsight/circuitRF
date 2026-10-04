// brief-em3d-106 R-em3d106-4a / overview D8 — the procedural studios: a soft vertical gradient (ceiling, horizon, floor) and two or
// three rectangular area lights ("softboxes"), one of them the KEY light, whose direction brief 107 shadows from. A preset is DATA
// (a record below), never code: adding one is a row. No image asset ships, so there is no licence question, and every value is
// deterministic.
//
// The values are radiance in linear light, on the scale the base colours live on (a white Lambert surface under a uniform
// radiance of 1 reads 1). They are a first, visual tuning; brief 108's Look panel is where they get looked at.

using System.Numerics;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Render.Scene3D.Look;

/// <summary>One softbox: where its centre is (azimuth from +x toward +y, elevation above the horizon, degrees), its angular width and
/// height, its radiance, and how soft its edge is (the fraction of its half-size the fall-off spans).</summary>
public sealed record Softbox(double AzimuthDeg, double ElevationDeg, double WidthDeg, double HeightDeg, Vector3 Radiance, float Edge = 0.25f)
{
    public Vector3 Direction
    {
        get
        {
            double az = AzimuthDeg * Math.PI / 180, el = ElevationDeg * Math.PI / 180;
            return Vector3.Normalize(new((float)(Math.Cos(el) * Math.Cos(az)), (float)(Math.Cos(el) * Math.Sin(az)), (float)Math.Sin(el)));
        }
    }
}

/// <summary>A studio: the gradient's three colours (and how fast the floor comes in below the horizon), its softboxes, which one is
/// the key, and the key's DIRECT irradiance (a directional light on top of the environment: the highlight's sharpness and, in 107,
/// the shadow).</summary>
public sealed record StudioPreset(
    C3dStudio Studio, Vector3 Ceiling, Vector3 Horizon, Vector3 Floor, float FloorFalloff, IReadOnlyList<Softbox> Lights, int Key,
    float KeyDirect);

public static class StudioEnvironment
{
    public static readonly StudioPreset Studio = new(
        C3dStudio.Studio,
        Ceiling: new(0.70f, 0.71f, 0.74f), Horizon: new(0.42f, 0.42f, 0.43f), Floor: new(0.08f, 0.08f, 0.085f), FloorFalloff: 0.15f,
        Lights:
        [
            new Softbox(135, 40, 55, 38, new(3.2f, 3.1f, 3.0f)),         // key: front left, high
            new Softbox(-55, 18, 60, 40, new(0.9f, 0.92f, 0.95f)),        // fill: right, low
            new Softbox(0, 75, 45, 45, new(1.4f, 1.4f, 1.45f), 0.4f),     // top
        ],
        Key: 0, KeyDirect: 1.1f);

    public static readonly StudioPreset HighKey = new(
        C3dStudio.HighKey,
        Ceiling: new(1.00f, 1.00f, 1.00f), Horizon: new(0.93f, 0.93f, 0.93f), Floor: new(0.82f, 0.82f, 0.82f), FloorFalloff: 0.3f,
        Lights:
        [
            new Softbox(135, 38, 70, 50, new(2.0f, 2.0f, 2.0f), 0.4f),
            new Softbox(-45, 30, 70, 50, new(1.6f, 1.6f, 1.6f), 0.4f),
        ],
        Key: 0, KeyDirect: 0.6f);

    public static readonly StudioPreset Dark = new(
        C3dStudio.Dark,
        Ceiling: new(0.012f, 0.012f, 0.013f), Horizon: new(0.006f, 0.006f, 0.006f), Floor: new(0.003f, 0.003f, 0.003f), FloorFalloff: 0.1f,
        Lights:
        [
            new Softbox(90, 60, 35, 35, new(4.0f, 3.9f, 3.8f)),                  // key: overhead, a little forward
            new Softbox(160, 25, 15, 70, new(8.0f, 8.0f, 8.4f), 0.15f),          // rim, left behind
            new Softbox(-160, 25, 15, 70, new(8.0f, 8.0f, 8.4f), 0.15f),         // rim, right behind
        ],
        Key: 0, KeyDirect: 1.5f);

    public static StudioPreset Of(C3dStudio s) => s switch { C3dStudio.HighKey => HighKey, C3dStudio.Dark => Dark, _ => Studio };

    /// <summary>The preset's radiance toward the unit direction <paramref name="d"/> (its own frame: +z up).</summary>
    public static Vector3 Radiance(StudioPreset p, Vector3 d)
    {
        Vector3 c;
        if (d.Z >= 0)
        {
            float t = MathF.Sqrt(d.Z);
            c = Vector3.Lerp(p.Horizon, p.Ceiling, Smooth(t));
        }
        else c = Vector3.Lerp(p.Horizon, p.Floor, Smooth(Math.Clamp(-d.Z / MathF.Max(p.FloorFalloff, 1e-3f), 0, 1)));
        foreach (var box in p.Lights) c += box.Radiance * Weight(box, d);
        return c;
    }

    /// <summary>How much of <paramref name="box"/> lies toward <paramref name="d"/>: 1 inside, 0 outside, a smooth edge between. The
    /// box is a true rectangle on a plane facing its centre (gnomonic coordinates), so its edges are straight in a reflection.</summary>
    public static float Weight(Softbox box, Vector3 d)
    {
        var c = box.Direction;
        float dc = Vector3.Dot(d, c);
        if (dc <= 1e-4f) return 0;
        var right = MathF.Abs(c.Z) > 0.999f ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, c));
        var up = Vector3.Cross(c, right);
        float x = MathF.Abs(Vector3.Dot(d, right) / dc) / MathF.Tan((float)(box.WidthDeg * Math.PI / 360));
        float y = MathF.Abs(Vector3.Dot(d, up) / dc) / MathF.Tan((float)(box.HeightDeg * Math.PI / 360));
        float e = MathF.Max(box.Edge, 1e-3f);
        return Smooth(Math.Clamp((1 - x) / e, 0, 1)) * Smooth(Math.Clamp((1 - y) / e, 0, 1));
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    /// <summary>The key light: its direction in the environment's frame and its direct irradiance (the key softbox's colour,
    /// normalised, times <see cref="StudioPreset.KeyDirect"/>).</summary>
    public static (Vector3 Direction, Vector3 Radiance) Key(StudioPreset p)
    {
        var k = p.Lights[p.Key];
        float m = MathF.Max(k.Radiance.X, MathF.Max(k.Radiance.Y, k.Radiance.Z));
        return (k.Direction, m > 0 ? k.Radiance / m * p.KeyDirect : Vector3.Zero);
    }
}
