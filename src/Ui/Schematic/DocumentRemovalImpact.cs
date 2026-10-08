using System.Text.Json.Nodes;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  DocumentRemovalImpact — what a workspace loses when one of its non-cell documents
//  is removed.
//
//  Five document kinds (.ctech, .cem, .charm, .wBond, .ccolor) had no in-app remove
//  at all until now: ProjectTreeNodeViewModel.IsRemovableFile listed the three view
//  extensions, OtherFile and UserFolder, and nothing else. The reported symptom was
//  "not possible to delete a tech ... unless I use the file manager", and the same
//  hole was under the other four.
//
//  Adding the menu item is the small half. A .ctech in particular is referenced from
//  three places, and only two of them are visible to a user looking at the file:
//    1. .cws DefaultTechRef;
//    2. a .clay's own TechRef;
//    3. EVERY microstrip component in EVERY schematic of the workspace — which
//       resolves its substrate from the workspace default ONLY
//       (MicrostripSubstrateInjection.ResolveWorkspaceTechnology), never from a
//       per-schematic reference, because none exists.
//  (3) is the one nothing else would report: the parts keep their W and L, the
//  schematic still opens, and the electrical model silently falls back to the
//  component defaults until someone runs a simulation and wonders why the answer
//  moved. So the confirmation counts it.
//
//  Read-only, best effort, and deliberately generous: an unreadable file counts as a
//  user, which over-states the blast radius rather than hiding part of it — the same
//  direction ExternalWorkspaceGate.LayoutsFollowingWorkspaceDefault already chose.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What removing one <c>.ctech</c> would cost. Paths, not just counts, so the caller can name a few
/// of them — "3 layouts" sends a user hunting; three names do not.
/// </summary>
/// <param name="IsWorkspaceDefault">This file is the <c>.cws</c>'s <c>DefaultTechRef</c>.</param>
/// <param name="LayoutsFollowingDefault">Layouts with no <c>TechRef</c> of their own, which follow the
/// default and therefore lose their layer table with it. Empty unless
/// <paramref name="IsWorkspaceDefault"/>.</param>
/// <param name="LayoutsPointingHere">Layouts whose own <c>TechRef</c> resolves to this file, in this
/// workspace and in every other open one.</param>
/// <param name="MicrostripCells">Cells holding at least one microstrip component, which resolve their
/// substrate from the workspace default. Empty unless <paramref name="IsWorkspaceDefault"/>.</param>
/// <param name="OtherTechnologies">The other <c>.ctech</c> files in this workspace — what the default
/// could be re-pointed at instead of simply being cleared.</param>
public readonly record struct TechnologyRemovalImpact(
    bool                  IsWorkspaceDefault,
    IReadOnlyList<string> LayoutsFollowingDefault,
    IReadOnlyList<string> LayoutsPointingHere,
    IReadOnlyList<string> MicrostripCells,
    IReadOnlyList<string> OtherTechnologies);

public static class DocumentRemovalImpact
{
    // ── Technology ────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything that resolves to <paramref name="techPath"/> today.
    /// </summary>
    /// <param name="otherOpenWorkspaceRoots">MW2 R-mw2-14's rule applied to technologies: a layout in
    /// another OPEN workspace can point here across a reference, and a workspace nobody has open
    /// cannot be scanned at all — which is what the caller's wording has to say.</param>
    public static TechnologyRemovalImpact ForTechnology(
        string workspaceRoot, string techPath, IEnumerable<string>? otherOpenWorkspaceRoots = null)
    {
        string target = Normalize(techPath);

        bool isDefault = false;
        try
        {
            var cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws"));
            if (cws.DefaultTechRef is { Length: > 0 } r)
                isDefault = string.Equals(
                    Normalize(Path.Combine(workspaceRoot, r)), target, StringComparison.OrdinalIgnoreCase);
        }
        catch { /* an unreadable .cws answers nothing; the other scans still run */ }

        var pointingHere = new List<string>(LayoutsWithTechRef(workspaceRoot, target));
        foreach (var root in otherOpenWorkspaceRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (string.Equals(Normalize(root), Normalize(workspaceRoot), StringComparison.OrdinalIgnoreCase))
                continue;
            pointingHere.AddRange(LayoutsWithTechRef(root, target));
        }
        pointingHere.Sort(StringComparer.OrdinalIgnoreCase);

        return new TechnologyRemovalImpact(
            isDefault,
            isDefault ? ExternalWorkspaceGate.LayoutsFollowingWorkspaceDefault(workspaceRoot) : [],
            pointingHere,
            isDefault ? CellsWithMicrostrip(workspaceRoot) : [],
            OtherTechnologiesIn(workspaceRoot, target));
    }

