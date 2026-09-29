// brief-em3d-93 R-em3d93-1a — the record a result carries of which document port each of its ports is, when a port turned
// off (Model) left the rest renumbered 1…N. The notes say it too, but a Touchstone file is read by a netlist months later
// where the notes are gone (MIM-10's reason for the port lines), so the FILE carries it: one header line per port, and the
// .npy's diagnostics group a cube of the document numbers. Both writers — Palace's and openEMS's — call this, and both write
// nothing extra when no port was renumbered, so every result written before this is byte-identical.

using CircuitRF.Engine.Em3d;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

public static class Em3dPortMap
{
    /// <summary>The header line's prefix.</summary>
    public const string Prefix = "circuitRF-EM 3D port map: ";

    /// <summary>The diagnostics cube's name: each result port's number in the document.</summary>
    public const string DocumentPortCube = "DocumentPort";

    /// <summary>True when the problem's ports were renumbered from the document's.</summary>
    public static bool Renumbered(IReadOnlyList<Em3dPort> ports) => ports.Any(p => p.SourceNumber is not null);

    /// <summary>One line per result port — <c>result port 2 is the 3D view's port 3 'P3'</c> — and one saying what the gap means;
    /// none when nothing was renumbered. ASCII, as every header line is.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<Em3dPort> ports)
    {
        if (!Renumbered(ports)) return [];
        var lines = ports.OrderBy(p => p.Number)
            .Select(p => $"{Prefix}result port {p.Number} is the 3D view's port {p.SourceNumber ?? p.Number} '{Ascii(p.SourceLabel ?? p.Name)}'")
            .ToList();
        lines.Add(Prefix + "a port of the 3D view missing above was turned off (Model) for this run: it was left out entirely, " +
                  "open, not terminated.");
        return lines;
    }

    /// <summary>The document numbers as a real cube over the result's ports, in <paramref name="group"/>; nothing when nothing
    /// was renumbered.</summary>
    public static void AddTo(DataSet ds, string group, IReadOnlyList<Em3dPort> ports)
    {
        if (!Renumbered(ports)) return;
        var ordered = ports.OrderBy(p => p.Number).ToList();
        ds.AddToGroup(group, DocumentPortCube, new DataCube(
            [new Axis("port", [.. ordered.Select(p => (double)p.Number)], "port")],
            [.. ordered.Select(p => (double)(p.SourceNumber ?? p.Number))]));
    }

    private static string Ascii(string s) => new([.. s.Select(c => c < 128 ? c : '?')]);
}
