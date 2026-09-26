namespace CircuitRF.Design.Cells;

// ──────────────────────────────────────────────────────────────────────────────
//  PrimaryViewRepair — what happens to a cell's PRIMARY when one of its view files
//  is removed.
//
//  Removing a .csch / .csym / .clay used to be a file delete and nothing else, so a
//  cell whose primary was the file that went away was left in
//  PrimaryState.MissingNamedPrimary — the state CellFolder's own doc comment calls
//  "a blatant contradiction" and the Project Tree renders as a warning. The user who
//  deleted one of two schematics got a warning triangle on a cell they had just
//  tidied, and no indication of what to do about it.
//
//  The .ccell is repaired in the SAME operation as the removal, because the two are
//  one act: the file is gone, so the record that names it is wrong the instant it is.
//  Which repair is right depends only on what survives (see Apply).
//
//  In src/Design rather than in the view model because it is .ccell arithmetic over a
//  cell folder — no dialog, no canvas, no workspace — and because the same repair is
//  owed to any future non-GUI caller that removes a view.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>What <see cref="PrimaryViewRepair.Apply"/> did — the caller reports it, and the
/// wording differs enough per case that a bool would not carry it.</summary>
public enum PrimaryRepairAction
{
    /// <summary>The removed file was not the primary; the <c>.ccell</c> was not touched.</summary>
    NotPrimary,

    /// <summary>It was, and nothing of that view type is left. The entry was cleared — the cell
    /// simply has no view of that type now, which is <see cref="PrimaryState.NoView"/> and not an
    /// error.</summary>
    NoViewsLeft,

    /// <summary>It was, and exactly one file survives, which is now named in the <c>.ccell</c>.
    /// <see cref="PrimaryState.SoleFile"/> would have made that file primary implicitly anyway; it
    /// is written down so the file does not merely say something ELSE is primary.</summary>
    PromotedSurvivor,

    /// <summary>It was, and several files survive with no basis for choosing between them. The entry
    /// was cleared, leaving <see cref="PrimaryState.NoPrimary"/> — "not chosen yet", which is not an
    /// error — rather than <see cref="PrimaryState.MissingNamedPrimary"/>, which is. The caller has
    /// to say so: only the user can pick.</summary>
    ClearedForChoice,
}

/// <summary>
/// The removal, examined BEFORE it happens: which cell and view type the file belongs to, whether it
/// is the primary, and what would be left. Planned first because the confirmation dialog has to
/// state the consequence before the file is in the Trash.
/// </summary>
/// <param name="CellDir">The cell folder, or null when the file is not inside one (a loose view file
/// — nothing to repair, and no primacy to speak of).</param>
/// <param name="ViewType">The view type its sub-folder names.</param>
/// <param name="WasPrimary">True when the file is the cell's primary for that view — named in the
/// <c>.ccell</c>, or the sole file of its type, which is primary implicitly.</param>
/// <param name="Survivors">The file names of that view type that would remain, sorted.</param>
public readonly record struct PrimaryRepairPlan(
    string?               CellDir,
    ViewType              ViewType,
    bool                  WasPrimary,
    IReadOnlyList<string> Survivors)
{
    /// <summary>True when there is a cell folder to repair at all.</summary>
    public bool InCell => CellDir is not null;
}

