// 3D vector copy and drawing export (2026-09-27) — an OUTLINE of a 3D problem along any direction: a drawing's Top,
// Front, Right …, its isometric, or the 3D view's own camera — in perspective when that camera is (Em3dProjection.Eye). The isometric outline beside it
// (Em3dSectionScene.Iso, `render --iso`) is left exactly as it was: its bytes are what the CLI's tests and the
// documentation's figures compare, and it is a different picture (a fixed +x +y +z viewer, scaled as an isometric
// DRAWING rather than projected).
//
// ── What is drawn ─────────────────────────────────────────────────────────────────────────────
//
// Every solid and sheet is taken as triangles — Em3dTessellation's, the ones every other consumer sees — welded by
// position, and an edge of that mesh is drawn when it is a FEATURE: an open edge (a sheet's outline), a turn sharper
// than SharpEdgeDegrees, or a silhouette (its two faces on opposite sides of the viewer). One rule for every
// primitive, so a kernel solid's fillet reads the same as a drawn box. A box is clipped to the frame first, as the
// isometric clips it: a slab is as wide as the air box by construction, and a picture of the air box is a dot.
//
// ── Hidden edges ──────────────────────────────────────────────────────────────────────────────
//
// An edge is split where it passes behind a surface, by sampling it against every projected triangle in a 2D bucket
// grid and bisecting each change of state. A point is behind a triangle when it projects inside it (boundary
// INCLUDED — a point on the diagonal of the face in front of it must not leak through) and the triangle is nearer
// the viewer at that point by more than a part in ten million of the model; that tolerance is what keeps an edge
// from hiding behind the very faces it bounds, and a trace from hiding in the substrate it lies on. Above a triangle
// budget nothing is hidden and the caller is told why, rather than a drawing taking minutes.

using System.Globalization;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render;

/// <summary>What an outline does with an edge behind a surface.</summary>
public enum Em3dHiddenEdges
{
    /// <summary>Drawn like any other edge — a wire-frame.</summary>
    Shown,
    /// <summary>Drawn dashed and lighter, under the visible edges — the drafting convention.</summary>
    Dashed,
    /// <summary>Left out: what a shaded view shows, in lines.</summary>
    Removed,
}

