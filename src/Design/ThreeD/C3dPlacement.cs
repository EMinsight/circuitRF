// brief-em3d-46 R-em3d46-3 — composing an operation into a placement, and the placement's CANONICAL form.
//
// A rotation or a mirror is applied to an object by composing it into the object's placement, never into its
// geometry (overview §1d): the geometry stays integer in the object's own frame. Composed naively, the rotation
// list would grow by one entry per edit and soon say nothing a person could read, so after each composition the
// rotation part is re-stated canonically:
//
//   * a SIGNED PERMUTATION (any composition of quarter turns and mirrors) is stored EXACTLY — MirrorX plus at most
//     three quarter-turn entries, in the list order Z, Y, X (Z applied first). It is found by trying every
//     quarter-turn triple against the integer matrix, so it is exact by construction, not by rounding;
//   * anything else is stored as the same three entries with angles in degrees (Z-Y-X Euler angles, the matrix
//     Rx·Ry·Rz), each rounded to 1e-9°.
//
// A zero angle is left out, so the identity is an EMPTY list and a placement that has come back to where it
// started is omitted from the file again — four quarter turns about one axis write the file byte for byte as it
// was (gate 3). The origin is the composed translation; it is an integer whenever the pivot and the matrix are,
// and is otherwise rounded once, here, which the caller reports as "≈".

namespace CircuitRF.Design.ThreeD;

public sealed partial class C3dPlacement
{
    /// <summary>A deep copy.</summary>
    public C3dPlacement Clone() => new()
    {
        Origin = Origin,
        Rotate = [.. Rotate.Select(r => new C3dRotation { Axis = r.Axis, Deg = r.Deg, Exprs = C3dBindings.Copy(r.Exprs) })],
        MirrorX = MirrorX,
        Exprs = C3dBindings.Copy(Exprs),
    };

    /// <summary>This placement moved by <paramref name="by"/>: only the origin changes, so the rotation list is kept
    /// exactly as it was written.</summary>
    public C3dPlacement Translated(C3dPoint3 by)
    {
        var p = Clone();
        p.Origin += by;
        return p;
    }

    /// <summary>
    /// This placement, then <paramref name="op"/> (a rigid transform of the world, DBU) — re-stated in canonical
    /// form. <paramref name="exact"/> is false when the composed origin was not a whole DBU and was rounded.
    /// </summary>
    public C3dPlacement Then(C3dTransform op, out bool exact) => Canonical(ToTransform().Then(op), out exact);

    /// <summary>The quarter-turn angles a signed permutation is written with, in the order they are tried.</summary>
    private static readonly double[] Quarters = [0, 90, 180, -90];

    /// <summary>
    /// The canonical placement whose transform is <paramref name="t"/> (R-em3d46-3b). <paramref name="exact"/> is
    /// false when the translation was not a whole DBU and was rounded.
    /// </summary>
    public static C3dPlacement Canonical(C3dTransform t, out bool exact)
    {
        var linear = t with { Tx = 0, Ty = 0, Tz = 0 };
        bool mirror = Determinant(linear) < 0;
        // T = Translate(origin) ∘ R ∘ MirrorX, so the rotation part is L · MirrorX (MirrorX is its own inverse).
        var r = mirror ? C3dTransform.MirrorX.Then(linear) : linear;
        // Every integer rotation matrix is a composition of quarter turns; Euler is the fallback that cannot be reached.
        var rotate = r.IntegerMatrix() is { } m && Quarter(m) is { } q ? q : Euler(r);
        var (ox, ex) = Whole(t.Tx);
        var (oy, ey) = Whole(t.Ty);
        var (oz, ez) = Whole(t.Tz);
        exact = ex && ey && ez;
        return new C3dPlacement { Origin = new C3dPoint3(ox, oy, oz), Rotate = rotate, MirrorX = mirror };
    }

    /// <summary>The rotation entries (Z, Y, X order, zeros left out) of a composition of quarter turns: the fewest
    /// entries whose product is <paramref name="m"/> exactly.</summary>
    private static List<C3dRotation>? Quarter(int[] m)
    {
        List<C3dRotation>? best = null;
        foreach (double a in Quarters)
            foreach (double b in Quarters)
                foreach (double c in Quarters)
                {
                    var list = Entries(a, b, c);
                    if (best is not null && list.Count >= best.Count) continue;
                    var p = new C3dPlacement { Rotate = list }.ToTransform().IntegerMatrix();
                    if (p is not null && p.AsSpan().SequenceEqual(m)) best = list;
                }
        return best;
    }

