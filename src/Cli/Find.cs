using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf find &lt;root&gt; [--depth n]</c> — what is HERE
/// (brief-automation-11-missing-verbs.md R-aut11-3).
///
/// <para><b>Why it exists.</b> There was no way to ask the surface what exists. Locating a workspace
/// that holds a particular device meant searching the filesystem outside the automation surface
/// entirely — and a protocol client that has the server and nothing else cannot do that at all. The
/// server already knew how to read every one of these documents; it simply never offered to
/// enumerate them.</para>
///
/// <para><b>It reads what the other verbs read.</b> A workspace is a directory holding a
/// <c>.cws</c> (<see cref="DocumentKinds.Classify"/>), its cells are <see cref="CellLookup"/>'s
/// answer, each view is <c>CellFolder.ResolvePrimary</c>'s, and the analyses are the ones the
/// elaborator would see — <see cref="CircuitSource"/>'s extraction, which is the GUI's own Simulate
/// path. A second enumerator here could disagree with what <c>render</c> draws and what <c>run</c>
/// dispatches, and the disagreement would look like a missing cell.</para>
///
/// <para><b>The walk is BOUNDED and says when it stopped short.</b> A listing that quietly gave up
/// is the one failure this verb must not have: a caller reads a short answer as "the workspace I am
/// looking for is not here" and goes elsewhere. So <c>--depth</c> is an argument, the default is
/// shallow, and <c>truncated</c> is in the document.</para>
///
/// <para><b>It never leaves the root.</b> A directory symbolic link is not followed, and every path
/// reported is under the root as given. On the protocol adapter the root is already
/// <see cref="Serve.PathRoot"/>'s; this is what keeps a link inside it from listing what is
/// outside.</para>
///
/// <para><b>It writes nothing</b>, for <c>check</c>'s reason: it must run on a read-only tree and on
/// a workspace another process has open.</para>
/// </summary>
internal static class Find
{
    /// <summary>How deep below the root a workspace is looked for. Deep enough for the
    /// folder-of-projects arrangements people keep, shallow enough that pointing this at a home
    /// directory is not a full disk walk. <c>--depth</c> raises it, and the answer says whether the
    /// bound was reached.</summary>
    private const int DefaultDepth = 4;
    private const int MaxDepth     = 12;

    public static int Run(string[] args)
    {
        string? root = null;
        int     depth = DefaultDepth;
        bool    analyses = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--depth" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out depth)
                        || depth < 0 || depth > MaxDepth)
                        return JsonRun.Fail(CliDiagnostics.FindDepthMalformed(args[i], MaxDepth));
                    continue;

                // On by default because the analyses are half the point — "which cell can I run"
                // is the question this verb is usually asked. Skippable because each one costs an
                // extraction, and a caller enumerating a large tree to find a NAME does not need it.
                case "--no-analyses": analyses = false; continue;

