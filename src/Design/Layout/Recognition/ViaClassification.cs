// Which vias matter — brief-artsch-3-board-graph.md R-as3-4, overview D7; docs/design/artwork-to-schematic.md §4.3.
//
// A via is classified by what is at its two ends, read off the partition's own barrel record:
//
//   ground copper  – ground copper   STITCHING        dropped, counted
//   signal         – signal          SIGNAL TRANSITION a VIA element
//   a ground pad   – ground copper   PAD GROUND       a VIAGND, the nearest few per pad, the rest counted
//   signal         – ground copper   SIGNAL SHORT     a VIAGND
//
// A part's ground pad that sits ON a pour is part of the pour's piece, so its vias are stitching and the
// part is a plain GND — nothing here needs a rule for it. vias=ground turns every VIAGND into GND.
// Mutual inductance between neighbouring VIAGNDs is not modelled; the report says so once.

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What a via joins.</summary>
public enum ViaClass
{
    /// <summary>Ground copper to ground copper — stitching, fences, plane ties.</summary>
    Stitching,

    /// <summary>A signal island on one layer to its continuation on another.</summary>
    SignalTransition,

    /// <summary>A ground pad that is its own small island, to ground copper.</summary>
    PadGround,

    /// <summary>A signal island straight to ground — a shorted stub or line end.</summary>
    SignalShort,

    /// <summary>A barrel meeting one conductor or none: it joins nothing.</summary>
    JoinsNothing,
}

/// <summary>What a via becomes in the circuit.</summary>
public enum ViaElement
{
    /// <summary>Nothing: dropped (stitching, joining nothing, or past a pad's cap).</summary>
    None,

    /// <summary>A <c>VIA</c>, stackup-bound by <c>ViaSubstrateInjection</c> (AS-6).</summary>
    Via,

    /// <summary>A <c>VIAGND</c>.</summary>
    ViaGnd,

    /// <summary>A plain ground, under <see cref="ViaPolicy.Ground"/>.</summary>
    Gnd,
}

/// <summary>One via, classified.</summary>
/// <param name="X">Centre, DBU.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">Its barrel's drawing layer.</param>
/// <param name="DrillDbu">Drill diameter, DBU.</param>
/// <param name="PadDbu">Pad diameter, DBU (0 when the via states none).</param>
/// <param name="SpanFrom">The stackup conductor its span starts at, or null for a through hole.</param>
/// <param name="SpanTo">Where it ends.</param>
/// <param name="Islands">The islands at its non-ground ends (ids into <see cref="BoardGraph.Islands"/>).</param>
public sealed record RecognizedVia(
    long X, long Y, LayerKey Layer, long DrillDbu, long PadDbu, string? SpanFrom, string? SpanTo,
    ViaClass Class, ViaElement Element, IReadOnlyList<int> Islands);

/// <summary>D7, written once.</summary>
public static class ViaClassification
{
    /// <summary>
    /// Classifies every via barrel of <paramref name="board"/>.
    /// </summary>
    /// <param name="body">Per piece: ground copper.</param>
    /// <param name="islandOf">Per piece: its island, -1 for ground copper and barrels.</param>
    /// <param name="padIsland">Per island: every piece of it is a candidate ground pad.</param>
    /// <param name="isPadGround">Filled per island: it is a ground pad (pad-sized, joined to ground by vias).</param>
    internal static List<RecognizedVia> Classify(
        BoardCopper board, bool[] body, int[] islandOf, bool[] padIsland, RecognitionOptions options,
        out bool[] isPadGround)
    {
        var barrels = board.Vias;
        var ends = new (bool Ground, List<int> Islands, int Count)[barrels.Count];
        var groundVia = new bool[padIsland.Length];

        for (int k = 0; k < barrels.Count; k++)
        {
            var v = barrels[k];
            bool ground = v.ReachesUndrawnGround;
            var islands = new List<int>();
            foreach (int c in v.Touched)
            {
                if (body[c]) ground = true;
                else if (islandOf[c] is var i and >= 0 && !islands.Contains(i)) islands.Add(i);
            }
            ends[k] = (ground, islands, v.Touched.Count + (v.ReachesUndrawnGround ? 1 : 0));
            if (ground) foreach (int i in islands) groundVia[i] = true;
        }

        var padGround = new bool[padIsland.Length];
        for (int i = 0; i < padIsland.Length; i++) padGround[i] = padIsland[i] && groundVia[i];
        isPadGround = padGround;

        var classes = new ViaClass[barrels.Count];
        for (int k = 0; k < barrels.Count; k++)
        {
            var (ground, islands, count) = ends[k];
            classes[k] = count < 2 ? ViaClass.JoinsNothing
                : islands.Count == 0 ? ViaClass.Stitching
                : !ground ? ViaClass.SignalTransition
                : islands.Any(i => padGround[i]) ? ViaClass.PadGround
                : ViaClass.SignalShort;
        }

        // ── the nearest few per ground pad, the rest counted ─────────────────────────────────────
        var kept = new bool[barrels.Count];
        foreach (var group in Enumerable.Range(0, barrels.Count)
                     .Where(k => classes[k] == ViaClass.PadGround)
                     .GroupBy(k => ends[k].Islands.First(i => padGround[i])))
        {
            var pad = PadCentre(board, islandOf, group.Key);
            foreach (int k in group
                         .OrderBy(k => Dist2(barrels[k].Shape.X, barrels[k].Shape.Y, pad))
                         .ThenBy(k => k)
                         .Take(Math.Max(0, options.MaxGroundViasPerPad)))
                kept[k] = true;
        }

        var toGround = options.Vias == ViaPolicy.Ground ? ViaElement.Gnd : ViaElement.ViaGnd;
        var result = new List<RecognizedVia>(barrels.Count);
        for (int k = 0; k < barrels.Count; k++)
        {
            var element = classes[k] switch
            {
                ViaClass.SignalTransition => ViaElement.Via,
                ViaClass.SignalShort => toGround,
                ViaClass.PadGround when kept[k] => toGround,
                _ => ViaElement.None,
            };
            var s = barrels[k].Shape;
            result.Add(new RecognizedVia(s.X, s.Y, s.Layer, s.DrillSize, s.PadSize,
                                         barrels[k].SpanFrom, barrels[k].SpanTo, classes[k], element, ends[k].Islands));
        }
        return result;
    }

    /// <summary>The centre of a ground pad island's copper — what "nearest to the pad" is measured from.</summary>
    private static (double X, double Y) PadCentre(BoardCopper board, int[] islandOf, int island)
    {
        var box = Bbox.Empty;
        for (int p = 0; p < islandOf.Length; p++)
            if (islandOf[p] == island) box = box.Union(board.Pieces.BoundsOfPiece(p));
        return (0.5 * (box.MinX + box.MaxX), 0.5 * (box.MinY + box.MaxY));
    }

    private static double Dist2(long x, long y, (double X, double Y) c) =>
        (x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y);
}
