// A GDSII BOUNDARY holds at most 8191 points: its XY record's length field is 16 bits, so 65531 bytes of
// payload at 8 bytes a point, and the closing point repeats the first. A polygon with more — a fine-
// pitched spiral inductor is the ordinary case — is written as several abutting polygons whose union is
// exactly the original, which is what GDSII writers conventionally do. Before brief-gdf-5 the record
// writer threw on one and `circuitrf convert` died with an unhandled exception.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

public static class GdsiiPolygonSplit
{
    /// <summary>The most distinct points one BOUNDARY may carry (8191 written, the last closing it).</summary>
    public const int MaxPoints = 8190;

    /// <summary>How many polygons in <paramref name="library"/> need splitting.</summary>
    internal static int CountOver(StreamLibrary library) =>
        library.Structures.Sum(s => s.Shapes.Count(e => e is StreamPolygon p && p.Xy.Length / 2 > MaxPoints));

    /// <summary><paramref name="xy"/> itself when it fits, else abutting rings (holes keyholed) that each do.
    /// The region is cut in half across its longer side, again and again, until every piece fits; each cut
    /// is at an integer coordinate, so the pieces share edges exactly.</summary>
    internal static IReadOnlyList<long[]> Split(long[] xy)
    {
        if (xy.Length / 2 <= MaxPoints) return [xy];
        var pieces = new List<long[]>();
        Cut([ToPath(xy)], pieces, depth: 0);
        return pieces;
    }

    private static void Cut(Paths64 region, List<long[]> pieces, int depth)
    {
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, region, [], tree, LayoutClipper.Rule);
        foreach (var (outer, holes) in Outers(tree))
        {
            int points = outer.Count + holes.Sum(h => h.Count + 2);   // a keyhole adds the hole, closed, and the bridge back
            if (points <= MaxPoints || depth > 48)
            {
                pieces.Add(StreamLowering.Keyhole(ToXy(outer), [.. holes.Select(ToXy)]));
                continue;
            }
            var b = Clipper.GetBounds(outer);
            bool acrossX = b.Width >= b.Height;
            long mid = acrossX ? b.left + b.Width / 2 : b.top + b.Height / 2;
            Paths64 own = [outer, .. holes];
            foreach (var half in acrossX
                         ? new[] { Box(b.left, b.top, mid, b.bottom), Box(mid, b.top, b.right, b.bottom) }
                         : new[] { Box(b.left, b.top, b.right, mid), Box(b.left, mid, b.right, b.bottom) })
                Cut(Clipper.Intersect(own, [half], LayoutClipper.Rule), pieces, depth + 1);
        }
    }

    /// <summary>Every outer ring with its own holes; an island inside a hole is an outer of its own.</summary>
    private static IEnumerable<(Path64 Outer, List<Path64> Holes)> Outers(PolyPath64 node)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var outer = node[i];
            var holes = new List<Path64>();
            for (int j = 0; j < outer.Count; j++)
            {
                var hole = outer[j];
                holes.Add(hole.Polygon!);
                foreach (var island in Outers(hole)) yield return island;
            }
            yield return (outer.Polygon!, holes);
        }
    }

    private static Path64 Box(long x0, long y0, long x1, long y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    private static Path64 ToPath(long[] xy)
    {
        var path = new Path64(xy.Length / 2);
        for (int i = 0; i + 1 < xy.Length; i += 2) path.Add(new Point64(xy[i], xy[i + 1]));
        return path;
    }

    private static long[] ToXy(Path64 path)
    {
        var xy = new long[path.Count * 2];
        for (int i = 0; i < path.Count; i++) { xy[2 * i] = path[i].X; xy[2 * i + 1] = path[i].Y; }
        return xy;
    }
}
