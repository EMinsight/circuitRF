// The board region and its edge band (brief-review-4 R-rev4-2 / R-rev4-3, built in brief-review-2).
//
// The board is read off the outline layers the way Crop to Rectangle reads it, by the same code: the
// outline's shapes unioned (LayoutBooleans), turned into polygons (PlanarExtractor) and classified by
// Em3dLayoutSolids.Builder.BoardRegions — a closed region is the board as drawn, a LINE drawn round the
// board (a Gerber profile's strokes, merged) is read through its holes, and a stroked outline's edge is
// its CENTRE line, so the holes are grown back by half the stroke as the crop grows them. Holes in the
// board (slots, milled openings) are edges too (brief-review-4 D3). Never the outline's bounding box: a
// board with a notch would have its edge checked where there is no edge.
//
// The band is `board − erode(board, w)` with ROUND joins: the strip within w of the edge by true
// distance. A mitred erosion is exact at the board's convex corners but under-measures at a notch's
// inner corner, which is the one place an edge check has to be right.

using Clipper2Lib;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Review;

internal sealed class ReviewBoard
{
    /// <summary>The board, or empty when <see cref="Refusal"/> says why there is none.</summary>
    public Paths64 Region { get; }

    /// <summary>Why the board has no edge to measure from — the sentence every edge check reports as
    /// not run — or null.</summary>
    public string? Refusal { get; }

    private readonly List<(Point64 A, Point64 B, Bbox Box)> _edges = [];
    private readonly Dictionary<long, Paths64> _bands = new();

