// Export through gdstk (brief-oasis-gdstk.md §6b, R-oas-2b). The plan is GdsiiExport.Analyze's — the
// hierarchy walk, the structure names and the fidelity counts are format-agnostic — and the elements are
// StreamLowering's, the same ones GdsiiWriter serialises. Only the last step differs: the elements go to
// the gdstk worker, which writes the file. So the two routes' plans are the same numbers by construction.

using System.Text.Json.Nodes;

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

public static class GdstkExport
{
    /// <summary>
    /// Writes <paramref name="plan"/> to <paramref name="filePath"/> through the gdstk worker, in
    /// <paramref name="format"/> with the format's <paramref name="options"/> (null: the worker's defaults).
    /// Returns the lowering's counts — the plan's — with whatever this route adds to say.
    ///
    /// <para>Refuses, writing nothing: a plan that cannot write (<see cref="GdsiiExportException"/>, as
    /// <see cref="GdsiiExport.Write"/>); and a reference to a cell the export does not hold
    /// (<see cref="GdstkException"/>), which circuitRF's own writer writes as a dangling name but gdstk
    /// cannot. The worker writes to a temporary name and renames it, so a failure part-way leaves no
    /// partial file either.</para>
    /// </summary>
    public static GdsiiExportSummary Write(
        string filePath, GdsiiExport.ExportPlan plan, GdstkFormat format = GdstkFormat.Gdsii, JsonObject? options = null,
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
        int handle = session.BeginWrite(format, plan.Units.UserUnitMeters, plan.Units.DbUnitMeters, options, token);
        foreach (var cell in cells)
            session.AddCell(handle, cell, filePath, token);
        var result = session.FinishWrite(handle, filePath, token);

        var diagnostics = new List<string>(library.Diagnostics);
        if (portLabels > 0)
            diagnostics.Add($"{portLabels} port label(s) written as plain text: the gdstk route writes no properties, " +
                            "and the port flag is one.");
        diagnostics.AddRange(result.Messages.Select(m => $"gdstk: {m}"));

        return new GdsiiExportSummary(
            library.CurvedShapesFlattened, library.HolesKeyholed, library.BitmapsSkipped, diagnostics,
            library.LabelRecordsWritten, library.ViaPadsSkipped, library.LayersRenumbered);
    }
}