                default:
                    if (args[i].StartsWith('-'))
                    { JsonRun.Report(CliDiagnostics.FindUnknownOption(args[i])); return Usage(); }
                    if (root is not null)
                    { JsonRun.Report(CliDiagnostics.FindMultipleRoots()); return Usage(); }
                    root = args[i];
                    continue;
            }
        }

        if (root is null) { JsonRun.Report(CliDiagnostics.FindRootRequired()); return Usage(); }
        JsonRun.InputPath = root;

        // A `.cws` is accepted as well as its directory, because that is what `create` handed the
        // caller — the same courtesy `new cell` already extends.
        if (File.Exists(root)
            && Path.GetFileName(root).Equals(DocumentKinds.CwsFileName, StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(Path.GetFullPath(root))!;

        if (!Directory.Exists(root)) return JsonRun.Fail(CliDiagnostics.FindRootNotFound(root));

        string full = Path.GetFullPath(root);

        var workspaces = new List<string>();
        bool truncated = false;
        WalkForWorkspaces(full, 0, depth, workspaces, ref truncated);
        workspaces.Sort(StringComparer.Ordinal);

        var found = new List<FoundWorkspaceJson>(workspaces.Count);
        int cells = 0;
        foreach (string ws in workspaces)
        {
            var one = Describe(ws, analyses);
            cells += one.Cells.Count;
            found.Add(one);
        }

        // Cell folders that belong to no workspace are REPORTED rather than listed: they are a real
        // state (a folder of loose cells, a cell handed over on its own) and an empty answer would
        // read as "there is nothing here". They are not invented into a workspace, because a
        // workspace decides the default technology and attributing a cell to one it is not in is
        // worse than not listing it.
        if (found.Count == 0 && DocumentKinds.LooksLikeCellFolder(full))
            JsonRun.Note(CliDiagnostics.FindRootIsACell(full));
        else if (found.Count == 0)
            JsonRun.Note(CliDiagnostics.FindNothingHere(full, depth));

        if (truncated) JsonRun.Note(CliDiagnostics.FindTruncated(depth));

        JsonRun.Find = new FindReportJson(full, depth, truncated, found);

        Console.WriteLine($"{full}  ({found.Count} workspace(s), {cells} cell(s), depth {depth})");
        foreach (var ws in found)
        {
            Console.WriteLine($"  {ws.Name}  {ws.Path}"
                            + (ws.Technology is { } t ? $"  [tech {t}]" : ""));
            foreach (var c in ws.Cells)
            {
                string views = string.Join(", ", c.Views.Select(v =>
                    v.File is { } f ? $"{v.Type}: {f}" : $"{v.Type}: {v.State}"));
                Console.WriteLine($"    {c.Name,-24} {views}");
                if (c.Analyses is { Count: > 0 } a)
                    Console.WriteLine($"    {"",-24} analyses: {string.Join(", ", a)}");
            }
        }
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf find <root> [--depth n] [--no-analyses]");
        return 1;
    }

    // ── the walk ─────────────────────────────────────────────────────────────

    private static void WalkForWorkspaces(
        string dir, int level, int limit, List<string> found, ref bool truncated)
    {
        if (File.Exists(Path.Combine(dir, DocumentKinds.CwsFileName)))
        {
            // A workspace is a leaf of THIS walk. A workspace nested inside another is somebody
            // else's tree, for CellLookup's reason: two workspaces have different default
            // technologies, and reporting the inner one's cells as the outer one's would attribute
            // them to the wrong process.
            found.Add(dir);
            return;
        }

        if (level >= limit)
        {
            // Only a directory that could still have held something counts as a truncation — a leaf
            // reached exactly at the bound has nothing below it and did not stop short.
            try { if (Directory.EnumerateDirectories(dir).Any()) truncated = true; }
            catch { /* unreadable: nothing to report and nothing was missed that could be listed */ }
            return;
        }

        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { return; }   // an unreadable folder holds nothing anyone can list

        Array.Sort(subs, StringComparer.Ordinal);
        foreach (string sub in subs)
        {
            // A directory symbolic link is where a bounded walk stops being bounded, and where a
            // listing confined to a root stops being confined: one link is a path outside it, or a
            // cycle. Not followed, and not an error — the link is simply not a place this verb
            // reports on.
            if (IsLink(sub)) continue;
            if (ReservedFolders.IsReserved(sub)) continue;
            WalkForWorkspaces(sub, level + 1, limit, found, ref truncated);
        }
    }

    private static bool IsLink(string dir)
    {
        try { return new DirectoryInfo(dir).LinkTarget is not null; }
        catch { return true; }   // cannot tell: do not walk it
    }

    // ── one workspace ────────────────────────────────────────────────────────

    private static FoundWorkspaceJson Describe(string workspaceDir, bool analyses)
    {
        string? tech = null;
        try
        {
            var cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceDir, DocumentKinds.CwsFileName));
            tech = cws.DefaultTechRef;
        }
        catch { /* an unreadable .cws is `check`'s finding, not this verb's */ }

        var cells = new List<FoundCellJson>();
        foreach (string cellDir in CellLookup.All(workspaceDir))
        {
            var views = new List<FoundViewJson>();
            foreach (var type in DocumentKinds.AllViewTypes)
            {
                var res = CellFolder.ResolvePrimary(cellDir, type);
                if (res.State is PrimaryState.NoView) continue;
                // The sub-folder's name is the token: "schematic", "symbol", "layout", "3d" — the
                // spelling `new cell --views` takes.
                views.Add(new FoundViewJson(
                    CellFolder.SubFolderName(type), res.ResolvedName, res.State.ToString()));
            }

            cells.Add(new FoundCellJson(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir)),
                cellDir, views, analyses ? AnalysesOf(cellDir) : null));
        }

        return new FoundWorkspaceJson(
            workspaceDir,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(workspaceDir)),
            tech, cells);
    }

    /// <summary>
    /// What the cell's primary schematic declares, as the elaborator would see it — a typed analysis
    /// by name, and a RAW <c>analysis</c> directive by the name on its own line, since one of those
    /// never becomes typed and is dispatched straight from its text.
    ///
    /// <para>Null when the extraction could not be performed at all. That is a different answer from
    /// "declares none", and reporting the two the same way would tell a caller a runnable cell is
    /// not runnable.</para>
    /// </summary>
    private static IReadOnlyList<string>? AnalysesOf(string cellDir)
    {
        string? why = null;
        var source = CircuitSource.Read(cellDir, DocumentKind.Cell, m => why = m);
        if (source is not { } s)
        {
            if (why is not null) JsonRun.Note(CliDiagnostics.FindCellUnreadable(cellDir, why));
            return null;
        }

        var names = new List<string>();
        foreach (var a in s.Tb.Analyses) names.Add(a.Name);
        foreach (var raw in s.Tb.RawDirectives)
        {
            if (raw.Kind != "analysis") continue;
            var tokens = raw.RawLine.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
            // `analysis <name> type=…` — the token after the keyword is the name.
            if (tokens.Length >= 2 && !names.Contains(tokens[1], StringComparer.Ordinal))
                names.Add(tokens[1]);
        }
        return names;
    }
}