    private ReviewBoard(Paths64 region, string? refusal)
    {
        Region = region;
        Refusal = refusal;
        foreach (var path in region)
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % path.Count];
                _edges.Add((a, b, new Bbox(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))));
            }
    }

    internal static ReviewBoard Build(IReadOnlyList<LayoutShape> shapes, Technology tech, ReviewLayers layers,
                                      LayoutUnit unit, int dbuPerMicron)
    {
        if (layers.Outline.Count == 0)
            return new([], "No layer of this technology is the board outline, so there is no board edge to measure "
                         + "from — set the outline layer's Gerber X2 Function to Profile in the Technology Editor.");

        var outline = shapes.Where(s => layers.Outline.Contains(s.Layer) && s is not (LabelShape or BitmapShape)).ToList();
        string names = string.Join(", ", layers.Outline.OrderBy(k => k.Layer).ThenBy(k => k.Datatype).Select(layers.NameOf));
        if (outline.Count == 0)
            return new([], $"Nothing is drawn on the board outline ({names}), so there is no board edge to measure from — "
                         + "draw the board's outline there.");

        var merged = LayoutBooleans.Union(outline, tech).Shapes;
        var drawn = merged.SelectMany(s => PlanarExtractor.ToPolygons(s, tech, 1.0)).ToList();
        var boards = Em3dLayoutSolids.Builder.BoardRegions(drawn, out int lines);
        // A line that does not close has no hole, so BoardRegions keeps it as a region — the strokes
        // themselves. A board is never a strip: one as thin as a line is an outline with a gap in it.
        boards.RemoveAll(b =>
        {
            if (b.HoleRings.Count > 0) return false;
            var (x0, y0, x1, y1) = b.Bounds();
            return Em3dLayoutSolids.Builder.CharacteristicWidth(b)
                 < Em3dLayoutSolids.Builder.OutlineLineFraction * Math.Min(x1 - x0, y1 - y0);
        });

        var paths = new Paths64();
        foreach (var b in boards)
        {
            paths.Add(Oriented(b.Outer, positive: true));
            foreach (var h in b.HoleRings) paths.Add(Oriented(h, positive: false));
        }
        if (lines > 0 && outline.OfType<PathShape>().MaxBy(p => p.Width) is { Width: > 0 } line)
            paths = Clipper.InflatePaths(paths, line.Width / 2.0, JoinType.Miter, EndType.Polygon);
        var region = paths.Count == 0 ? [] : Clipper.BooleanOp(ClipType.Union, paths, [], LayoutClipper.Rule);

        if (region.Count > 0) return new(region, null);
        return new([], Unclosed(outline, names, unit, dbuPerMicron));
    }

    /// <summary>The sentence for an outline that encloses nothing: where the largest gap between stroke
    /// ends is, when it is drawn in strokes.</summary>
    private static string Unclosed(List<LayoutShape> outline, string names, LayoutUnit unit, int dbuPerMicron)
    {
        var ends = new List<(long X, long Y)>();
        foreach (var p in outline.OfType<PathShape>())
            if (p.Xy.Length >= 4)
            {
                ends.Add((p.Xy[0], p.Xy[1]));
                ends.Add((p.Xy[^2], p.Xy[^1]));
            }

        string fix = "so there is no board edge to measure from — close the outline";
        if (ends.Count < 2) return $"The board outline ({names}) encloses no region, {fix}.";

        double worst = -1;
        (long X, long Y) at = default;
        for (int i = 0; i < ends.Count; i++)
        {
            double nearest = double.PositiveInfinity;
            int j0 = -1;
            for (int j = 0; j < ends.Count; j++)
            {
                if (j == i) continue;
                double dx = ends[j].X - ends[i].X, dy = ends[j].Y - ends[i].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < nearest) { nearest = d; j0 = j; }
            }
            if (nearest > worst)
            {
                worst = nearest;
                at = ((ends[i].X + ends[j0].X) / 2, (ends[i].Y + ends[j0].Y) / 2);
            }
        }
        string where = $"({LayoutUnits.Format(at.X, unit, dbuPerMicron, 3)}, {LayoutUnits.Format(at.Y, unit, dbuPerMicron, 3)}) {LayoutUnits.Suffix(unit)}";
        return $"The board outline ({names}) does not close: its largest gap, {ReviewText.Length((long)Math.Round(worst), unit, dbuPerMicron)}, "
             + $"is at {where}, {fix} there.";
    }

    private static Path64 Oriented(IReadOnlyList<EmPoint> ring, bool positive)
    {
        var path = new Path64(ring.Count);
        foreach (var p in ring) path.Add(new Point64((long)Math.Round(p.X), (long)Math.Round(p.Y)));
        if (Clipper.IsPositive(path) != positive) path.Reverse();
        return path;
    }

    /// <summary>The strip of board within <paramref name="width"/> of its edge, outer edge and cut-outs alike.</summary>
    public Paths64 Band(long width)
    {
        if (Region.Count == 0 || width <= 0) return [];
        if (_bands.TryGetValue(width, out var hit)) return hit;
        var inner = Clipper.InflatePaths(Region, -width, JoinType.Round, EndType.Polygon);
        var band = Clipper.BooleanOp(ClipType.Difference, Region, inner, LayoutClipper.Rule);
        _bands[width] = band;
        return band;
    }

    /// <summary>The line through the middle of the band of <paramref name="width"/>: the edge moved in by half
    /// of it with ROUND joins, one closed loop per edge (the outer edge and each cut-out). Empty where the board
    /// is narrower than the band.</summary>
    public Paths64 MidLine(long width) =>
        Region.Count == 0 || width <= 0 ? [] : Clipper.InflatePaths(Region, -width / 2.0, JoinType.Round, EndType.Polygon);

    /// <summary>The closest approach of <paramref name="piece"/> to the board edge, DBU — searched only
    /// among the edges within <paramref name="within"/> of it, which is all an edge check asks about.</summary>
    public long DistanceToEdge(Paths64 piece, long within)
    {
        var box = Drc.DrcRegions.Grow(Drc.DrcRegions.BoundsOf(piece), within);
        var near = new Paths64();
        foreach (var (a, b, edge) in _edges)
            if (edge.Intersects(box)) near.Add([a, b]);
        return near.Count == 0 ? within : Drc.DrcRegions.MinDistance(piece, near);
    }
}
