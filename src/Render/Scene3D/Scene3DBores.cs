// A plated via's bore, DRAWN. The solver gets a plated barrel as a copper cylinder plus a coaxial air cylinder of higher
// precedence (R-em3d3-5d), and the pads the barrel passes through as whole polygons: the precedence rule subtracts the
// air from both. A surface renderer draws every solid's own surface and hides air, so the barrel read as a solid copper
// rod under an unbroken pad — a plated via looked exactly like a filled one.
//
// VIEW ONLY. Nothing here reaches a solver, the document or a picture's geometry beyond the triangles the 3D view draws:
// the problem keeps its cylinder and its air core, bit for bit. What the view asks for instead is the surface the
// precedence rule leaves — the barrel as a tube, and a hole the bore's size in each metal polygon it passes right
// through. Only that case is carved: a vertical air cylinder spanning a conductor's whole height, lying wholly inside it.
// A bore that stops part-way, clips an edge or overlaps another hole is left to the precedence rule alone, as before.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>The vertical air cylinders that carve metal in a problem, and the surface each carved conductor is drawn as.</summary>
internal sealed class Scene3DBores
{
    /// <summary>Metres: geometry closer than this is the same place (the generator writes both from one number).</summary>
    private const double Tol = 1e-9;

    private readonly record struct Bore(double X, double Y, double R, double Z0, double Z1, int Priority);

    private readonly Bore[] _bores;   // by X
    private readonly Em3dPrecedence _precedence;

    private Scene3DBores(Bore[] bores, Em3dPrecedence precedence) { _bores = bores; _precedence = precedence; }

    /// <summary>The problem's bores, or null when it has none (the ordinary problem, which then costs nothing).</summary>
    public static Scene3DBores? Of(Em3dProblem problem)
    {
        Em3dPrecedence? precedence = null;
        var bores = new List<Bore>();
        foreach (var s in problem.Solids)
        {
            if (s.Role != Em3dRole.Air || s.Primitive is not Em3dCylinder c || !Vertical(c)) continue;
            precedence ??= Em3dPrecedence.Of(problem);
            bores.Add(new Bore(c.AxisStart.X, c.AxisStart.Y, c.Radius,
                               Math.Min(c.AxisStart.Z, c.AxisEnd.Z), Math.Max(c.AxisStart.Z, c.AxisEnd.Z), precedence.Of(s)));
        }
        if (bores.Count == 0) return null;
        bores.Sort((a, b) => a.X.CompareTo(b.X));
        return new Scene3DBores([.. bores], precedence!);
    }

    /// <summary>
    /// What <paramref name="s"/> is drawn as when a bore carves it — the cache key and the tessellation — or null to draw
    /// its own primitive. A cylinder with a coaxial bore is a tube; an extruded polygon gains one hole per bore through it.
    /// </summary>
    public (object Key, Func<Em3dTriangleMesh> Make)? Carved(Em3dSolid s)
    {
        if (s.Role != Em3dRole.Conductor) return null;
        int priority = _precedence.Of(s);
        switch (s.Primitive)
        {
            case Em3dCylinder c when Vertical(c):
            {
                double z0 = Math.Min(c.AxisStart.Z, c.AxisEnd.Z), z1 = Math.Max(c.AxisStart.Z, c.AxisEnd.Z);
                foreach (var b in Near(c.AxisStart.X - Tol, c.AxisStart.X + Tol))
                    if (b.Priority > priority && Math.Abs(b.Y - c.AxisStart.Y) <= Tol && b.R < c.Radius - Tol && Spans(b, z0, z1))
                    {
                        string name = s.Name;
                        return (new TubeKey(c, b.R), () => Em3dTessellation.OfTube(name, c, b.R));
                    }
                return null;
            }
            case Em3dExtrudedPolygon e:
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                foreach (var p in e.Outline) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); }
                List<Bore>? taken = null;
                foreach (var b in Near(minX, maxX))
                {
                    if (b.Priority <= priority || !Spans(b, e.ZBottom, e.ZTop)) continue;
                    var centre = new Point2(b.X, b.Y);
                    if (!Inside(e.Outline, centre) || Clearance(e.Outline, centre) < b.R - Tol) continue;
                    if (e.Holes.Any(h => Inside(h, centre) || Clearance(h, centre) < b.R - Tol)) continue;
                    if (taken?.Any(o => Math.Sqrt((o.X - b.X) * (o.X - b.X) + (o.Y - b.Y) * (o.Y - b.Y)) < o.R + b.R) == true) continue;
                    (taken ??= []).Add(b);
                }
                if (taken is null) return null;