/// <summary>
/// A direction to look along: <see cref="Toward"/> is the unit vector from the model toward the viewer,
/// <see cref="Right"/> and <see cref="Up"/> the picture's axes. Orthographic (no <see cref="Eye"/>), a point's picture
/// coordinates are its components along Right and Up; its depth is its component along Toward (larger is nearer).
/// <para>
/// With an <see cref="Eye"/> it is the 3D view's PERSPECTIVE camera: a point is scaled by <see cref="Focal"/> over its
/// distance ahead of the eye, about the point straight ahead of it. So a point in the plane <see cref="Focal"/> ahead —
/// the camera's orbit centre — lands exactly where the orthographic projection puts it, and a window framed on that plane
/// (the scale bar's) frames both alike. Its depth is affine in the reciprocal of that distance, which is what makes it
/// linear across a projected triangle, as the hidden-edge test interpolates it.
/// </para>
/// </summary>
public readonly record struct Em3dProjection(Point3 Toward, Point3 Right, Point3 Up, string Name)
{
    /// <summary>The perspective camera's eye, metres; null for an orthographic projection.</summary>
    public Point3? Eye { get; init; }

    /// <summary>With an <see cref="Eye"/>, the distance ahead of it at which the picture's scale is the orthographic one.</summary>
    public double Focal { get; init; }

    /// <summary>A perspective projection's nearest drawn distance, as a fraction of <see cref="Focal"/>: what lies nearer
    /// the eye (or behind it) is clipped away.</summary>
    public const double NearFraction = 1e-3;

    /// <summary>This projection seen from <paramref name="eye"/>, scaled as the orthographic one at <paramref name="focal"/> ahead.</summary>
    public Em3dProjection WithEye(Point3 eye, double focal) => this with { Eye = eye, Focal = focal };

    /// <summary>The 3D view's camera angles (Camera3D: yaw about +z from +x, pitch up from the xy plane) — so a drawing's
    /// Top is exactly what Standard Views ▸ Top shows.</summary>
    public static Em3dProjection FromYawPitch(double yaw, double pitch, string name)
    {
        double cp = Math.Cos(pitch);
        var toward = Clean(new Point3(cp * Math.Cos(yaw), cp * Math.Sin(yaw), Math.Sin(pitch)));
        var right  = Clean(new Point3(-Math.Sin(yaw), Math.Cos(yaw), 0));
        var up     = Clean(Cross(right, new Point3(-toward.X, -toward.Y, -toward.Z)));
        return new Em3dProjection(toward, right, Normalize(up), name);
    }

    /// <summary>A direction from three camera vectors (any length; they are normalised).</summary>
    public static Em3dProjection FromVectors(Point3 toward, Point3 right, Point3 up, string name)
        => new(Normalize(toward), Normalize(right), Normalize(up), name);

    /// <summary>One of the 3D view's Standard Views, at Camera3D.SetStandardView's angles.</summary>
    public static Em3dProjection Standard(Em3dStandardView view) => view switch
    {
        Em3dStandardView.Top    => FromYawPitch(-Math.PI / 2,  Math.PI / 2, "Top"),
        Em3dStandardView.Bottom => FromYawPitch(-Math.PI / 2, -Math.PI / 2, "Bottom"),
        Em3dStandardView.Front  => FromYawPitch(-Math.PI / 2, 0, "Front"),
        Em3dStandardView.Back   => FromYawPitch( Math.PI / 2, 0, "Back"),
        Em3dStandardView.Right  => FromYawPitch(0, 0, "Right"),
        Em3dStandardView.Left   => FromYawPitch(Math.PI, 0, "Left"),
        _                       => FromYawPitch(-Math.PI / 4, Math.Asin(1 / Math.Sqrt(3)), "Isometric"),
    };

    /// <summary>A point in the picture's plane, metres.</summary>
    public Uv Project(Point3 p)
    {
        if (Eye is not { } e) return new(Dot(p, Right), Dot(p, Up));
        var r = new Point3(p.X - e.X, p.Y - e.Y, p.Z - e.Z);
        double s = Focal / Math.Max(-Dot(r, Toward), NearFraction * Focal);
        return new(Dot(e, Right) + s * Dot(r, Right), Dot(e, Up) + s * Dot(r, Up));
    }

    /// <summary>How near the viewer a point is, metres (larger is nearer). In perspective, affine in 1/distance and equal
    /// to the orthographic depth on the focal plane.</summary>
    public double Depth(Point3 p)
    {
        if (Eye is not { } e) return Dot(p, Toward);
        double ahead = Math.Max(Ahead(p), NearFraction * Focal);
        return Dot(e, Toward) - Focal * Focal / ahead;
    }

    /// <summary>How far ahead of the eye a point is, metres; for an orthographic projection, +∞.</summary>
    public double Ahead(Point3 p)
        => Eye is { } e ? -((p.X - e.X) * Toward.X + (p.Y - e.Y) * Toward.Y + (p.Z - e.Z) * Toward.Z) : double.PositiveInfinity;

    /// <summary>The unit vector from <paramref name="p"/> toward the viewer: <see cref="Toward"/>, or toward the eye.</summary>
    public Point3 TowardFrom(Point3 p)
        => Eye is { } e ? Normalize(new Point3(e.X - p.X, e.Y - p.Y, e.Z - p.Z)) : Toward;

    /// <summary>
    /// The part of <paramref name="a"/>–<paramref name="b"/> a perspective camera can draw — what lies at least
    /// <see cref="NearFraction"/> × <see cref="Focal"/> ahead of the eye — or null when none does. Orthographic: the segment.
    /// </summary>
    public (Point3 A, Point3 B)? ClipNear(Point3 a, Point3 b)
    {
        if (Eye is null) return (a, b);
        double near = NearFraction * Focal, da = Ahead(a), db = Ahead(b);
        if (da >= near && db >= near) return (a, b);
        if (da < near && db < near) return null;
        double f = (near - da) / (db - da);
        var cut = new Point3(a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y), a.Z + f * (b.Z - a.Z));
        return da < near ? (cut, b) : (a, cut);
    }

    internal static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Point3 Cross(Point3 a, Point3 b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Point3 Normalize(Point3 a)
    {
        double l = Math.Sqrt(Dot(a, a));
        return l > 0 ? new Point3(a.X / l, a.Y / l, a.Z / l) : a;
    }

    /// <summary>cos(π/2) is 6e-17, not 0: a Top view's right axis would carry a y component of that size.</summary>
    private static Point3 Clean(Point3 a)
        => new(Math.Abs(a.X) < 1e-12 ? 0 : a.X, Math.Abs(a.Y) < 1e-12 ? 0 : a.Y, Math.Abs(a.Z) < 1e-12 ? 0 : a.Z);
}

