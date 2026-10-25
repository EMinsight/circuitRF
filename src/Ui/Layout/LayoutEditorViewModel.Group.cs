using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L3c — Group into Cell (docs/sonnet-briefs/brief-L3c-flatten-and-group.md §4). The geometry
/// math (origin choice, translate) lives in <see cref="LayoutGroup"/> (framework-free); this file is
/// selection/file-IO/undo/Messages plumbing, mirroring how <c>.Flatten.cs</c>/<c>.Clipboard.cs</c>
/// split concerns out of the main VM file.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    public LayoutCommandAvailability GroupIntoCellAvailability =>
        _selectedIndices.Count == 0 && _selectedInstanceIndices.Count == 0
            ? LayoutCommandAvailability.Disabled("Group into Cell: nothing selected.")
            : LayoutCommandAvailability.Enabled;

    /// <summary>R-L3c-1a-style outcome preview for the confirmation dialog — counts only, no geometry
    /// built yet (that only happens once the user actually confirms a name).</summary>
    public string? GroupIntoCellOutcomeText =>
        _selectedIndices.Count == 0 && _selectedInstanceIndices.Count == 0
            ? null
            : $"→ 1 instance ({_selectedIndices.Count} shape(s), {_selectedInstanceIndices.Count} instance(s))";

    /// <summary>
    /// Groups the current selection into a brand-new cell under <paramref name="parentDir"/> named
    /// <paramref name="cellName"/>, and replaces the selection with one instance of it (§4). The
    /// geometry never visibly moves (R-L3c-5): <see cref="LayoutGroup.BuildContents"/> picks the
    /// selection's own bbox-minimum as the new cell's local origin, and the replacement instance is
    /// placed at that exact point in the parent, R0/no-mirror/Mag 1. The new cell inherits the
    /// PARENT's technology (its <c>TechRef</c> rewritten to resolve from the new cell's own folder —
    /// <see cref="TechRefFrom"/>) and <c>DbuPerMicron</c> (§4 — same technology means R-L3c-3's
    /// reconciliation never fires for a freshly grouped cell, and identical resolution means no
    /// rescale, which is exactly why inheriting beats defaulting). A selected INSTANCE moves into the
    /// new cell as an instance, unchanged apart from the translate — grouping does not flatten.
    /// <br/><br/>
    /// R-L3a-2's cycle check runs even though a brand-new, reference-free cell cannot actually close a
    /// cycle — "run the check anyway rather than special-casing" (§4's own instruction).
    /// <br/><br/>
    /// Returns <c>false</c> (and reports why) on an empty selection, a name collision, or any I/O
    /// failure; the cell folder is only created once every earlier check has passed, and is removed
    /// again if the (structurally-impossible-in-practice) cycle check somehow refuses it — no
    /// half-created cell is ever left behind by a REFUSED group. <b>Undoing a SUCCESSFUL group does
    /// NOT delete the created cell folder (R-L3c-6)</b> — see this method's own commit path below for
    /// why, and the Messages note it posts after commit says so directly.
    /// </summary>
    public bool CommitGroupIntoCell(string parentDir, string cellName)
    {
        if (_selectedIndices.Count == 0 && _selectedInstanceIndices.Count == 0)
        {
            _messageSink?.Error("Group into Cell: nothing selected.");
            return false;
        }

        string? leftOut = GroupIntoCellLeftOutText;   // before the selection is replaced by the new instance
        var shapes = _selectedIndices.Select(i => Model.Shapes[i]).ToList();
        var instances = _selectedInstanceIndices.Select(i => Model.Instances[i]).ToList();

        var contents = LayoutGroup.BuildContents(shapes, instances, InstanceBaseDir);
        if (contents is null)
        {
            _messageSink?.Error("Group into Cell: the selection has no measurable extent.");
            return false;
        }

        string cellDir;
        try
        {
            cellDir = CellFolder.CreateCellFolder(parentDir, cellName);
        }
        catch (Exception ex)
        {
            _messageSink?.Error($"Group into Cell: {ex.Message}");
            return false;
        }

        string effectiveParentDir = InstanceBaseDir.Length > 0 ? InstanceBaseDir : parentDir;
        string cellRef;
        try
        {
            cellRef = RefPath.ToStored(Path.GetRelativePath(effectiveParentDir, cellDir));
        }
        catch (Exception ex)
        {
            TryDeleteCellFolder(cellDir);
            _messageSink?.Error($"Group into Cell: {ex.Message}");
            return false;
        }

        // R-L3a-2: run the same edit-time cycle guard every other instance placement gets, even though
        // a brand-new cell with no references of its own cannot actually close a cycle (§4's own
        // instruction — "run the check anyway rather than special-casing").
        if (!CheckNotCyclic(cellRef))
        {
            TryDeleteCellFolder(cellDir);
            return false;
        }

        // A selected INSTANCE's own CellRef was resolved relative to THIS document's InstanceBaseDir —
        // moving it into the new cell's own layout/ directory (a different directory) means it must be
        // rebased, exactly like a cross-directory paste already rebases (LayoutFragment.RebaseInstances) —
        // never left pointing at a path that only happened to be correct in the OLD location.
        string newCellLayoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var newView = new LayoutView
        {
            DbuPerMicron = Model.DbuPerMicron,
            DisplayUnit = Model.DisplayUnit,
            SnapDbu = Model.SnapDbu,
            TechRef = TechRefFrom(newCellLayoutDir),
        };
        newView.Shapes.AddRange(contents.Shapes);
        foreach (var inst in contents.Instances)
        {
            inst.CellRef = LayoutFlatten.RebaseCellRef(inst.CellRef, effectiveParentDir, newCellLayoutDir);
            newView.Instances.Add(inst);
        }

        try
        {
            string layoutFileName = cellName + ".clay";
            string layoutFilePath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), layoutFileName);
            LayoutPersistence.SaveToFile(layoutFilePath, newView);

            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.PrimaryLayout = layoutFileName;
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
        catch (Exception ex)
        {
            TryDeleteCellFolder(cellDir);
            _messageSink?.Error($"Group into Cell: {ex.Message}");
            return false;
        }

        var replacement = new LayoutInstance { CellRef = cellRef, X = contents.OriginX, Y = contents.OriginY, Mag = 1.0 };

        IUiCommand combined = _selectedIndices.Count > 0
            ? new Commands.Layout.DeleteShapesCommand(Model, _selectedIndices.ToList())
            : new Commands.Layout.DeleteInstancesCommand(Model, _selectedInstanceIndices.ToList());
        if (_selectedIndices.Count > 0 && _selectedInstanceIndices.Count > 0)
            combined = new CompositeCommand(combined, new Commands.Layout.DeleteInstancesCommand(Model, _selectedInstanceIndices.ToList()));

        int newInstanceIndex = Model.Instances.Count;   // computed pre-execution, exactly as InsertPastedMixed does
        combined = new CompositeCommand(combined, new Commands.Layout.AddInstanceCommand(Model, replacement));

        Execute(combined);
        SetInstanceSelection([newInstanceIndex]);

        // R-L3c-6: undo removes the instance and restores the shapes, but deliberately does NOT delete
        // this cell folder — the user may already have opened/edited it, another layout may have
        // instantiated it in the meantime, and file deletion is not something an undo stack should be
        // doing. Stated here, in the confirmation the caller already showed, AND in this success note.
        _messageSink?.Success(
            $"Group into Cell: created '{cellName}' ({contents.Shapes.Count} shape(s), {contents.Instances.Count} instance(s)). " +
            "Undo restores this selection but does not delete the cell folder.");
        if (leftOut is { } note) _messageSink?.Info("Group into Cell: " + note);
        return true;
    }

    /// <summary>
    /// brief-dfr26-2 R-dfr26-2-2: the new cell's <c>TechRef</c>, written from the technology the parent RESOLVED and
    /// relative to <paramref name="newLayoutDir"/>. The parent's own string is relative to the parent's folder, and
    /// the new cell is at a different depth, so copying it verbatim pointed one level off — <c>Technology file not
    /// found</c> on every open. A parent with no <c>TechRef</c> takes the workspace default, which the new cell (in
    /// the same workspace) resolves the same way, so it stays null.
    /// </summary>
    private string? TechRefFrom(string newLayoutDir)
    {
        if (Model.TechRef is null) return null;
        string? techPath = ResolvedTechPath
            ?? (InstanceBaseDir.Length > 0 ? RefPath.Resolve(InstanceBaseDir, Model.TechRef) : null);
        if (techPath is null) return Model.TechRef;
        try { return RefPath.ToStored(Path.GetRelativePath(Path.GetFullPath(newLayoutDir), Path.GetFullPath(techPath))); }
        catch { return Model.TechRef; }
    }

    /// <summary>
    /// brief-dfr26-2 R-dfr26-2-3: what overlaps the selection without being in it, said rather than left behind in
    /// silence. Group into Cell takes every selected shape of every kind; what a designer finds missing afterwards
    /// (a ground fill) was never SELECTED — a left-to-right marquee takes only shapes wholly inside it, a fill
    /// spanning the board never is, and no marquee or click reaches a hidden or locked layer. Null when nothing was
    /// left out. Bounding boxes, so a fill whose box covers the selection counts even where it has a hole.
    /// </summary>
    public string? GroupIntoCellLeftOutText
    {
        get
        {
            if (_selectedIndices.Count == 0 && _selectedInstanceIndices.Count == 0) return null;
            var box = Bbox.Empty;
            foreach (var i in _selectedIndices) box = box.Union(LayoutGeometry.BboxOf(Model.Shapes[i]));
            foreach (var i in _selectedInstanceIndices) box = box.Union(CellHierarchy.InstanceBbox(Model.Instances[i], InstanceBaseDir));
            if (box.IsEmpty) return null;

            var selected = _selectedIndices.ToHashSet();
            int hidden = 0, beyond = 0;
            for (int i = 0; i < Model.Shapes.Count; i++)
            {
                if (selected.Contains(i) || !LayoutGeometry.BboxOf(Model.Shapes[i]).Intersects(box)) continue;
                var def = ResolveLayerDef(Model.Shapes[i].Layer);
                if (!def.Visible || !def.Selectable) hidden++; else beyond++;
            }
            if (hidden + beyond == 0) return null;

            var parts = new List<string>();
            if (hidden > 0) parts.Add($"{hidden} on hidden or locked layers");
            if (beyond > 0) parts.Add($"{beyond} reaching past the selection (a ground fill is one)");
            return $"{hidden + beyond} shape(s) overlapping the selection are not in it and stay where they are — " +
                   $"{string.Join(", ", parts)}. To take a region whole, Crop to Rectangle first.";
        }
    }

    private static void TryDeleteCellFolder(string cellDir)
    {
        try { if (Directory.Exists(cellDir)) Directory.Delete(cellDir, recursive: true); }
        catch { /* best-effort cleanup of a folder that was never actually used */ }
    }
}
