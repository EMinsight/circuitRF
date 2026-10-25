using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Workspace;

/// <summary>
/// Which cell folders a workspace holds, and where the one called <c>&lt;name&gt;</c> is.
///
/// <para><b>By SHAPE, not by a manifest.</b> A cell folder is legal with no <c>.ccell</c> at all —
/// primacy is implicit when a view sub-folder holds exactly one file — so the test is
/// <see cref="LooksLikeCellFolder"/>, the same one <c>check</c>'s own walk uses. A scan keyed on the
/// manifest would miss a perfectly ordinary cell.</para>
///
/// <para><b>A nested workspace is somebody else's tree</b> and is not descended into, for the reason
/// <c>Check.WalkFolder</c> gives: two workspaces have different default technologies, and a cell
/// attributed to the wrong one is worse than a cell not found.</para>
///
/// <para><b>Ambiguity is reported, never resolved by taking the first.</b> Two cells of one name in
/// one tree is a real state — a library folder and a design folder both holding <c>Stage1</c> — and
/// picking one silently renders a picture of the wrong cell.</para>
///
/// <para><b>A folder the GUI hides is hidden here too</b> (RND-3 R-rnd3-4), through
/// <see cref="ReservedFolders"/> — the one place those names are written down, shared with the project
/// tree's own scanner. <c>.generated-cells</c> is the case that matters: it holds one
/// content-addressed cell folder per distinct PCell placement, it can hold hundreds, and none of them
/// is a cell anyone authored. It is a DEFAULT, not a wall — <c>includeGenerated</c> is what
/// <c>explain --cells --all</c> passes, because a caller debugging a PCell genuinely does want to
/// reach one.</para>
///
/// <para>Separate from <c>Render</c> because it is the answer <c>explain --cells</c> gives (RND-3),
/// and a second copy of it there would be free to disagree with what <c>render</c> actually drew.
/// <b>In <c>src/Design</c> rather than <c>src/Cli</c></b> because a loose schematic's cell reference
/// is looked up by name in the workspace it is run from (<see cref="ExternalCellRef.FindInHostWorkspace"/>),
/// and that lookup must find exactly the cells <c>explain --cells</c> lists.</para>
/// </summary>
public static class CellLookup
{
    /// <summary>How deep a scan goes below the workspace root. Deep enough for the folder-per-library
    /// arrangements circuitRF's own workspaces use, shallow enough that a workspace sitting above a
    /// large unrelated tree does not turn a name lookup into a full disk walk.</summary>
    private const int MaxDepth = 6;

    /// <summary>Every cell folder path under <paramref name="root"/>, in a stable order.</summary>
    /// <param name="includeGenerated">Whether to descend into the reserved <c>.generated-cells</c>
    /// folder. False everywhere but <c>explain --cells --all</c>.</param>
    public static IReadOnlyList<string> All(string root, bool includeGenerated = false)
    {
        var found = new List<string>();
        Walk(root, 0, found, includeGenerated);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Every cell NAME under <paramref name="root"/> — what a refusal lists.</summary>
    public static IReadOnlyList<string> Names(string root)
        => [.. All(root).Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p)))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Every cell folder called <paramref name="name"/>. Zero, one or several — all three are
    /// answers the caller has to be told apart.</summary>
    public static IReadOnlyList<string> Find(string root, string name)
        => [.. All(root, includeGenerated: true).Where(p => string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(p)), name, StringComparison.OrdinalIgnoreCase))];

    private static void Walk(string dir, int depth, List<string> found, bool includeGenerated)
    {
        if (depth > MaxDepth) return;

        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { return; }   // an unreadable folder holds no cells anyone can render

        foreach (var sub in subs)
        {
            if (File.Exists(Path.Combine(sub, CwsFileName))) continue;
            if (ReservedFolders.IsReserved(sub) && !(includeGenerated && !IsGit(sub))) continue;
            if (LooksLikeCellFolder(sub)) { found.Add(sub); continue; }
            Walk(sub, depth + 1, found, includeGenerated);
        }
    }

    private const string CwsFileName = ".cws";

    /// <summary>
    /// True when a directory has the SHAPE of a cell folder. Deliberately structural rather than a
    /// `.ccell` test alone: a cell folder is legal with no `.ccell` at all (primacy is implicit when
    /// a sub-folder holds exactly one file — <see cref="PrimaryState.SoleFile"/>), so requiring the
    /// manifest would classify a perfectly ordinary cell as a plain folder and check nothing in it.
    /// </summary>
    public static bool LooksLikeCellFolder(string dir)
    {
        if (File.Exists(Path.Combine(dir, CellFolder.CcellFileName))) return true;

        foreach (var t in Enum.GetValues<ViewType>())
            if (Directory.Exists(Path.Combine(dir, CellFolder.SubFolderName(t))))
                return true;

        return false;
    }

    /// <summary><c>.git</c> is never walked, whatever <c>--all</c> says: it is a history, not a design,
    /// and nothing in it is a cell. <c>--all</c> lifts the generated-cells exclusion only.</summary>
    private static bool IsGit(string dir) => string.Equals(
        Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)),
        ReservedFolders.Git, StringComparison.OrdinalIgnoreCase);
}
