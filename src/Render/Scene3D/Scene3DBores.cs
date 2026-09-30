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
//
// A DIELECTRIC is carved the same way (2026-09-30), by a BARE bore only: a non-plated hole is an air cylinder with no
// barrel around it, so an uncarved substrate showed no hole at all. A plated via's bore is not carved out of the
// substrate — its barrel hides that — so a board with thousands of vias does not triangulate a slab with thousands of
// holes. A box-shaped slab (no board outline) is carved as its rectangle extruded, its faces given back the box's own.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>The vertical air cylinders that carve metal in a problem, and the surface each carved conductor is drawn as.</summary>
internal sealed class Scene3DBores
{
    /// <summary>Metres: geometry closer than this is the same place (the generator writes both from one number).</summary>
    private const double Tol = 1e-9;

    /// <param name="Bare">No metal cylinder around it: a hole, not a barrel's bore — the only kind a dielectric is carved by.</param>
    private readonly record struct Bore(double X, double Y, double R, double Z0, double Z1, int Priority, bool Bare);

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
                               Math.Min(c.AxisStart.Z, c.AxisEnd.Z), Math.Max(c.AxisStart.Z, c.AxisEnd.Z), precedence.Of(s), Bare: true));
        }
        if (bores.Count > 0)
            foreach (var s in problem.Solids)
                if (s.Role == Em3dRole.Conductor && s.Primitive is Em3dCylinder m && Vertical(m))
                    for (int i = 0; i < bores.Count; i++)
                        if (bores[i].Bare && Math.Abs(bores[i].X - m.AxisStart.X) <= Tol && Math.Abs(bores[i].Y - m.AxisStart.Y) <= Tol &&
                            bores[i].R < m.Radius - Tol)
                            bores[i] = bores[i] with { Bare = false };
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
        if (s.Role is not (Em3dRole.Conductor or Em3dRole.Dielectric)) return null;
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
            case Em3dBox box when s.Role == Em3dRole.Dielectric:
            {
                // Counter-clockwise from (min, min): its walls are y min, x max, y max, x min — box faces 2, 1, 3, 0.
                var rect = new Em3dExtrudedPolygon(
                    [new Point2(box.Min.X, box.Min.Y), new Point2(box.Max.X, box.Min.Y),
                     new Point2(box.Max.X, box.Max.Y), new Point2(box.Min.X, box.Max.Y)], [], box.Min.Z, box.Max.Z);
                return Extruded(s, rect, priority, BoxFace);
            }
            case Em3dExtrudedPolygon e:
                return Extruded(s, e, priority, null);
            default:
                return null;
        }
    }

    /// <summary>A box's face for the face of its rectangle extruded: bottom, top, then the walls in ring order.</summary>
    private static int BoxFace(int f) => f switch { 0 => 4, 1 => 5, 2 => 2, 3 => 1, 4 => 3, 5 => 0, _ => f };

    /// <summary>An extruded polygon with one hole per bore right through it, or null when none is. <paramref name="face"/>
    /// renumbers the primitive's own faces (a box's are not an extrusion's).</summary>
    private (object Key, Func<Em3dTriangleMesh> Make)? Extruded(Em3dSolid s, Em3dExtrudedPolygon e, int priority, Func<int, int>? face)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (var p in e.Outline) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); }
        List<Bore>? taken = null;
        foreach (var b in Near(minX, maxX))
        {
            if (b.Priority <= priority || !Spans(b, e.ZBottom, e.ZTop) || (s.Role == Em3dRole.Dielectric && !b.Bare)) continue;
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
        var key = new BoredKey(e, face is not null,
                               string.Join(";", taken.Select(b => FormattableString.Invariant($"{b.X:R},{b.Y:R},{b.R:R}"))));
        return (key, () =>
        {
            var mesh = Em3dTessellation.Of(new Em3dSolid(solidName, s.Material, s.Role, drawn, s.Order));
            // The bores' walls are no face of the primitive's: its own faces keep their numbers (bottom 0, top 1, one per
            // edge of its own rings), so a face picked on a pad is named exactly as it was before it was carved.
            return new Em3dTriangleMesh(mesh.Vertices,
                [.. mesh.Triangles.Select(t => t.Face >= 2 + ownEdges ? t with { Face = -1 }
                                             : face is not null && t.Face >= 0 ? t with { Face = face(t.Face) } : t)]);
        });
    }

    private sealed record TubeKey(Em3dCylinder Cylinder, double Bore);

    private sealed record BoredKey(Em3dExtrudedPolygon Primitive, bool Box, string Bores);

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
