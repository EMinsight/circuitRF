// The layer EXPRESSION a rule measures (docs/design/layout-view.md §9A.5).
//
// DRC v1 assumed a rule names one drawing layer. Real process rule decks do not work that way:
// a rule measures a DERIVED layer built from boolean algebra, sizing, and topological selection
// over the drawing layers, and only then applies a width/spacing/enclosure check to the result.
// A rule that says "minimum metal width, excluding the keep-out region, where it meets a pad" is
// three region operations before any measurement happens. v1 could not read those rules
// at all — not because the check was missing, but because there was nowhere to put the operand.
//
// <para><b>The vocabulary here is deliberately the one real decks use</b>, so translating a deck
// statement into this model is mechanical rather than interpretive. Adding an operation that no
// deck actually invokes buys nothing; leaving one out means a rule cannot be expressed and must be
// reported as unsupported instead — which is the honest outcome, but a worse one.</para>
//
// Serialized as TEXT, not as polymorphic JSON — see <see cref="DrcLayerExprParser"/> for why.

namespace CircuitRF.Design.Layout.Drc;

/// <summary>
/// A region-valued expression over drawing layers. Evaluated by <see cref="DrcRegionEval"/>.
///
/// <para>Every case is an immutable record, so an expression is safe to share across rules and
/// across threads, and two structurally identical expressions compare equal — which is what lets
/// the evaluator cache sub-expression results within one run without a hand-written key.</para>
/// </summary>
public abstract record DrcLayerExpr
{
    /// <summary>A drawing layer, by its own <see cref="LayerKey"/>. The only leaf.</summary>
    public sealed record Layer(LayerKey Key) : DrcLayerExpr;

