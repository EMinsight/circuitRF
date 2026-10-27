using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Crop to Rectangle (brief-dfr26-2 R-dfr26-2-1): drag a box and the whole board is cut down to it — every
/// shape on every layer, shown, hidden or locked — in one undo entry. All geometry is <see cref="LayoutCrop"/>'s;
/// this file is the gesture, the confirmation hand-off and the Messages record.
///
/// <para><b>Why every layer, when Clip All deliberately takes only visible, selectable ones.</b> Clip All is
/// "Select All, then Clip", and a layer the user locked is one they said they cannot touch. Crop to Rectangle
/// names the whole board as its scope, so the gate that protects a layer from a mis-aimed clip is exactly what
/// made a ten-layer crop a layer-by-layer job. The CONFIRMATION replaces it: before anything changes, the count on
/// every layer is shown, hidden and locked ones marked, so nothing is touched in silence.</para>
///
/// <para>The gesture is the EM solve region's: armed from the menu, it owns the next press whatever tool is
/// active, the rubber band is drawn through the marquee slot, and Escape puts it back.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>Asked with the computed crop and its summary before anything changes; null applies at once (a test,
    /// or a host with no window).</summary>
    private Func<LayoutCropResult, string, Task<bool>>? _cropConfirm;
    /// <summary>Set while the drag is Crop to New Cell's: handed the computed crop to name, confirm and write.</summary>
    private Func<LayoutCropResult, Task>? _cropToNewCell;
    private bool _cropArmed;
    private (long X, long Y)? _cropPress;
    private long _cropCurX, _cropCurY;

    /// <summary>True while a Crop to Rectangle drag is armed or in progress.</summary>
    public bool IsPickingCropRectangle => _cropArmed;

    public LayoutCommandAvailability CropToRectangleAvailability =>
        Model.Shapes.Count + Model.Instances.Count > 0
            ? LayoutCommandAvailability.Enabled
            : LayoutCommandAvailability.Disabled("The layout is empty");

    /// <summary>Arms the drag. <paramref name="confirm"/> is shown the crop before it is applied and answers
    /// whether to go ahead.</summary>
    public void ArmCropToRectangle(Func<LayoutCropResult, string, Task<bool>>? confirm)
    {
        _cropConfirm = confirm;
        _cropToNewCell = null;
        _cropArmed   = true;
        _cropPress   = null;
        OnPropertyChanged(nameof(IsPickingCropRectangle));
        ReportMessage("Crop to Rectangle: drag a box around the part of the board to keep. Every layer is cut " +
                      "to it, hidden and locked ones included; you will see the counts first. Escape cancels.");
    }

    public void CancelCropPick()
    {
        if (!_cropArmed) return;
        _cropArmed   = false;
        _cropPress   = null;
        _cropConfirm = null;
        _cropToNewCell = null;
        OnPropertyChanged(nameof(IsPickingCropRectangle));
        RebuildOverlay();
    }

    /// <summary>
    /// brief-dfr27-6 R-dfr27-6-2 — Crop to New Cell: the same drag, but <paramref name="createCell"/> is handed the
    /// computed crop (to ask the name, show the confirmation and call <see cref="CommitCropToNewCell"/>) and this
    /// board is never changed.
    /// </summary>
    public void ArmCropToNewCell(Func<LayoutCropResult, Task> createCell)
    {
        _cropConfirm   = null;
        _cropToNewCell = createCell;
        _cropArmed     = true;
        _cropPress     = null;
        OnPropertyChanged(nameof(IsPickingCropRectangle));
        ReportMessage("Crop to New Cell: drag a box around the part of the board to copy. Every layer is cut to it, " +
                      "hidden and locked ones included, into a new cell; this board is not changed. Escape cancels.");
    }

    private void CropPress(double wx, double wy)
    {
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
        _cropPress = (sx, sy);
        _cropCurX = sx; _cropCurY = sy;
        RebuildOverlay();
    }

    private void CropMove(double wx, double wy, bool leftDown)
    {
        if (_cropPress is null || !leftDown) return;
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
        _cropCurX = sx; _cropCurY = sy;
        RebuildOverlay();
    }

    private void CropRelease(double wx, double wy)
    {
        if (!_cropArmed || _cropPress is not { } p) return;
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);

        // A click encloses nothing, and cropping to it would delete the whole board.
        if (sx == p.X || sy == p.Y)
        {
            _cropPress = null;
            RebuildOverlay();
            ReportWarning("Crop to Rectangle: drag a box — a click encloses no area. Escape cancels.");
            return;
        }

        var confirm = _cropConfirm;
        var toNewCell = _cropToNewCell;
        CancelCropPick();
        var plan = PlanCropToRectangle(p.X, p.Y, sx, sy);
        if (toNewCell is not null) { _ = toNewCell(plan); return; }
        if (confirm is null) { ApplyCrop(plan); return; }
        _ = ConfirmThenApplyAsync(plan, confirm);
    }

    private async Task ConfirmThenApplyAsync(LayoutCropResult plan, Func<LayoutCropResult, string, Task<bool>> confirm)
    {
        if (await confirm(plan, CropSummary(plan))) ApplyCrop(plan);
    }

    /// <summary>The rubber band while the drag is live, for <see cref="RebuildOverlay"/>'s marquee slot.</summary>
    private LayoutMarquee? CropRubberBand =>
        _cropArmed && _cropPress is { } p ? new LayoutMarquee(p.X, p.Y, _cropCurX, _cropCurY) : null;

    /// <summary>The crop of this layout to the box with corners (x1, y1) and (x2, y2), DBU — computed, not applied.</summary>
    public LayoutCropResult PlanCropToRectangle(long x1, long y1, long x2, long y2) =>
        LayoutCrop.Crop(Model.Shapes, Model.Instances,
                        new Bbox(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)), Technology);

    /// <summary>Computes and applies in one call — the tested surface for the whole command.</summary>
    public void CropToRectangle(long x1, long y1, long x2, long y2) => ApplyCrop(PlanCropToRectangle(x1, y1, x2, y2));

    /// <summary>
    /// What the confirmation shows: one row per layer that has anything on it, marked when it is hidden or
    /// locked, then the instances, the bitmaps left alone and the outline. With <paramref name="newCellName"/> it is
    /// Crop to New Cell's: the same rows, headed by the cell they are copied into.
    /// </summary>
    public string CropSummary(LayoutCropResult plan, string? newCellName = null)
    {
        var sb = new StringBuilder();
        string w = LayoutUnits.Format(plan.Rect.MaxX - plan.Rect.MinX, DisplayUnit, Model.DbuPerMicron);
        string h = LayoutUnits.Format(plan.Rect.MaxY - plan.Rect.MinY, DisplayUnit, Model.DbuPerMicron);
        sb.Append(newCellName is null
            ? $"Crop every layer to {w} × {h} {UnitSuffix(DisplayUnit)}:\n"
            : $"Copy every layer, cropped to {w} × {h} {UnitSuffix(DisplayUnit)}, into the new cell '{newCellName}' — " +
              "this board is not changed:\n");
        foreach (var l in plan.Layers)
        {
            var def = ResolveLayerDef(l.Layer);
            string flags = (!def.Visible, !def.Selectable) switch
            {
                (true, true)  => " (hidden, locked)",
                (true, false) => " (hidden)",
                (false, true) => " (locked)",
                _             => "",
            };
            sb.Append(l.IsOutline
                ? $"\n{def.Name}{flags}: board outline redrawn to the crop ({Plural(l.Shapes, "shape", "shapes")} → {l.OutlineAfter})"
                : $"\n{def.Name}{flags}: {Plural(l.Shapes, "shape", "shapes")} — {l.Inside} kept, {l.Cut} cut at the edge, {l.Removed} removed");
        }
        int instances = plan.KeptInstances + plan.RemovedInstances.Count;
        if (instances > 0)
            sb.Append($"\nInstances: {plan.KeptInstances} kept, {plan.RemovedInstances.Count} removed (each follows its origin)");
        if (plan.BitmapsUntouched > 0)
            sb.Append(newCellName is null
                ? $"\n{Plural(plan.BitmapsUntouched, "bitmap", "bitmaps")} left as they are"
                : $"\n{Plural(plan.BitmapsUntouched, "bitmap", "bitmaps")} not copied");
        if (plan.EdgeCutVias > 0)
            sb.Append($"\n{EdgeCutViasSentence(plan.EdgeCutVias)}");
        return sb.ToString();
    }

    /// <summary>
    /// Applies a computed crop as ONE undo entry. Refused when the layout changed after the crop was computed — the
    /// confirmation is a window, and the indices it describes are only valid for the layout it was shown.
    /// </summary>
    public void ApplyCrop(LayoutCropResult plan)
    {
        if (!CropPlanIsCurrent(plan))
        {
            _messageSink?.Warning("Crop to Rectangle: the layout changed before the crop was applied — nothing was cut. Crop again.");
            return;
        }

        var removed = plan.ReplacedIndices.Select((i, k) => (i, plan.Originals[k])).ToList();
        IUiCommand cmd = new Commands.Layout.ReplaceShapesCommand(Model, removed, plan.Shapes, "Crop to Rectangle");
        if (plan.RemovedInstances.Count > 0)
            cmd = new CompositeCommand(new Commands.Layout.DeleteInstancesCommand(Model, plan.RemovedInstances), cmd);
        Execute(cmd);
        SetSelection([]);

        if (plan.AnyCurvedOperand) WarnCurvedOperandOnce("Crop to Rectangle");

        int kept = plan.Layers.Where(l => !l.IsOutline).Sum(l => l.Inside);
        int cut = plan.Layers.Sum(l => l.Cut);
        int gone = plan.Layers.Sum(l => l.Removed);
        string instances = plan.KeptInstances + plan.RemovedInstances.Count == 0 ? ""
            : $"; instances {plan.KeptInstances} kept, {plan.RemovedInstances.Count} removed";
        string outline = plan.Layers.Any(l => l.IsOutline) ? "; board outline redrawn to the crop" : "";
        _messageSink?.Success(
            $"Crop to Rectangle: {Plural(plan.Layers.Count, "layer", "layers")} — {kept} shapes kept, {cut} cut at the " +
            $"edge, {gone} removed{instances}{outline}.");
        if (plan.EdgeCutVias > 0)
            _messageSink?.Info($"Crop to Rectangle: {EdgeCutViasSentence(plan.EdgeCutVias)}");
        if (plan.BitmapsUntouched > 0)
            _messageSink?.Info($"Crop to Rectangle: {Plural(plan.BitmapsUntouched, "bitmap", "bitmaps")} left untouched — a picture is not cropped.");
    }

    /// <summary>R-dfr28-1-2 — the vias the new edge cuts through, said at crop time rather than by the mesher.</summary>
    private static string EdgeCutViasSentence(int n) =>
        $"{Plural(n, "via is", "vias are")} kept with the new edge through {(n == 1 ? "its" : "their")} drill — " +
        "the 3D view builds only the part of each barrel on the board";

    private bool CropPlanIsCurrent(LayoutCropResult plan)
    {
        for (int k = 0; k < plan.ReplacedIndices.Count; k++)
        {
            int i = plan.ReplacedIndices[k];
            if (i >= Model.Shapes.Count || !ReferenceEquals(Model.Shapes[i], plan.Originals[k])) return false;
        }
        return true;
    }

    /// <summary>Where Crop to New Cell writes its cell: beside this one when this layout is a cell's, else at the
    /// workspace root (Group into Cell's folder). Null with neither.</summary>
    public string? CropToNewCellParentDir =>
        CurrentLayoutPath is { Length: > 0 } path &&
        CellFolder.SiblingView(path, ViewType.Layout, ViewType.Layout).CellDir is { } cellDir
            ? Path.GetDirectoryName(cellDir)
            : WorkspaceRootDir;

    /// <summary>Opens a layout this editor just created (Crop to New Cell's cell); wired by the workspace.</summary>
    public Func<string, Task>? OpenCreatedLayout { get; set; }

    /// <summary>
    /// brief-dfr27-6 R-dfr27-6-2: writes <paramref name="plan"/> as a new cell under <paramref name="parentDir"/> —
    /// a cropped COPY of the board: every layer, hidden and locked included, the outline rebuilt, the instances
    /// whose origin is inside, at the coordinates they have here so the copy lines up with the board. Bitmaps are a
    /// picture whose path is relative to this document, so they stay out. <b>This board is not changed and nothing
    /// is placed on it</b>, so there is no undo entry — the whole command is one step and cannot lose anything.
    /// The cell is written through Group into Cell's own path (<see cref="WriteCellLayout"/>) and then opened.
    /// Returns the new layout's path, or null after reporting why.
    /// </summary>
    public string? CommitCropToNewCell(LayoutCropResult plan, string parentDir, string cellName)
    {
        if (!CropPlanIsCurrent(plan))
        {
            _messageSink?.Warning("Crop to New Cell: the layout changed before the cell was written — nothing was created. Crop again.");
            return null;
        }

        string cellDir;
        try { cellDir = CellFolder.CreateCellFolder(parentDir, cellName); }
        catch (Exception ex)
        {
            _messageSink?.Error($"Crop to New Cell: {ex.Message}");
            return null;
        }

        var removed = plan.RemovedInstances.ToHashSet();
        var instances = Model.Instances.Where((_, i) => !removed.Contains(i)).Select(LayoutGeometry.Clone).ToList();
        string layoutPath;
        try
        {
            layoutPath = WriteCellLayout(cellDir, cellName, plan.Shapes, instances,
                                         InstanceBaseDir.Length > 0 ? InstanceBaseDir : parentDir);
        }
        catch (Exception ex)
        {
            TryDeleteCellFolder(cellDir);
            _messageSink?.Error($"Crop to New Cell: {ex.Message}");
            return null;
        }

        if (plan.AnyCurvedOperand) WarnCurvedOperandOnce("Crop to New Cell");
        int cut = plan.Layers.Sum(l => l.Cut);
        _messageSink?.Success(
            $"Crop to New Cell: created '{cellName}' — {Plural(plan.Shapes.Count, "shape", "shapes")} on " +
            $"{Plural(plan.Layers.Count, "layer", "layers")} ({cut} cut at the edge), {Plural(instances.Count, "instance", "instances")}. " +
            "This board is unchanged.");
        if (plan.BitmapsUntouched > 0)
            _messageSink?.Info($"Crop to New Cell: {Plural(plan.BitmapsUntouched, "bitmap", "bitmaps")} not copied — a picture is not cropped.");
        _ = OpenCreatedLayout?.Invoke(layoutPath);
        return layoutPath;
    }
}
