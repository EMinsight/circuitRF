// What a schematic created from artwork records about where it came from — brief-artsch-6 R-as6-5,
// overview D12. Plain data, persisted in the .csch as its `ArtworkSource` block exactly as written here.
//
// Its PRESENCE is what marks a schematic as Create Schematic from Artwork's to replace (D4): a re-run
// rewrites a schematic carrying one, and refuses one that does not. Nothing in a run reads it.

namespace CircuitRF.Design.Schematic;

/// <summary>A recognised schematic's provenance (D12).</summary>
public sealed class ArtworkProvenance
{
    /// <summary>The source <c>.clay</c>, relative to the schematic, forward slashes.</summary>
    public string Layout { get; set; } = "";

    /// <summary><c>whole</c>, <c>rectangle</c> or <c>polygons</c>.</summary>
    public string Scope { get; set; } = "whole";

    /// <summary>The scope's rings in the layout's DBU, each a flat x0,y0,x1,y1,… list; absent for the whole
    /// layout.</summary>
    public List<long[]>? Rings { get; set; }

    /// <summary>The options the recognition ran with, as name → text — what a re-run needs to say the same thing.</summary>
    public SortedDictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The SHA-256 of the parts table's CSV, lower-case hex, when one was read.</summary>
    public string? PartsCsvSha256 { get; set; }

    /// <summary>The circuitRF version that wrote it (the <c>VERSION</c> file, through the assembly).</summary>
    public string Version { get; set; } = "";

    /// <summary>When, UTC, to the second — <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public string CreatedUtc { get; set; } = "";
}

/// <summary>An artwork anchor's points, DBU — a point for a part or a discontinuity, a polyline for a line (D12).</summary>
public static class ArtworkAnchors
{
    /// <summary>The anchor's middle: the point itself, or the polyline's middle vertex pair averaged — what a
    /// drawing hint and a cross-probe centre on.</summary>
    public static (long X, long Y)? Centre(IReadOnlyList<(long X, long Y)> anchor)
    {
        if (anchor.Count == 0) return null;
        if (anchor.Count == 1) return anchor[0];
        var a = anchor[(anchor.Count - 1) / 2];
        var b = anchor[anchor.Count / 2];
        return ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }
}
