// Evaluating a DrcLayerExpr to a region (docs/design/layout-view.md §9A.5).
//
// Every operation funnels through Clipper2 over the same integer DBU coordinates the rest of the
// layout uses — §6.1's single conversion point, unchanged. This file adds no second geometry
// notion; it composes the one that already exists.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Drc;

/// <summary>
/// Evaluates <see cref="DrcLayerExpr"/> against the per-layer regions a run has already built.
///
/// <para>One instance per DRC run. Sub-expression results are memoized for the lifetime of that
/// run: a deck routinely measures several rules against the same derived layer (metal minus a
/// keep-out region can appear in a dozen rules), and re-deriving it per rule is the difference between
/// a check that runs once and one that runs once per rule. The cache is keyed by the expression
/// itself — <see cref="DrcLayerExpr"/> is a record tree, so two structurally identical expressions
/// are equal and share an entry with no hand-written key to get wrong.</para>
///
/// <para><b>The net partition is optional</b> (brief-review-1 R-rev1-4). A run builds connectivity
/// only when some rule asks for it, and hands the one it built — <see cref="ConnectivityCache"/>'s —
/// to the evaluator. Without one, a net-aware selection selects nothing and says so in
/// <see cref="Diagnostics"/>, once.</para>
/// </summary>
internal sealed class DrcRegionEval(
    IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
    IReadOnlySet<LayerKey>?                definedLayers = null,
    ConnectivityPartition?                 partition = null)
{
    /// <summary>
    /// How far apart two regions may be and still count as touching, for the topological
    /// selections.
    ///
    /// <para><b>This is not a tolerance in the usual sense and must not be raised.</b> Clipper2
    /// reports the intersection of two regions that share only an EDGE as empty — zero area is
    /// still zero — so a strict area test would report two abutting polygons as not interacting,
    /// which is the opposite of what every deck means by the word. Testing against a one-DBU
    /// dilation makes edge contact register. At the default resolution one DBU is a nanometre,
    /// far below any drawn feature, so nothing that is genuinely apart is caught by it.</para>
    /// </summary>
    private const double TouchDilationDbu = 1.0;

    /// <summary>Miter limit for <see cref="DrcLayerExpr.Sized"/>. Matches the width check's own
    /// erosion, so a sized derived layer and the measurement of it agree about corners.</summary>
    private const double MiterLimit = 2.0;

    private readonly Dictionary<DrcLayerExpr, Paths64> _cache = new();

    /// <summary>
    /// Layers named by an expression that the TECHNOLOGY does not define — not merely layers this
    /// design has no geometry on. Accumulated across every evaluation and reported once per run.
    /// </summary>
    public HashSet<LayerKey> MissingLayers { get; } = [];

    /// <summary>How many sub-expressions were evaluated rather than served from the cache.
    /// Test-only instrumentation — there is no other way to observe that memoization is working.</summary>
    internal int EvaluatedNodeCount { get; private set; }

    /// <summary>
    /// Clipper operations run by <see cref="DrcLayerExpr.Near"/> and a counted
    /// <c>interacting</c> to test one polygon of A against one of B — the pairs that survived the
    /// bounding-box rejection. The cost gate for those two (R-rev1-5): a board with thousands of
    /// drills and pads must not clip every pair.
    /// </summary>
    internal long NearClipCount { get; private set; }

    /// <summary>
    /// What an evaluation had to say that is not a missing layer — a net selection with no
    /// partition, an <c>on_ground</c> with no ground reference. Each sentence appears once however
    /// many nodes said it; the run reports them.
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    private readonly List<string> _diagnostics = [];

    private void Report(string sentence)
    {
        if (!_diagnostics.Contains(sentence)) _diagnostics.Add(sentence);
    }

    public Paths64 Evaluate(DrcLayerExpr expr)
    {
        if (_cache.TryGetValue(expr, out var hit)) return hit;

        EvaluatedNodeCount++;
        var result = Compute(expr);
        _cache[expr] = result;
        return result;
    }

    private Paths64 Compute(DrcLayerExpr expr)
    {
        switch (expr)
        {
            case DrcLayerExpr.Layer l:
                if (layerRegions.TryGetValue(l.Key, out var region)) return region;

                // Either way the layer contributes NOTHING rather than failing the rule outright: a
                // deck written for a full process names layers a simpler technology omits, and
                // refusing those rules would hide the ones that ARE evaluable.
                //
                // But the two reasons are NOT the same fact and must not be reported as one. A layer
                // the technology defines that this design simply has no geometry on is entirely
                // normal and unremarkable — most layers of a real process are empty in any one cell.
                // Reporting those as "not defined in the technology" told users their technology was
                // broken when it was fine: measured on a real process, 31 layers were named that way
                // and every single one was defined. Only a genuinely UNDEFINED layer is recorded.
                if (definedLayers is null || !definedLayers.Contains(l.Key))
                    MissingLayers.Add(l.Key);

                return [];

            case DrcLayerExpr.And a:
                return Boolean(ClipType.Intersection, Evaluate(a.A), Evaluate(a.B));

            case DrcLayerExpr.Or o:
                return Boolean(ClipType.Union, Evaluate(o.A), Evaluate(o.B));

            case DrcLayerExpr.Not n:
                return Boolean(ClipType.Difference, Evaluate(n.A), Evaluate(n.B));

            case DrcLayerExpr.Xor x:
                return Boolean(ClipType.Xor, Evaluate(x.A), Evaluate(x.B));

            case DrcLayerExpr.Sized s:
            {
                var src = Evaluate(s.A);
                if (src.Count == 0 || s.ByDbu == 0) return src;
                var grown = Clipper.InflatePaths(src, s.ByDbu, JoinType.Miter, EndType.Polygon, MiterLimit);
                // A shrink can dissolve a region entirely; that is a legitimate empty result.
                return grown.Count == 0 ? [] : DrcRegions.Union(grown);
            }

            case DrcLayerExpr.Select sel:
                // Uncounted is today's selection, by today's code: R-rev1-3's "exactly today's".
                return sel.IsCounted
                    ? CountWithin(Evaluate(sel.A), Evaluate(sel.B), 0, sel.MinCount ?? 0, sel.MaxCount)
                    : Selection(Evaluate(sel.A), Evaluate(sel.B), sel.Op);

            case DrcLayerExpr.Near nr:
                return nr.MinCount is null && nr.MaxCount is null
                    ? CountWithin(Evaluate(nr.A), Evaluate(nr.B), nr.DistanceDbu, 1, null)
                    : CountWithin(Evaluate(nr.A), Evaluate(nr.B), nr.DistanceDbu, nr.MinCount ?? 0, nr.MaxCount);

            case DrcLayerExpr.SameNet sn:
                return SameNetOf(sn);

            case DrcLayerExpr.OnGround og:
                return OnGroundOf(og);

            case DrcLayerExpr.Holes h:
                return HolesOf(Evaluate(h.A));

            case DrcLayerExpr.Merged m:
                return DrcRegions.Union(Evaluate(m.A));

            case DrcLayerExpr.WithArea wa:
                return SelectByMeasure(Evaluate(wa.A), wa.MinDbu2, wa.MaxDbu2, AreaOfComponent);

            case DrcLayerExpr.WithPerimeter wp:
                return SelectByMeasure(Evaluate(wp.A), wp.MinDbu, wp.MaxDbu, PerimeterOfComponent);

            default:
                throw new NotSupportedException($"Unhandled expression node {expr.GetType().Name}.");
        }
    }

    /// <summary>
    /// Keeps each component of A for which the number of distinct components of B within
    /// <paramref name="distanceDbu"/> lies in [<paramref name="min"/>, <paramref name="max"/>].
    /// See <see cref="DrcLayerExpr.Near"/> for why the growth is ROUND.
    ///
    /// <para>A is grown by the distance and tested against each B component with the same one-DBU
    /// touch dilation <c>interacting</c> uses, so a distance of zero is "touches". B's bounding boxes
    /// reject a pair before any clip, and counting stops as soon as the answer is settled.</para>
    /// </summary>
    private Paths64 CountWithin(Paths64 a, Paths64 b, long distanceDbu, int min, int? max)
    {
        if (a.Count == 0) return [];

        // Settled: past the maximum it can only stay past; with no maximum, reaching the minimum is
        // enough.
        var components = DrcRegions.Components(a);
        var within = WithinEach(components, DrcRegions.Components(b), distanceDbu, max is { } m ? m + 1 : Math.Max(min, 1));

        var kept = new Paths64();
        for (int i = 0; i < components.Count; i++)
        {
            int count = within[i].Count;
            if (count >= min && (max is null || count <= max)) kept.AddRange(components[i]);
        }
        return kept;
    }

    /// <summary>
    /// <see cref="DrcLayerExpr.Near"/>'s own routine with the identities kept: for each polygon of
    /// <paramref name="a"/>, the indices into <paramref name="b"/> of the polygons within
    /// <paramref name="distanceDbu"/>, in <paramref name="b"/>'s order — at most
    /// <paramref name="stopAt"/> of them, because the scan for one A stops there. A reader that
    /// needs WHICH polygons are near (brief-review-3's shared ground via) asks this rather than
    /// measuring a second time.
    ///
    /// <para>A is grown by the distance with ROUND joins and tested against each B component with
    /// the same one-DBU touch dilation <c>interacting</c> uses, so a distance of zero is "touches".
    /// B's bounding boxes, sorted by left edge, reject a pair before any clip.</para>
    /// </summary>
    internal List<List<int>> WithinEach(IReadOnlyList<Paths64> a, IReadOnlyList<Paths64> b, long distanceDbu,
                                        int stopAt = int.MaxValue)
    {
        var bs = new List<(Bbox Bounds, Paths64 Touch, int Index)>(b.Count);
        for (int j = 0; j < b.Count; j++)
        {
            var touch = Clipper.InflatePaths(b[j], TouchDilationDbu, JoinType.Miter, EndType.Polygon, MiterLimit);
            bs.Add((DrcRegions.BoundsOf(touch), touch, j));
        }
        bs.Sort((x, y) => x.Bounds.MinX != y.Bounds.MinX ? x.Bounds.MinX.CompareTo(y.Bounds.MinX) : x.Index.CompareTo(y.Index));

        var result = new List<List<int>>(a.Count);
        foreach (var component in a)
        {
            var hits = new List<int>();
            result.Add(hits);
            if (stopAt <= 0) continue;

            var grown = distanceDbu > 0
                ? Clipper.InflatePaths(component, distanceDbu, JoinType.Round, EndType.Polygon)
                : component;
            var gb = DrcRegions.BoundsOf(grown);

            foreach (var (bounds, touch, index) in bs)
            {
                if (bounds.MinX > gb.MaxX) break;
                if (!bounds.Intersects(gb)) continue;

                NearClipCount++;
                if (IsEmpty(Clipper.BooleanOp(ClipType.Intersection, grown, touch, LayoutClipper.Rule))) continue;

                hits.Add(index);
                if (hits.Count >= stopAt) break;
            }
            hits.Sort();
        }
        return result;
    }

    // ── Net-aware selections (R-rev1-4) ──────────────────────────────────────────────────────

    private Paths64 SameNetOf(DrcLayerExpr.SameNet sn)
    {
        var a = Evaluate(sn.A);
        var b = Evaluate(sn.B);
        if (!HasPartition("same_net", sn)) return [];
        if (a.Count == 0 || b.Count == 0) return [];

        var netsOfB = new HashSet<int>();
        var bLayers = sn.B.ReferencedLayers();
        foreach (var c in DrcRegions.Components(b)) netsOfB.UnionWith(NetsOf(c, bLayers));

        var aLayers = sn.A.ReferencedLayers();
        var kept = new Paths64();
        foreach (var c in DrcRegions.Components(a))
            if (NetsOf(c, aLayers).Overlaps(netsOfB)) kept.AddRange(c);
        return kept;
    }

    private Paths64 OnGroundOf(DrcLayerExpr.OnGround og)
    {
        var a = Evaluate(og.A);
        if (!HasPartition("on_ground", og)) return [];

        var ground = GroundNets(out string? why);
        if (why is not null) Report(ground.Count == 0 ? $"{og} selected nothing: {why}" : $"{og} {why}");
        if (ground.Count == 0 || a.Count == 0) return [];

        var layers = og.A.ReferencedLayers();
        var kept = new Paths64();
        foreach (var c in DrcRegions.Components(a))
            if (NetsOf(c, layers).Overlaps(ground)) kept.AddRange(c);
        return kept;
    }

    private (IReadOnlySet<int> Nets, string? Said, int Plane)? _ground;

    /// <summary>
    /// The nets <c>on_ground</c> reads as ground, and the clause saying how — or, with no nets, why
    /// there are none.
    ///
    /// <para>An undrawn reference: the nets the partition's via walk carried down to it
    /// (<see cref="GroundReach.Nets"/>). A DRAWN reference names no ground net in the partition — it
    /// is ordinary copper there, for the reasons <see cref="GroundReach"/>'s remarks give — so here
    /// ground is the net of the LARGEST piece on the reference conductor's drawing layers: the
    /// plane (brief-review-3; the artwork reader's <c>GroundSource.ReferenceConductor</c> is the same
    /// rule). Largest, not every net with copper on it: a two-layer board that flags its routed
    /// bottom copper would otherwise read every bottom trace as ground. The choice is stated, never
    /// silent.</para>
    /// </summary>
    internal IReadOnlySet<int> GroundNets(out string? said)
    {
        if (_ground is { } hit) { said = hit.Said; return hit.Nets; }
        _ground = ComputeGround();
        said = _ground.Value.Said;
        return _ground.Value.Nets;
    }

    /// <summary>The partition piece read as the plane on a drawn reference, or -1.</summary>
    internal int GroundPlanePiece
    {
        get
        {
            GroundNets(out _);
            return _ground!.Value.Plane;
        }
    }

    private (IReadOnlySet<int>, string?, int) ComputeGround()
    {
        var ground = partition!.Ground;
        if (ground.ReferenceName is null)
            return (new HashSet<int>(), "no conductor in the stackup is marked as the ground " +
                                        "reference. Flag the ground plane in the Technology Editor.", -1);

        if (!ground.ReferenceDraws)
            return ground.Nets.Count == 0
                ? (ground.Nets, $"no via reaches the ground reference \"{ground.ReferenceName}\".", -1)
                : (ground.Nets, null, -1);

        int best = -1;
        double bestArea = -1;
        var pieces = partition.Pieces;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (!ground.ReferenceLayers.Contains(pieces[i].Layer)) continue;
            double area = AreaOfComponent(pieces[i].Paths);
            if (area > bestArea) { best = i; bestArea = area; }
        }
        if (best < 0)
            return (new HashSet<int>(), $"nothing is drawn on the ground reference \"{ground.ReferenceName}\", " +
                                        "so no copper is the ground plane.", -1);
        return (new HashSet<int> { pieces[best].Net },
                $"reads ground as the net of the largest copper on the ground reference \"{ground.ReferenceName}\".", best);
    }

    private bool HasPartition(string name, DrcLayerExpr node)
    {
        if (partition is not null) return true;
        Report($"{node} selected nothing: '{name}' needs connectivity, which this run did not build.");
        return false;
    }

    /// <summary>The nets of one polygon: those of every piece on <paramref name="layers"/> it
    /// overlaps or touches. See <see cref="DrcLayerExpr.OnGround"/>.</summary>
    /// <remarks>Asked through the connectivity walk's own <see cref="DrcConnectivity.LocalTouch"/>, so
    /// a pad beside a pour is tested against the pour's edges near it rather than all of them: a pour's
    /// or a plane's box is the board, so every pad on it would clip its whole outline (brief-review-3).</remarks>
    private HashSet<int> NetsOf(Paths64 component, IReadOnlyCollection<LayerKey> layers)
    {
        var nets = new HashSet<int>();
        var touch = Clipper.InflatePaths(component, TouchDilationDbu, JoinType.Miter, EndType.Polygon, MiterLimit);
        var bounds = DrcRegions.BoundsOf(touch);
        var pieces = partition!.Pieces;
        _local ??= new DrcConnectivity.LocalTouch([.. pieces.Select(p => (p.Layer, p.Paths, p.Bounds))], null);
        _piecesByLayer ??= pieces.Select((p, i) => (p.Layer, i)).GroupBy(t => t.Layer)
                                 .ToDictionary(g => g.Key, g => g.Select(t => t.i).ToArray());

        foreach (var layer in layers)
        {
            if (!_piecesByLayer.TryGetValue(layer, out var onLayer)) continue;
            foreach (int i in onLayer)
            {
                var piece = pieces[i];
                if (nets.Contains(piece.Net) || !piece.Bounds.Intersects(bounds)) continue;
                if (_local.Touches(i, touch, bounds, wantMeet: false, out _)) nets.Add(piece.Net);
            }
        }
        return nets;
    }

    private DrcConnectivity.LocalTouch? _local;
    private Dictionary<LayerKey, int[]>? _piecesByLayer;

    private static Paths64 Boolean(ClipType op, Paths64 a, Paths64 b)
    {
        // Short-circuits that are correct rather than merely fast: Clipper2 handles empty operands,
        // but naming the identities here keeps a chain of derived layers over a sparse design from
        // walking the clipper for every step.
        if (a.Count == 0)
            return op switch
            {
                ClipType.Union or ClipType.Xor => DrcRegions.Union(b),
                _ => [],   // Intersection and Difference with an empty A are both empty.
            };

        if (b.Count == 0)
            return op switch
            {
                ClipType.Intersection => [],
                _ => DrcRegions.Union(a),   // Union, Xor and Difference all leave A alone.
            };

        return Clipper.BooleanOp(op, a, b, LayoutClipper.Rule);
    }

    /// <summary>
    /// Whole-polygon selection. Splits A into connected components and keeps or drops each one
    /// INTACT — partial area is never returned, which is the whole difference between
    /// <c>interacting</c> and <c>and</c>.
    /// </summary>
    private static Paths64 Selection(Paths64 a, Paths64 b, DrcSelectOp op)
    {
        if (a.Count == 0) return [];

        // With an empty B every relation collapses to a constant, and saying so explicitly avoids
        // a per-component clipper call that would return the same answer.
        if (b.Count == 0)
            return op switch
            {
                DrcSelectOp.NotInteracting or DrcSelectOp.Outside or DrcSelectOp.NotCovering
                    => DrcRegions.Union(a),
                _ => [],
            };

        var touchB = op is DrcSelectOp.Interacting or DrcSelectOp.NotInteracting or DrcSelectOp.Outside
            ? Clipper.InflatePaths(b, TouchDilationDbu, JoinType.Miter, EndType.Polygon, MiterLimit)
            : b;

        var bComponents = op is DrcSelectOp.Covering or DrcSelectOp.NotCovering
            ? DrcRegions.Components(b)
            : null;

        var kept = new Paths64();

        foreach (var component in DrcRegions.Components(a))
        {
            if (Keeps(component, b, touchB, bComponents, op))
                kept.AddRange(component);
        }

        return kept;
    }

    private static bool Keeps(
        Paths64 component,
        Paths64 b,
        Paths64 touchB,
        List<Paths64>? bComponents,
        DrcSelectOp op)
    {
        switch (op)
        {
            case DrcSelectOp.Interacting:
                return !IsEmpty(Clipper.BooleanOp(ClipType.Intersection, component, touchB, LayoutClipper.Rule));

            case DrcSelectOp.NotInteracting:
                return IsEmpty(Clipper.BooleanOp(ClipType.Intersection, component, touchB, LayoutClipper.Rule));

            case DrcSelectOp.Inside:
                // Entirely within B iff nothing of it survives subtracting B.
                return IsEmpty(Clipper.BooleanOp(ClipType.Difference, component, b, LayoutClipper.Rule));

            case DrcSelectOp.Outside:
                // Stricter than "not inside": no overlap AND no shared edge.
                return IsEmpty(Clipper.BooleanOp(ClipType.Intersection, component, touchB, LayoutClipper.Rule));

            case DrcSelectOp.Covering:
                return bComponents is not null && bComponents.Exists(bc =>
                    IsEmpty(Clipper.BooleanOp(ClipType.Difference, bc, component, LayoutClipper.Rule)));

            case DrcSelectOp.NotCovering:
                return bComponents is null || !bComponents.Exists(bc =>
                    IsEmpty(Clipper.BooleanOp(ClipType.Difference, bc, component, LayoutClipper.Rule)));

            default:
                throw new NotSupportedException($"Unhandled select op {op}.");
        }
    }

    /// <summary>
    /// True when a Clipper2 result encloses no area.
    ///
    /// <para>A path count of zero is the common case, but not the only one: a difference that
    /// cancels exactly can leave degenerate rings with zero area. Testing area rather than count
    /// is what makes <c>inside</c> report correctly for a polygon that exactly coincides with
    /// its container.</para>
    /// </summary>
    private static bool IsEmpty(Paths64 paths)
    {
        if (paths.Count == 0) return true;
        double area = 0;
        foreach (var p in paths) area += Clipper.Area(p);
        return Math.Abs(area) < 1.0;   // Less than one square DBU is not a region.
    }

    /// <summary>
    /// Keeps whole components whose measured value falls in [min, max]. A null bound is open.
    ///
    /// <para>Whole components, like every other selection here — a polygon either survives intact
    /// or is dropped. Bounds are INCLUSIVE at both ends, which matches how a deck states them: a
    /// minimum-area rule of 100 permits a shape of exactly 100.</para>
    /// </summary>
    private static Paths64 SelectByMeasure(
        Paths64 region, long? min, long? max, Func<Paths64, double> measure)
    {
        if (region.Count == 0) return [];

        var kept = new Paths64();
        foreach (var component in DrcRegions.Components(region))
        {
            double v = measure(component);
            if (min.HasValue && v < min.Value) continue;
            if (max.HasValue && v > max.Value) continue;
            kept.AddRange(component);
        }
        return kept;
    }

    /// <summary>Enclosed area of one component, holes subtracted.</summary>
    internal static double AreaOfComponent(Paths64 component)
    {
        double sum = 0;
        foreach (var p in component) sum += Clipper.Area(p);
        return Math.Abs(sum);
    }

    /// <summary>Total boundary length of one component, holes included.</summary>
    internal static double PerimeterOfComponent(Paths64 component)
    {
        double sum = 0;
        foreach (var path in component)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % path.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                sum += Math.Sqrt(dx * dx + dy * dy);
            }
        }
        return sum;
    }

    /// <summary>The holes of a region, returned as solids.</summary>
    private static Paths64 HolesOf(Paths64 region)
    {
        if (region.Count == 0) return [];

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, region, [], tree, LayoutClipper.Rule);

        var holes = new Paths64();
        Collect(tree);
        return holes;

        void Collect(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (child.IsHole && child.Polygon is { } ring) holes.Add(ring);
                Collect(child);
            }
        }
    }
}
