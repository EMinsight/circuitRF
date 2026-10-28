// `edge-trace` and `edge-stitching` — traces at the board edge, and the edge closed with ground
// (brief-review-4 R-rev4-6 / R-rev4-7). `edge-copper` is the third edge check, in its own file.
//
// Both measure from ReviewBoard: the outer edge and every cut-out (D3).
//
// edge-stitching measures STRETCHES along the line through the middle of its band, not the band's area
// over its width. A ground pour pulled back from the edge by the pullback edge-copper asks for leaves a
// strip of band uncovered all the way round the board, and area over width would count that strip as
// edge with no ground; on the middle line a pour covers the edge wherever it reaches half way across the
// band. For the same reason a via's disc is sized on the middle line: a via at depth d from the edge
// covers the line for max-gap / 2 either side of where it is, whatever d is, so a stretch between two
// vias is exactly their spacing less max-gap, and the gap reported is the stretch plus max-gap / 2 at
// each end that a via bounds.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class EdgeTraceCheck : ReviewCheck
{
    public const string Id = "edge-trace";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Traces at the board edge",
        "A trace on any copper layer closer to the board edge than the minimum distance. Copper that is not a "
      + "trace (a pour) is edge-copper's business only.",
        [new("min-distance", "Minimum distance", ReviewThresholdKind.Length, 1.0e-3)]);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        if (ctx.Layers.Copper.Count == 0) { run.NotRun(EdgeCopperCheck.NoCopper); return; }
        var board = ctx.Board;
        if (board.Refusal is { } why) { run.NotRun(why); return; }
        if (ctx.Traces.Refusal is { } noTraces) { run.NotRun(noTraces); return; }

        long min = run.Length("min-distance");
        var band = board.Band(min);
        if (band.Count == 0) return;
        string asks = ctx.Length(min);

        foreach (var copper in ctx.Layers.Copper)
            foreach (var (layer, trace) in ctx.TracesOn(copper))
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (board.DistanceToEdge(trace.Region, min) >= min) continue;
                string width = ctx.Length((long)Math.Round(trace.Width));
                var inside = Clipper.BooleanOp(ClipType.Intersection, trace.Region, band, LayoutClipper.Rule);
                foreach (var piece in DrcRegions.Components(inside))
                {
                    long distance = board.DistanceToEdge(piece, min);
                    string measured = ctx.Length(distance);
                    run.Add(layer,
                        distance == 0
                            ? $"A trace ({width} wide) reaches the board edge; the review asks for {asks} clear of it"
                            : $"Trace {measured} from the board edge ({width} wide); the review asks for {asks}",
                        measured, piece);
                }
            }
    }
}

