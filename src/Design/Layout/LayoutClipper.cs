// The single Clipper2 conversion point (docs/design/layout-view.md §6.1): booleans, offsets, DRC
// (L5b), the mesher (L6), and export (L4) all call ToClipperPaths/FromClipperTree, so the flattening
// tolerance is never chosen twice with two different answers. Built on LayoutFlattener — this file
// must NOT re-implement curve flattening.
//
// Our DBU integers go straight in: Clipper2's Path64/Point64 are long-based, exactly §1.1's storage
// type. No scaling to a working integer grid, no float conversion, no precision loss anywhere in this
// pipeline — the tempting "scale to a working integer grid" step other clipping libraries need is
// simply absent here. Coordinate magnitudes (<= ~10^9) sit far inside Clipper2's safe range.
//
// Fill rule: FillRule.NonZero everywhere, stated once here and never varied per call site — it is
// what makes self-intersection repair (LayoutBooleans.Repair) produce the outer region rather than a
// checkerboard.

using Clipper2Lib;

namespace CircuitRF.Design.Layout;

public static class LayoutClipper
{
    public const FillRule Rule = FillRule.NonZero;

    /// <summary>Converts one shape's geometry to Clipper2's integer path form, at
    /// <paramref name="tolDbu"/> flattening tolerance. <c>PathShape</c> gets its geometry OUTLINE
    /// here, via <c>InflatePaths</c> on the flattened centerline at <c>Width/2</c> with the join/cap
    /// matching its <c>End</c> style — this is NOT the display outline (R-L1e-1): the renderer's
    /// <c>LayoutRenderer.BuildPathOutline</c> keeps using the Skia stroker + Simplify so a curved
    /// trace still renders with adaptive, zoom-correct curves. Two outlines, two purposes; do not
    /// unify them.</summary>
    /// <param name="arcTolDbu">How finely a round join or end cap is flattened, in DBU. <b>Zero — the
    /// default, and what every caller but the raster-fill coalescer passes — leaves Clipper2's own
    /// default in force</b>, so this parameter changes no existing geometry anywhere. It exists
    /// because that default resolves to roughly 1 DBU on a 12,700 DBU offset, i.e. ~250 segments per
    /// cap circle: correct, and unaffordable when 29,000 painted scanlines are being unioned at once
    /// (<see cref="LayoutRasterFillCoalesce"/>, R-rf3-5, which states the tolerance it passes).</param>
    public static Paths64 ToClipperPaths(LayoutShape shape, long tolDbu, double arcTolDbu = 0)
    {
        if (shape is PathShape path)
            return PathOutlinePaths(path, tolDbu, arcTolDbu);

        // ORIENTATION IS NORMALISED HERE, AND ONLY HERE (brief-impedance-7): the outer ring positive,
        // every hole negative. A shape's rings come out of the flattener in whatever order they were
        // stored, and a RectShape's winding flips with swapped corners — so under NonZero two
        // overlapping shapes wound opposite ways summed to 0 (their overlap was not copper) and a hole
        // wound like its outer summed to 2 (it was filled). Every consumer — DRC, the booleans, the
        // extractors, the impedance tools — goes through this conversion, so none of them orients.
        var rings = LayoutFlattener.Flatten(shape, tolDbu);
        var paths = new Paths64(rings.Count);
        for (int i = 0; i < rings.Count; i++)
        {
            var ring = RingToPath64(rings[i]);
            if (Clipper.IsPositive(ring) != (i == 0)) ring.Reverse();
            paths.Add(ring);
        }
        return paths;
    }

    /// <summary>Wraps rings a caller already flattened itself (e.g. <c>LayoutTextFlatten</c>'s glyph
    /// contours, each flattened individually via <see cref="LayoutFlattener.Flatten"/> since they are
    /// not one shape's own rings) into Clipper2 <see cref="Paths64"/> — for callers that need the
    /// DBU-to-Clipper2 conversion without also re-flattening through <see cref="ToClipperPaths"/>'s
    /// single-shape path. <b>Orientation is kept as given, unlike <see cref="ToClipperPaths"/>:</b>
    /// these rings are not one shape's outer-then-holes, and a glyph's contour winding IS its hole
    /// information (the counter of an 'O' is wound against its outline), so there is no rule to
    /// normalise them to. A single shape's ring belongs in <see cref="ToClipperPaths"/>.</summary>
    public static Paths64 RingsToClipperPaths(IEnumerable<long[]> rings)
    {
        var paths = new Paths64();
        foreach (var ring in rings) paths.Add(RingToPath64(ring));
        return paths;
    }

