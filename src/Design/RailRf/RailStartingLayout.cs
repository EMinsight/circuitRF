// Which layout Tools ▸ railRF starts on (field report, 2026-10-01).
//
// A workspace holding a board opened railRF on an empty window, and the designer's question was why
// it did not start on the board that was right there. The rule is the one a user would state:
//
//   1. the layout in front of them — the active tab, when it is a saved layout;
//   2. else the one layout they have open, when exactly one is;
//   3. else the workspace's one layout, when it holds exactly one;
//   4. else none — several candidates and nothing saying which is a choice, not an inference, so the
//      window starts empty and NAMES them (Open picks one).
//
// Headless so the rule has a gate that needs no window.

using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.RailRf;

/// <summary>The layout a fresh railRF window starts on, or why it starts on none.</summary>
/// <param name="Path">The <c>.clay</c> to open, or null.</param>
/// <param name="Note">Said when there were several and none was chosen; null otherwise.</param>
public sealed record RailStartingLayout(string? Path, string? Note)
{
    /// <summary>How deep the workspace is walked for layouts — a cell folder is two levels down
    /// (<c>Cell/layout/Cell.clay</c>), a library of cells a few more.</summary>
    public const int MaxDepth = 6;

    /// <summary>How many layouts the note names before it says "and N more".</summary>
    private const int Named = 5;

    /// <summary>Chooses — see this file's header for the rule.</summary>
    /// <param name="activeLayout">The active tab's <c>.clay</c>, or null when the active tab is not a
    /// saved layout.</param>
    /// <param name="openLayouts">Every saved layout open in the workspace window.</param>
    /// <param name="workspaceRoot">The open workspace's folder, or null with none open.</param>
    public static RailStartingLayout Choose(string? activeLayout, IEnumerable<string?> openLayouts, string? workspaceRoot)
    {
        if (Existing(activeLayout) is { } active) return new(active, null);

        var open = openLayouts.Select(Existing).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (open.Count == 1) return new(open[0], null);

        var inWorkspace = workspaceRoot is null ? [] : LayoutsUnder(workspaceRoot);
        if (inWorkspace.Count == 1) return new(inWorkspace[0], null);

        var candidates = open.Count > 1 ? open : inWorkspace;
        if (candidates.Count == 0) return new(null, null);

        var names = candidates.Select(p => System.IO.Path.GetFileNameWithoutExtension(p)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        string list = string.Join(", ", names.Take(Named)) + (names.Count > Named ? $" and {names.Count - Named} more" : "");
        return new(null, $"{(open.Count > 1 ? "Open in this workspace" : "This workspace holds")} {names.Count} layouts " +
                         $"({list}) — Open picks the board to start on.");
    }

    /// <summary>Every <c>.clay</c> under <paramref name="root"/>, bounded at <see cref="MaxDepth"/>, never
    /// descending into a reserved folder (generated cells, history), a dot-folder or a symlinked
    /// directory.</summary>
    public static IReadOnlyList<string> LayoutsUnder(string root)
    {
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.clay");
                dirs = Directory.EnumerateDirectories(dir);
                found.AddRange(files);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

            if (depth >= MaxDepth) return;
            foreach (string sub in dirs)
            {
                string name = System.IO.Path.GetFileName(sub);
                if (name.StartsWith('.') || ReservedFolders.IsReserved(sub)) continue;
                try { if (new DirectoryInfo(sub).LinkTarget is not null) continue; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                Walk(sub, depth + 1);
            }
        }
        if (Directory.Exists(root)) Walk(root, 0);
        return found;
    }

    private static string? Existing(string? path) =>
        path is { Length: > 0 } p && File.Exists(p) ? System.IO.Path.GetFullPath(p) : null;
}
