// What is ground — brief-artsch-3-board-graph.md R-as3-3, overview D6; docs/design/artwork-to-schematic.md §4.2.
//
// In order of evidence: a point or a net the user names; a net the artwork itself names as ground (GND,
// AGND, 0); the piece holding the largest copper on the stackup's reference conductor; and, where the
// reference is declared but drawn nowhere, the metal the stackup says is there.
//
// ── THE GROUND NET IS NOT ALL GROUND COPPER ──────────────────────────────────────────────────────────
//
// Everything joined to the chosen piece is in the ground NET — and so is a shunt part's ground pad that
// six vias tie to the plane, and so is a shorted stub. Neither is ground copper: the vias between them
// and the plane are what the circuit needs to model. So a piece of the ground net is GROUND COPPER (a
// pour or a plane, the "body") only when it is the chosen piece, or is wider than any trace somewhere,
// or carries a row of vias and is not pad-sized — the trace review's pour rule read at the piece level
// (src/Design/Layout/Recognition/RESOLVED.md says why its via count cannot apply to a pad).

using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>How ground was chosen.</summary>
public enum GroundSource
{
    /// <summary>The copper under a point the user named.</summary>
    Point,

    /// <summary>A net the user named.</summary>
    NamedNet,

    /// <summary>A net the artwork names as ground — GND, AGND or 0.</summary>
    StatedNet,

    /// <summary>The largest copper on the stackup's ground reference conductor.</summary>
    ReferenceConductor,

    /// <summary>The ground reference the stackup declares and the artwork does not draw.</summary>
    UndrawnReference,

    /// <summary>No reference is flagged and nothing is named: the largest copper on the board.</summary>
    LargestCopper,
}

/// <summary>What was read as ground.</summary>
/// <param name="Source">How it was chosen.</param>
/// <param name="NetName">The net name the chosen copper carries, or null.</param>
/// <param name="Anchor">A point on the chosen copper; null for an undrawn reference.</param>
/// <param name="ReferenceName">The stackup's ground reference conductor, or null.</param>
public sealed record GroundChoice(
    GroundSource Source, string? NetName, RecognitionAnchor? Anchor, string? ReferenceName)
{
    /// <summary>The partition's net indices read as ground.</summary>
    internal IReadOnlySet<int> Nets { get; init; } = new HashSet<int>();

    /// <summary>The chosen piece, or -1.</summary>
    internal int AnchorPiece { get; init; } = -1;

    /// <summary>Per partition piece: true where it is ground copper — a pour or a plane, not merely
    /// on the ground net.</summary>
    internal bool[] Body { get; init; } = [];

    /// <summary>Per partition piece: on the ground net, pad-sized and not ground copper — a
    /// candidate ground pad.</summary>
    internal bool[] PadCandidate { get; init; } = [];
}

/// <summary>D6, written once.</summary>
public static class BoardGround
{
    /// <summary>The names the artwork may state ground by, case-insensitively.</summary>
    public static readonly IReadOnlyList<string> GroundNames = ["GND", "AGND", "0"];

    /// <summary>
    /// The trace review's via count for a pour, read at the piece level: copper carrying this many
    /// vias and longer than a pad is a ground strip.
    /// </summary>
    private const int PourViaCount = Em.TraceImpedanceAnalysis.PourViaCount;