    /// <summary>Layouts under <paramref name="root"/> whose own <c>TechRef</c> resolves to
    /// <paramref name="target"/>. A <c>TechRef</c> of null is NOT counted here — that layout follows
    /// the default and is <see cref="ExternalWorkspaceGate.LayoutsFollowingWorkspaceDefault"/>'s
    /// answer, which the caller only asks for when this file IS the default.</summary>
    private static IEnumerable<string> LayoutsWithTechRef(string root, string target)
    {
        foreach (var clay in EnumerateFilesSafe(root, "*.clay"))
        {
            string? techRef;
            try { techRef = LayoutPersistence.LoadFromFile(clay).TechRef; }
            catch { continue; }
            if (techRef is not { Length: > 0 }) continue;

            string resolved;
            try { resolved = Normalize(RefPath.Resolve(Path.GetDirectoryName(clay)!, techRef)); }
            catch { continue; }

            if (string.Equals(resolved, target, StringComparison.OrdinalIgnoreCase))
                yield return clay;
        }
    }

    /// <summary>
    /// Cells holding at least one microstrip component. Reported as CELLS rather than schematics
    /// because that is the unit a user thinks in, and because one cell's several schematics would
    /// otherwise inflate the number.
    ///
    /// <para>Read straight out of the JSON rather than through the schematic reader: this runs on a
    /// whole workspace inside a confirmation dialog, and every component only has to be identified by
    /// its <c>Symbol</c>. <see cref="PlanarLineSubstrateInjection.IsStackupLineKind"/> stays the one
    /// definition of which kinds those are — the microstrip family, CPWG and SLIN.</para>
    /// </summary>
    private static IReadOnlyList<string> CellsWithMicrostrip(string root)
    {
        var cells = new List<string>();
        foreach (var csch in EnumerateFilesSafe(root, "*.csch"))
        {
            if (!SchematicHasMicrostrip(csch)) continue;
            var cellDir = CellDirOf(csch);
            if (cellDir is null || cells.Contains(cellDir, StringComparer.OrdinalIgnoreCase)) continue;
            cells.Add(cellDir);
        }
        cells.Sort(StringComparer.OrdinalIgnoreCase);
        return cells;
    }

