// Where the ports are — brief-artsch-3-board-graph.md R-as3-6, overview D8 and D9;
// docs/design/artwork-to-schematic.md §4.5.
//
// In priority order:
//   1. the layout's EM setup's ports — the ports the user already placed for EM;
//   2. port labels and layout pins;
//   3. the pads of a multi-pin part (more than two pads) that land on a signal island — the device is cut
//      out and the schematic is the networks around it as the device sees them (D9);
//   4. a signal island reaching the board outline (an edge launch), or a connector footprint's pads;
//   5. a crossing of the scope's boundary (R-as3-7).
// A port found within PortMergeMicrons of a higher-priority port on the same island is that port. Ports
// of classes 1 and 2 keep the numbers they state; the rest take the lowest free numbers, left to right,
// top to bottom.

using System.Numerics;
using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>Where a port came from, in R-as3-6's priority order.</summary>
public enum PortSource
{
    /// <summary>A port of the layout's EM setup.</summary>
    EmSetup = 1,

    /// <summary>A port label.</summary>
    PortLabel = 2,

    /// <summary>A layout pin.</summary>
    LayoutPin = 3,

    /// <summary>A pad of a part with more than two pads (D9).</summary>
    MultiPinPart = 4,

    /// <summary>A signal island reaching the board outline.</summary>
    BoardEdge = 5,

    /// <summary>A pad of a connector footprint (refdes J, P or X).</summary>
    Connector = 6,

    /// <summary>A crossing of the scope's boundary.</summary>
    ScopeCut = 7,
}

/// <summary>One port.</summary>
/// <param name="Number">Its port number, 1-based.</param>
/// <param name="Name"><c>P1</c>…, a label's own text, a pin's name, <c>U1.3</c> for a part's pad, or
/// <c>X1</c>… for a scope crossing.</param>
/// <param name="X">DBU.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">The drawing layer of the copper it is on.</param>
/// <param name="Island">Its island's id.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Z0">Its reference impedance — the EM setup's where it came from one, 50 Ω otherwise.</param>
/// <param name="Refdes">The part it is a pad of, or null.</param>
public sealed record RecognizedPort(
    int Number, string Name, long X, long Y, LayerKey Layer, int Island, PortSource Source, Complex Z0,
    string? Refdes);

/// <summary>R-as3-6, written once.</summary>
public static class PortDiscovery
{
    /// <summary>The refusal when nothing in scope reaches a port.</summary>
    public const string NoPortRefusal =
        "Nothing in scope reaches a port — add port labels, a .cem, or widen the selection.";

    /// <summary>A connector footprint's designator: J, P or X followed by a digit.</summary>
    public static bool IsConnectorRefdes(string? refdes) =>
        refdes is { Length: >= 2 } r && char.ToUpperInvariant(r[0]) is 'J' or 'P' or 'X' && char.IsDigit(r[1]);

    private sealed record Candidate(
        int Priority, PortSource Source, long X, long Y, LayerKey Layer, int Island,
        int? StatedNumber, string? Name, string? Refdes, Complex Z0);

    internal static List<RecognizedPort> Discover(PortContext ctx, out List<RecognitionAnchor> notOnSignal)
    {
        var board = ctx.Board;
        var found = new List<Candidate>();
        notOnSignal = [];
        var z50 = new Complex(50, 0);
        bool InScope(long x, long y) => ctx.Scope is null || Regions.Contains(ctx.Scope, x, y);

        // Where a point lands: the island and the layer, or nothing (ground copper, no copper).
        (int Island, LayerKey Layer)? Land(long x, long y, LayerKey? layer)
        {
            int piece = board.ConductorAt(x, y, layer);
            if (piece < 0 || ctx.Body[piece] || ctx.IslandOf[piece] < 0) return null;
            return (ctx.IslandOf[piece], board.Pieces.LayerOfPiece(piece));
        }

        // ── 1 and 2: port labels (the EM setup's ports when there is one), then layout pins ──────────
        var labelSource = ctx.Setup is null ? PortSource.PortLabel : PortSource.EmSetup;
        foreach (var (number, label) in EmPortExtraction.NumberPorts(ctx.View.Shapes))
        {
            if (!InScope(label.X, label.Y)) continue;
            if (Land(label.X, label.Y, label.PortLayer ?? label.Layer) is not { } at)
            {
                notOnSignal.Add(new RecognitionAnchor(label.X, label.Y, label.PortLayer ?? label.Layer));
                continue;
            }
            var z0 = ctx.Setup?.ResolvePortZ0(number - 1) ?? z50;
            found.Add(new Candidate((int)labelSource, labelSource, label.X, label.Y, at.Layer, at.Island,
                                    number, LabelName(label.Text, number), null, z0));
        }

        foreach (var pin in ctx.View.Pins)
        {
            if (!InScope(pin.X, pin.Y)) continue;
            if (Land(pin.X, pin.Y, pin.Layer) is not { } at)
            {
                notOnSignal.Add(new RecognitionAnchor(pin.X, pin.Y, pin.Layer));
                continue;
            }
            found.Add(new Candidate((int)PortSource.LayoutPin, PortSource.LayoutPin, pin.X, pin.Y, at.Layer, at.Island,
                                    null, pin.Name is { Length: > 0 } n ? n : null, null, z50));
        }

        // ── 3 and 4: a multi-pin part's pads, a connector's pads ─────────────────────────────────────
        var placedRefdes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in ctx.Placed.Where(p => p.Refdes is { Length: > 0 }).GroupBy(p => p.Refdes!, StringComparer.OrdinalIgnoreCase))
        {
            placedRefdes.Add(part.Key);
            bool connector = IsConnectorRefdes(part.Key);
            if (!connector && part.Count() <= 2) continue;
            var source = connector ? PortSource.Connector : PortSource.MultiPinPart;
            foreach (var pad in part)
            {
                if (!InScope(pad.X, pad.Y)) continue;
                // A pad on ground joins ground; a pad on no signal copper is left open (D9).
                if (Land(pad.X, pad.Y, pad.Layer) is not { } at || !ctx.SignalIsland[at.Island]) continue;
                found.Add(new Candidate(connector ? 4 : 3, source, pad.X, pad.Y, at.Layer, at.Island,
                                        null, $"{part.Key}.{pad.Pin}", part.Key, z50));
            }
        }

