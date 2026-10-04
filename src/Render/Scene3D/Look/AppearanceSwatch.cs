// brief-em3d-108 R-em3d108-1b — the Materials editor's swatch: a sphere shaded on the CPU by THE reference (Pbr.Shade), under an
// environment, so what the swatch shows is what the realistic view draws. The sphere is ANALYTIC — each pixel's normal is the unit
// sphere's at that point, with no mesh and no rasteriser — so it needs no GPU and no 3D view open. It is seen from the iso view's
// direction (the default camera's), orthographically, over a small checker so a transmissive material reads as see-through.
//
// Cheap by construction: 96 × 96 is 9,216 pixels, of which the disc is ~6,500, one Shade each. The cost is held by a COUNTER of
// evaluations (Evaluations), never a timing: a redraw per edit is a fixed number of evaluations whatever the machine.

using System.Numerics;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;

namespace CircuitRF.Render.Scene3D.Look;

public static class AppearanceSwatch
{
    /// <summary>The swatch's side, pixels.</summary>
    public const int Size = 96;

    /// <summary>The disc's radius as a fraction of half the side: a pixel's margin for the antialiased rim.</summary>
    public const float Radius = 0.94f;

    /// <summary>The checker's cell, pixels, and its two greys (display values).</summary>
    public const int CheckerCell = 8;
    public const byte CheckerLight = 0xD8, CheckerDark = 0xB4;

    private static long _evaluations;

    /// <summary>How many pixels have been shaded (one <see cref="Pbr.Shade"/> each) in this process: the swatch's cost, counted.</summary>
    public static long Evaluations => Interlocked.Read(ref _evaluations);

    /// <summary>The view's direction toward the eye: the iso camera's (Camera3D's standard Iso view).</summary>
    public static Vector3 Back => Vector3.Normalize(new Vector3(1, -1, 1));

    /// <summary>
    /// The swatch as display colours: each pixel's premultiplied colour (0–1, sRGB-encoded, the sphere over the checker) and its
    /// coverage of the sphere (0 off it). Deterministic: the same appearance and environment give the same floats on any thread.
    /// </summary>
    public static Vector4[] Shade(in AppearanceValues look, PrefilteredEnvironment environment, C3dLook? lighting = null, int size = Size)
    {
        var m = PbrMaterial.Of(look);
        var light = RealisticLook.From(lighting).Lighting(environment);
        var back = Back;
        var right = Vector3.Normalize(new Vector3(1, 1, 0));
        var up = Vector3.Cross(right, -back);
        var pixels = new Vector4[size * size];
        float half = size / 2f, radius = Radius * half;
        long shaded = 0;
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float px = i + 0.5f - half, py = half - (j + 0.5f);
                float checker = ((i / CheckerCell + j / CheckerCell) & 1) == 0 ? CheckerLight / 255f : CheckerDark / 255f;
                var under = new Vector3(checker);
                float r = MathF.Sqrt(px * px + py * py);
                float coverage = Math.Clamp(radius - r + 0.5f, 0, 1);
                if (coverage <= 0)
                {
                    pixels[j * size + i] = new Vector4(under, 0);
                    continue;
                }
                // The normal at the nearest point of the disc (a rim pixel's centre may lie just outside it).
                float x = px / radius, y = py / radius, rr = x * x + y * y;
                if (rr > 1) { float s = 1 / MathF.Sqrt(rr); x *= s; y *= s; rr = 1; }
                var n = Vector3.Normalize(right * x + up * y + back * MathF.Sqrt(MathF.Max(0, 1 - rr)));
                var c = Pbr.Shade(m, n, back, environment, light);
                shaded++;
                // c is premultiplied by its own alpha; the checker shows through what is left, then the rim's coverage blends both.
                var over = new Vector3(c.X, c.Y, c.Z) + under * (1 - c.W);
                pixels[j * size + i] = new Vector4(Vector3.Lerp(under, over, coverage), coverage);
            }
        Interlocked.Add(ref _evaluations, shaded);
        return pixels;
    }

    /// <summary>The swatch as opaque RGBA8 rows, top first: what the editor shows.</summary>
    public static byte[] Rgba(in AppearanceValues look, PrefilteredEnvironment environment, C3dLook? lighting = null, int size = Size)
    {
        var px = Shade(look, environment, lighting, size);
        var bytes = new byte[px.Length * 4];
        for (int k = 0; k < px.Length; k++)
        {
            bytes[4 * k] = Byte(px[k].X);
            bytes[4 * k + 1] = Byte(px[k].Y);
            bytes[4 * k + 2] = Byte(px[k].Z);
            bytes[4 * k + 3] = 255;
        }
        return bytes;

        static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
    }
}