                var holes = new List<IReadOnlyList<Point2>>(e.Holes);
                holes.AddRange(taken.Select(b => Ring(b)));
                var drawn = e with { Holes = holes };
                int ownEdges = e.Outline.Count + e.Holes.Sum(h => h.Count);
                string solidName = s.Name;
                var key = new BoredKey(e, string.Join(";", taken.Select(b => FormattableString.Invariant($"{b.X:R},{b.Y:R},{b.R:R}"))));
                return (key, () =>
                {
                    var mesh = Em3dTessellation.Of(new Em3dSolid(solidName, s.Material, s.Role, drawn, s.Order));
                    // The bores' walls are no face of the primitive's: its own faces keep their numbers (bottom 0, top 1, one per
                    // edge of its own rings), so a face picked on a pad is named exactly as it was before it was carved.
                    return new Em3dTriangleMesh(mesh.Vertices,
                        [.. mesh.Triangles.Select(t => t.Face >= 2 + ownEdges ? t with { Face = -1 } : t)]);
                });
            }
            default:
                return null;
        }
    }

    private sealed record TubeKey(Em3dCylinder Cylinder, double Bore);

    private sealed record BoredKey(Em3dExtrudedPolygon Primitive, string Bores);

    private static bool Vertical(Em3dCylinder c)
        => Math.Abs(c.AxisStart.X - c.AxisEnd.X) <= Tol && Math.Abs(c.AxisStart.Y - c.AxisEnd.Y) <= Tol && Math.Abs(c.AxisEnd.Z - c.AxisStart.Z) > Tol;

    private static bool Spans(Bore b, double z0, double z1) => b.Z0 <= z0 + Tol && b.Z1 >= z1 - Tol;

    /// <summary>The bores whose centre's x lies in [lo, hi].</summary>
    private IEnumerable<Bore> Near(double lo, double hi)
    {
        int a = 0, z = _bores.Length;
        while (a < z) { int m = (a + z) / 2; if (_bores[m].X < lo) a = m + 1; else z = m; }
        for (int i = a; i < _bores.Length && _bores[i].X <= hi; i++) yield return _bores[i];
    }

    /// <summary>The bore's circle at the tube's own vertices, so a pad's hole and the barrel's bore meet edge to edge.</summary>
    private static Point2[] Ring(Bore b)
    {
        int n = Em3dTessellation.CylinderSegments;
        var ring = new Point2[n];
        for (int k = 0; k < n; k++)
        {
            double t = 2 * Math.PI * k / n;
            ring[k] = new Point2(b.X + b.R * Math.Cos(t), b.Y + b.R * Math.Sin(t));
        }
        return ring;
    }

    private static bool Inside(IReadOnlyList<Point2> ring, Point2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) &&
                p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>The distance from <paramref name="p"/> to the nearest edge of <paramref name="ring"/>.</summary>
    private static double Clearance(IReadOnlyList<Point2> ring, Point2 p)
    {
        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double dx = ring[i].X - ring[j].X, dy = ring[i].Y - ring[j].Y, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((p.X - ring[j].X) * dx + (p.Y - ring[j].Y) * dy) / len2, 0, 1) : 0;
            double ex = ring[j].X + t * dx - p.X, ey = ring[j].Y + t * dy - p.Y;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }
}