        foreach (var row in ctx.Placement?.Rows ?? [])
        {
            if (!IsConnectorRefdes(row.Refdes) || placedRefdes.Contains(row.Refdes) || !InScope(row.X, row.Y)) continue;
            if (Land(row.X, row.Y, null) is not { } at || !ctx.SignalIsland[at.Island])
            {
                notOnSignal.Add(new RecognitionAnchor(row.X, row.Y));
                continue;
            }
            found.Add(new Candidate(4, PortSource.Connector, row.X, row.Y, at.Layer, at.Island, null, null, row.Refdes, z50));
        }

        // ── 4: an edge launch ────────────────────────────────────────────────────────────────────────
        long reach = Math.Max(1, (long)Math.Round(ctx.Options.EdgeReachMicrons * board.DbuPerMicron));
        var outline = ctx.Outline;
        if (!outline.IsEmpty)
        {
            for (int p = 0; p < board.Pieces.Count; p++)
            {
                if (ctx.IslandOf[p] is not (var island and >= 0) || !ctx.SignalIsland[island] || ctx.SeparatePour[island]) continue;
                var b = board.Pieces.BoundsOfPiece(p);
                var paths = board.Pieces.PathsOfPiece(p);
                var layer = board.Pieces.LayerOfPiece(p);

                void Edge(bool reaches, long x0, long y0, long x1, long y1, Func<Bbox, (long X, long Y)> at)
                {
                    if (!reaches) return;
                    var strip = new Paths64 { new Path64 { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) } };
                    foreach (var component in DrcRegions.Components(Clipper.BooleanOp(ClipType.Intersection, paths, strip, LayoutClipper.Rule)))
                    {
                        var (x, y) = at(DrcRegions.BoundsOf(component));
                        found.Add(new Candidate(4, PortSource.BoardEdge, x, y, layer, island, null, null, null, z50));
                    }
                }

                long ox0 = outline.MinX - reach, oy0 = outline.MinY - reach, ox1 = outline.MaxX + reach, oy1 = outline.MaxY + reach;
                Edge(b.MinX <= outline.MinX + reach, ox0, oy0, outline.MinX + reach, oy1, c => (c.MinX, (c.MinY + c.MaxY) / 2));
                Edge(b.MaxX >= outline.MaxX - reach, outline.MaxX - reach, oy0, ox1, oy1, c => (c.MaxX, (c.MinY + c.MaxY) / 2));
                Edge(b.MinY <= outline.MinY + reach, ox0, oy0, ox1, outline.MinY + reach, c => ((c.MinX + c.MaxX) / 2, c.MinY));
                Edge(b.MaxY >= outline.MaxY - reach, ox0, outline.MaxY - reach, ox1, oy1, c => ((c.MinX + c.MaxX) / 2, c.MaxY));
            }
        }

        // ── 5: where the scope's boundary cuts a signal island ──────────────────────────────────────
        if (ctx.Scope is { } scope)
        {
            long t = Math.Max(2, board.DbuPerMicron);
            var outside = Clipper.BooleanOp(ClipType.Difference,
                Clipper.InflatePaths(scope, t, JoinType.Miter, EndType.Polygon), scope, LayoutClipper.Rule);
            var whole = ctx.Whole.Pieces;
            for (int p = 0; p < board.Pieces.Count; p++)
            {
                if (ctx.IslandOf[p] is not (var island and >= 0) || !ctx.SignalIsland[island]) continue;
                var layer = board.Pieces.LayerOfPiece(p);
                var near = DrcRegions.Grow(board.Pieces.BoundsOfPiece(p), 2 * t);
                var original = new Paths64();
                for (int w = 0; w < whole.Count; w++)
                    if (whole.LayerOfPiece(w) == layer && whole.BoundsOfPiece(w).Intersects(near)) original.AddRange(whole.PathsOfPiece(w));
                if (original.Count == 0) continue;

                var grown = Clipper.InflatePaths(board.Pieces.PathsOfPiece(p), 2 * t, JoinType.Miter, EndType.Polygon);
                var cut = Clipper.BooleanOp(ClipType.Intersection,
                    Clipper.BooleanOp(ClipType.Intersection, original, outside, LayoutClipper.Rule), grown, LayoutClipper.Rule);
                foreach (var component in DrcRegions.Components(cut))
                {
                    var c = DrcRegions.BoundsOf(component);
                    found.Add(new Candidate(5, PortSource.ScopeCut, (c.MinX + c.MaxX) / 2, (c.MinY + c.MaxY) / 2,
                                            layer, island, null, null, null, z50));
                }
            }
        }

        return Number(Merge(found, ctx.Options.PortMergeMicrons * board.DbuPerMicron));
    }

    /// <summary>Keeps the highest-priority port of any group on one island within <paramref name="radius"/>.</summary>
    private static List<Candidate> Merge(List<Candidate> found, double radius)
    {
        var accepted = new List<Candidate>();
        foreach (var c in found.OrderBy(c => c.Priority).ThenBy(c => c.StatedNumber ?? int.MaxValue)
                               .ThenBy(c => c.X).ThenByDescending(c => c.Y))
        {
            bool same = accepted.Any(a => a.Island == c.Island &&
                                          Math.Sqrt((double)(a.X - c.X) * (a.X - c.X) + (double)(a.Y - c.Y) * (a.Y - c.Y)) <= radius);
            if (!same) accepted.Add(c);
        }
        return accepted;
    }

    /// <summary>D8's numbering: stated numbers kept, the rest the lowest free ones in order.</summary>
    private static List<RecognizedPort> Number(List<Candidate> accepted)
    {
        var stated = accepted.Where(c => c.StatedNumber is not null).OrderBy(c => c.StatedNumber).ToList();
        var pins = accepted.Where(c => c.StatedNumber is null && c.Source == PortSource.LayoutPin).ToList();
        var rest = accepted.Where(c => c.StatedNumber is null && c.Source != PortSource.LayoutPin)
                           .OrderBy(c => c.X).ThenByDescending(c => c.Y).ToList();

        var used = new HashSet<int>(stated.Select(c => c.StatedNumber!.Value));
        int next = 1, cut = 0;
        int Free() { while (used.Contains(next)) next++; used.Add(next); return next; }

        var ports = new List<RecognizedPort>(accepted.Count);
        foreach (var c in stated) ports.Add(Make(c, c.StatedNumber!.Value));
        foreach (var c in pins.Concat(rest))
        {
            int n = Free();
            ports.Add(Make(c, n, c.Source == PortSource.ScopeCut ? $"X{++cut}" : null));
        }
        return [.. ports.OrderBy(p => p.Number)];

        static RecognizedPort Make(Candidate c, int number, string? name = null) =>
            new(number, name ?? c.Name ?? $"P{number}", c.X, c.Y, c.Layer, c.Island, c.Source, c.Z0, c.Refdes);
    }

    /// <summary>A label's text as a port name: a bare number (or P and a number) is <c>P&lt;n&gt;</c>.</summary>
    private static string LabelName(string text, int number)
    {
        string t = text.Trim();
        if (t.Length == 0 || int.TryParse(t, out _)
            || (t.Length > 1 && char.ToUpperInvariant(t[0]) == 'P' && int.TryParse(t[1..], out _)))
            return $"P{number}";
        return t;
    }
}

/// <summary>What port discovery reads.</summary>
internal sealed record PortContext(
    BoardCopper Board, BoardCopper Whole, bool[] Body, int[] IslandOf, bool[] SignalIsland, bool[] SeparatePour,
    LayoutView View, EmSetup? Setup, IReadOnlyList<PlacedPin> Placed, PlacementTable? Placement,
    Paths64? Scope, Bbox Outline, RecognitionOptions Options);
