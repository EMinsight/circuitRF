// brief-em3d-41 R-em3d41-3c — a placement as one rigid transform, composed and inverted EXACTLY.
//
// Exactness is not a property of the arithmetic type, it is a property of the entries: a rotation by a
// multiple of 90° is built from LayoutAngle.CosSin, whose quadrant table returns 0 and ±1 exactly, and
// a product or sum of integers held in doubles is exact while it stays below 2^53. So a composition of
// quarter turns and mirrors is an INTEGER matrix, its inverse is its transpose, and inverse ∘ placement
// is the identity with no tolerance. Any other angle is exact in the object's own frame and double in
// the world, which is what a rotated object genuinely is (overview §1d).

using CircuitRF.Design.Layout;

namespace CircuitRF.Design.ThreeD;

/// <summary>
/// A rigid transform p ↦ M·p + t, where M is orthogonal (a rotation, possibly with a mirror). Placements
/// never scale, which is what makes <see cref="Inverse"/> a transpose.
/// </summary>
public readonly record struct C3dTransform(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22,
    double Tx,  double Ty,  double Tz)
{
    public static C3dTransform Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0);

    /// <summary>Negates x — a layout instance's MirrorX, in 3D.</summary>
    public static C3dTransform MirrorX { get; } = new(-1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0);

    public static C3dTransform Translation(C3dPoint3 by) => Identity with { Tx = by.X, Ty = by.Y, Tz = by.Z };

    /// <summary>A right-handed rotation about a world axis. Exact at every multiple of 90°.</summary>
    public static C3dTransform Rotation(C3dAxis axis, double deg)
    {
        var (c, s) = LayoutAngle.CosSin(deg);
        return axis switch
        {
            C3dAxis.X => new(1, 0, 0,   0, c, -s,   0, s, c,    0, 0, 0),
            C3dAxis.Y => new(c, 0, s,   0, 1, 0,    -s, 0, c,   0, 0, 0),
            C3dAxis.Z => new(c, -s, 0,  s, c, 0,    0, 0, 1,    0, 0, 0),
            _         => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    /// <summary>This transform, then <paramref name="next"/>: the composition next ∘ this.</summary>
    public C3dTransform Then(C3dTransform next)
    {
        var a = next;
        return new(
            a.M00 * M00 + a.M01 * M10 + a.M02 * M20, a.M00 * M01 + a.M01 * M11 + a.M02 * M21, a.M00 * M02 + a.M01 * M12 + a.M02 * M22,
            a.M10 * M00 + a.M11 * M10 + a.M12 * M20, a.M10 * M01 + a.M11 * M11 + a.M12 * M21, a.M10 * M02 + a.M11 * M12 + a.M12 * M22,
            a.M20 * M00 + a.M21 * M10 + a.M22 * M20, a.M20 * M01 + a.M21 * M11 + a.M22 * M21, a.M20 * M02 + a.M21 * M12 + a.M22 * M22,
            a.M00 * Tx + a.M01 * Ty + a.M02 * Tz + a.Tx,
            a.M10 * Tx + a.M11 * Ty + a.M12 * Tz + a.Ty,
            a.M20 * Tx + a.M21 * Ty + a.M22 * Tz + a.Tz);
    }

    /// <summary>The inverse: Mᵀ and −Mᵀ·t. Exact whenever the entries are integers.</summary>
    public C3dTransform Inverse() => new(
        M00, M10, M20,
        M01, M11, M21,
        M02, M12, M22,
        -(M00 * Tx + M10 * Ty + M20 * Tz),
        -(M01 * Tx + M11 * Ty + M21 * Tz),
        -(M02 * Tx + M12 * Ty + M22 * Tz));

    /// <summary>The point, transformed, in (double) DBU.</summary>
    public (double X, double Y, double Z) Apply(C3dPoint3 p) => (
        M00 * p.X + M01 * p.Y + M02 * p.Z + Tx,
        M10 * p.X + M11 * p.Y + M12 * p.Z + Ty,
        M20 * p.X + M21 * p.Y + M22 * p.Z + Tz);

    /// <summary>True when every entry — the matrix and the translation — is an integer, which is when a
    /// transformed integer point is still an integer point.</summary>
    public bool IsIntegral =>
        IsInt(M00) && IsInt(M01) && IsInt(M02) && IsInt(M10) && IsInt(M11) && IsInt(M12)
        && IsInt(M20) && IsInt(M21) && IsInt(M22) && IsInt(Tx) && IsInt(Ty) && IsInt(Tz);

    /// <summary>The matrix, row-major, as integers — null unless every entry is one.</summary>
    public int[]? IntegerMatrix()
    {
        double[] m = [M00, M01, M02, M10, M11, M12, M20, M21, M22];
        if (!m.All(IsInt)) return null;
        return [.. m.Select(v => (int)v)];
    }

    private static bool IsInt(double v) => double.IsFinite(v) && Math.Floor(v) == v;
}