    /// <summary>
    /// Z-Y-X Euler angles of a rotation matrix M = Rx(c)·Ry(b)·Rz(a) — Z applied first — rounded to 1e-9°:
    /// M02 = sin b; M00 = cos b cos a, M01 = −cos b sin a; M12 = −sin c cos b, M22 = cos c cos b. At cos b = 0 the
    /// split between a and c is not determined; c is taken as 0.
    /// </summary>
    private static List<C3dRotation> Euler(C3dTransform r)
    {
        double sb = Math.Clamp(r.M02, -1, 1);
        double b = Math.Asin(sb), a, c;
        if (Math.Sqrt(r.M00 * r.M00 + r.M01 * r.M01) < 1e-12)
        {
            c = 0;
            a = Math.Atan2(r.M10, r.M11);
        }
        else
        {
            a = Math.Atan2(-r.M01, r.M00);
            c = Math.Atan2(-r.M12, r.M22);
        }
        return Entries(Deg(a), Deg(b), Deg(c));

        static double Deg(double rad)
        {
            double d = Math.Round(rad * 180 / Math.PI, 9);
            if (d <= -180) d += 360;
            return d == 0 ? 0 : d;                     // never −0
        }
    }

    private static List<C3dRotation> Entries(double z, double y, double x)
    {
        var list = new List<C3dRotation>(3);
        if (z != 0) list.Add(new C3dRotation { Axis = C3dAxis.Z, Deg = z });
        if (y != 0) list.Add(new C3dRotation { Axis = C3dAxis.Y, Deg = y });
        if (x != 0) list.Add(new C3dRotation { Axis = C3dAxis.X, Deg = x });
        return list;
    }

    private static double Determinant(C3dTransform m)
        => m.M00 * (m.M11 * m.M22 - m.M12 * m.M21) - m.M01 * (m.M10 * m.M22 - m.M12 * m.M20) + m.M02 * (m.M10 * m.M21 - m.M11 * m.M20);

    private static (long Value, bool Whole) Whole(double v)
    {
        double r = Math.Round(v, MidpointRounding.AwayFromZero);
        return ((long)r, Math.Abs(v - r) <= 1e-6);
    }
}

/// <summary>brief-em3d-46 — the rigid operations the 3D editor composes into placements.</summary>
public static class C3dOperations
{
    /// <summary><paramref name="linear"/> (a rotation or reflection about the world origin) moved to act about
    /// <paramref name="pivot"/>: the pivot stays where it is.</summary>
    public static C3dTransform AboutPivot(C3dTransform linear, C3dPoint3 pivot)
        => C3dTransform.Translation(new C3dPoint3(-pivot.X, -pivot.Y, -pivot.Z)).Then(linear).Then(C3dTransform.Translation(pivot));

    /// <summary>A rotation by <paramref name="deg"/> about the world axis <paramref name="axis"/> through <paramref name="pivot"/>.</summary>
    public static C3dTransform Rotation(C3dAxis axis, double deg, C3dPoint3 pivot) => AboutPivot(C3dTransform.Rotation(axis, deg), pivot);

    /// <summary>The reflection across <paramref name="plane"/> through the origin: the plane's normal coordinate negated.</summary>
    public static C3dTransform Reflection(C3dPlane plane) => plane switch
    {
        C3dPlane.YZ => C3dTransform.MirrorX,
        C3dPlane.XZ => C3dTransform.Identity with { M11 = -1 },
        _           => C3dTransform.Identity with { M22 = -1 },
    };

    /// <summary>The reflection across <paramref name="plane"/> through <paramref name="pivot"/> (R-em3d46-3c).</summary>
    public static C3dTransform Mirror(C3dPlane plane, C3dPoint3 pivot) => AboutPivot(Reflection(plane), pivot);

    /// <summary>
    /// A copy's name (R-em3d46-4a): the source's trailing number incremented past any name in
    /// <paramref name="used"/> — <c>pad3</c> → <c>pad4</c>, <c>lid</c> → <c>lid2</c>. The name returned is added to
    /// <paramref name="used"/>, so a batch of copies never collides with itself.
    /// </summary>
    public static string NextFreeName(string source, ISet<string> used)
    {
        int end = source.Length;
        while (end > 0 && char.IsAsciiDigit(source[end - 1])) end--;
        string stem = source[..end];
        long n = end < source.Length && long.TryParse(source[end..], out long k) ? k + 1 : 2;
        string name;
        while (used.Contains(name = stem + n)) n++;
        used.Add(name);
        return name;
    }
}
