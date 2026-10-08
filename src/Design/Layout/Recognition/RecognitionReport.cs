// What a recognition guessed and what it left out — brief-artsch-3-board-graph.md R-as3-8, overview D16.
//
// A list of FINDINGS, never free text alone: each carries its class, a count, one sentence and the
// places it is about, so the GUI can expand a class and cross-probe its anchors and the CLI can print one
// line per class. A recognition is never refused for being imperfect; this is where the imperfection
// goes instead.

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>The kinds of thing a recognition reports. Later phases add their own.</summary>
public enum RecognitionFindingClass
{
    /// <summary>Which copper was read as ground, and how it was chosen (D6).</summary>
    GroundChosen,

    /// <summary>Ground-to-ground vias — stitching, fences, plane ties — dropped (D7).</summary>
    StitchingViasDropped,

    /// <summary>Vias to ground kept as <c>VIAGND</c> (or <c>GND</c> under <c>vias=ground</c>).</summary>
    GroundViasKept,

    /// <summary>A ground pad's vias past the nearest few, counted and not modelled.</summary>
    GroundViasCapped,

    /// <summary>Several <c>VIAGND</c>s on one pad: their mutual inductance is not modelled. Said once.</summary>
    GroundViaCouplingNotModelled,

    /// <summary>Signal-to-signal vias between layers kept as <c>VIA</c>.</summary>
    SignalViasKept,

    /// <summary>Vias whose barrel meets one conductor or none — they join nothing.</summary>
    ViasJoiningNothing,

    /// <summary>A pour galvanically separate from ground, read as a signal island.</summary>
    SeparatePour,

    /// <summary>Copper touching no part, port or line — a test point, a logo, a fiducial.</summary>
    CopperReadAsNothing,

    /// <summary>One piece of copper carrying two different net names; it took neither.</summary>
    ConflictingNetNames,

    /// <summary>Ports from the layout's EM setup (R-as3-6, priority 1).</summary>
    PortsFromEmSetup,

    /// <summary>Ports from port labels (priority 2).</summary>
    PortsFromLabels,

    /// <summary>Ports from layout pins (priority 2).</summary>
    PortsFromPins,

    /// <summary>Ports at a multi-pin part's pads (priority 3, D9).</summary>
    PortsAtMultiPinParts,

    /// <summary>Ports where a line reaches the board outline (priority 4).</summary>
    PortsAtBoardEdge,

    /// <summary>Ports at a connector footprint (priority 4).</summary>
    PortsAtConnectors,

    /// <summary>Ports where the scope's boundary cuts a signal island (priority 5, D11).</summary>
    PortsAtScopeCut,

    /// <summary>A port label, pin or pad that lands on no signal copper, and so is no port.</summary>
    PortsNotOnSignalCopper,

    /// <summary>The scope cuts a ground pour or plane; ground was read from the whole board.</summary>
    ScopeCutGround,
}

/// <summary>A place a finding is about, DBU, with the drawing layer when one is known.</summary>
public readonly record struct RecognitionAnchor(long X, long Y, LayerKey? Layer = null);

/// <summary>One class of finding: how many, one sentence, and where.</summary>
public sealed record RecognitionFinding(
    RecognitionFindingClass Class, int Count, string Sentence, IReadOnlyList<RecognitionAnchor> Anchors);

/// <summary>Every finding of one recognition, in the order they were made.</summary>
public sealed class RecognitionReport
{
    private readonly List<RecognitionFinding> _findings = [];

    /// <summary>The findings.</summary>
    public IReadOnlyList<RecognitionFinding> Findings => _findings;

    /// <summary>The finding of <paramref name="cls"/>, or null when nothing of that class was found.</summary>
    public RecognitionFinding? Of(RecognitionFindingClass cls) => _findings.FirstOrDefault(f => f.Class == cls);

    /// <summary>The count stated for <paramref name="cls"/>; zero when there is no such finding.</summary>
    public int Count(RecognitionFindingClass cls) => Of(cls)?.Count ?? 0;

    internal void Add(RecognitionFindingClass cls, int count, string sentence, IReadOnlyList<RecognitionAnchor>? anchors = null)
    {
        if (count <= 0) return;
        _findings.Add(new RecognitionFinding(cls, count, sentence, anchors ?? []));
    }

    /// <summary>One line per class — the CLI's stderr form.</summary>
    public IEnumerable<string> Lines() => _findings.Select(f => f.Sentence);
}