/// <summary>The directions a drawing offers, named as the 3D view's Standard Views name them.</summary>
public enum Em3dStandardView { Isometric, Top, Bottom, Front, Back, Left, Right }

/// <summary>How <see cref="Em3dSectionScene.Outline"/> draws.</summary>
public sealed record Em3dOutlineOptions
{
    public Em3dHiddenEdges Hidden { get; init; } = Em3dHiddenEdges.Removed;

    /// <summary>Solid and sheet names left out entirely — what the 3D view has hidden.</summary>
    public IReadOnlySet<string>? Omit { get; init; }

    /// <summary>Above this many triangles, hidden edges are not worked out (they are drawn) and the note says so.</summary>
    public int TriangleBudget { get; init; } = DefaultTriangleBudget;

    public const int DefaultTriangleBudget = 400_000;
}

public static partial class Em3dSectionScene
{
    /// <summary>The largest number of samples one edge is tested at, before the changes of state are bisected.</summary>
    private const int MaxSamplesPerEdge = 256;

    /// <summary>
    /// The outline of <paramref name="problem"/> seen along <paramref name="projection"/>: every solid's and sheet's feature
    /// edges (not air), hidden edges per <paramref name="options"/>. <paramref name="note"/> is set when hidden edges could
    /// not be worked out.
    /// </summary>
    public static Em3dScene Outline(Em3dProblem problem, Em3dProjection projection, Em3dOutlineOptions? options, out string? note)
    {
        ArgumentNullException.ThrowIfNull(problem);
        options ??= new Em3dOutlineOptions();
        note = null;
        var box = problem.Boundary;
        double largest = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        double tol = Math.Max(1e-15, SnapFraction * largest);
        var (fMin, fMax) = Frame(problem, tol);
        var dielectrics = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric)
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        bool Omitted(string name) => options.Omit?.Contains(name) == true;

        // ── the parts, as triangles ─────────────────────────────────────────────────────────────
        var parts = new List<OutlinePart>();
        foreach (var s in problem.Solids)
        {
            if (s.Role == Em3dRole.Air || Omitted(s.Name)) continue;
            Em3dTriangleMesh mesh;
            if (s.Primitive is Em3dBox b)
            {
                var lo = new Point3(Math.Max(b.Min.X, fMin.X), Math.Max(b.Min.Y, fMin.Y), Math.Max(b.Min.Z, fMin.Z));
                var hi = new Point3(Math.Min(b.Max.X, fMax.X), Math.Min(b.Max.Y, fMax.Y), Math.Min(b.Max.Z, fMax.Z));
                if (!(hi.X > lo.X && hi.Y > lo.Y && hi.Z > lo.Z)) continue;
                mesh = Em3dTessellation.Of(s with { Primitive = new Em3dBox(lo, hi) });
            }
            else mesh = Em3dTessellation.Of(s);
            parts.Add(new OutlinePart(s.Name, s.Role, s.Material, s.Order, false, mesh));
        }
        foreach (var sh in problem.Sheets)
        {
            if (Omitted(sh.Name)) continue;
            parts.Add(new OutlinePart(sh.Name, Em3dRole.Conductor, sh.Material, sh.Order, true, Em3dTessellation.OfSheet(sh)));
        }

        // ── the feature edges ───────────────────────────────────────────────────────────────────
        var edges = new List<(int Part, Point3 A, Point3 B)>();
        for (int k = 0; k < parts.Count; k++)
        {
            int part = k;
            FeatureEdges(parts[k].Mesh, projection.TowardFrom, (a, b) =>
            {
                if (projection.ClipNear(a, b) is { } seg) edges.Add((part, seg.A, seg.B));
            });
        }

        double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
        void Grow(Uv q) { u0 = Math.Min(u0, q.U); v0 = Math.Min(v0, q.V); u1 = Math.Max(u1, q.U); v1 = Math.Max(v1, q.V); }
        foreach (var (_, a, b) in edges) { Grow(projection.Project(a)); Grow(projection.Project(b)); }
        var frameCorners = BoxEdges(fMin, fMax).Select(e => projection.ClipNear(e.A, e.B)).OfType<(Point3 A, Point3 B)>()
                                                .SelectMany(e => new[] { e.A, e.B }).Select(projection.Project).ToList();
        if (double.IsInfinity(u0)) foreach (var q in frameCorners) Grow(q);
        double span = Math.Max(u1 - u0, v1 - v0);