    /// <summary>Intersection — the region covered by BOTH operands.</summary>
    public sealed record And(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>Union — the region covered by EITHER operand.</summary>
    public sealed record Or(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>Difference — <paramref name="A"/> with <paramref name="B"/> removed.
    /// Named <c>Not</c> to match deck vocabulary (<c>a.not(b)</c>), NOT a unary complement:
    /// the complement of a region is unbounded and has no representation here.</summary>
    public sealed record Not(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>Symmetric difference — covered by exactly one operand.</summary>
    public sealed record Xor(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>
    /// Grow (positive) or shrink (negative) by <paramref name="ByDbu"/>.
    ///
    /// <para><b>Join style is MITER, matching the width check's own erosion</b> — a derived layer
    /// built by sizing and then measured for width must not disagree with the measurement about
    /// what a corner is. Round joins would round every convex corner by the sizing distance and
    /// make the derived layer's own corners read as narrow.</para>
    /// </summary>
    public sealed record Sized(DrcLayerExpr A, long ByDbu) : DrcLayerExpr;

    /// <summary>
    /// Whole-polygon topological selection: keep the polygons of <paramref name="A"/> that stand in
    /// <paramref name="Op"/> relation to <paramref name="B"/>.
    ///
    /// <para><b>This selects WHOLE polygons, never partial area</b> — that is the entire difference
    /// between <c>a.interacting(b)</c> and <c>a.and(b)</c>, and getting it wrong produces a region
    /// that looks plausible and measures differently. A polygon either survives intact or is
    /// dropped.</para>
    ///
    /// <para><b>A count</b> (brief-review-1 R-rev1-3) applies to <see cref="DrcSelectOp.Interacting"/>
    /// only: keep a polygon of A when the number of DISTINCT polygons of B it touches lies in
    /// [<paramref name="MinCount"/>, <paramref name="MaxCount"/>] — <see cref="Near"/>'s rule at a
    /// distance of zero. Both null is today's <c>interacting</c>, evaluated by today's code; with
    /// either stated, an open minimum is 0 and an open maximum unbounded. <c>not_interacting</c>
    /// takes no count: it is <c>interacting(a, b, 0, 0)</c>, and a second spelling of it would be two
    /// ways to write one rule.</para>
    /// </summary>
    public sealed record Select(
        DrcLayerExpr A, DrcLayerExpr B, DrcSelectOp Op, int? MinCount = null, int? MaxCount = null) : DrcLayerExpr
    {
        /// <summary>True when a count was stated — the only case that is not today's selection.</summary>
        public bool IsCounted => MinCount is not null || MaxCount is not null;
    }

    /// <summary>
    /// Keeps each polygon of <paramref name="A"/> for which the number of distinct polygons of
    /// <paramref name="B"/> whose closest approach to it is at most <paramref name="DistanceDbu"/>
    /// lies in [<paramref name="MinCount"/>, <paramref name="MaxCount"/>] (brief-review-1 R-rev1-2).
    /// A whole-polygon selection on <see cref="Select"/>'s terms.
    ///
    /// <para>No count stated (both null) means [1, ∞) — "within <c>d</c> of some B". With either
    /// stated, an open minimum is 0 and an open maximum unbounded, so <c>near(a, b, d, 0, 0)</c> is
    /// "no B within <c>d</c>".</para>
    ///
    /// <para><b>The distance is the true Euclidean closest approach, so this one grows A with ROUND
    /// joins, where <see cref="Sized"/> uses MITER.</b> The two answer different questions.
    /// <c>Sized</c> builds a derived layer that a width check then measures, and the two must agree
    /// about what a corner is. <c>near</c> asks how far apart two shapes are, and a miter offset
    /// reaches past a convex corner by up to the miter limit times the distance — a square whose
    /// corner sits 1.05·d diagonally from a disc would be counted as within d. Round growth is the
    /// Minkowski sum with a disc of radius d, which is exactly "every point within d".</para>
    /// </summary>
    public sealed record Near(
        DrcLayerExpr A, DrcLayerExpr B, long DistanceDbu, int? MinCount = null, int? MaxCount = null) : DrcLayerExpr;

    /// <summary>
    /// Keeps each polygon of <paramref name="A"/> that shares a net with some polygon of
    /// <paramref name="B"/> (brief-review-1 R-rev1-4). See <see cref="OnGround"/> for what a
    /// derived polygon's nets are.
    /// </summary>
    public sealed record SameNet(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>
    /// Keeps each polygon of <paramref name="A"/> on a net the stackup's ground reference reaches —
    /// the nets <c>GroundReach.Nets</c> names (brief-review-1 R-rev1-4). Nothing infers a ground net:
    /// a technology that flags no reference selects nothing, and says so.
    ///
    /// <para><b>A derived polygon's nets</b>, for this and <see cref="SameNet"/>: the nets of every
    /// connectivity piece it overlaps or touches, among the pieces on the drawing layers its own
    /// expression READS. A polygon touching no such piece has no net and is never kept; one touching
    /// pieces of two nets is on both. The layer restriction is what makes the answer mean anything:
    /// a pad on top copper lies over an inner ground plane everywhere it sits, and taking every piece
    /// under it in plan view would put every pad on ground.</para>
    /// </summary>
    public sealed record OnGround(DrcLayerExpr A) : DrcLayerExpr;

    /// <summary>The holes of <paramref name="A"/>, as solid regions in their own right.</summary>
    public sealed record Holes(DrcLayerExpr A) : DrcLayerExpr;

    /// <summary>
    /// Union of <paramref name="A"/> with itself — merges abutting and overlapping polygons into
    /// single regions.
    ///
    /// <para>Not redundant: every other operation here already returns a merged result, but a deck
    /// states <c>merged</c> explicitly where the merge is the POINT of the step (two abutting
    /// rectangles must be measured as one shape, not two), and carrying that through keeps a
    /// translated rule readable against its source.</para>
    /// </summary>
    public sealed record Merged(DrcLayerExpr A) : DrcLayerExpr;

    /// <summary>
    /// Keeps the polygons of <paramref name="A"/> whose AREA falls in
    /// [<paramref name="MinDbu2"/>, <paramref name="MaxDbu2"/>].
    ///
    /// <para>Area is in square DBU, and the bound is inclusive at both ends. A null bound is
    /// open — <c>with_area(1/0, 100, )</c> is "at least 100". Selecting by area is how a deck
    /// isolates fill shapes, stray slivers and minimum-area candidates before measuring them.</para>
    /// </summary>
    public sealed record WithArea(DrcLayerExpr A, long? MinDbu2, long? MaxDbu2) : DrcLayerExpr;

    /// <summary>
    /// Keeps the polygons of <paramref name="A"/> whose PERIMETER falls in
    /// [<paramref name="MinDbu"/>, <paramref name="MaxDbu"/>], in DBU.
    ///
    /// <para>Perimeter rather than "edge length" deliberately: a deck's <c>with_length</c> selects
    /// on an EDGE collection, which this model has no type for, and approximating it by the
    /// polygon's own perimeter would silently select different shapes. This is the polygon-level
    /// question that IS expressible; an edge-level one is reported as unsupported instead.</para>
    /// </summary>
    public sealed record WithPerimeter(DrcLayerExpr A, long? MinDbu, long? MaxDbu) : DrcLayerExpr;

    /// <summary>Renders the expression in the parser's own syntax. Round-trips through
    /// <see cref="DrcLayerExprParser.Parse"/>.</summary>
    public sealed override string ToString() => DrcLayerExprParser.Format(this);

    /// <summary>True when evaluating this expression needs the net partition — it contains a
    /// <see cref="SameNet"/> or an <see cref="OnGround"/>. A run builds connectivity only when some
    /// rule asks for it, so this is how an expression asks.</summary>
    public bool NeedsConnectivity()
    {
        return Any(this);

        static bool Any(DrcLayerExpr e) => e switch
        {
            SameNet or OnGround => true,
            And a           => Any(a.A) || Any(a.B),
            Or o            => Any(o.A) || Any(o.B),
            Not n           => Any(n.A) || Any(n.B),
            Xor x           => Any(x.A) || Any(x.B),
            Sized s         => Any(s.A),
            Select sel      => Any(sel.A) || Any(sel.B),
            Near nr         => Any(nr.A) || Any(nr.B),
            Holes h         => Any(h.A),
            Merged m        => Any(m.A),
            WithArea wa     => Any(wa.A),
            WithPerimeter wp => Any(wp.A),
            _               => false,
        };
    }

    /// <summary>Every drawing layer this expression reads, deduplicated. Used to decide which
    /// layers a run must build regions for, and to report a rule that names a layer the technology
    /// does not define.</summary>
    public IReadOnlyCollection<LayerKey> ReferencedLayers()
    {
        var acc = new HashSet<LayerKey>();
        Walk(this, acc);
        return acc;

        static void Walk(DrcLayerExpr e, HashSet<LayerKey> into)
        {
            switch (e)
            {
                case Layer l:                   into.Add(l.Key); break;
                case And a:                     Walk(a.A, into); Walk(a.B, into); break;
                case Or o:                      Walk(o.A, into); Walk(o.B, into); break;
                case Not n:                     Walk(n.A, into); Walk(n.B, into); break;
                case Xor x:                     Walk(x.A, into); Walk(x.B, into); break;
                case Sized s:                   Walk(s.A, into); break;
                case Select sel:                Walk(sel.A, into); Walk(sel.B, into); break;
                case Holes h:                   Walk(h.A, into); break;
                case Merged m:                  Walk(m.A, into); break;
                case WithArea wa:               Walk(wa.A, into); break;
                case WithPerimeter wp:          Walk(wp.A, into); break;
                case Near nr:                   Walk(nr.A, into); Walk(nr.B, into); break;
                case SameNet sn:                Walk(sn.A, into); Walk(sn.B, into); break;
                case OnGround og:               Walk(og.A, into); break;
            }
        }
    }
}

/// <summary>
/// The whole-polygon selection relations a deck uses. Each keeps polygons of A; they differ only in
/// which ones.
/// </summary>
public enum DrcSelectOp
{
    /// <summary>A polygon of A that touches or overlaps B anywhere.</summary>
    Interacting,

    /// <summary>The complement of <see cref="Interacting"/> within A.</summary>
    NotInteracting,

    /// <summary>A polygon of A entirely contained within B.</summary>
    Inside,

    /// <summary>A polygon of A entirely outside B (no touch, no overlap).</summary>
    Outside,

    /// <summary>A polygon of A that entirely contains at least one polygon of B.</summary>
    Covering,

    /// <summary>The complement of <see cref="Covering"/> within A.</summary>
    NotCovering,
}
