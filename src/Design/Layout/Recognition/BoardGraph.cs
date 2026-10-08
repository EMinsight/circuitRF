// The recognised board — brief-artsch-3-board-graph.md R-as3-2 and R-as3-5; docs/design/artwork-to-schematic.md §4.
//
// From a flattened board: what is ground, which vias matter, which copper islands carry signal and where
// the ports are — so a board with hundreds of stitching vias becomes a handful of signal islands, a
// ground node and a few ports. Every later phase states what it does in terms of this graph.
//
// ── THE PARTITION IS THE EXISTING ONE ────────────────────────────────────────────────────────────
//
// Islands are built over CopperPieces — the one partition DRC, railRF and LVS already share — and a
// via is classified by the pieces its barrel touched, which is the partition's own record
// (ConnectivityPartition.Barrels), never a barrel tested again. What this file adds is a reading of that
// partition at the PIECE level rather than the net level: a shunt part's ground pad joined by six vias
// to the plane is in the ground NET, and so is a shorted stub, but neither is ground copper — the vias
// between them and the plane are exactly the elements the circuit needs.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What one copper island is to the circuit.</summary>
public enum IslandKind
{
    /// <summary>A node of the circuit: it touches a part pad, a port or a line.</summary>
    Signal,

    /// <summary>A ground pad that is its own small island, joined to ground by vias — each kept via
    /// is a <c>VIAGND</c> (D7).</summary>
    PadGround,

    /// <summary>Copper touching nothing — a test point, a logo, a fiducial. Listed and dropped; kept
    /// in the graph so a later phase that finds a part pad on it can promote it.</summary>
    Nothing,
}

/// <summary>One copper island: the non-ground conductor pieces joined by the vias between them.</summary>
/// <param name="Id">Index into <see cref="BoardGraph.Islands"/>.</param>
/// <param name="Layers">The drawing layers its copper is on, in stackup order.</param>
/// <param name="AreaDbu2">Its copper area, DBU².</param>
/// <param name="Bounds">Its bounding box, DBU.</param>
/// <param name="Vias">Indices into <see cref="BoardGraph.Vias"/> of the vias on it.</param>
/// <param name="Pins">The placed pins and pads that land on it.</param>
/// <param name="NetName">The net its shapes state, or null.</param>
/// <param name="ShortedToGround">Whether a via joins it straight to ground (a shorted stub or line
/// end, or a ground pad).</param>
/// <param name="IsSeparatePour">Whether it holds a pour galvanically separate from ground (R-as3-3).</param>
public sealed record BoardIsland(
    int Id, IslandKind Kind, IReadOnlyList<LayerKey> Layers, double AreaDbu2, Bbox Bounds,
    IReadOnlyList<int> Vias, IReadOnlyList<PlacedPin> Pins, string? NetName,
    bool ShortedToGround, bool IsSeparatePour)
{
    /// <summary>The partition pieces it is made of — internal because a later phase reads its
    /// copper through the same partition rather than walking the artwork again.</summary>
    internal IReadOnlyList<int> Pieces { get; init; } = [];
}

/// <summary>The recognised board.</summary>
/// <param name="Ground">What was read as ground, and how.</param>
/// <param name="Islands">Every non-ground island in scope.</param>
/// <param name="Vias">Every via in scope, classified.</param>
/// <param name="Ports">The ports, numbered (D8).</param>
/// <param name="DbuPerMicron">The layout's resolution.</param>
public sealed record BoardGraph(
    GroundChoice Ground, IReadOnlyList<BoardIsland> Islands, IReadOnlyList<RecognizedVia> Vias,
    IReadOnlyList<RecognizedPort> Ports, int DbuPerMicron)
{
    /// <summary>The partition the islands were built over — the scoped one when a scope cut the board.</summary>
    internal CopperPieces Copper { get; init; } = CopperPieces.Empty;

    /// <summary>Island of each partition piece, -1 for ground copper and via barrels.</summary>
    internal int[] IslandOfPiece { get; init; } = [];

    /// <summary>The signal islands — the circuit's nodes before AS-5 subdivides them.</summary>
    public IEnumerable<BoardIsland> SignalIslands => Islands.Where(i => i.Kind == IslandKind.Signal);

    /// <summary>The island covering (<paramref name="x"/>, <paramref name="y"/>) on
    /// <paramref name="layer"/> (any layer when null), or null — ground copper is no island.</summary>
    public BoardIsland? IslandAt(long x, long y, LayerKey? layer = null)
    {
        // Every layer an island is on, rather than the partition's any-layer lookup: that answers with
        // the first piece under the point, and under a line that is the plane.
        foreach (var l in layer is { } only ? [only] : Islands.SelectMany(i => i.Layers).Distinct())
        {
            int piece = Copper.IndexAt(x, y, l);
            if (piece >= 0 && piece < IslandOfPiece.Length && IslandOfPiece[piece] is var i and >= 0) return Islands[i];
        }
        return null;
    }
}

