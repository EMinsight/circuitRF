// brief-em3d-45 R-em3d45-1 — the drawing plane: XY, YZ or XZ, at an integer-DBU offset along its normal.
//
// EDITOR STATE, NOT DESIGN (R-em3d45-1a). The plane is where the next click lands; the .c3d never records it,
// and a shape drawn on it records its OWN plane and offset (a sheet, a prism) or none at all (a box). So it is
// kept per document in the workspace's window state, beside the camera.
//
// THE PLANE NAMES ITS AXES the way the document does (C3dPlane): u is the first, v the second, and the normal
// the third — XY → (x, y) along +z, YZ → (y, z) along +x, XZ → (x, z) along +y. Every conversion below goes
// through that one table, so a box drawn on YZ is exactly a box drawn on XY with its axes permuted (gate 2).
//
// EXACT (overview §1d). Points are integer DBU; only the ray a cursor casts is metres, and it is turned into a
// DBU point once, by rounding, at the moment a tool needs one. A plane seen edge-on is refused, never
// intersected: a ray nearly parallel to it meets it at infinity (R-em3d45-1c).

using System.Numerics;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>A drawing plane: <see cref="Plane"/> at <see cref="OffsetDbu"/> along its normal.</summary>
public readonly record struct DrawingPlane(C3dPlane Plane, long OffsetDbu)
{
    /// <summary>A ray this close to parallel with the plane (the cosine of its angle to the normal) sees the
    /// plane edge-on: about 1.1°.</summary>
    public const double EdgeOnCosine = 0.02;

    public static DrawingPlane Default => new(C3dPlane.XY, 0);

    public C3dAxis Normal => NormalOf(Plane);

    public static C3dAxis NormalOf(C3dPlane p) => p switch { C3dPlane.YZ => C3dAxis.X, C3dPlane.XZ => C3dAxis.Y, _ => C3dAxis.Z };

    /// <summary>The plane's u and v axes.</summary>
    public static (C3dAxis U, C3dAxis V) AxesOf(C3dPlane p) => p switch
    {
        C3dPlane.YZ => (C3dAxis.Y, C3dAxis.Z), C3dPlane.XZ => (C3dAxis.X, C3dAxis.Z), _ => (C3dAxis.X, C3dAxis.Y),
    };

    /// <summary>The plane whose normal is <paramref name="axis"/>.</summary>
    public static C3dPlane PlaneNormalTo(C3dAxis axis) => axis switch { C3dAxis.X => C3dPlane.YZ, C3dAxis.Y => C3dPlane.XZ, _ => C3dPlane.XY };

    /// <summary>The plane spanning two distinct axes.</summary>
    public static C3dPlane Spanning(C3dAxis a, C3dAxis b)
    {
        if (a == b) throw new ArgumentException("Two distinct axes span a plane.");
        return PlaneNormalTo((C3dAxis)(3 - (int)a - (int)b));
    }

    public static long Get(C3dPoint3 p, C3dAxis a) => a switch { C3dAxis.X => p.X, C3dAxis.Y => p.Y, _ => p.Z };

    public static double Get(Point3 p, C3dAxis a) => a switch { C3dAxis.X => p.X, C3dAxis.Y => p.Y, _ => p.Z };

    public static C3dPoint3 With(C3dPoint3 p, C3dAxis a, long v) => a switch
    {
        C3dAxis.X => p with { X = v }, C3dAxis.Y => p with { Y = v }, _ => p with { Z = v },
    };

    /// <summary>A world point's (u, v) on this plane.</summary>
    public C3dPoint2 ToUv(C3dPoint3 p)
    {
        var (u, v) = AxesOf(Plane);
        return new C3dPoint2(Get(p, u), Get(p, v));
    }

    /// <summary>A world point's coordinate along the normal.</summary>
    public long W(C3dPoint3 p) => Get(p, Normal);

    /// <summary>The world point at (u, v, w) of this plane's axes.</summary>
    public C3dPoint3 FromUvw(long u, long v, long w)
    {
        var (au, av) = AxesOf(Plane);
        var p = With(With(default, au, u), av, v);
        return With(p, Normal, w);
    }

    /// <summary>The world point at (u, v) on the plane itself.</summary>
    public C3dPoint3 FromUv(C3dPoint2 uv) => FromUvw(uv.U, uv.V, OffsetDbu);

    /// <summary><paramref name="p"/> moved along the normal onto the plane.</summary>
    public C3dPoint3 Project(C3dPoint3 p) => With(p, Normal, OffsetDbu);

    public bool Contains(C3dPoint3 p) => W(p) == OffsetDbu;

    /// <summary>The unit normal, +axis.</summary>
    public static Vector3 UnitNormal(C3dAxis a) => a switch { C3dAxis.X => Vector3.UnitX, C3dAxis.Y => Vector3.UnitY, _ => Vector3.UnitZ };

    /// <summary>R-em3d45-1c — a ray (any length direction) that meets the plane at a grazing angle.</summary>
    public bool IsEdgeOn(Point3 direction)
    {
        double len = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
        return !(len > 0) || Math.Abs(Get(direction, Normal)) / len < EdgeOnCosine;
    }

    /// <summary>Where the ray from <paramref name="origin"/> along <paramref name="direction"/> (world metres) meets
    /// the plane, or null when it is edge-on or the plane is behind it.</summary>
    public Point3? Hit(Point3 origin, Point3 direction, int dbuPerMicron)
    {
        if (IsEdgeOn(direction)) return null;
        double w = C3dLowering.Metres(OffsetDbu, dbuPerMicron);
        double t = (w - Get(origin, Normal)) / Get(direction, Normal);
        if (!(t >= 0) || double.IsInfinity(t)) return null;
        return new Point3(origin.X + direction.X * t, origin.Y + direction.Y * t, origin.Z + direction.Z * t);
    }

    /// <summary>
    /// 3D editor bugs round 3 — the world axis whose line through <paramref name="linePoint"/> passes nearest the cursor's
    /// ray: what Shift holds a Move to. The distance is the two lines' closest approach, so the axis the cursor has moved
    /// out along wins whichever plane it is drawn on. An axis the ray runs (nearly) along is never chosen — its line is
    /// a dot on screen. Null only when the ray is degenerate.
    /// </summary>
    public static C3dAxis? AxisNearestRay(Point3 linePoint, Point3 origin, Point3 direction)
    {
        double dx = direction.X, dy = direction.Y, dz = direction.Z;
        double c = dx * dx + dy * dy + dz * dz;
        if (!(c > 0)) return null;
        double wx = linePoint.X - origin.X, wy = linePoint.Y - origin.Y, wz = linePoint.Z - origin.Z;
        C3dAxis? best = null;
        double bestDist = double.PositiveInfinity;
        foreach (var axis in (ReadOnlySpan<C3dAxis>)[C3dAxis.X, C3dAxis.Y, C3dAxis.Z])
        {
            var a = UnitNormal(axis);
            // n = a × d; the lines' distance is |w · n| / |n|.
            double nx = a.Y * dz - a.Z * dy, ny = a.Z * dx - a.X * dz, nz = a.X * dy - a.Y * dx;
            double nn = nx * nx + ny * ny + nz * nz;
            if (nn <= 0.0025 * c) continue;       // within ~3° of the line of sight
            double dist = Math.Abs(wx * nx + wy * ny + wz * nz) / Math.Sqrt(nn);
            if (dist < bestDist) { bestDist = dist; best = axis; }
        }
        return best;
    }

    /// <summary>
    /// R-em3d45-3a step 3 — the parameter (metres, along +<paramref name="axis"/>) of the point on the line through
    /// <paramref name="linePoint"/> along that axis that is closest to the ray: the closest points of two lines.
    /// Null when the ray runs along the line, where every point is equally close.
    /// </summary>
    public static double? AlongAxisClosestToRay(Point3 linePoint, C3dAxis axis, Point3 origin, Point3 direction)
    {
        // Line L(t) = P + t·a (a the unit axis); ray R(s) = O + s·d. With w = P − O:
        //   t = (b·e − c·d') / (a·a·c − b²)  where b = a·d, c = d·d, d' = a·w, e = d·w.
        var a = UnitNormal(axis);
        double dx = direction.X, dy = direction.Y, dz = direction.Z;
        double wx = linePoint.X - origin.X, wy = linePoint.Y - origin.Y, wz = linePoint.Z - origin.Z;
        double b = a.X * dx + a.Y * dy + a.Z * dz;
        double c = dx * dx + dy * dy + dz * dz;
        double aw = a.X * wx + a.Y * wy + a.Z * wz;
        double dw = dx * wx + dy * wy + dz * wz;
        double denom = c - b * b;
        if (!(c > 0) || denom <= 1e-9 * c) return null;
        return (b * dw - c * aw) / denom;
    }

    /// <summary>
    /// R-em3d45-1b — the plane a face lies in, when its (unit) normal is along an axis: its axis, and the plane
    /// normal to it. Null for a tilted face — the planes are XY, YZ and XZ only.
    /// </summary>
    public static C3dAxis? AxisOfNormal(Vector3 n, float tolerance = 1e-4f)
    {
        float len = n.Length();
        if (!(len > 0)) return null;
        n /= len;
        if (MathF.Abs(MathF.Abs(n.X) - 1) <= tolerance) return C3dAxis.X;
        if (MathF.Abs(MathF.Abs(n.Y) - 1) <= tolerance) return C3dAxis.Y;
        if (MathF.Abs(MathF.Abs(n.Z) - 1) <= tolerance) return C3dAxis.Z;
        return null;
    }

    public override string ToString() => Plane.ToString();
}
