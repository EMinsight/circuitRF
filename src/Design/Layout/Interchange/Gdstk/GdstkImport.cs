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
    /// the format's limit (<see cref="GdstkMapping.MaxExpandedFor"/>), and
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
        var read = Read(path, format, worker, token);
        return StreamLayoutImport.Import(
            read.Structures, read.SourceDbuPerMicron, read.Diagnostics, GdstkSession.DisplayName(format),
            parentDir, destTech, destDbuPerMicron, preferSourceResolution, resolveLayerMapping, pinRules,
            read.LayerNames);
    }

    /// <summary>What <see cref="Read"/> gives: the same three things <see cref="GdsiiReader"/> gives
    /// <see cref="GdsiiImport"/>, and the names the file gives its layers (OASIS <c>LAYERNAME</c>, per
    /// used key; empty for GDSII).</summary>
    public sealed record ReadResult(List<InterchangeStructure> Structures, double SourceDbuPerMicron, IReadOnlyList<string> Diagnostics)
    {
        public IReadOnlyDictionary<LayerKey, string> LayerNames { get; init; } = new Dictionary<LayerKey, string>();
    }

    /// <summary>The read alone: every cell as an <see cref="InterchangeStructure"/> in the file's own
    /// database units, the file's resolution, what the read had to say, and the file's layer names.</summary>
    public static ReadResult Read(
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

        var said = notes.TakeIgnoredRecords(library.Messages);
        var diagnostics = said.Select(m => $"gdstk: {m}").Concat(notes.Finish(library.LibraryProperties)).ToList();
        return new ReadResult(structures, GridOf(library, format), diagnostics)
        {
            LayerNames = GdstkLayerNames.For(structures, library.LayerNames),
        };
    }

    /// <summary>The file's database units per micrometre. G0's Q6: gdstk's OASIS writer — and so other
    /// writers' files read through it — cannot state a 1 nm or 0.25 nm grid exactly, so 1000 arrives as
    /// 1000.0000000000001. A grid within a part in 10⁹ of a whole number IS that number; anything
    /// further off is the file's own and stays as it is.</summary>
    private static double GridOf(GdstkLibrary library, GdstkFormat format)
    {
        double grid = library.SourceDbuPerMicron;
        if (format != GdstkFormat.Oasis) return grid;
        double whole = Math.Round(grid);
        return whole > 0 && Math.Abs(grid - whole) <= 1e-9 * whole ? whole : grid;
    }
}
