// brief-em3d-46 R-em3d46-5 — the move gizmo's geometry and its hit test, in SCREEN space, from the camera alone.
//
// The gizmo is 2D chrome (Viewer3DOverlay draws it): three arrows along the world axes from the pivot, and three
// small squares between them for the planes. It keeps a constant size on screen, so it is laid out in pixels
// from the projected directions of the axes, never in world units — which also means its hit test is a distance
// to a projected segment and a point-in-parallelogram, below the firewall and tested headlessly (gate 8).
//
// An axis pointing (nearly) at the viewer projects to almost nothing; its arrow is then not drawn and cannot be
// hit, and neither can the two squares that use it, rather than offering a handle a few pixels long that moves
// the selection wildly for a small drag.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>A gizmo handle: an axis arrow (a move along it) or a plane square (a move in it, named by its normal).</summary>
public enum GizmoHandle { None, AxisX, AxisY, AxisZ, PlaneX, PlaneY, PlaneZ }

/// <summary>
/// The gizmo on screen (pixels, the pane's DIPs): the pivot's point, each axis's arrow tip (an unusable axis has
/// <see cref="AxisUsable"/> false), and each plane square's four corners.
/// </summary>
public sealed class GizmoLayout
{
    public Vector2 Centre;
    public readonly Vector2[] Tips = new Vector2[3];
    public readonly Vector2[] Directions = new Vector2[3];
    public readonly bool[] AxisUsable = new bool[3];
    /// <summary>Square k (normal axis k) — corners in order, or unusable when either of its axes is.</summary>
    public readonly Vector2[][] Squares = [new Vector2[4], new Vector2[4], new Vector2[4]];
    public readonly bool[] SquareUsable = new bool[3];
}

public static class GizmoGeometry
{
    /// <summary>The arrows' length on screen, pixels.</summary>
    public const float ArmPixels = 64;

    /// <summary>Where a plane square starts and ends along each of its two arms, pixels from the pivot.</summary>
    public const float SquareFrom = 16, SquareTo = 30;

    /// <summary>How close to an arrow a press must be, pixels.</summary>
    public const float HitPixels = 7;

    /// <summary>An arrow shorter than this fraction of its length (the axis points at the viewer) is not offered.</summary>
    public const float MinProjected = 0.2f;

    /// <summary>The axis a handle moves along, or the normal of the plane it moves in.</summary>
    public static int AxisOf(GizmoHandle h) => h switch
    {
        GizmoHandle.AxisX or GizmoHandle.PlaneX => 0,
        GizmoHandle.AxisY or GizmoHandle.PlaneY => 1,
        GizmoHandle.AxisZ or GizmoHandle.PlaneZ => 2,
        _ => -1,
    };

    public static bool IsPlane(GizmoHandle h) => h is GizmoHandle.PlaneX or GizmoHandle.PlaneY or GizmoHandle.PlaneZ;

    /// <summary>
    /// Lays the gizmo out at <paramref name="pivotLocal"/> (scene-local metres) for a <paramref name="width"/> ×
    /// <paramref name="height"/> view. Null when the pivot is behind the camera. Allocates the layout only when
    /// <paramref name="into"/> is null.
    /// </summary>
    public static GizmoLayout? Layout(in Camera3D camera, Vector3 pivotLocal, float width, float height, GizmoLayout? into = null)
    {
        var (cx, cy, front) = camera.Project(pivotLocal, width, height);
        if (!front) return null;
        var g = into ?? new GizmoLayout();
        g.Centre = new Vector2(cx, cy);
        // A world step that is a few pixels at the pivot, so perspective foreshortening reads correctly.
        float step = MathF.Max(1e-12f, camera.WorldPerPixel(height) * 10f * MathF.Max(0.05f, camera.Projection == Projection3D.Perspective
            ? MathF.Abs(camera.ViewDepth(pivotLocal)) / MathF.Max(1e-12f, camera.Distance) : 1f));
        for (int k = 0; k < 3; k++)
        {
            var axis = k == 0 ? Vector3.UnitX : k == 1 ? Vector3.UnitY : Vector3.UnitZ;
            var (x, y, ok) = camera.Project(pivotLocal + axis * step, width, height);
            var d = new Vector2(x - cx, y - cy);
            float len = d.Length();
            // Ten pixels' worth of world at the pivot projects to ~10 px when the axis lies across the view.
            g.AxisUsable[k] = ok && len >= 10f * MinProjected;
            g.Directions[k] = len > 1e-6f ? d / len : Vector2.Zero;
            g.Tips[k] = g.Centre + g.Directions[k] * ArmPixels;
        }
        for (int k = 0; k < 3; k++)
        {
            int a = (k + 1) % 3, b = (k + 2) % 3;
            g.SquareUsable[k] = g.AxisUsable[a] && g.AxisUsable[b];
            var u = g.Directions[a];
            var v = g.Directions[b];
            var s = g.Squares[k];
            s[0] = g.Centre + u * SquareFrom + v * SquareFrom;
            s[1] = g.Centre + u * SquareTo + v * SquareFrom;
            s[2] = g.Centre + u * SquareTo + v * SquareTo;
            s[3] = g.Centre + u * SquareFrom + v * SquareTo;
        }
        return g;
    }

    /// <summary>The handle under (<paramref name="x"/>, <paramref name="y"/>): a plane square it lies inside, else the
    /// nearest arrow within <see cref="HitPixels"/>, else none.</summary>
    public static GizmoHandle Hit(GizmoLayout g, float x, float y)
    {
        var p = new Vector2(x, y);
        for (int k = 0; k < 3; k++)
            if (g.SquareUsable[k] && Inside(g.Squares[k], p)) return GizmoHandle.PlaneX + k;
        var best = GizmoHandle.None;
        float bestD = HitPixels;
        for (int k = 0; k < 3; k++)
        {
            if (!g.AxisUsable[k]) continue;
            // The arrow from just outside the pivot's own marker to its tip.
            float d = SegmentDistance(p, g.Centre + g.Directions[k] * 6f, g.Tips[k] + g.Directions[k] * 4f);
            if (d <= bestD) { bestD = d; best = GizmoHandle.AxisX + k; }
        }
        return best;
    }

    public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Inside a convex quadrilateral (either winding).</summary>
    private static bool Inside(Vector2[] q, Vector2 p)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % 4];
            float c = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = c > 0 ? 1 : c < 0 ? -1 : 0;
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return sign != 0;
    }
}