/// <summary>
/// One partition read at the piece level: which pieces are conductors and which via barrels, what each
/// barrel touched, how many vias land on each piece, and the two shape tests ground reading needs.
/// </summary>
internal sealed class BoardCopper
{
    public BoardCopper(CopperPieces pieces, Technology tech, IReadOnlyList<LayoutShape> shapes,
                       int dbuPerMicron, IReadOnlyDictionary<LayerKey, double> widestTrace)
    {
        Pieces = pieces;
        Tech = tech;
        DbuPerMicron = dbuPerMicron;
        _widest = widestTrace;

        var conductors = Conductors.Of(tech);
        ConductorLayers = conductors.SelectMany(c => c.DrawingLayers).ToHashSet();
        var viaLayers = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).SelectMany(l => l.DrawingLayers).ToHashSet();

        int n = pieces.Count;
        IsConductor = new bool[n];
        for (int i = 0; i < n; i++) IsConductor[i] = ConductorLayers.Contains(pieces.LayerOfPiece(i));

        // ── the reference conductor, when the stackup states it and the artwork does not draw it ──
        var reference = tech.Stackup.Layers.FirstOrDefault(
            l => l.Kind == StackupKind.Conductor && l.IsGroundReference && l.Name.Length > 0);
        ReferenceName = reference?.Name;
        UndrawnReference = reference is { DrawingLayers.Count: 0 };
        var order = conductors.Select(c => c.StackupName).ToList();