        // ── hidden edges ────────────────────────────────────────────────────────────────────────
        var mode = options.Hidden;
        Occluder? occluder = null;
        if (mode != Em3dHiddenEdges.Shown && edges.Count > 0)
        {
            long triangles = parts.Sum(p => (long)p.Mesh.Triangles.Count);
            if (triangles > options.TriangleBudget)
            {
                note = string.Create(CultureInfo.InvariantCulture,
                    $"Hidden edges are drawn: the model has {triangles:N0} triangles, over the {options.TriangleBudget:N0} this works hidden lines out for.");
                mode = Em3dHiddenEdges.Shown;
            }
            else occluder = new Occluder(parts, projection, Math.Max(span, 1e-30));
        }

        var lines = new List<Em3dSceneLine>();
        foreach (var (k, a, b) in edges)
        {
            var p = parts[k];
            void Emit(Point3 x, Point3 y, bool hidden)
            {
                if (hidden && mode == Em3dHiddenEdges.Removed) return;
                lines.Add(new Em3dSceneLine(p.Name, p.Role, p.Material, p.Order, p.IsSheet,
                                            projection.Project(x), projection.Project(y), 0, hidden));
            }
            if (occluder is null) Emit(a, b, false);
            else occluder.Split(a, b, Math.Max(span, 1e-30), Emit);
        }

        // Precedence is paint order, as the isometric's; hidden edges first, so a visible edge over one is not dashed.
        var precedence = Em3dPrecedence.Of(problem);
        lines = [.. lines.OrderBy(l => l.Hidden ? 0 : 1).ThenBy(l => precedence.Of(l.Role, l.Order, l.IsSheet))];

        var ports = problem.Ports.Select(p =>
        {
            Point3[] c = p.Min.X == p.Max.X
                ? [p.Min, new(p.Min.X, p.Max.Y, p.Min.Z), p.Max, new(p.Min.X, p.Min.Y, p.Max.Z)]
                : p.Min.Y == p.Max.Y
                    ? [p.Min, new(p.Max.X, p.Min.Y, p.Min.Z), p.Max, new(p.Min.X, p.Min.Y, p.Max.Z)]
                    : [p.Min, new(p.Max.X, p.Min.Y, p.Min.Z), p.Max, new(p.Min.X, p.Max.Y, p.Min.Z)];
            return c.All(q => projection.Ahead(q) >= Em3dProjection.NearFraction * projection.Focal)
                ? new Em3dScenePort(p.Number, [.. c.Select(projection.Project)]) : null;
        }).OfType<Em3dScenePort>().ToList();

        var boxEdges = BoxEdges(fMin, fMax).Select(e => projection.ClipNear(e.A, e.B)).OfType<(Point3 A, Point3 B)>()
                                           .Select(e => new Em3dBoxEdge(projection.Project(e.A), projection.Project(e.B), true)).ToList();
        string[] names = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
        Em3dBoundaryKind[] kinds = [box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax];
        var faces = names.Select((n, f) => new Em3dSceneFace(n, kinds[f], Em3dFaceSide.None, 0)).ToList();

