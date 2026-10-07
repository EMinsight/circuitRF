// Import through gdstk (brief-oasis-gdstk.md §6a, R-oas-2a): the worker reads the bytes, GdstkMapping turns
// each cell into an InterchangeStructure, and StreamLayoutImport does everything after — the SAME function
// GdsiiImport ends in. Every cell is read before anything is created, so a worker that crashes, hangs,
// refuses or runs out of memory part-way through a file leaves no cell folder behind.

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

public static class GdstkImport
{
    /// <summary>
    /// Imports every cell of <paramref name="path"/> (<paramref name="format"/>: GDSII or OASIS) as a real
    /// cell folder under <paramref name="parentDir"/>. Every other parameter is
    /// <see cref="GdsiiImport.Import"/>'s, with the same meaning. Throws <see cref="GdstkException"/> when
    /// there is no worker, it fails (its sentence names the file) or a repetition would expand past
    /// <see cref="GdstkMapping.MaxExpanded"/>, and
    /// <see cref="OperationCanceledException"/> on <paramref name="token"/> — in every case having created
    /// nothing.
    /// </summary>
    public static GdsiiImport.ImportResult Import(
        string path,
        GdstkFormat format,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        bool preferSourceResolution,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        PinInferenceRules? pinRules = null,
        GdstkWorkerOptions? worker = null,
        CancellationToken token = default)
    {
        var (structures, sourceDbuPerMicron, diagnostics) = Read(path, format, worker, token);
        return StreamLayoutImport.Import(
            structures, sourceDbuPerMicron, diagnostics, GdstkSession.DisplayName(format),
            parentDir, destTech, destDbuPerMicron, preferSourceResolution, resolveLayerMapping, pinRules);
    }

    /// <summary>The read alone: every cell as an <see cref="InterchangeStructure"/> in the file's own
    /// database units, the file's resolution, and what the read had to say — the same three things
    /// <see cref="GdsiiReader"/> gives <see cref="GdsiiImport"/>.</summary>
    public static (List<InterchangeStructure> Structures, double SourceDbuPerMicron, IReadOnlyList<string> Diagnostics) Read(
        string path, GdstkFormat format, GdstkWorkerOptions? worker = null, CancellationToken token = default)
    {
        using var w = GdstkWorker.Start(worker, token);
        var session = new GdstkSession(w);
        var library = session.Open(path, format, token);

        var notes = new GdstkMapping.ImportNotes(format);
        var structures = new List<InterchangeStructure>(library.Cells.Count);
        foreach (var cell in library.Cells)
            structures.Add(GdstkMapping.ToInterchange(session.ReadCell(library, cell.Name, token), notes));
        session.Close(library, token);

        var diagnostics = library.Messages.Select(m => $"gdstk: {m}").Concat(notes.Finish(library.LibraryProperties)).ToList();
        return (structures, library.SourceDbuPerMicron, diagnostics);
    }
}
