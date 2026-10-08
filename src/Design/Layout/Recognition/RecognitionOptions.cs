// What a user (or an agent) may say about a recognition before it runs — brief-artsch-3-board-graph.md
// R-as3-1, R-as3-3 and R-as3-4; docs/design/artwork-to-schematic.md §4.
//
// Every option here is one the GUI dialog and the CLI both expose under the same meaning, which is why
// it is a record below the firewall and not a view-model property: a rule that lives only in the dialog
// is a rule the CLI does not apply (overview D2).

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What a via to ground becomes — overview D7's policy switch.</summary>
public enum ViaPolicy
{
    /// <summary>A via to ground that matters is a <c>VIAGND</c>, stackup-bound — the default.</summary>
    Model,

    /// <summary>Every <c>VIAGND</c> becomes a plain <c>GND</c>: the "regular ground as an
    /// alternative" the owner asked for.</summary>
    Ground,
}

/// <summary>The options of one recognition.</summary>
public sealed record RecognitionOptions
{
    /// <summary>A net to read as ground instead of the one D6 would choose — <c>--ground &lt;net&gt;</c>.
    /// A name the artwork does not state is a refusal naming it.</summary>
    public string? GroundNet { get; init; }

    /// <summary>A point, DBU, whose copper is ground — the dialog's click on copper. A point on no
    /// copper is a refusal naming the point. Wins over <see cref="GroundNet"/>.</summary>
    public (long X, long Y)? GroundAt { get; init; }

    /// <summary>D7's switch.</summary>
    public ViaPolicy Vias { get; init; } = ViaPolicy.Model;

    /// <summary>At most this many <c>VIAGND</c>s per ground pad, the nearest to it; the rest are
    /// counted (D7).</summary>
    public int MaxGroundViasPerPad { get; init; } = DefaultMaxGroundViasPerPad;

    /// <summary>Copper within this of the board outline reaches it, µm — an edge launch stops a little
    /// short of the routed edge, never exactly on it.</summary>
    public double EdgeReachMicrons { get; init; } = DefaultEdgeReachMicrons;

    /// <summary>A port found within this of a higher-priority port on the same island is the same
    /// port, µm (R-as3-6's priority order).</summary>
    public double PortMergeMicrons { get; init; } = DefaultPortMergeMicrons;

    public const int DefaultMaxGroundViasPerPad = 4;
    public const double DefaultEdgeReachMicrons = 500;
    public const double DefaultPortMergeMicrons = 1000;
}
