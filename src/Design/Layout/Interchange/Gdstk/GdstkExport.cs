// Export through gdstk (brief-oasis-gdstk.md §6b, R-oas-2b). The plan is GdsiiExport.Analyze's — the
// hierarchy walk, the structure names and the fidelity counts are format-agnostic — and the elements are
// StreamLowering's, the same ones GdsiiWriter serialises. Only the last step differs: the elements go to
// the gdstk worker, which writes the file. So the two routes' plans are the same numbers by construction.

using System.Text.Json.Nodes;

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

/// <summary>How an OASIS file's END record lets a reader check its bytes (SEMI P39 §35).</summary>
public enum OasisValidation { None, Crc32, Checksum32 }

/// <summary>
/// The OASIS writer's options (brief-oasis-gdstk.md §9b, R-oas-4b) — the four the export dialog offers and
/// remembers per user, at the brief's defaults. <c>circle_tolerance</c> is not one: it stays 0 in the
/// worker, so no OASIS <c>CIRCLE</c> is ever written — circles reach gdstk already flattened by the shared
/// lowering, exactly as for GDSII.
/// </summary>
public sealed record OasisWriteOptions(
    int CompressionLevel = 6,
    bool DetectRectanglesAndTrapezoids = true,
    OasisValidation Validation = OasisValidation.Crc32,
    bool StandardProperties = false)
{
    public static OasisWriteOptions Default { get; } = new();

    /// <summary>The deflate level CBLOCKs are written at: 0 (stored, no compression) to 9.</summary>
    public int CompressionLevel { get; init; } = CompressionLevel is >= 0 and <= 9
        ? CompressionLevel
        : throw new ArgumentOutOfRangeException(nameof(CompressionLevel), CompressionLevel, "The OASIS compression level is 0 to 9.");

    /// <summary>The <c>begin-write</c> options, in the worker's names.</summary>
    public JsonObject ToJson() => new()
    {
        ["compression_level"] = CompressionLevel,
        ["detect_rectangles"] = DetectRectanglesAndTrapezoids,
        ["detect_trapezoids"] = DetectRectanglesAndTrapezoids,
        ["validation"] = Validation switch
        {
            OasisValidation.None => "none",
            OasisValidation.Checksum32 => "checksum32",
            _ => "crc32",
        },
        ["standard_properties"] = StandardProperties,
    };
}

/// <summary>
/// What an OASIS file cannot hold of a lowered layout (G0's Q5, conditions 1 and 2): a round path end,
/// which OASIS has no extension scheme for and gdstk writes flush; an odd path width, which OASIS stores
/// as a half-width and gdstk rounds up; and a label's rotation or size, which OASIS TEXT carries neither
/// of. Counted from the elements the write sends, so the export dialog and the write's messages are the
/// same numbers, and none of the three is ever silent.
/// </summary>
public sealed record OasisLosses(int RoundEnds, int OddWidths, int LabelTransforms)
{
    public static OasisLosses Of(StreamLibrary library)
    {
        int round = 0, odd = 0, labels = 0;
        foreach (var element in library.Structures.SelectMany(s => s.Shapes))
        {
            switch (element)
            {
                case StreamPath p:
                    if (p.End == PathEndStyle.Round) round++;
                    if (p.Width % 2 != 0) odd++;
                    break;
                case StreamLabel l when l.AngleDegrees != 0 || l.Height != GdstkMapping.HeightPerMagnification:
                    labels++;
                    break;
            }
        }
        return new OasisLosses(round, odd, labels);
    }

    /// <summary>The losses of <paramref name="plan"/>'s write, before it is written: the export dialog's lines.</summary>
    public static OasisLosses Of(GdsiiExport.ExportPlan plan) => Of(StreamLowering.Lower(plan.Structures, plan.Tech));

    public IReadOnlyList<string> Messages()
    {
        var m = new List<string>();
        if (RoundEnds > 0)
            m.Add($"{RoundEnds} path(s) with round ends will be written with flush ends: OASIS has no round path end.");
        if (OddWidths > 0)
            m.Add($"{OddWidths} path(s) of odd width will be written one database unit wider: OASIS stores a path's half-width.");
        if (LabelTransforms > 0)
            m.Add($"{LabelTransforms} label(s) will lose their rotation or size: OASIS text carries neither, so they read back upright at the default height.");
        return m;
    }
}

public static class GdstkExport
{
    /// <summary>
    /// Writes <paramref name="plan"/> to <paramref name="filePath"/> through the gdstk worker, in
    /// <paramref name="format"/>; an OASIS write takes <paramref name="oasis"/> (null: the defaults), and
    /// reports <see cref="OasisLosses"/> among its messages.
    /// Returns the lowering's counts — the plan's — with whatever this route adds to say.
    ///
    /// <para>Refuses, writing nothing: a plan that cannot write (<see cref="GdsiiExportException"/>, as
    /// <see cref="GdsiiExport.Write"/>); and a reference to a cell the export does not hold
    /// (<see cref="GdstkException"/>), which circuitRF's own writer writes as a dangling name but gdstk
    /// cannot. The worker writes to a temporary name and renames it, so a failure part-way leaves no
    /// partial file either.</para>
    /// </summary>
    public static GdsiiExportSummary Write(
        string filePath, GdsiiExport.ExportPlan plan, GdstkFormat format = GdstkFormat.Gdsii, OasisWriteOptions? oasis = null,
        GdstkWorkerOptions? worker = null, CancellationToken token = default)
    {
        if (!plan.CanWrite) throw new GdsiiExportException(plan.CoordinateOverflowOffenders);
        var library = StreamLowering.Lower(plan.Structures, plan.Tech);

        var held = new HashSet<string>(library.Structures.Select(s => s.Name), StringComparer.Ordinal);
        var dangling = library.Structures
            .SelectMany(s => s.References.Where(r => !held.Contains(r.Cell)).Select(r => $"{s.Name} → \"{r.Cell}\""))
            .Distinct().ToList();
        if (dangling.Count > 0)
            throw new GdstkException(GdstkFailure.Refused,
                GdstkDiagnostics.DanglingReference(GdstkSession.DisplayName(format), string.Join("; ", dangling)), "write.missing-cell");

        int portLabels = 0;
        var cells = library.Structures.Select(s => GdstkMapping.FromStream(s, ref portLabels)).ToList();

        using var w = GdstkWorker.Start(worker, token);
        var session = new GdstkSession(w);
        var options = format == GdstkFormat.Oasis ? (oasis ?? OasisWriteOptions.Default).ToJson() : null;
        int handle = session.BeginWrite(format, plan.Units.UserUnitMeters, plan.Units.DbUnitMeters, options, token);
        foreach (var cell in cells)
            session.AddCell(handle, cell, filePath, token);
        var result = session.FinishWrite(handle, filePath, token);

        var diagnostics = new List<string>(library.Diagnostics);
        if (format == GdstkFormat.Oasis) diagnostics.AddRange(OasisLosses.Of(library).Messages());
        if (portLabels > 0)
            diagnostics.Add($"{portLabels} port label(s) written as plain text: the gdstk route writes no properties, " +
                            "and the port flag is one.");
        diagnostics.AddRange(result.Messages.Select(m => $"gdstk: {m}"));

        return new GdsiiExportSummary(
            library.CurvedShapesFlattened, library.HolesKeyholed, library.BitmapsSkipped, diagnostics,
            library.LabelRecordsWritten, library.ViaPadsSkipped, library.LayersRenumbered);
    }
}