    /// <summary>Rebuilds shapes from a Clipper2 boolean/offset result, preserving the hole structure
    /// (§3.1a) the tree already encodes: every non-hole node becomes one <see cref="PolygonShape"/>
    /// whose immediate hole children become its <c>Holes</c>; islands nested inside a hole recurse as
    /// further top-level shapes. Deterministic — a fixed walk order over Clipper2's own (deterministic)
    /// tree, no dictionaries, no parallelism (R-L1c-1's determinism discipline, extended here).</summary>
    public static IReadOnlyList<LayoutShape> FromClipperTree(PolyTree64 tree, LayerKey layer, string? net)
    {
        var results = new List<LayoutShape>();
        CollectSolids(tree, results, layer, net);
        return results;
    }

    private static void CollectSolids(PolyPath64 node, List<LayoutShape> results, LayerKey layer, string? net)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var solid = node[i];   // IsHole == false at this recursion level
            List<long[]>? holes = null;
            for (int j = 0; j < solid.Count; j++)
            {
                var hole = solid[j];   // IsHole == true
                (holes ??= []).Add(Path64ToRing(hole.Polygon));
                CollectSolids(hole, results, layer, net);   // islands nested inside this hole
            }
            results.Add(new PolygonShape { Layer = layer, Net = net, Xy = Path64ToRing(solid.Polygon), Holes = holes });
        }
    }

    // ── DBU <-> Clipper2 ─────────────────────────────────────────────────────

    private static Path64 RingToPath64(long[] ring)
    {
        int n = ring.Length / 2;
        var path = new Path64(n);
        for (int i = 0; i < n; i++)
            path.Add(new Point64(ring[2 * i], ring[2 * i + 1]));
        return path;
    }

    private static long[] Path64ToRing(Path64? path)
    {
        if (path is null) return [];
        var xy = new long[path.Count * 2];
        for (int i = 0; i < path.Count; i++)
        {
            xy[2 * i] = path[i].X;
            xy[2 * i + 1] = path[i].Y;
        }
        return xy;
    }

    // ── PathShape -> geometry outline via InflatePaths ────────────────────────

    private static Paths64 PathOutlinePaths(PathShape path, long tolDbu, double arcTolDbu = 0)
    {
        var centerline = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tolDbu);
        if (centerline.Length < 4) return [];   // fewer than 2 points — no outline to build

        var subject = new Paths64 { RingToPath64(centerline) };
        double delta = path.Width / 2.0;
        var endType = path.End switch
        {
            PathEndStyle.Round  => EndType.Round,
            PathEndStyle.Square => EndType.Square,
            PathEndStyle.Extended => EndType.Square,   // same offset amount as Square — see LayoutRenderer.ExtendedCenterline
            _                    => EndType.Butt,       // Flush
        };
        return Clipper.InflatePaths(subject, delta, JoinType.Round, endType, 2.0, arcTolDbu);
    }

    // ── R-L1e-0 / §3.1a R10b: enforce hole validity on any non-Clipper2 construction path ─────────

    /// <summary>A hole must lie inside its outer ring and intersect neither that ring nor another
    /// hole. Clipper2's own <see cref="PolyTree64"/> output (every boolean/offset/repair in
    /// <c>LayoutBooleans</c>) already satisfies this by construction. This is the enforcement point
    /// for any OTHER way holes can enter the model — today that is a hand-edited <c>.clay</c> file
    /// (<c>LayoutPersistence</c> calls this for every loaded shape); a future paste (L1f) or import
    /// (L4) should call it too. Cheap no-op when the shape is already valid — only a genuinely invalid
    /// hole triggers the Clipper2 <c>Union</c> re-derivation, which may reorder vertices/holes (a
    /// normally-constructed shape never observes that, since it is already valid).</summary>
    public static IReadOnlyList<LayoutShape> EnsureValidHoles(LayoutShape shape)
    {
        var holes = shape switch
        {
            PolygonShape p => p.Holes,
            CurveShape c    => c.Holes,
            _               => null,
        };
        if (holes is not { Count: > 0 }) return [shape];

        long tol = LayoutFlattener.ResolveTolDbu(shape, null);
        var rings = LayoutFlattener.Flatten(shape, tol);
        if (HolesAreValid(rings)) return [shape];

        var paths = ToClipperPaths(shape, tol);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, Rule);
        return FromClipperTree(tree, shape.Layer, shape.Net);
    }

    /// <summary>
    /// The three conditions: every hole vertex lies inside-or-on the outer ring, no hole crosses that
    /// ring, and no two holes cross each other.
    ///
    /// <para><b>Bounding boxes are a PREFILTER, never a decision.</b> Every reject below is a case in
    /// which no segment pair can possibly meet, so this returns exactly what the unfiltered triple
    /// loop returns — <c>LayoutClipperHoleValidityTests</c> holds that against a brute-force copy of
    /// the original over a randomized corpus. What they buy, on the shape that motivated this (a
    /// Gerber-imported copper pour: 228 holes, 21,772 hole vertices, a 1,751-vertex outer ring):</para>
    /// <list type="bullet">
    /// <item>the hole-vs-hole pairs are ~26k ring-box tests instead of ~233M segment-pair tests,
    /// because the holes of one pour are disjoint by construction and essentially every pair dies on
    /// its box;</item>
    /// <item>the hole-vs-outer crossing test is ~1,751 segment-box tests plus a handful of full
    /// scans, instead of 38M segment-pair tests — see <see cref="RingsIntersect"/> for why the LONGER
    /// ring has to be the one on the outside of that loop.</item>
    /// </list>
    /// <para>The remaining term is the point-in-ring test, which no box helps: a ray cast has to see
    /// every segment the ray can cross, so cutting it needs an index over the outer ring rather than
    /// a rejection test.</para>
    /// </summary>
    internal static bool HolesAreValid(IReadOnlyList<long[]> rings)
    {
        var outer = rings[0];

        // One pass per ring, computed once. Every prefilter below reads these rather than re-deriving
        // them per pair, which is what makes the hole-vs-hole reject O(1) per pair.
        var info = new RingInfo[rings.Count];
        for (int i = 0; i < rings.Count; i++) info[i] = RingInfo.Of(rings[i]);

        // The y-band index, built lazily per ring: the two terms below that a bounding box cannot help
        // are exactly the two a band CAN. Null for a ring too small to be worth indexing, which every
        // query below falls back over. See RingBands.
        var bands = new RingBands?[rings.Count];
        RingBands? BandsOf(int i) => bands[i] ??= RingBands.Build(rings[i]);

        for (int i = 1; i < rings.Count; i++)
        {
            var hole = rings[i];
            foreach (var v in EnumeratePoints(hole))
                if (!PointInOrOnRing(outer, BandsOf(0), v.X, v.Y)) return false;
            if (RingsIntersect(hole, outer, info[i], info[0], BandsOf(i), BandsOf(0))) return false;

            for (int j = i + 1; j < rings.Count; j++)
                if (RingsIntersect(hole, rings[j], info[i], info[j], BandsOf(i), BandsOf(j))) return false;
        }
        return true;
    }

    /// <summary>
    /// What one pass over a ring tells the prefilters: its axis-aligned extent, its segment count,
    /// and whether it carries a ZERO-LENGTH segment.
    ///
    /// <para><c>MaxX &lt; MinX</c> marks a ring with no vertices at all, which <see cref="Overlap"/>
    /// then reports as overlapping nothing — correct, since a ring with no vertices has no segments
    /// to intersect.</para>
    /// </summary>
    private readonly record struct RingInfo(
        long MinX, long MinY, long MaxX, long MaxY, int Segments, bool HasZeroLengthSegment)
    {
        public static RingInfo Of(long[] xy)
        {
            int n = xy.Length / 2;
            if (n == 0) return new RingInfo(0, 0, -1, -1, 0, false);

            long minX = xy[0], maxX = xy[0], minY = xy[1], maxY = xy[1];
            for (int i = 2; i < xy.Length; i += 2)
            {
                long x = xy[i], y = xy[i + 1];
                if (x < minX) minX = x; else if (x > maxX) maxX = x;
                if (y < minY) minY = y; else if (y > maxY) maxY = y;
            }

            // A one-vertex ring's single segment runs from the vertex to itself, so it counts too —
            // the (i + 1) % n wrap in RingSegment is what makes that so.
            bool zeroLength = n == 1;
            for (int i = 0; i < n && !zeroLength; i++)
            {
                int j = (i + 1) % n;
                zeroLength = xy[2 * i] == xy[2 * j] && xy[2 * i + 1] == xy[2 * j + 1];
            }

            return new RingInfo(minX, minY, maxX, maxY, n, zeroLength);
        }
    }

    /// <summary>Touching boxes count as overlapping — a rejection has to be certain, and two rings
    /// whose boxes share an edge can share a point.</summary>
    private static bool Overlap(in RingInfo a, in RingInfo b)
        => a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY;

    private static IEnumerable<(long X, long Y)> EnumeratePoints(long[] xy)
    {
        for (int i = 0; i < xy.Length; i += 2)
            yield return (xy[i], xy[i + 1]);
    }

    /// <summary>
    /// Ray-cast containment. <b>The one term no BOX helps</b> — a ray has to see every segment it can
    /// cross, so there is nothing a bounding box can reject — which is why, after the two box
    /// prefilters in <see cref="HolesAreValid"/>, this was one of the two terms left standing.
    ///
    /// <para><b>Gating <see cref="OnSegment"/> behind the segment's own box was tried and MEASURED NO
    /// BETTER</b> — 0.34 s against a 0.30-0.34 s spread for this, i.e. inside the noise. The
    /// point-lies-on-this-segment test is three multiplies on values already in registers, so four
    /// integer compares and a branch per segment buy back about what they cost; the simpler code
    /// wins on a tie.</para>
    ///
    /// <para><b>What did work is the <see cref="RingBands"/> index</b> — segments bucketed by y, so a
    /// cast at height <c>py</c> visits one band instead of all N. That was recorded here as "a
    /// different piece of work, not done", and it was done on 2026-09-12 when a workspace holding
    /// three imported boards took ~9 s to open. See <c>src/Design/RESOLVED.md</c>.</para>
    /// </summary>
    private static bool PointInOrOnRing(long[] ring, RingBands? bands, long px, long py)
    {
        int n = ring.Length / 2;
        if (n < 3) return false;

        if (bands is null)
        {
            bool all = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
                if (Cast(ring, i, j, px, py, ref all)) return true;
            return all;
        }

        // A segment whose y-extent does not straddle py can neither contain the point nor be crossed
        // by the ray, so it changes neither answer — which is what makes visiting one band the same
        // computation as visiting all N. The band is indexed by SEGMENT j (the pair j -> j+1), and the
        // pair is then read in the same (i, j) order the unindexed loop reads it in, so the crossing
        // arithmetic is bit-for-bit what it always was.
        bool inside = false;
        foreach (int j in bands.SegmentsAt(py))
            if (Cast(ring, (j + 1) % n, j, px, py, ref inside)) return true;
        return inside;
    }

    /// <summary>One segment's contribution to <see cref="PointInOrOnRing"/> — true means the point is
    /// ON it and the answer is settled; otherwise <paramref name="inside"/> flips if the ray crosses.
    /// Extracted so the indexed and unindexed walks cannot drift into two arithmetics.</summary>
    private static bool Cast(long[] ring, int i, int j, long px, long py, ref bool inside)
    {
        double xi = ring[2 * i], yi = ring[2 * i + 1];
        double xj = ring[2 * j], yj = ring[2 * j + 1];
        if (OnSegment(px, py, xi, yi, xj, yj)) return true;
        bool crosses = (yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi;
        if (crosses) inside = !inside;
        return false;
    }

    private static bool OnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double cross = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        if (Math.Abs(cross) > 1e-6) return false;
        double dot = (px - ax) * (bx - ax) + (py - ay) * (by - ay);
        double lenSq = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
        return dot >= 0 && dot <= lenSq;
    }

    /// <summary>
    /// Whether any segment of <paramref name="a"/> meets any segment of <paramref name="b"/>, with
    /// the callers' precomputed boxes used to skip pairs that provably cannot meet.
    ///
    /// <para><b>The LONGER ring goes on the outside of the loop, and that is the whole point.</b> The
    /// per-segment reject can only throw work away when it is tested against the OTHER ring's box, so
    /// the ring being rejected has to be the long one: a hole lies inside the outer ring's box, so
    /// rejecting the hole's few segments against the outer's box discards nothing, while rejecting
    /// the outer's thousands against the hole's small box discards nearly all of them. Both orders
    /// give the same answer — <see cref="SegmentsIntersect"/> is symmetric in its two segments — so
    /// this picks the one that is fast.</para>
    /// </summary>
    private static bool RingsIntersect(long[] a, long[] b, in RingInfo ia, in RingInfo ib,
                                      RingBands? ba, RingBands? bb)
    {
        // A ZERO-LENGTH segment reports as meeting ANYTHING, wherever the two rings are — so this
        // case has to be answered before the boxes get a say, and it is the one place a box reject
        // would otherwise change the result. OnSegment's window is `0 <= dot <= lenSq`, and a
        // segment from a point to itself has lenSq = 0 and dot = 0 for every point, so it passes for
        // all of them; SegmentsIntersect's collinear branch then returns true. That makes the
        // unfiltered algorithm call a ring with a repeated consecutive vertex invalid against
        // everything, and such a shape is re-derived through Clipper on load today. Preserved, not
        // corrected: whether that repair should happen is a question about R10b, not something a
        // performance edit gets to settle silently. Found by the differential gate, not by reading.
        if ((ia.HasZeroLengthSegment && ib.Segments > 0) ||
            (ib.HasZeroLengthSegment && ia.Segments > 0)) return true;

        if (!Overlap(ia, ib)) return false;
        return a.Length >= b.Length ? ScanAgainst(a, b, ib, bb) : ScanAgainst(b, a, ia, ba);
    }

    private static bool ScanAgainst(long[] scanned, long[] against, in RingInfo againstBox,
                                    RingBands? againstBands)
    {
        int na = scanned.Length / 2, nb = against.Length / 2;
        for (int i = 0; i < na; i++)
        {
            var (ax0, ay0, ax1, ay1) = RingSegment(scanned, i, na);

            // A segment whose own extent misses the other ring's box cannot meet any segment of it —
            // every one of them is inside that box. Kept inclusive (< / >, never <= / >=) so a
            // segment merely touching the box's edge still goes through the real test.
            if (Math.Max(ax0, ax1) < againstBox.MinX || Math.Min(ax0, ax1) > againstBox.MaxX ||
                Math.Max(ay0, ay1) < againstBox.MinY || Math.Min(ay0, ay1) > againstBox.MaxY)
                continue;

            // THE RING'S box is what the reject above can use, and on the pair this exists for — a
            // hole against the outer ring that contains it — that box rejects nothing, because the
            // hole is inside it. The band index is the same reject at SEGMENT granularity: two
            // segments that meet share a point, so their y-extents overlap, so a segment outside this
            // one's band of `against` cannot be met by it.
            if (againstBands is not null)
            {
                foreach (int j in againstBands.SegmentsOverlapping(Math.Min(ay0, ay1), Math.Max(ay0, ay1)))
                {
                    var (bx0, by0, bx1, by1) = RingSegment(against, j, nb);
                    if (SegmentsIntersect(ax0, ay0, ax1, ay1, bx0, by0, bx1, by1)) return true;
                }
                continue;
            }

            for (int j = 0; j < nb; j++)
            {
                var (bx0, by0, bx1, by1) = RingSegment(against, j, nb);
                if (SegmentsIntersect(ax0, ay0, ax1, ay1, bx0, by0, bx1, by1)) return true;
            }
        }
        return false;
    }

    private static (double, double, double, double) RingSegment(long[] xy, int i, int n)
    {
        int j = (i + 1) % n;
        return (xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1]);
    }

    private static bool SegmentsIntersect(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double d1 = Cross(bx1 - bx0, by1 - by0, ax0 - bx0, ay0 - by0);
        double d2 = Cross(bx1 - bx0, by1 - by0, ax1 - bx0, ay1 - by0);
        double d3 = Cross(ax1 - ax0, ay1 - ay0, bx0 - ax0, by0 - ay0);
        double d4 = Cross(ax1 - ax0, ay1 - ay0, bx1 - ax0, by1 - ay0);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        // Collinear/touching cases — conservative (treat touching as intersecting, per R10b).
        if (d1 == 0 && OnSegment(ax0, ay0, bx0, by0, bx1, by1)) return true;
        if (d2 == 0 && OnSegment(ax1, ay1, bx0, by0, bx1, by1)) return true;
        if (d3 == 0 && OnSegment(bx0, by0, ax0, ay0, ax1, ay1)) return true;
        if (d4 == 0 && OnSegment(bx1, by1, ax0, ay0, ax1, ay1)) return true;
        return false;
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    /// <summary>
    /// The ring size at which <see cref="RingBands"/> starts being built. Below it the scan an index
    /// would replace is already short enough that building one costs more than it saves, so a smaller
    /// ring is walked whole.
    ///
    /// <para>Internal because a differential test over rings BELOW it proves nothing about the index —
    /// it would not have been built. <c>LayoutClipperHoleValidityTests</c> reads this rather than
    /// hard-coding a number that could drift out from under it and quietly make that corpus
    /// vacuous.</para>
    /// </summary>
    internal const int RingIndexMinimumSegments = 48;

    /// <summary>
    /// A ring's segments bucketed by y, so a query that can only interact with one horizontal band of
    /// the ring visits that band instead of all N segments.
    ///
    /// <para><b>What it buys, and why a bounding box could not.</b> <see cref="HolesAreValid"/>'s box
    /// prefilters kill the pairs that provably cannot meet, and on an ordinary pour that is nearly all
    /// of them. Two terms are structurally beyond them: the point-in-ring cast, because a ray has to
    /// see every segment it could cross; and a hole tested against the outer ring that CONTAINS it,
    /// whose box therefore rejects nothing. Both reduce to the same fact — two things that meet share
    /// a point, so their y-extents overlap — which is a band lookup.</para>
    ///
    /// <para>Measured on the pour that motivated this, a Gerber import with 28 holes carrying 59,996
    /// vertices against a 271-vertex outer ring (2026-09-12): 379 ms for that one shape, 453 ms for
    /// the board's 445 holed shapes, 575 ms to read the whole 10.6 MB file. See
    /// <c>src/Design/RESOLVED.md</c> for what those became.</para>
    ///
    /// <para><b>It is a candidate filter, never a decision.</b> Every segment a query returns still
    /// goes through the same unchanged arithmetic; the index only declines to hand over segments that
    /// could not have changed the answer. That is what keeps
    /// <c>LayoutClipperHoleValidityTests</c>' differential gate against the unfiltered algorithm
    /// meaningful — it is the only thing that says so.</para>
    /// </summary>
    private sealed class RingBands
    {
        private const int MaxBands = 4096;

        /// <summary>Roughly how many segments a band should hold. Small enough that a query is short,
        /// large enough that a segment spanning several bands is not copied into many of them.</summary>
        private const int TargetPerBand = 4;

        private readonly int[] _starts;   // CSR: _bands + 1 offsets into _items
        private readonly int[] _items;    // segment indices, grouped by band
        private readonly long _minY, _span;
        private readonly int _bands;

        // Dedupe for the ranged query: a segment tall enough to sit in several bands must still be
        // handed over once. Stamps rather than a cleared bitmap, so a query costs only what it returns.
        private readonly int[] _stamp;
        private int[] _gathered;
        private int _generation;

        private RingBands(int[] starts, int[] items, long minY, long span, int bands, int segments)
        {
            _starts = starts; _items = items; _minY = minY; _span = span; _bands = bands;
            _stamp = new int[segments];
            _gathered = new int[Math.Min(segments, 64)];
        }

        /// <summary>Null when the ring is too small to index, degenerate, or so dominated by tall
        /// segments that the index would hold more copies than it saves lookups.</summary>
        public static RingBands? Build(long[] xy)
        {
            int n = xy.Length / 2;
            if (n < RingIndexMinimumSegments) return null;

            long minY = xy[1], maxY = xy[1];
            for (int i = 3; i < xy.Length; i += 2)
            {
                long y = xy[i];
                if (y < minY) minY = y; else if (y > maxY) maxY = y;
            }

            long span = maxY - minY;
            if (span <= 0) return null;   // a ring flat in y has one band, which is no index at all

            int bands = Math.Clamp(n / TargetPerBand, 1, MaxBands);
            // (y - minY) * bands must not overflow. Coordinates are DBU and nothing real comes close,
            // but a hand-written file is not obliged to be real.
            while (bands > 1 && span > long.MaxValue / bands) bands /= 2;
            if (bands < 2) return null;

            // Two passes, CSR. A segment goes in every band its y-extent touches — and if that adds up
            // to far more entries than segments, the ring is mostly tall segments and the index would
            // be a copy of itself per band. Coarsen, then give up rather than pay for it.
            int[] counts;
            long total;
            while (true)
            {
                counts = new int[bands + 1];
                total = 0;
                for (int i = 0; i < n; i++)
                {
                    int j = i + 1 == n ? 0 : i + 1;
                    int lo = BandOf(xy[2 * i + 1], minY, span, bands);
                    int hi = BandOf(xy[2 * j + 1], minY, span, bands);
                    if (lo > hi) (lo, hi) = (hi, lo);
                    for (int b = lo; b <= hi; b++) counts[b]++;
                    total += hi - lo + 1;
                }
                if (total <= 8L * n || bands <= 4) break;
                bands /= 4;
            }
            if (total > 8L * n) return null;

            var starts = new int[bands + 1];
            int running = 0;
            for (int b = 0; b < bands; b++) { starts[b] = running; running += counts[b]; }
            starts[bands] = running;

            var fill = (int[])starts.Clone();
            var items = new int[running];
            for (int i = 0; i < n; i++)
            {
                int j = i + 1 == n ? 0 : i + 1;
                int lo = BandOf(xy[2 * i + 1], minY, span, bands);
                int hi = BandOf(xy[2 * j + 1], minY, span, bands);
                if (lo > hi) (lo, hi) = (hi, lo);
                for (int b = lo; b <= hi; b++) items[fill[b]++] = i;
            }

            return new RingBands(starts, items, minY, span, bands, n);
        }

        private static int BandOf(long y, long minY, long span, int bands)
        {
            if (y <= minY) return 0;
            if (y >= minY + span) return bands - 1;
            return (int)((y - minY) * bands / (span + 1));
        }

        /// <summary>The segments whose y-extent can contain <paramref name="y"/>. Segment <c>i</c> is
        /// the pair <c>i -> i+1</c> (wrapping), which is <see cref="RingSegment"/>'s numbering.</summary>
        public ReadOnlySpan<int> SegmentsAt(long y)
        {
            if (y < _minY || y > _minY + _span) return default;
            int b = BandOf(y, _minY, _span, _bands);
            return _items.AsSpan(_starts[b], _starts[b + 1] - _starts[b]);
        }

        /// <summary>The segments whose y-extent can overlap <paramref name="lo"/>..<paramref name="hi"/>,
        /// each returned once. The single-band case — most of them — hands back the band itself with
        /// nothing copied.</summary>
        public ReadOnlySpan<int> SegmentsOverlapping(double lo, double hi)
        {
            if (hi < _minY || lo > _minY + _span) return default;

            // RingSegment hands its caller doubles; these came from the same long[] this index was
            // built from, so the conversion is exact — and rounded OUTWARD regardless, because a band
            // range that is one too wide costs a few extra segment tests while one that is too narrow
            // would drop an intersection.
            int bLo = BandOf((long)Math.Floor(lo), _minY, _span, _bands);
            int bHi = BandOf((long)Math.Ceiling(hi), _minY, _span, _bands);
            if (bLo == bHi) return _items.AsSpan(_starts[bLo], _starts[bLo + 1] - _starts[bLo]);

            _generation++;
            int count = 0;
            for (int b = bLo; b <= bHi; b++)
                for (int k = _starts[b]; k < _starts[b + 1]; k++)
                {
                    int seg = _items[k];
                    if (_stamp[seg] == _generation) continue;
                    _stamp[seg] = _generation;
                    if (count == _gathered.Length) Array.Resize(ref _gathered, _gathered.Length * 2);
                    _gathered[count++] = seg;
                }
            return _gathered.AsSpan(0, count);
        }
    }
}
