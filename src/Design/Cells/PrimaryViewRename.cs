namespace CircuitRF.Design.Cells;

// ──────────────────────────────────────────────────────────────────────────────
//  PrimaryViewRename — give a cell's PRIMARY view files the cell's own name.
//
//  Two operations owe this and both used to do it themselves: Rename Cell (when the
//  user asks for the primaries to follow) and Duplicate Cell (always — a copy named
//  "amp_v2" holding "amp.csch" is a copy whose folder and files disagree from the
//  moment it exists).
//
//  They were two copies of the same twenty lines, which is exactly how the layout
//  came to be renamed by one and not the other: ViewType.Layout was added to Rename's
//  list and there was no reason for anyone to think of Duplicate's. One list, here,
//  and both callers get every view type a cell can have.
//
//  In src/Design rather than in the view model for PrimaryViewRepair's reason: it is
//  file names and .ccell arithmetic over a cell folder — no dialog, no canvas, no
//  workspace — and a headless caller that copies a cell owes exactly the same tidy-up.
//
//  What it deliberately does NOT do is touch anything a view file POINTS at. A .clay
//  is paired with its .wBond by shared stem (WBondCell.Resolve), so renaming one owes
//  the other; that pairing lives above this layer and the Layout result carries the
//  old stem so the caller can honour it.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>What <see cref="PrimaryViewRename.ToCellName"/> did to one view type.</summary>
public enum PrimaryRenameOutcome
{
    /// <summary>No primary of this type to rename — no view at all, or several with none chosen.
    /// Not an error: a cell need not have a layout, and "not chosen yet" is not this operation's
    /// to decide.</summary>
    NoPrimary,

    /// <summary>The primary was already named after the cell. Nothing moved; the <c>.ccell</c> was
    /// still written, so an implicitly-primary sole file is now named as one.</summary>
    AlreadyNamed,

    /// <summary>The file was renamed and the <c>.ccell</c> now names it.</summary>
    Renamed,

    /// <summary>A DIFFERENT, non-primary file already holds the target name. Nothing was renamed and
    /// nothing was overwritten — the primary keeps its old name, which is a visible oddity rather
    /// than a lost file.</summary>
    Blocked,

    /// <summary>The move itself failed; <see cref="PrimaryRenameResult.Error"/> carries the reason.
    /// The <c>.ccell</c> is left alone, so it still names the file that is still there.</summary>
    Failed,
}

/// <summary>One view type's outcome.</summary>
/// <param name="ViewType">Which view this is about.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="OldFileName">The primary's file name before the rename, or null when there was no
/// primary. Carried because the STEM is what pairs a <c>.clay</c> with its wirebond design, and the
/// caller cannot recover it once the file has moved.</param>
/// <param name="NewFileName">The name the primary was given, or would have been given.</param>
/// <param name="Error">The failure message, for <see cref="PrimaryRenameOutcome.Failed"/> only.</param>
public readonly record struct PrimaryRenameResult(
    ViewType             ViewType,
    PrimaryRenameOutcome Outcome,
    string?              OldFileName,
    string               NewFileName,
    string?              Error);

public static class PrimaryViewRename
{
    /// <summary>
    /// Renames the primary schematic, symbol, layout and 3D view of <paramref name="cellDir"/> to
    /// <paramref name="cellName"/> + the view's extension, updating the <c>.ccell</c> to match.
    /// Non-primary files of every type keep their names — they are the author's, not the cell's.
    /// </summary>
    /// <returns>One result per view type, in <see cref="ViewType"/> order, so a caller can
    /// report each and act on the layout's old stem.</returns>
    public static IReadOnlyList<PrimaryRenameResult> ToCellName(string cellDir, string cellName)
    {
        var results = new List<PrimaryRenameResult>(4);
        foreach (var viewType in Enum.GetValues<ViewType>())
            results.Add(RenameOne(cellDir, cellName, viewType));
        return results;
    }

    private static PrimaryRenameResult RenameOne(string cellDir, string cellName, ViewType viewType)
    {
        var targetName = cellName + CellFolder.ViewExtension(viewType);

        var res = CellFolder.ResolvePrimary(cellDir, viewType);
        if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent)
            || res.ResolvedName is null)
            return new PrimaryRenameResult(viewType, PrimaryRenameOutcome.NoPrimary, null, targetName, null);

        var subDir     = CellFolder.SubFolderPath(cellDir, viewType);
        var sourcePath = Path.Combine(subDir, res.ResolvedName);
        var targetPath = Path.Combine(subDir, targetName);

        if (string.Equals(res.ResolvedName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            UpdateCcellPrimary(cellDir, viewType, targetName);
            return new PrimaryRenameResult(
                viewType, PrimaryRenameOutcome.AlreadyNamed, res.ResolvedName, targetName, null);
        }

        // A different file already holds the name. Refuse rather than overwrite: that file is a view
        // of this cell too, and the only thing wrong with the situation is a name.
        if (File.Exists(targetPath))
            return new PrimaryRenameResult(
                viewType, PrimaryRenameOutcome.Blocked, res.ResolvedName, targetName, null);

        try { File.Move(sourcePath, targetPath); }
        catch (Exception ex)
        {
            return new PrimaryRenameResult(
                viewType, PrimaryRenameOutcome.Failed, res.ResolvedName, targetName, ex.Message);
        }

        UpdateCcellPrimary(cellDir, viewType, targetName);
        return new PrimaryRenameResult(
            viewType, PrimaryRenameOutcome.Renamed, res.ResolvedName, targetName, null);
    }

    // Best-effort by design: the file has already moved by the time this runs, and a cell whose
    // .ccell could not be written still resolves its sole file implicitly.
    private static void UpdateCcellPrimary(string cellDir, ViewType viewType, string newPrimaryFileName)
    {
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return;
        try
        {
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.SetPrimary(viewType, newPrimaryFileName);
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
        catch { /* nothing to report to from here; the caller's own result already stands */ }
    }
}