public static class PrimaryViewRepair
{
    /// <summary>
    /// Works out what removing <paramref name="viewFilePath"/> would do to its cell's primacy.
    /// Reads the filesystem; changes nothing.
    ///
    /// <para>Returns a plan with a null <see cref="PrimaryRepairPlan.CellDir"/> for anything that is
    /// not a view file inside a cell's own view sub-folder — a loose <c>.csch</c> bookmarked as a
    /// Known File, say. That is not a failure: such a file has no <c>.ccell</c> and never was
    /// anyone's primary.</para>
    /// </summary>
    public static PrimaryRepairPlan Plan(string viewFilePath)
    {
        if (!TryClassify(viewFilePath, out string? cellDir, out ViewType viewType))
            return new PrimaryRepairPlan(null, default, false, []);

        string subDir   = CellFolder.SubFolderPath(cellDir!, viewType);
        string ext      = CellFolder.ViewExtension(viewType);
        string fileName = Path.GetFileName(viewFilePath);

        List<string> survivors;
        try
        {
            survivors = Directory.GetFiles(subDir, "*" + ext)
                .Select(Path.GetFileName)
                .Where(f => f is not null
                         && !string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)               { return new PrimaryRepairPlan(null, viewType, false, []); }
        catch (UnauthorizedAccessException) { return new PrimaryRepairPlan(null, viewType, false, []); }

        var resolved = CellFolder.ResolvePrimary(cellDir!, viewType);
        bool wasPrimary = resolved.State is PrimaryState.SoleFile or PrimaryState.NamedPresent
                       && string.Equals(resolved.ResolvedName, fileName, StringComparison.OrdinalIgnoreCase);

        return new PrimaryRepairPlan(cellDir, viewType, wasPrimary, survivors);
    }

    /// <summary>
    /// Writes the repair the plan implies. Call AFTER the file is gone.
    ///
    /// <para>A plan whose file was not primary writes nothing at all — including when the
    /// <c>.ccell</c> already names something absent, because that contradiction was there before
    /// this removal and silently "fixing" it here would hide a state the tree is deliberately
    /// warning about.</para>
    /// </summary>
    /// <param name="promoted">The file name now named primary, when one was promoted.</param>
    public static PrimaryRepairAction Apply(PrimaryRepairPlan plan, out string? promoted)
    {
        promoted = null;
        if (!plan.WasPrimary || plan.CellDir is null) return PrimaryRepairAction.NotPrimary;

        string ccellPath = Path.Combine(plan.CellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath))
            return plan.Survivors.Count == 1 ? PrimaryRepairAction.PromotedSurvivor
                 : plan.Survivors.Count == 0 ? PrimaryRepairAction.NoViewsLeft
                 : PrimaryRepairAction.ClearedForChoice;

        // One survivor is promoted; zero or several leave the entry empty. "Several" deliberately
        // does not guess — alphabetical order is not a statement about which schematic is the real
        // one — and PrimaryState.NoPrimary is the honest record of that.
        string? newPrimary = plan.Survivors.Count == 1 ? plan.Survivors[0] : null;

        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.SetPrimary(plan.ViewType, newPrimary);
        CellPersistence.SaveToFile(ccellPath, ccell);

        promoted = newPrimary;
        return newPrimary is not null ? PrimaryRepairAction.PromotedSurvivor
             : plan.Survivors.Count == 0 ? PrimaryRepairAction.NoViewsLeft
             : PrimaryRepairAction.ClearedForChoice;
    }

    /// <summary>
    /// True when <paramref name="viewFilePath"/> sits directly in a cell's <c>schematic/</c>,
    /// <c>symbol/</c>, <c>layout/</c> or <c>3d/</c> sub-folder AND the folder above that holds a <c>.ccell</c>.
    /// Both halves matter: the sub-folder name gives the view type, and the <c>.ccell</c> is what
    /// makes the folder a cell rather than a directory that happens to be called "layout".
    /// </summary>
    public static bool TryClassify(string viewFilePath, out string? cellDir, out ViewType viewType)
    {
        cellDir  = null;
        viewType = default;

        string? subDir = Path.GetDirectoryName(Path.GetFullPath(viewFilePath));
        if (subDir is null) return false;
        string? parent = Path.GetDirectoryName(subDir);
        if (parent is null) return false;

        ViewType? kind = Path.GetFileName(subDir).ToLowerInvariant() switch
        {
            CellFolder.SchematicSubFolder => ViewType.Schematic,
            CellFolder.SymbolSubFolder    => ViewType.Symbol,
            CellFolder.LayoutSubFolder    => ViewType.Layout,
            CellFolder.ThreeDSubFolder    => ViewType.ThreeD,
            _                             => null,
        };
        if (kind is null) return false;
        viewType = kind.Value;

        if (!File.Exists(Path.Combine(parent, CellFolder.CcellFileName))) return false;

        cellDir = parent;
        return true;
    }
}