        return new Em3dScene(new Em3dView(Em3dViewKind.Projection, 0) { Projection = projection }, 0, tol, [], lines, ports,
                             boxEdges, new Uv(u0, v0), new Uv(u1, v1), faces, dielectrics);
    }

    private sealed record OutlinePart(string Name, Em3dRole Role, string Material, int Order, bool IsSheet, Em3dTriangleMesh Mesh);

    /// <summary>
    /// A mesh's feature edges seen from <paramref name="toward"/>: open or non-manifold edges, turns sharper than
    /// <see cref="SharpEdgeDegrees"/>, and silhouettes. The mesh is welded by position first — a kernel solid's display
    /// mesh repeats a vertex per face, and unwelded every triangle edge would read as open.
    /// </summary>
    internal static void FeatureEdges(Em3dTriangleMesh mesh, Point3 toward, Action<Point3, Point3> edge)
        => FeatureEdges(mesh, _ => toward, edge);

    /// <summary>As above, with the viewer's direction taken at each edge's midpoint — a perspective camera's silhouette.</summary>
    internal static void FeatureEdges(Em3dTriangleMesh mesh, Func<Point3, Point3> towardFrom, Action<Point3, Point3> edge)
    {
        const double Quantum = 1e-12;   // a picometre: far below any drawn feature, far above a double's rounding here
        var weld = new Dictionary<(long, long, long), int>();
        var at = new List<Point3>();
        var index = new int[mesh.Vertices.Count];
        for (int i = 0; i < index.Length; i++)
        {
            var p = mesh.Vertices[i];
            var key = ((long)Math.Round(p.X / Quantum), (long)Math.Round(p.Y / Quantum), (long)Math.Round(p.Z / Quantum));
            if (!weld.TryGetValue(key, out int w)) { w = at.Count; weld[key] = w; at.Add(p); }
            index[i] = w;
        }

        var normals = new List<Point3>(mesh.Triangles.Count);
        var faces = new Dictionary<(int, int), List<int>>();
        foreach (var t in mesh.Triangles)
        {
            int a = index[t.A], b = index[t.B], c = index[t.C];
            if (a == b || b == c || c == a) continue;
            int k = normals.Count;
            normals.Add(Normal(at[a], at[b], at[c]));
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = x < y ? (x, y) : (y, x);
                (faces.TryGetValue(key, out var l) ? l : faces[key] = []).Add(k);
            }
        }
        foreach (var (key, list) in faces.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
        {
            bool draw;
            if (list.Count != 2) draw = true;
            else
            {
                var n0 = normals[list[0]]; var n1 = normals[list[1]];
                double cos = Dot(n0, n1);
                if (cos < CosSharp) draw = true;
                else
                {
                    var (p, q) = (at[key.Item1], at[key.Item2]);
                    var toward = towardFrom(new Point3((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2));
                    draw = Dot(n0, toward) > 0 != Dot(n1, toward) > 0;
                }
            }
            if (draw) edge(at[key.Item1], at[key.Item2]);
        }
    }

    /// <summary>Every triangle of the outline's parts, projected, in a bucket grid: what hides an edge.</summary>
    private sealed class Occluder
    {
        // Per triangle, 10 doubles: vertex 0's (u, v), edge 1 and edge 2 in (u, v), 1/det, vertex 0's depth and the
        // depth's change along each edge.
        private readonly double[] _t;
        private readonly int[] _cellStart, _cellItems;
        private readonly int _g;
        private readonly double _u0, _v0, _cw, _ch, _depthEps;
        private readonly Em3dProjection _p;

        private const double InsideEps = 1e-9;   // barycentric, so the boundary is INCLUDED

        public Occluder(List<OutlinePart> parts, Em3dProjection p, double span)
        {
            _p = p;
            _depthEps = 1e-7 * span;
            var t = new List<double>();
            var boxes = new List<(double U0, double V0, double U1, double V1)>();
            foreach (var part in parts)
            {
                var m = part.Mesh;
                foreach (var tri in m.Triangles)
                {
                    var a = m.Vertices[tri.A]; var b = m.Vertices[tri.B]; var c = m.Vertices[tri.C];
                    // A perspective camera's: a triangle reaching the eye's near plane is left out rather than folded
                    // through infinity. Orthographic, Ahead is +∞ and nothing is.
                    double near = Em3dProjection.NearFraction * p.Focal;
                    if (p.Ahead(a) < near || p.Ahead(b) < near || p.Ahead(c) < near) continue;
                    var pa = p.Project(a); var pb = p.Project(b); var pc = p.Project(c);
                    double e1u = pb.U - pa.U, e1v = pb.V - pa.V, e2u = pc.U - pa.U, e2v = pc.V - pa.V;
                    double det = e1u * e2v - e1v * e2u;
                    // Edge-on: it hides nothing (its neighbours, facing the viewer, do).
                    if (Math.Abs(det) <= 1e-18 * span * span) continue;
                    double da = p.Depth(a);
                    t.AddRange([pa.U, pa.V, e1u, e1v, e2u, e2v, 1 / det, da, p.Depth(b) - da, p.Depth(c) - da]);
                    boxes.Add((Math.Min(pa.U, Math.Min(pb.U, pc.U)), Math.Min(pa.V, Math.Min(pb.V, pc.V)),
                               Math.Max(pa.U, Math.Max(pb.U, pc.U)), Math.Max(pa.V, Math.Max(pb.V, pc.V))));
                }
            }
            _t = [.. t];
            int n = boxes.Count;
            _g = Math.Clamp((int)Math.Ceiling(Math.Sqrt(n / 4.0)), 1, 256);
            double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
            foreach (var bx in boxes) { u0 = Math.Min(u0, bx.U0); v0 = Math.Min(v0, bx.V0); u1 = Math.Max(u1, bx.U1); v1 = Math.Max(v1, bx.V1); }
            if (n == 0) { u0 = v0 = 0; u1 = v1 = 1; }
            _u0 = u0; _v0 = v0;
            _cw = Math.Max((u1 - u0) / _g, 1e-30); _ch = Math.Max((v1 - v0) / _g, 1e-30);

            var counts = new int[_g * _g + 1];
            void Cells(int k, Action<int> each)
            {
                var bx = boxes[k];
                int i0 = Cell(bx.U0, _u0, _cw), i1 = Cell(bx.U1, _u0, _cw), j0 = Cell(bx.V0, _v0, _ch), j1 = Cell(bx.V1, _v0, _ch);
                for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) each(j * _g + i);
            }
            for (int k = 0; k < n; k++) Cells(k, c => counts[c + 1]++);
            for (int c = 0; c < _g * _g; c++) counts[c + 1] += counts[c];
            _cellStart = counts;
            _cellItems = new int[counts[_g * _g]];
            var fill = (int[])counts.Clone();
            for (int k = 0; k < n; k++) { int kk = k; Cells(k, c => _cellItems[fill[c]++] = kk); }
        }

        private int Cell(double x, double origin, double size) => Math.Clamp((int)Math.Floor((x - origin) / size), 0, _g - 1);

        /// <summary>Whether a triangle lies in front of <paramref name="q"/>.</summary>
        public bool Hidden(Point3 q)
        {
            var pq = _p.Project(q);
            double depth = _p.Depth(q);
            double fu = (pq.U - _u0) / _cw, fv = (pq.V - _v0) / _ch;
            if (fu < -1e-9 || fv < -1e-9 || fu > _g + 1e-9 || fv > _g + 1e-9) return false;
            int cell = Cell(pq.U, _u0, _cw) + Cell(pq.V, _v0, _ch) * _g;
            for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
            {
                int o = _cellItems[k] * 10;
                double pu = pq.U - _t[o], pv = pq.V - _t[o + 1];
                double s = (pu * _t[o + 5] - pv * _t[o + 4]) * _t[o + 6];
                if (s < -InsideEps) continue;
                double r = (_t[o + 2] * pv - _t[o + 3] * pu) * _t[o + 6];
                if (r < -InsideEps || s + r > 1 + InsideEps) continue;
                if (_t[o + 7] + s * _t[o + 8] + r * _t[o + 9] > depth + _depthEps) return true;
            }
            return false;
        }

        /// <summary>Emits <paramref name="a"/>–<paramref name="b"/> as runs, each visible or hidden.</summary>
        public void Split(Point3 a, Point3 b, double span, Action<Point3, Point3, bool> emit)
        {
            Point3 At(double f) => new(a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y), a.Z + f * (b.Z - a.Z));
            var pa = _p.Project(a); var pb = _p.Project(b);
            double len = Math.Sqrt((pb.U - pa.U) * (pb.U - pa.U) + (pb.V - pa.V) * (pb.V - pa.V));
            int n = Math.Clamp((int)Math.Ceiling(len / span * MaxSamplesPerEdge), 1, MaxSamplesPerEdge);
            double Mid(int i) => (i + 0.5) / n;

            bool cur = Hidden(At(Mid(0)));
            double start = 0;
            for (int i = 1; i < n; i++)
            {
                bool h = Hidden(At(Mid(i)));
                if (h == cur) continue;
                // The change is somewhere between the two samples: bisect it to a part in 2^12 of their gap.
                double lo = Mid(i - 1), hi = Mid(i);
                for (int it = 0; it < 12; it++)
                {
                    double m = (lo + hi) / 2;
                    if (Hidden(At(m)) == cur) lo = m; else hi = m;
                }
                double cut = (lo + hi) / 2;
                emit(At(start), At(cut), cur);
                start = cut;
                cur = h;
            }
            emit(At(start), b, cur);
        }
    }
}