internal sealed class EdgeStitchingCheck : ReviewCheck
{
    public const string Id = "edge-stitching";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Edge stitching",
        "A stretch of board edge, at least the maximum gap long, with no ground copper along it on an outer copper "
      + "layer, or between two ground stitching vias. Both are measured within the band width of the edge.",
        [
            new("band-width", "Band width", ReviewThresholdKind.Length, 1.0e-3),
            new("max-gap", "Maximum gap", ReviewThresholdKind.Length, 2.0e-3),
        ]);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var board = ctx.Board;
        if (board.Refusal is { } why) { run.NotRun(why); return; }
        if (ctx.Layers.GroundReference is null) { run.NotRun(ReviewPads.NoGroundReference); return; }
        var eval = ctx.Eval;
        var groundNets = eval.GroundNets(out string? said);
        if (groundNets.Count == 0) { run.NotRun(ReviewPads.NoGroundCopper(said)); return; }
        bool copperHalf = ctx.Layers.Copper.Count > 0, viaHalf = ctx.HasPlatedDrills;
        if (!copperHalf && !viaHalf) { run.NotRun(ReviewPads.NoPlatedDrill); return; }

        long width = run.Length("band-width");
        long gap = run.Length("max-gap");
        var band = board.Band(width);
        var mid = board.MidLine(width);
        if (band.Count == 0 || mid.Count == 0) return;
        string asks = ctx.Length(gap);

        // ── no ground copper along the edge, per OUTER copper layer ──────────────────────────────────
        if (copperHalf)
        {
            var outer = ctx.Layers.Copper.Count == 1 ? new[] { ctx.Layers.Copper[0] } : new[] { ctx.Layers.Copper[0], ctx.Layers.Copper[^1] };
            foreach (var copper in outer)
            {
                ctx.Token.ThrowIfCancellationRequested();
                // The partition's ground pieces on this layer — what on_ground(layer) selects, read off the
                // pieces' own nets: asking on_ground of the whole layer re-derives each piece's net by
                // touch-testing it against every piece its box meets, and a pour's box is the board.
                var ground = new Paths64();
                foreach (var piece in ctx.Partition.Pieces)
                    if (groundNets.Contains(piece.Net) && copper.DrawingLayers.Contains(piece.Layer)) ground.AddRange(piece.Paths);
                var key0 = copper.DrawingLayers[0];
                foreach (var stretch in Uncovered(mid, ground))
                {
                    long length = (long)Math.Round(LengthOf(stretch));
                    if (length < gap) continue;
                    string measured = ctx.Length(length);
                    run.Add(key0, $"{measured} of board edge on {ctx.Layers.NameOf(key0)} with no ground copper",
                            measured, Marker(stretch, width, band));
                }
            }
        }
        else ctx.Diagnostics.Add("edge-stitching: no copper layer is bound to the stackup, so only the stitching vias were checked.");

        // ── gaps between ground stitching vias ─────────────────────────────────────────────────────
        if (!viaHalf)
        {
            ctx.Diagnostics.Add($"edge-stitching checked the ground copper along the edge only: {ReviewPads.NoPlatedDrill}");
            return;
        }
        _ = ctx.Pads;   // marks a through-hole pin's hole, which stitches nothing
        double half = gap / 2.0;
        var discs = new Paths64();
        var centres = new List<(Point64 At, double R)>();
        foreach (var via in ctx.Vias)
        {
            if (!via.Ground || via.PinHole || board.DistanceToEdge(via.Region, width) >= width) continue;
            var (cx, cy) = via.Centre;
            var at = new Point64(cx, cy);
            double depth = board.DistanceToEdge([[at]], 2 * width);
            double off = depth - width / 2.0;
            double r = Math.Sqrt(half * half + off * off);
            discs.Add(Clipper.Ellipse(at, r, r, 64));
            centres.Add((at, r));
        }

        foreach (var stretch in Uncovered(mid, discs))
        {
            ctx.Token.ThrowIfCancellationRequested();
            double length = LengthOf(stretch);
            // An end a via's disc bounds is max-gap / 2 short of that via; an end on no disc (a loop with no
            // via on it at all) is not.
            foreach (var end in (ReadOnlySpan<Point64>)[stretch[0], stretch[^1]])
                if (stretch[0] != stretch[^1] && centres.Any(c => Distance(end, c.At) <= c.R * 1.02 + 1)) length += half;
            long reported = (long)Math.Round(length);
            if (reported < gap) continue;
            string measured = ctx.Length(reported);
            run.Add(null, $"{measured} of board edge with no ground stitching via; the review asks for one every {asks}",
                    measured, Marker(stretch, width, band));
        }
    }

    /// <summary>The stretches of <paramref name="loops"/> outside <paramref name="cover"/>, each one open
    /// polyline — a stretch through a loop's start point is joined back into one.</summary>
    private static List<Path64> Uncovered(Paths64 loops, Paths64 cover)
    {
        var open = new Paths64(loops.Count);
        foreach (var loop in loops)
        {
            if (loop.Count < 2) continue;
            var p = new Path64(loop) { loop[0] };
            open.Add(p);
        }
        if (cover.Count == 0) return [.. open];

        var clipper = new Clipper64();
        clipper.AddOpenSubject(open);
        clipper.AddClip(cover);
        var closed = new Paths64();
        var result = new Paths64();
        clipper.Execute(ClipType.Difference, LayoutClipper.Rule, closed, result);

        var stretches = result.Where(p => p.Count >= 2).Select(p => new Path64(p)).ToList();
        for (bool joined = true; joined;)
        {
            joined = false;
            for (int i = 0; i < stretches.Count && !joined; i++)
                for (int j = 0; j < stretches.Count && !joined; j++)
                {
                    if (i == j || stretches[i][0] == stretches[i][^1]) continue;
                    var a = stretches[i];
                    var b = stretches[j];
                    if (a[^1] == b[0]) a.AddRange(b.Skip(1));
                    else if (a[^1] == b[^1]) a.AddRange(Enumerable.Reverse(b).Skip(1));
                    else continue;
                    stretches.RemoveAt(j);
                    joined = true;
                }
        }
        return stretches;
    }

    private static double LengthOf(Path64 path)
    {
        double s = 0;
        for (int i = 1; i < path.Count; i++) s += Distance(path[i - 1], path[i]);
        return s;
    }

    private static double Distance(Point64 a, Point64 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The band along a stretch: the stretch widened to the band's width, inside the band.</summary>
    private static Paths64 Marker(Path64 stretch, long width, Paths64 band)
    {
        var strip = Clipper.InflatePaths([stretch], width, JoinType.Round, EndType.Butt);
        var marker = Clipper.BooleanOp(ClipType.Intersection, strip, band, LayoutClipper.Rule);
        return marker.Count > 0 ? marker : strip;
    }
}