    internal static GroundChoice? Read(
        BoardCopper board, RecognitionOptions options, RailLengthFormat fmt, out string? refusal)
    {
        refusal = null;
        var pieces = board.Pieces;
        int anchor = -1;
        GroundSource source;
        var nets = new HashSet<int>();

        if (options.GroundAt is { } at)
        {
            anchor = board.ConductorAt(at.X, at.Y, null);
            if (anchor < 0)
            {
                refusal = $"The ground point {fmt.Point(at.X, at.Y)} lands on no copper. Pick a point on the copper " +
                          "that is ground, or leave ground to the recognition.";
                return null;
            }
            source = GroundSource.Point;
            nets.Add(pieces.NetOfPiece(anchor));
        }
        else if (options.GroundNet is { Length: > 0 } named)
        {
            anchor = LargestOf(board, i => string.Equals(pieces.NameOfNet(pieces.NetOfPiece(i)), named, StringComparison.OrdinalIgnoreCase));
            if (anchor < 0)
            {
                string stated = pieces.Nets.Count == 0 ? "This artwork states no net names at all."
                    : $"The nets it states are: {string.Join(", ", pieces.Nets.Take(12))}{(pieces.Nets.Count > 12 ? ", …" : "")}.";
                refusal = $"No copper on this board carries the net '{named}'. {stated}";
                return null;
            }
            source = GroundSource.NamedNet;
            AddNetsNamed(board, named, nets);
        }
        else if (LargestOf(board, i => IsGroundName(pieces.NameOfNet(pieces.NetOfPiece(i)))) is var stated and >= 0)
        {
            anchor = stated;
            source = GroundSource.StatedNet;
            foreach (string n in GroundNames) AddNetsNamed(board, n, nets);
        }
        else if (board.ReferenceName is { } reference && !board.UndrawnReference
                 && board.Tech.Stackup.Layers.First(l => l.Name == reference && l.IsGroundReference).DrawingLayers is var refLayers
                 && LargestOf(board, i => refLayers.Contains(pieces.LayerOfPiece(i))) is var onRef and >= 0)
        {
            anchor = onRef;
            source = GroundSource.ReferenceConductor;
            nets.Add(pieces.NetOfPiece(anchor));
        }
        else if (board.UndrawnReference)
        {
            source = GroundSource.UndrawnReference;
        }
        else
        {
            anchor = LargestOf(board, _ => true);
            if (anchor < 0) { refusal = "The artwork has no copper on any conductor of the stackup."; return null; }
            source = GroundSource.LargestCopper;
            nets.Add(pieces.NetOfPiece(anchor));
        }

        // The stackup's undrawn reference is metal at ground wherever it is declared: every net a barrel
        // carried down to it is ground, whichever way the rest of ground was chosen.
        if (board.UndrawnReference) nets.UnionWith(pieces.Ground.Nets);

        // ── ground copper, piece by piece ──────────────────────────────────────────────────────────
        var body = new bool[pieces.Count];
        var pad = new bool[pieces.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            if (!board.IsConductor[i] || !nets.Contains(pieces.NetOfPiece(i))) continue;
            bool padShaped = board.IsPadShaped(i);
            body[i] = i == anchor || board.IsWide(i) || (board.ViaCountOnPiece[i] >= PourViaCount && !padShaped);
            pad[i] = !body[i] && padShaped;
        }

        RecognitionAnchor? point = null;
        if (anchor >= 0)
        {
            var (x, y) = board.ProbeOf(anchor);
            point = new RecognitionAnchor(x, y, pieces.LayerOfPiece(anchor));
        }

        return new GroundChoice(source, anchor >= 0 ? pieces.NameOfNet(pieces.NetOfPiece(anchor)) : null, point, board.ReferenceName)
        {
            Nets = nets, AnchorPiece = anchor, Body = body, PadCandidate = pad,
        };
    }

    /// <summary>The sentence the report states the choice in.</summary>
    internal static string Describe(GroundChoice g, BoardCopper board, RailLengthFormat fmt)
    {
        string where = g.Anchor is { } a ? $" at {fmt.Point(a.X, a.Y)}" : "";
        string layer = g.Anchor is { Layer: { } l } ? $" on {LayerName(board.Tech, l)}" : "";
        return g.Source switch
        {
            GroundSource.Point => $"Ground is the copper{layer}{where}, as asked.",
            GroundSource.NamedNet => $"Ground is the net '{g.NetName}', as asked.",
            GroundSource.StatedNet => $"Ground is the net '{g.NetName}', which the artwork names.",
            GroundSource.ReferenceConductor => $"Ground is the largest copper on the reference conductor '{g.ReferenceName}'{where}, and everything joined to it.",
            GroundSource.UndrawnReference => $"Ground is the reference conductor '{g.ReferenceName}', which the stackup declares and the artwork does not draw.",
            _ => $"No ground is named and the stackup flags no reference conductor, so the largest copper{layer}{where} was read as ground.",
        };
    }

    internal static string LayerName(Technology tech, LayerKey key) =>
        tech.Layers.FirstOrDefault(l => l.Key == key)?.Name is { Length: > 0 } n ? n : $"layer {key.Layer}/{key.Datatype}";

    private static bool IsGroundName(string? name) =>
        name is not null && GroundNames.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

    private static void AddNetsNamed(BoardCopper board, string name, HashSet<int> nets)
    {
        var pieces = board.Pieces;
        for (int i = 0; i < pieces.Count; i++)
            if (string.Equals(pieces.NameOfNet(pieces.NetOfPiece(i)), name, StringComparison.OrdinalIgnoreCase))
                nets.Add(pieces.NetOfPiece(i));
    }

    /// <summary>The conductor piece of largest area among those <paramref name="wanted"/> admits, or -1.</summary>
    private static int LargestOf(BoardCopper board, Func<int, bool> wanted)
    {
        int best = -1;
        double bestArea = -1;
        for (int i = 0; i < board.Pieces.Count; i++)
        {
            if (!board.IsConductor[i] || !wanted(i)) continue;
            double a = board.Area(i);
            if (a > bestArea) { best = i; bestArea = a; }
        }
        return best;
    }
}