        // ── one record per via BARREL, located through the partition ─────────────────────────────
        var touchesOf = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var b in pieces.Barrels) touchesOf[b.Via] = b.Touched;

        ViaCountOnPiece = new int[n];
        var seen = new HashSet<int>();
        foreach (var shape in shapes)
        {
            if (shape is not ViaShape via || !viaLayers.Contains(via.Layer)) continue;
            int piece = pieces.IndexAt(via.X, via.Y, via.Layer);
            if (piece < 0 || !seen.Add(piece)) continue;   // a drill hit and its pad, one barrel

            var entry = ViaSpanResolver.EntryFor(via.Layer, tech);
            IReadOnlyList<string> spanned = order;
            if (entry is { SpanFromLayer: { Length: > 0 } from, SpanToLayer: { Length: > 0 } to }
                && order.IndexOf(from) is var a and >= 0 && order.IndexOf(to) is var b and >= 0)
                spanned = order.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);

            // The partition records a barrel that met two conductors or more. One that met one (or
            // none) joins nothing in the partition; it is located here by the same point lookup the
            // partition answers with, so a via landing on one pad and an undrawn plane still names its pad.
            IReadOnlyList<int> touched;
            if (touchesOf.TryGetValue(piece, out var t)) touched = t;
            else
            {
                var single = new List<int>();
                foreach (var c in conductors)
                    if (spanned.Contains(c.StackupName))
                        foreach (var layer in c.DrawingLayers)
                            if (pieces.IndexAt(via.X, via.Y, layer) is var hit and >= 0 && !single.Contains(hit)) single.Add(hit);
                touched = single;
            }

            bool reachesUndrawn = UndrawnReference && ReferenceName is { } rn && spanned.Contains(rn);
            Vias.Add(new ViaBarrel(via, piece, touched, reachesUndrawn,
                                   entry?.SpanFromLayer, entry?.SpanToLayer));
            foreach (int c in touched) ViaCountOnPiece[c]++;
        }

        _area = new double[n];
        _wide = new bool?[n];
        Array.Fill(_area, double.NaN);
    }

    public CopperPieces Pieces { get; }
    public Technology Tech { get; }
    public int DbuPerMicron { get; }
    public HashSet<LayerKey> ConductorLayers { get; }
    public bool[] IsConductor { get; }
    public int[] ViaCountOnPiece { get; }
    public string? ReferenceName { get; }
    public bool UndrawnReference { get; }
    public List<ViaBarrel> Vias { get; } = [];

    private readonly IReadOnlyDictionary<LayerKey, double> _widest;
    private readonly double[] _area;
    private readonly bool?[] _wide;

    /// <summary>The widest trace the trace review reads on <paramref name="layer"/>, DBU — its own
    /// <c>WidthRange</c>; the review's upper clamp where the stackup gives no answer.</summary>
    public double WidestOn(LayerKey layer) =>
        _widest.TryGetValue(layer, out double w) ? w : 8000.0 * DbuPerMicron;

    public double Area(int piece)
    {
        if (double.IsNaN(_area[piece]))
        {
            double a = 0;
            foreach (var p in Pieces.PathsOfPiece(piece)) a += Clipper.Area(p);
            _area[piece] = Math.Abs(a);
        }
        return _area[piece];
    }

    /// <summary>
    /// <b>Wider than any trace</b> somewhere: the piece still has copper after eroding it by half the
    /// widest trace the review reads on its layer — a pour or a plane, never a line.
    /// </summary>
    public bool IsWide(int piece)
    {
        if (_wide[piece] is { } known) return known;
        double half = 0.5 * WidestOn(Pieces.LayerOfPiece(piece));
        var eroded = Clipper.InflatePaths(Pieces.PathsOfPiece(piece), -half, JoinType.Miter, EndType.Polygon);
        bool wide = eroded.Count > 0;
        _wide[piece] = wide;
        return wide;
    }

    /// <summary>
    /// <b>A pad, not a trace</b>, by the trace review's own pad rule: no longer than
    /// <see cref="TraceImpedanceAnalysis.MinAspect"/> of its widths, and no larger than the widest
    /// trace either way.
    /// </summary>
    public bool IsPadShaped(int piece)
    {
        var b = Pieces.BoundsOfPiece(piece);
        double w = b.MaxX - b.MinX, h = b.MaxY - b.MinY;
        double lo = Math.Max(1, Math.Min(w, h)), hi = Math.Max(w, h);
        return hi <= WidestOn(Pieces.LayerOfPiece(piece)) && hi < TraceImpedanceAnalysis.MinAspect * lo;
    }

    /// <summary>A point certainly on <paramref name="piece"/>: its first vertex (a boundary point is
    /// on the piece, <see cref="Regions"/>' own rule).</summary>
    public (long X, long Y) ProbeOf(int piece)
    {
        var paths = Pieces.PathsOfPiece(piece);
        return paths.Count > 0 && paths[0].Count > 0 ? (paths[0][0].X, paths[0][0].Y) : (0, 0);
    }

    /// <summary>The conductor piece at a point, searching <paramref name="layer"/> first and then every
    /// conductor layer in stackup order; -1 where no copper is.</summary>
    public int ConductorAt(long x, long y, LayerKey? layer)
    {
        if (layer is { } only && ConductorLayers.Contains(only) && Pieces.IndexAt(x, y, only) is var hit and >= 0) return hit;
        foreach (var c in Conductors.Of(Tech))
            foreach (var l in c.DrawingLayers)
                if (Pieces.IndexAt(x, y, l) is var p and >= 0) return p;
        return -1;
    }

    /// <summary>Bounding box of every conductor piece.</summary>
    public Bbox CopperBounds()
    {
        var box = Bbox.Empty;
        for (int i = 0; i < Pieces.Count; i++) if (IsConductor[i]) box = box.Union(Pieces.BoundsOfPiece(i));
        return box;
    }
}

/// <summary>One via barrel of the partition and what its ends are.</summary>
/// <param name="Shape">The via shape it was located from (the first, where several share a barrel).</param>
/// <param name="Piece">The via-layer piece.</param>
/// <param name="Touched">The conductor pieces the barrel met.</param>
/// <param name="ReachesUndrawnGround">Whether its span passes a ground reference the artwork does not draw.</param>
internal sealed record ViaBarrel(
    ViaShape Shape, int Piece, IReadOnlyList<int> Touched, bool ReachesUndrawnGround,
    string? SpanFrom, string? SpanTo);