    /// <summary>
    /// True when any schematic of <paramref name="cellDir"/> holds a microstrip component — the cells
    /// for which a technology decision has an ELECTRICAL consequence as well as a drawn one.
    /// </summary>
    public static bool CellHasMicrostrip(string cellDir)
    {
        string subDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        if (!Directory.Exists(subDir)) return false;
        try
        {
            foreach (var csch in Directory.EnumerateFiles(subDir, "*.csch"))
                if (SchematicHasMicrostrip(csch)) return true;
        }
        catch (IOException)                 { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static bool SchematicHasMicrostrip(string cschPath)
    {
        try
        {
            var array = JsonNode.Parse(File.ReadAllText(cschPath))?["Components"]?.AsArray();
            if (array is null) return false;
            foreach (var item in array)
            {
                var symbol = item?["Symbol"]?.GetValue<string?>();
                if (symbol is null) continue;
                if (Enum.TryParse<SymbolKind>(symbol, ignoreCase: true, out var kind)
                    && PlanarLineSubstrateInjection.IsStackupLineKind(kind))
                    return true;
            }
        }
        catch { /* unreadable — reported by nothing here, and not evidence either way */ }
        return false;
    }

    /// <summary>The cell folder a view file belongs to (…/cell/schematic/x.csch → …/cell), or null
    /// when it is not inside one.</summary>
    private static string? CellDirOf(string viewFilePath)
        => PrimaryViewRepair.TryClassify(viewFilePath, out var cellDir, out _) ? cellDir : null;

    /// <summary>Every other <c>.ctech</c> in the workspace — the candidates for a default that is
    /// about to lose its file.</summary>
    private static IReadOnlyList<string> OtherTechnologiesIn(string root, string target)
        => [.. TechnologiesIn(root).Where(p =>
               !string.Equals(Normalize(p), target, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Every <c>.ctech</c> under <paramref name="root"/>, sorted. The workspace's technologies as the
    /// Project Tree shows them — the set a picker may offer, and the set a removal counts against.
    /// </summary>
    public static IReadOnlyList<string> TechnologiesIn(string root)
    {
        var all = EnumerateFilesSafe(root, "*.ctech").ToList();
        all.Sort(StringComparer.OrdinalIgnoreCase);
        return all;
    }

    // ── Colour theme ──────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="colorThemePath"/> is the scheme this workspace activates on open
    /// (<c>.cws</c> <c>ColorSchemeName</c>, matched by NAME — that is what the field stores, and what
    /// <c>ThemeResolver</c> looks up).
    /// </summary>
    public static bool IsWorkspaceColorScheme(string workspaceRoot, string colorThemePath)
    {
        try
        {
            var cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws"));
            return cws.ColorSchemeName is { Length: > 0 } name
                && string.Equals(name, Path.GetFileNameWithoutExtension(colorThemePath),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── wBond design ──────────────────────────────────────────────────────────

    /// <summary>
    /// The schematics holding a wBond component whose <c>File</c> link resolves to
    /// <paramref name="wbondPath"/> — the references a removal breaks.
    ///
    /// <para>Matched by RESOLUTION, exactly as <see cref="CellUsageScanner.RewriteWBondLinks"/> is
    /// and for the same reason: the link is relative to the schematic that holds it, so two cells can
    /// each own a <c>layout/top.wBond</c> and a name-only match would blame the wrong one.</para>
    /// </summary>
    public static IReadOnlyList<string> SchematicsLinkingWBond(string workspaceRoot, string wbondPath)
    {
        string target = Normalize(wbondPath);
        var hits = new List<string>();

        foreach (var csch in EnumerateFilesSafe(workspaceRoot, "*.csch"))
        {
            string? schDir = Path.GetDirectoryName(Path.GetFullPath(csch));
            if (schDir is null) continue;
            try
            {
                var array = JsonNode.Parse(File.ReadAllText(csch))?["Components"]?.AsArray();
                if (array is null) continue;
                foreach (var item in array)
                {
                    var stored = item?["Parameters"]?.AsArray()?
                        .FirstOrDefault(p => p?["Name"]?.GetValue<string?>() == WBondPlacement.FileParameter)
                        ?["Expression"]?.GetValue<string?>();
                    if (string.IsNullOrWhiteSpace(stored)) continue;

                    string resolved;
                    try
                    {
                        resolved = Path.IsPathRooted(stored)
                            ? Normalize(stored)
                            : Normalize(Path.Combine(schDir, stored.Replace('\\', '/')));
                    }
                    catch { continue; }

                    if (!string.Equals(resolved, target, StringComparison.OrdinalIgnoreCase)) continue;
                    hits.Add(csch);
                    break;
                }
            }
            catch { /* best effort */ }
        }

        hits.Sort(StringComparer.OrdinalIgnoreCase);
        return hits;
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible    = true,
                MatchCasing           = MatchCasing.CaseInsensitive,
            });
        }
        catch (IOException)                 { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
