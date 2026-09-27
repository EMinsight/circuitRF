// brief-em3d-67 R-em3d67-2 — an object's EDGES: named runs a user selects, not the segments a snap walks.
//
// A RUN, NOT A SEGMENT (R-em3d67-2a). The feature table (Scene3DFeatureTable) finds feature SEGMENTS face by face: where
// two different faces meet, or where a face has no neighbour (a sheet's rim). An edge is a maximal connected run of them
// between the SAME two faces, so a managed cylinder's rim is one edge of 32 segments. A segment with one face on both
// sides (a cylinder's seam) is no feature and never an edge — the table has already left it out.
//
// THE NAME (R-em3d67-2b, overview §1g as brief 61 Q7 corrected it) is the two faces' names, relative to the object, sorted
// ordinally and joined by '|': xmax|zmax, bottom|side, cavity:side|zmax. A sheet's rim is <face>| — one side. Where one
// pair bounds more than one run a THIRD FIELD numbers them — side|zmax|1, side|zmax|2 — never a '#n' suffix, which is a
// split face's and would make x|zmax#2 mean two different things. The runs are numbered in the geometric order the
// geometry worker numbers a kernel object's by (README "The geometric order", brief 64 §2d): by CENTROID in the object's
// OWN frame, x then y then z, each rounded to 1 nm, so moving or rotating the object renumbers nothing and a name read
// here on a managed box is the name the worker resolves when that box is filleted. (Brief 67 §2b proposed the lowest
// point; the worker's order was already fixed and the two must agree — src/Render/RESOLVED.md.)
//
// WHERE THE RUNS COME FROM (R-em3d67-2c). A managed object's from its triangles and face IDs, as above. A kernel object's
// from the worker's edge table — its names, its polylines, and per edge the curve kind, the ends, the tangents there, the
// midpoint along the curve and a circle's centre — never from triangle adjacency: a curved face's inner triangle edges
// are artefacts of the deflection.
//
// VERTICES. A kernel object's vertices are its B-rep's: the ends of its edges, welded. A curved edge's polyline points are
// NOT vertices, because they move with the display deflection (R-em3d67-3e). A managed object's run ends are recorded
// for the tangent chain only; its snapping is exactly what it was (gate 4) and never reads this table.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Edit;

/// <summary>What curve an edge is.</summary>
public enum Scene3DEdgeKind { Line, Circle, Arc, Other }

/// <summary>One named edge of an object, in the metres of the table it belongs to (an instance adds its offset).</summary>
public sealed class Scene3DEdge
{
    /// <summary>Relative to its object: <c>xmax|zmax</c>, <c>side|zmax|2</c>, <c>surface|</c>.</summary>
    public required string Name { get; init; }
    /// <summary>Its two faces (indices into the object's faces); <see cref="Face1"/> is −1 on a rim.</summary>
    public required int Face0 { get; init; }
    public required int Face1 { get; init; }
    /// <summary>Its faces' names, sorted; <see cref="FaceName1"/> is empty on a rim.</summary>
    public required string FaceName0 { get; init; }
    public required string FaceName1 { get; init; }
    public required Scene3DEdgeKind Kind { get; init; }
    /// <summary>The run, start to end. A closed edge's last point is its first.</summary>
    public required Point3[] Points { get; init; }
    public required bool Closed { get; init; }
    /// <summary>Metres, along the curve (the kernel's) or the run (a managed object's, which is exact).</summary>
    public required double Length { get; init; }
    /// <summary>The unit tangents at the start and the end, along the run.</summary>
    public required Point3 TangentStart { get; init; }
    public required Point3 TangentEnd { get; init; }
    /// <summary>Its end vertices (indices into <see cref="Scene3DEdges.Vertices"/>); a closed edge's are equal.</summary>
    public required int StartVertex { get; init; }
    public required int EndVertex { get; init; }
    /// <summary>The point halfway along the curve — the kernel's, exact — or null (a closed edge, or a managed one: its
    /// midpoints are the feature table's, unchanged).</summary>
    public Point3? Mid { get; init; }
    /// <summary>A circle's or an arc's centre, and its radius (metres); null for any other curve.</summary>
    public Point3? Centre { get; init; }
    public double Radius { get; init; }

    /// <summary>How many segments the run is.</summary>
    public int Segments => Math.Max(0, Points.Length - 1);
}

