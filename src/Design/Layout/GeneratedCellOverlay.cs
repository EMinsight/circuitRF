using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

/// <summary>
/// Generated cells that exist for ONE headless run and are never written — what lets a read-only
/// verb (<c>check</c>, <c>explain</c>, <c>render</c>, <c>lvs</c>) resolve a placed PCell whose
/// <c>.generated-cells</c> folder is absent without writing anything to disk
/// (brief-generated-cells-2 R-gc2-2).
///
/// <para><b>Two kinds of entry.</b> A CELL is the artwork and the <c>.ccell</c> the generator would
/// have written, keyed by the folder it would have been written to. A REDIRECT points a stale cell
/// folder — the name a layout's snapshot recorded before its generator or technology changed — at
/// the name the same snapshot builds today. The GUI answers that case by rewriting the layout's
/// instances and saving it (<c>GeneratedCellsLifecycle.Regenerate</c>); a headless run must not edit
/// a document nobody asked it to edit, so it redirects instead and the artwork is the same.</para>
///
/// <para><b>Consulted by the readers that resolve a cell folder</b> — <see cref="CellLayoutResolver"/>
/// and <see cref="TerminalMap"/> — and by nothing that writes. The GUI never populates it, so an
/// open workspace resolves exactly as it always has. Process-wide because those two readers are;
/// a run removes its own entries when it ends (<c>GeneratedCellsRun.Dispose</c>), which is what keeps
/// the long-lived <c>serve</c> process from answering one call with another call's cells.</para>
/// </summary>
public static class GeneratedCellOverlay
{
    private sealed record Cell(LayoutView View, CcellFile Ccell);

    private static readonly Dictionary<string, Cell> _cells = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _redirects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _gate = new();

    /// <summary>Holds <paramref name="view"/> as the primary layout of the cell folder
    /// <paramref name="cellAbsDir"/>, which need not exist.</summary>
    public static void Put(string cellAbsDir, LayoutView view, CcellFile ccell)
    {
        lock (_gate) _cells[Key(cellAbsDir)] = new Cell(view, ccell);
    }

    /// <summary>Resolves <paramref name="staleCellAbsDir"/> as <paramref name="currentCellAbsDir"/>
    /// from now on.</summary>
    public static void Redirect(string staleCellAbsDir, string currentCellAbsDir)
    {
        string from = Key(staleCellAbsDir), to = Key(currentCellAbsDir);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate) _redirects[from] = to;
    }

    /// <summary>The folder <paramref name="cellAbsDir"/> resolves as — itself unless redirected.</summary>
    public static string Follow(string cellAbsDir)
    {
        lock (_gate)
        {
            if (_redirects.Count == 0) return cellAbsDir;
            return _redirects.TryGetValue(Key(cellAbsDir), out var to) ? to : cellAbsDir;
        }
    }

    /// <summary>The in-memory primary layout of <paramref name="cellAbsDir"/>, if one is held.</summary>
    public static bool TryGetView(string cellAbsDir, out LayoutView view)
    {
        lock (_gate)
        {
            if (_cells.Count > 0 && _cells.TryGetValue(Key(cellAbsDir), out var cell)) { view = cell.View; return true; }
        }
        view = null!;
        return false;
    }

    /// <summary>The in-memory <c>.ccell</c> of <paramref name="cellAbsDir"/>, if one is held.</summary>
    public static bool TryGetCcell(string cellAbsDir, out CcellFile ccell)
    {
        lock (_gate)
        {
            if (_cells.Count > 0 && _cells.TryGetValue(Key(cellAbsDir), out var cell)) { ccell = cell.Ccell; return true; }
        }
        ccell = null!;
        return false;
    }

    /// <summary>Whether any cell or redirect is held — the readers' fast path.</summary>
    public static bool IsEmpty
    {
        get { lock (_gate) return _cells.Count == 0 && _redirects.Count == 0; }
    }

    /// <summary>Drops the given cells and redirects (by the folder each was keyed on).</summary>
    public static void Remove(IEnumerable<string> cellAbsDirs)
    {
        lock (_gate)
            foreach (string dir in cellAbsDirs)
            {
                string key = Key(dir);
                _cells.Remove(key);
                _redirects.Remove(key);
            }
    }

    private static string Key(string dir)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { return dir; }
    }
}