/// <summary>brief-em3d-67 — an object's named edges, found per face in O(1) (compressed rows, as the feature table's).</summary>
public sealed class Scene3DEdges
{
    public required Scene3DEdge[] Edges { get; init; }
    /// <summary>The object's vertices — a kernel object's B-rep vertices; a managed object's run ends.</summary>
    public required Point3[] Vertices { get; init; }
    /// <summary>Face f's edges are FaceEdges[FaceEdgeStart[f] .. FaceEdgeStart[f + 1]].</summary>
    public required int[] FaceEdgeStart { get; init; }
    public required int[] FaceEdges { get; init; }
    /// <summary>True for a kernel object: exact curve data, and the table snapping reads (R-em3d67-3e).</summary>
    public required bool FromKernel { get; init; }

    public int FaceCount => FaceEdgeStart.Length - 1;

    /// <summary>The edge named <paramref name="name"/>, or −1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Edges.Length; i++) if (Edges[i].Name == name) return i;
        return -1;
    }

    /// <summary>No edges: a sweep's or a sphere's.</summary>
    public static readonly Scene3DEdges Empty = new() { Edges = [], Vertices = [], FaceEdgeStart = [0], FaceEdges = [], FromKernel = false };

    // ── a managed object: runs of the feature table's segments ────────────────────────────────

    /// <summary>
    /// <paramref name="t"/>'s segments grouped into named runs. <paramref name="faceNames"/> names the faces (a face with no
    /// name is called <c>face&lt;n&gt;</c>); <paramref name="toOwn"/> takes a world point into the object's own frame — the
    /// frame the runs of one pair are numbered in — or null for the identity.
    /// </summary>
    public static Scene3DEdges OfSegments(Scene3DFeatureTable t, IReadOnlyList<string> faceNames, Func<Point3, Point3>? toOwn)
    {
        string FaceName(int f) => f < 0 ? "" : f < faceNames.Count && faceNames[f].Length > 0 ? faceNames[f] : $"face{f}";
        // Segments by their (sorted) face pair.
        var byPair = new Dictionary<(int, int), List<int>>();
        for (int e = 0; e < t.EdgeA.Length; e++)
        {
            int f0 = t.EdgeFace0[e], f1 = t.EdgeFace1[e];
            var key = f1 < 0 ? (f0, -1) : Sorted(f0, f1, FaceName);
            if (!byPair.TryGetValue(key, out var list)) byPair[key] = list = [];
            list.Add(e);
        }
        var named = new List<(string Pair, int F0, int F1, List<int> Run)>();
        foreach (var ((f0, f1), segments) in byPair)
            foreach (var run in Runs(t, segments))
                named.Add((f1 < 0 ? FaceName(f0) + "|" : FaceName(f0) + "|" + FaceName(f1), f0, f1, run));

        var vertices = new List<Point3>();
        var vertexOf = new Dictionary<int, int>();
        int Vertex(int corner)
        {
            if (!vertexOf.TryGetValue(corner, out int v)) { v = vertices.Count; vertices.Add(t.Vertices[corner]); vertexOf[corner] = v; }
            return v;
        }
        var edges = new List<Scene3DEdge>();
        foreach (var group in named.GroupBy(n => n.Pair, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var runs = group.Select(g => (g, Points: Walk(t, g.Run))).ToList();
            if (runs.Count > 1) runs = [.. runs.OrderBy(r => CentroidKey(r.Points.Points, toOwn), KeyOrder.Instance)];
            for (int k = 0; k < runs.Count; k++)
            {
                var ((pair, f0, f1, _), (points, corners)) = runs[k];
                bool closed = corners[0] == corners[^1] && points.Length > 2;
                var (kind, centre, radius) = Classify(points, closed);
                edges.Add(new Scene3DEdge
                {
                    Name = runs.Count > 1 ? $"{pair}|{k + 1}" : pair,
                    Face0 = f0, Face1 = f1, FaceName0 = FaceName(f0), FaceName1 = FaceName(f1), Kind = kind, Points = points,
                    Closed = closed, Length = Length(points), TangentStart = Unit(Sub(points[1], points[0])),
                    TangentEnd = Unit(Sub(points[^1], points[^2])), StartVertex = Vertex(corners[0]), EndVertex = Vertex(corners[^1]),
                    Centre = centre, Radius = radius,
                });
            }
        }
        return Finish(edges, vertices, t.FaceCount, fromKernel: false);
    }

    private static (int, int) Sorted(int a, int b, Func<int, string> name)
        => string.CompareOrdinal(name(a), name(b)) <= 0 ? (a, b) : (b, a);

    /// <summary>The connected runs of <paramref name="segments"/> (corner indices shared).</summary>
    private static IEnumerable<List<int>> Runs(Scene3DFeatureTable t, List<int> segments)
    {
        var byCorner = new Dictionary<int, List<int>>();
        foreach (int e in segments)
            foreach (int c in (ReadOnlySpan<int>)[t.EdgeA[e], t.EdgeB[e]])
            {
                if (!byCorner.TryGetValue(c, out var l)) byCorner[c] = l = [];
                l.Add(e);
            }
        var seen = new HashSet<int>();
        foreach (int start in segments)
        {
            if (!seen.Add(start)) continue;
            var run = new List<int> { start };
            var stack = new Stack<int>([start]);
            while (stack.Count > 0)
            {
                int e = stack.Pop();
                foreach (int c in (ReadOnlySpan<int>)[t.EdgeA[e], t.EdgeB[e]])
                    foreach (int n in byCorner[c])
                        if (seen.Add(n)) { run.Add(n); stack.Push(n); }
            }
            yield return run;
        }
    }

    /// <summary>A run's segments in order, as points and the corners they are: from an end (a corner on one segment), or —
    /// a closed loop — from its lowest corner, so the order does not depend on a dictionary's.</summary>
    private static (Point3[] Points, int[] Corners) Walk(Scene3DFeatureTable t, List<int> run)
    {
        var byCorner = new Dictionary<int, List<int>>();
        foreach (int e in run)
            foreach (int c in (ReadOnlySpan<int>)[t.EdgeA[e], t.EdgeB[e]])
            {
                if (!byCorner.TryGetValue(c, out var l)) byCorner[c] = l = [];
                l.Add(e);
            }
        var ends = byCorner.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList();
        int at = (ends.Count > 0 ? ends : [.. byCorner.Keys]).OrderBy(c => t.Vertices[c], PointOrder.Instance).First();
        var corners = new List<int> { at };
        var used = new HashSet<int>();
        while (true)
        {
            int next = -1;
            foreach (int e in byCorner[at])
                if (!used.Contains(e)) { next = e; break; }
            if (next < 0) break;
            used.Add(next);
            at = t.EdgeA[next] == at ? t.EdgeB[next] : t.EdgeA[next];
            corners.Add(at);
        }
        return ([.. corners.Select(c => t.Vertices[c])], [.. corners]);
    }

    /// <summary>A run's shape: a line when every point is on the first segment's line; a circle when it is closed and every
    /// point is equally far from their centroid in one plane (a managed cylinder's rim, made of chords); anything else.</summary>
    private static (Scene3DEdgeKind, Point3?, double) Classify(Point3[] p, bool closed)
    {
        var d = Unit(Sub(p[1], p[0]));
        double len = Length(p);
        double tol = 1e-9 * Math.Max(len, 1e-9);
        if (p.All(q => Norm(Cross(Sub(q, p[0]), d)) <= tol)) return (Scene3DEdgeKind.Line, null, 0);
        if (!closed || p.Length < 4) return (Scene3DEdgeKind.Other, null, 0);
        int n = p.Length - 1;
        var c = new Point3(p.Take(n).Average(q => q.X), p.Take(n).Average(q => q.Y), p.Take(n).Average(q => q.Z));
        double r = p.Take(n).Average(q => Norm(Sub(q, c)));
        return p.Take(n).All(q => Math.Abs(Norm(Sub(q, c)) - r) <= 1e-6 * r) ? (Scene3DEdgeKind.Circle, c, r) : (Scene3DEdgeKind.Other, null, 0);
    }

    // ── a kernel object: the worker's edge table ─────────────────────────────────────────────

    /// <summary>
    /// A kernel solid's named edges as the worker gave them (R-em3d67-2c): <paramref name="faceNames"/> are its faces by
    /// index (the display mesh's face numbering), and each edge's two faces are found there by name.
    /// </summary>
    public static Scene3DEdges OfKernel(IReadOnlyList<Em3dShapeEdge> kernel, IReadOnlyList<string> faceNames, int faceCount)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int f = 0; f < faceNames.Count; f++) index.TryAdd(faceNames[f], f);
        var vertices = new List<Point3>();
        var weld = new Dictionary<(long, long, long), int>();
        int Vertex(Point3 p)
        {
            // Two edges' ends at one B-rep vertex agree to far below a picometre.
            var key = ((long)Math.Round(p.X * 1e12), (long)Math.Round(p.Y * 1e12), (long)Math.Round(p.Z * 1e12));
            if (!weld.TryGetValue(key, out int v)) { v = vertices.Count; vertices.Add(p); weld[key] = v; }
            return v;
        }
        var edges = new List<Scene3DEdge>();
        foreach (var e in kernel)
        {
            if (e.Polyline.Count < 2) continue;
            Point3[] points = [.. e.Polyline];
            if (e.Closed && !Same(points[0], points[^1])) points = [.. points, points[0]];
            int f0 = index.GetValueOrDefault(e.FaceA, -1), f1 = index.GetValueOrDefault(e.FaceB, -1);
            var kind = e.Kind switch
            {
                "line" => Scene3DEdgeKind.Line,
                "circle" => e.Closed ? Scene3DEdgeKind.Circle : Scene3DEdgeKind.Arc,
                _ => Scene3DEdgeKind.Other,
            };
            var (t0, t1) = e.Tangents ?? (Unit(Sub(points[1], points[0])), Unit(Sub(points[^1], points[^2])));
            edges.Add(new Scene3DEdge
            {
                Name = e.Name, Face0 = f0, Face1 = f1, FaceName0 = e.FaceA, FaceName1 = e.FaceB, Kind = kind, Points = points,
                Closed = e.Closed, Length = e.LengthM > 0 ? e.LengthM : Length(points), TangentStart = t0, TangentEnd = t1,
                StartVertex = Vertex(points[0]), EndVertex = e.Closed ? Vertex(points[0]) : Vertex(points[^1]),
                Mid = e.Closed ? null : e.Mid, Centre = e.Centre, Radius = e.RadiusM,
            });
        }
        return Finish(edges, vertices, Math.Max(faceCount, faceNames.Count), fromKernel: true);
    }

    private static Scene3DEdges Finish(List<Scene3DEdge> edges, List<Point3> vertices, int faceCount, bool fromKernel)
    {
        var rows = new List<int>[Math.Max(0, faceCount)];
        for (int f = 0; f < rows.Length; f++) rows[f] = [];
        for (int e = 0; e < edges.Count; e++)
            foreach (int f in (ReadOnlySpan<int>)[edges[e].Face0, edges[e].Face1])
                if (f >= 0 && f < rows.Length && !rows[f].Contains(e)) rows[f].Add(e);
        var start = new int[rows.Length + 1];
        for (int f = 0; f < rows.Length; f++) start[f + 1] = start[f] + rows[f].Count;
        var items = new int[start[^1]];
        for (int f = 0; f < rows.Length; f++) rows[f].CopyTo(items, start[f]);
        return new Scene3DEdges { Edges = [.. edges], Vertices = [.. vertices], FaceEdgeStart = start, FaceEdges = items, FromKernel = fromKernel };
    }

    // ── the geometric order (the worker's: README "The geometric order") ─────────────────────

    /// <summary>A run's centroid (length-weighted, as a curve's centre of mass is) in the object's own frame, in nanometres.</summary>
    internal static (long X, long Y, long Z) CentroidKey(Point3[] p, Func<Point3, Point3>? toOwn)
    {
        double sx = 0, sy = 0, sz = 0, sl = 0;
        for (int i = 1; i < p.Length; i++)
        {
            double l = Norm(Sub(p[i], p[i - 1]));
            sx += l * (p[i].X + p[i - 1].X) / 2; sy += l * (p[i].Y + p[i - 1].Y) / 2; sz += l * (p[i].Z + p[i - 1].Z) / 2;
            sl += l;
        }
        var c = sl > 0 ? new Point3(sx / sl, sy / sl, sz / sl) : p[0];
        if (toOwn is not null) c = toOwn(c);
        return ((long)Math.Round(c.X * 1e9, MidpointRounding.AwayFromZero), (long)Math.Round(c.Y * 1e9, MidpointRounding.AwayFromZero),
                (long)Math.Round(c.Z * 1e9, MidpointRounding.AwayFromZero));
    }

    private sealed class KeyOrder : IComparer<(long X, long Y, long Z)>
    {
        public static readonly KeyOrder Instance = new();
        public int Compare((long X, long Y, long Z) a, (long X, long Y, long Z) b)
            => a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);
    }

    private sealed class PointOrder : IComparer<Point3>
    {
        public static readonly PointOrder Instance = new();
        public int Compare(Point3 a, Point3 b)
            => a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);
    }

    // ── a little vector arithmetic in doubles ────────────────────────────────────────────────

    internal static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    internal static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    internal static double Norm(Point3 a) => Math.Sqrt(Dot(a, a));
    internal static Point3 Unit(Point3 a) { double n = Norm(a); return n > 0 ? new Point3(a.X / n, a.Y / n, a.Z / n) : a; }
    internal static Point3 Neg(Point3 a) => new(-a.X, -a.Y, -a.Z);
    private static bool Same(Point3 a, Point3 b) => Norm(Sub(a, b)) <= 1e-12;

    internal static double Length(Point3[] p)
    {
        double s = 0;
        for (int i = 1; i < p.Length; i++) s += Norm(Sub(p[i], p[i - 1]));
        return s;
    }
}
