using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L1e — Clipper2 booleans/offsets, self-intersection repair, and Flatten to Polygon
/// (docs/sonnet-briefs/brief-L1e-clipper-operations.md); reshaped by Phase L1h
/// (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md): Merge is gone (R-L1h-1 — Union now
/// groups by layer, which is what Merge always did, so keeping both was two names for one command),
/// Flatten to Polygon collapsed to a single always-prompting entry (R-L1h-2), and every enablement
/// predicate below now answers via <see cref="LayoutCommandAvailability"/> (R-L1h-3) instead of a
/// bare bool, so a disabled context-menu item always carries its reason. All geometry lives in
/// <see cref="LayoutBooleans"/>/<see cref="LayoutClipper"/>/<see cref="LayoutFlattenToPolygon"/> (pure,
/// framework-free); this file is only selection plumbing + <c>Commands.Layout.ReplaceShapesCommand</c>
/// wiring + Messages reporting. One undo entry per operation, always — every method below builds
/// exactly one <c>ReplaceShapesCommand</c> and calls <see cref="Execute"/> exactly once.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>§3.2 R9e: "warn once per session, not once per operation." A session here is this open
    /// document's lifetime — mirrors <c>_warnedUnknownLayers</c>'s per-document scope above.</summary>
    private bool _warnedCurvedOperandThisSession;

    // ── Enablement (R-L1h-3) ─────────────────────────────────────────────────────

    private IReadOnlyList<int> ValidSelectedIndices =>
        _selectedIndices.Where(i => i >= 0 && i < Model.Shapes.Count).ToList();

    /// <summary>§3 of docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md: a
    /// <see cref="BitmapShape"/> is not geometry — booleans/offset/flatten/repair must exclude it from
    /// their operand set, disabled with a reason for a bitmap-only selection and SILENTLY skipped
    /// (never a crash) in a mixed selection, so those operations apply to the geometric shapes only.
    /// <c>LayoutClipper</c>/<c>LayoutFlattener</c> have no case for it and would throw if one ever
    /// reached them — this is the one filter that keeps that from happening.
    ///
    /// <para><b>R-clip-8 (brief-layout-clip-and-cut-out.md §8): a <see cref="LabelShape"/> and a
    /// <see cref="ViaShape"/> are excluded here for the same reason, and the test is now the POSITIVE
    /// one.</b> This used to name <c>BitmapShape</c> and nothing else, so selecting a label or a via
    /// together with geometry on the same layer and running any boolean threw
    /// <c>ArgumentOutOfRangeException</c> out of a context-menu click — the flattener's own message
    /// says plainly that neither is a filled region; the filter simply never learned about them. Asking
    /// <see cref="LayoutBooleans.IsClipperOperand"/> what the flattener ACCEPTS means a new non-region
    /// shape kind is excluded by default rather than reintroducing that crash.</para></summary>
    private IReadOnlyList<int> GeometricSelectedIndices =>
        ValidSelectedIndices.Where(i => LayoutBooleans.IsClipperOperand(Model.Shapes[i])).ToList();

    /// <summary>The OTHER exclusion, and it is deliberately not the one above: a rigid-body transform
    /// (rotate/mirror — <c>LayoutEditorViewModel.Rotate.cs</c>) moves any shape that has an
    /// orientation, and a <see cref="LabelShape"/> and a <see cref="ViaShape"/> both do — a port label's
    /// whole direction IS its rotation. Only a <see cref="BitmapShape"/> is excluded, because it is an
    /// axis-aligned image with no rotation of its own (R-bmp-3). Sharing one list with the clipper
    /// operand set above would silently stop ports and vias rotating.</summary>
    private IReadOnlyList<int> RotatableSelectedIndices =>
        ValidSelectedIndices.Where(i => Model.Shapes[i] is not BitmapShape).ToList();

    private const string SelectAtLeastOneReason = "Select at least one shape";
    private const string NotGeometryReason = "Bitmaps, labels and vias are not filled regions — select a shape";

    /// <summary>brief-L3a-followups.md §2/R-fix-2's table: "Boolean ops, offset, flatten, repair —
    /// No, an instance is not geometry." Unlike a bitmap (§3, above — silently skipped in a mixed
    /// selection because the OTHER operands are still meaningful shape geometry), an instance mixed
    /// into the selection disables the whole command outright, with a reason NAMING the instance
    /// count, rather than silently operating on the shape subset — R-fix-2's own example wording:
    /// "Boolean operations apply to shapes only; 2 instances selected." A shape-only selection is
    /// completely unaffected (this returns null).</summary>
    private string? ShapeOnlyBlockReason(string opLabel) => _selectedInstanceIndices.Count switch
    {
        0 => null,
        1 => $"{opLabel} apply to shapes only; 1 instance selected.",
        var n => $"{opLabel} apply to shapes only; {n} instances selected.",
    };

    /// <summary>Union/Intersect/Difference/XOR all require this (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md
    /// §1.5) — even though only Union's RESULT is grouped per layer (R-L1h-1), all four boolean ops
    /// need a same-layer pair to operate on; two shapes on different layers have nothing meaningful to
    /// combine (cross-layer combination, when genuinely intended, is Move-to-Layer then Union — two
    /// explicit steps, never one silent one). Bitmaps never count toward this pair (§3, above).</summary>
    private bool SelectionHasSameLayerPair =>
        GeometricSelectedIndices.Select(i => Model.Shapes[i].Layer).GroupBy(k => k).Any(g => g.Count() >= 2);

    /// <summary>
    /// <para><b>The refusals say what to do instead (field report, 2026-09-29).</b> The ordinary thing
    /// to try on a via transition is "merge the via and the trace", and both halves of that selection
    /// refused with a sentence that named no way forward: a generated line (MLIN and its kin) is an
    /// INSTANCE, and a via is not a filled region. Neither needs merging to connect — overlapping copper
    /// on one layer is one conductor to the EM extractor and to connectivity, and a via joins the copper
    /// it lands on in every layer it spans — so that is said, along with the way to weld an instance's
    /// artwork into a shape when that genuinely is what is wanted.</para>
    /// </summary>
    public LayoutCommandAvailability BooleanOpAvailability => ShapeOnlyBlockReason("Boolean operations") is { } r
        ? LayoutCommandAvailability.Disabled(r + " " + AlreadyConnectedNote +
                                             " To weld an instance's artwork into shapes, Flatten Hierarchy it " +
                                             "first — it then stops following its parameters.")
        : SelectionHasSameLayerPair
            ? LayoutCommandAvailability.Enabled
            : LayoutCommandAvailability.Disabled(ValidSelectedIndices.Any(i => Model.Shapes[i] is ViaShape)
                ? "Select 2 or more shapes on the same layer. A via is not a filled region and needs no merge: " +
                  "it joins the copper it lands on in every layer it spans."
                : "Select 2 or more shapes on the same layer");

    private const string AlreadyConnectedNote =
        "Copper that overlaps on one layer is already one conductor to EM and connectivity, so nothing " +
        "needs merging to connect it.";

    public LayoutCommandAvailability OffsetAvailability => ShapeOnlyBlockReason("Offset") is { } r
        ? LayoutCommandAvailability.Disabled(r)
        : GeometricSelectedIndices.Count >= 1
            ? LayoutCommandAvailability.Enabled
            : LayoutCommandAvailability.Disabled(ValidSelectedIndices.Count >= 1 ? NotGeometryReason : SelectAtLeastOneReason);

    public LayoutCommandAvailability FlattenAvailability => ShapeOnlyBlockReason("Flatten to Polygon") is { } r
        ? LayoutCommandAvailability.Disabled(r)
        : ValidSelectedIndices.Any(HasCurvedGeometryAt)
            ? LayoutCommandAvailability.Enabled
            : LayoutCommandAvailability.Disabled("No curved shapes in selection");

    public LayoutCommandAvailability RepairAvailability => ShapeOnlyBlockReason("Repair Self-Intersection") is { } r
        ? LayoutCommandAvailability.Disabled(r)
        : _selectedIndices.Count == 1 && IsSelfIntersecting(_selectedIndices[0])
            ? LayoutCommandAvailability.Enabled
            : LayoutCommandAvailability.Disabled("No self-intersecting shapes in selection");

    /// <summary>brief-L3a-followups.md §2/R-fix-2's table: "Move, nudge, delete, cut/copy/paste,
    /// duplicate — Yes, in a mixed selection." Unlike the shape-only ops above, this one counts BOTH
    /// kinds.</summary>
    public LayoutCommandAvailability CutCopyDeleteDuplicateAvailability => ValidSelectedIndices.Count + SelectedInstanceIndices.Count >= 1
        ? LayoutCommandAvailability.Enabled
        : LayoutCommandAvailability.Disabled(SelectAtLeastOneReason);

    /// <summary>Static, not instance state — Paste's availability depends on the system clipboard,
    /// which this VM never touches directly (the View owns <c>IClipboard</c> traffic); the caller
    /// passes in what it already knows from its own async clipboard peek.</summary>
    public static LayoutCommandAvailability PasteAvailability(bool clipboardHasFragment) => clipboardHasFragment
        ? LayoutCommandAvailability.Enabled
        : LayoutCommandAvailability.Disabled("Clipboard has no layout geometry");

    // Bare-bool aliases — kept for the few call sites/tests that only need the yes/no half.
    public bool CanBooleanOp => BooleanOpAvailability.CanExecute;
    public bool CanOffsetSelection => OffsetAvailability.CanExecute;
    public bool CanFlattenSelection => FlattenAvailability.CanExecute;
    public bool CanRepairSelected => RepairAvailability.CanExecute;

    /// <summary>R-lbl-4/R-lbl-5 (docs/sonnet-briefs/brief-layout-label-fix-and-text-flatten.md): widens
    /// "has something to flatten" from curved geometry alone to ALSO include a non-port
    /// <see cref="LabelShape"/> with text — §3.1 already specified text-to-polygon as an explicit
    /// extension of this SAME command, never a second menu entry. A port label (<c>IsPort</c>) is a
    /// terminal marker (§9/§10.6) — flattening one would silently destroy a port, so it is EXCLUDED
    /// here, not merely "has nothing curved": the same reason Rect/RoundedRect-with-zero-radius are
    /// excluded, extended to cover a shape kind that can never be flattened at all rather than one that
    /// merely isn't curved right now.</summary>
    public bool HasCurvedGeometryAt(int index) =>
        index >= 0 && index < Model.Shapes.Count && IsFlattenEligible(Model.Shapes[index]);

    private static bool IsFlattenEligible(LayoutShape shape) => shape switch
    {
        LabelShape label => !label.IsPort && !string.IsNullOrEmpty(label.Text),
        _                 => LayoutFlattenToPolygon.HasCurvedGeometry(shape),
    };

    public bool IsSelfIntersecting(int shapeIndex) =>
        shapeIndex >= 0 && shapeIndex < Model.Shapes.Count &&
        LayoutSelfIntersection.Test(Model.Shapes[shapeIndex], Technology);

    /// <summary>Whether "Delete Vertex" would actually remove anything — reuses
    /// <see cref="LayoutShapeEditing.RemoveVertex"/> itself (returns null when blocked) rather than
    /// re-deriving the minimum-count rule, so the two can never drift.</summary>
    public LayoutCommandAvailability DeleteVertexAvailability(int shapeIndex, int vertexIndex) =>
        shapeIndex >= 0 && shapeIndex < Model.Shapes.Count &&
        LayoutShapeEditing.RemoveVertex(Model.Shapes[shapeIndex], vertexIndex) is not null
            ? LayoutCommandAvailability.Enabled
            : LayoutCommandAvailability.Disabled("A closed shape needs at least 3 vertices");

    // ── Commands (menu/toolbar entry points; the public methods below are the tested surface) ─────

    public IRelayCommand UnionCommand { get; private set; } = null!;
    public IRelayCommand IntersectCommand { get; private set; } = null!;
    public IRelayCommand DifferenceCommand { get; private set; } = null!;
    public IRelayCommand XorCommand { get; private set; } = null!;
    public IRelayCommand ApplyOffsetCommand { get; private set; } = null!;
    public IRelayCommand RepairSelectedSelfIntersectionCommand { get; private set; } = null!;
    public IRelayCommand FlattenAllCurvesOnCurrentLayerCommand { get; private set; } = null!;
    public IRelayCommand FlattenAllCurvesCommand { get; private set; } = null!;

    private void InitBooleanCommands()
    {
        UnionCommand      = new RelayCommand(ApplyUnion,      () => CanBooleanOp);
        IntersectCommand  = new RelayCommand(ApplyIntersect,  () => CanBooleanOp);
        DifferenceCommand = new RelayCommand(ApplyDifference, () => CanBooleanOp);
        XorCommand        = new RelayCommand(ApplyXor,        () => CanBooleanOp);
        ApplyOffsetCommand = new RelayCommand(ApplyOffsetToSelection, () => CanOffsetSelection);
        RepairSelectedSelfIntersectionCommand = new RelayCommand(
            () => { if (_selectedIndices.Count == 1) RepairSelfIntersection(_selectedIndices[0]); },
            () => CanRepairSelected);
        FlattenAllCurvesOnCurrentLayerCommand = new RelayCommand(() => FlattenAllCurves(CurrentLayerKey, null));
        FlattenAllCurvesCommand = new RelayCommand(() => FlattenAllCurves(null, null));
    }

    // ── Booleans (§3; Union is R-L1h-1) ───────────────────────────────────────

    /// <summary>Union restricted to shapes sharing a layer, applied per layer (R-L1h-1) — every
    /// selected shape across every layer group folds into ONE undo entry. This is what L1e's "Merge"
    /// used to do; Merge itself is gone — for the overwhelmingly common single-layer selection the two
    /// were identical, and two commands differing only by a subtlety nobody reads a tooltip for is
    /// worse than one that does the obviously right thing. Cross-layer combination, when genuinely
    /// intended, is Move-to-Layer then Union.</summary>
    public void ApplyUnion()
    {
        var indices = GeometricSelectedIndices; // §3: a bitmap in the selection is silently excluded
        if (indices.Count == 0) return;

        var operands = indices.Select(i => Model.Shapes[i]).ToList();
        var groups = LayoutBooleans.Merge(operands, Technology);

        var added = new List<LayoutShape>();
        bool anyCurved = false, netsDiffered = false;
        foreach (var (_, result, _) in groups)
        {
            added.AddRange(result.Shapes);
            anyCurved |= result.AnyCurvedOperand;
            netsDiffered |= result.NetsDiffered;
        }

        CommitReplace(indices, added, "Union");

        if (anyCurved) WarnCurvedOperandOnce("Union");
        if (netsDiffered) _messageSink?.Warning("Union: operands were on different nets — net cleared.");

        // R-L1h-3's "no silent no-op": every group's own Union is legitimately enabled and legitimately
        // produced a result, but if no group actually combined anything (each operand stayed its own
        // separate output shape — the disjoint-shapes case, since Clipper2 does not annihilate operands
        // that never touch), nothing visible happened and that must be reported, not silently accepted.
        if (added.Count >= operands.Count)
            _messageSink?.Info("Union: selected shapes did not overlap — nothing was combined.");
    }

    public void ApplyIntersect()  => ApplyBoolean(LayoutBooleans.Intersect,  "Intersect");
    public void ApplyDifference() => ApplyBoolean(LayoutBooleans.Difference, "Difference");
    public void ApplyXor()        => ApplyBoolean(LayoutBooleans.Xor,        "XOR");

    /// <summary>Shared fold for Intersect/Difference/XOR — operands are passed to <paramref name="op"/>
    /// in <see cref="_selectedIndices"/>'s own (click) order, NOT sorted by index: Difference's
    /// "first-selected minus the rest" depends on that order being preserved.</summary>
    private void ApplyBoolean(Func<IReadOnlyList<LayoutShape>, Technology?, LayoutBooleanResult> op, string opName)
    {
        var indices = GeometricSelectedIndices; // §3: a bitmap in the selection is silently excluded
        if (indices.Count < 2) return;

        var operands = indices.Select(i => Model.Shapes[i]).ToList();
        var result = op(operands, Technology);
        CommitReplace(indices, result.Shapes, opName);
        ReportOperandOutcome(result, opName, operands);
    }

    // ── Clip / Cut Out (brief-layout-clip-and-cut-out.md) ─────────────────────

    /// <summary>
    /// R-clip-1: the topmost clippable shape under the right-click that opened the context menu — the
    /// STENCIL. Mirrors <see cref="FindBitmapForContextMenu"/>'s shape exactly (any shape under the
    /// click, not just an already-selected one), because two facts make that gesture exact: a
    /// right-click never disturbs the selection (<c>LayoutCanvas.OnPointerPressed</c>'s right-button
    /// branch only RECORDS the point), and the menu already hit-tests the click for its edge/vertex/
    /// ruler/bitmap items. So the gesture is "select what you want clipped, right-click the tool
    /// shape, choose Clip" — explicit, no extra click, no mode, no ordering convention.
    /// </summary>
    public int? FindClipStencil(double wx, double wy, long tolDbu)
    {
        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);
        foreach (var idx in LayoutHitTest.HitStack(Model, Technology, px, py, tolDbu))
            if (LayoutBooleans.IsClipperOperand(Model.Shapes[idx])) return idx;
        return null;
    }

    /// <summary>The operand set for a clip whose stencil is <paramref name="stencilIndex"/>: every
    /// selected geometric shape EXCEPT the stencil itself. R-clip-1 — the stencil need not be selected,
    /// and when it is (a <c>Select All</c> sweeps it up) it is excluded rather than clipped against
    /// itself. §4 — the operands may sit on any mix of layers: Clip combines nothing, so the same-layer
    /// rule the other booleans need has no meaning here.</summary>
    private IReadOnlyList<int> ClipOperandIndices(int stencilIndex) =>
        ClipSelectedIndices.Where(i => i != stencilIndex).ToList();

    /// <summary>R-clip-10 — Clip and Cut Out take a WIDER operand set than the other booleans:
    /// <see cref="LayoutBooleans.IsClipOperand"/>, which adds the point-anchored kinds (a via, a
    /// label) to the region kinds. Sharing <see cref="GeometricSelectedIndices"/> with Union/Offset/
    /// Flatten is what silently exempted every via on an imported board from a clip of it.</summary>
    private IReadOnlyList<int> ClipSelectedIndices =>
        ValidSelectedIndices.Where(i => LayoutBooleans.IsClipOperand(Model.Shapes[i])).ToList();

    /// <summary>
    /// The operand set for <b>Clip All</b> / <b>Cut Out All</b> (owner, 2026-09-15): every shape the
    /// user COULD have selected, minus the stencil — the selection step removed, since on an imported
    /// board "select the 1,284 shapes I want cropped" is the whole cost of the operation and the answer
    /// is almost always "all of them".
    ///
    /// <para><b>The filter is <see cref="SelectableShapeIndices"/>, not <c>Model.Shapes</c>, and that is
    /// the whole design.</b> It makes Clip All exactly equivalent to Select All then Clip, so there is
    /// one rule to learn rather than two, and it inherits the gate that a hidden or non-selectable
    /// layer is off limits — the same rule the 2026-09-09 Select All fix installed for the same reason
    /// (a layer the user has switched Select off for is precisely what they have said they cannot
    /// touch, and silently cropping artwork on it is the bug that fix closed). Shapes it skips are
    /// COUNTED and reported rather than dropped in silence.</para>
    /// </summary>
    private IReadOnlyList<int> ClipAllOperandIndices(int stencilIndex) =>
        SelectableShapeIndices()
            .Where(i => i != stencilIndex && LayoutBooleans.IsClipOperand(Model.Shapes[i]))
            .ToList();

    /// <summary>
    /// Deliberately independent of the selection — including <see cref="ShapeOnlyBlockReason"/>, which
    /// the selection-scoped <see cref="ClipAvailability"/> applies and this one must not. An instance
    /// mixed into the SELECTION is ambiguous (did the user mean to clip it?), so that command refuses;
    /// an instance merely present in the DOCUMENT is not ambiguous at all, and refusing on it would
    /// disable Clip All on essentially every real board. It is left untouched and reported instead
    /// (<see cref="ReportClipAllSkipped"/>).
    /// </summary>
    public LayoutCommandAvailability ClipAllAvailability(double wx, double wy, long tolDbu)
    {
        if (FindClipStencil(wx, wy, tolDbu) is not { } stencil)
            return LayoutCommandAvailability.Disabled("Right-click the shape to clip to");
        if (ClipAllOperandIndices(stencil).Count == 0)
            return LayoutCommandAvailability.Disabled(
                "Nothing to clip — no other shape is on a visible, selectable layer");
        return LayoutCommandAvailability.Enabled;
    }

    /// <summary>The enabled tooltip for the All variants — the COUNT is the safety signal, and it is
    /// the reason these two carry one at all: "Clips all 1,284 shapes to Rect · Top Copper" is a number
    /// a user can recognise as wrong before clicking, which a bare verb is not.</summary>
    public string ClipAllTooltip(int stencilIndex, bool cutOut)
    {
        int n = ClipAllOperandIndices(stencilIndex).Count;
        string what = cutOut ? "Cuts" : "Clips";
        string prep = cutOut ? "out of" : "to";
        return $"{what} all {Plural(n, "shape", "shapes")} {prep} {ClipStencilLabel(stencilIndex)} " +
               "— the selection is ignored";
    }

    /// <summary>
    /// §7's table, in its stated order. Deliberately NOT <see cref="BooleanOpAvailability"/>: that
    /// requires a same-layer pair, which §4 drops for these two.
    ///
    /// <para>Every disabled case names its remedy (R13a) — and here the reason text is also how a user
    /// learns the gesture, which is why none of them may be silent or hidden.</para>
    /// </summary>
    public LayoutCommandAvailability ClipAvailability(double wx, double wy, long tolDbu)
    {
        if (ShapeOnlyBlockReason("Clip and Cut Out") is { } r) return LayoutCommandAvailability.Disabled(r);
        if (ClipSelectedIndices.Count == 0)
            return LayoutCommandAvailability.Disabled(
                "Select the shapes to clip, then right-click the shape to clip them to");
        if (FindClipStencil(wx, wy, tolDbu) is not { } stencil)
            return LayoutCommandAvailability.Disabled("Right-click the shape to clip to");
        if (ClipOperandIndices(stencil).Count == 0)
            return LayoutCommandAvailability.Disabled(
                "Select the shapes to clip — the right-clicked shape is the stencil, not an operand");
        return LayoutCommandAvailability.Enabled;
    }

    /// <summary>R-clip-3: the stencil named by kind AND layer — <c>"Rect · Top Copper"</c> — so a
    /// mis-aimed right-click is visible BEFORE the click, not after. Used by the enabled tooltip and
    /// by every Messages sentence below. The menu HEADERS are the bare verbs (owner, 2026-09-09), so
    /// the tooltip is where the stencil is named on the way in.</summary>
    public string ClipStencilLabel(int stencilIndex) =>
        stencilIndex >= 0 && stencilIndex < Model.Shapes.Count
            ? $"{ShapeTypeName(Model.Shapes[stencilIndex])} · {LayerDisplayName(Model.Shapes[stencilIndex].Layer)}"
            : "";

    /// <summary>The enabled tooltip (R-clip-3): <c>"Clips the 67 selected shapes to Rect · Top
    /// Copper"</c>.</summary>
    public string ClipTooltip(int stencilIndex, bool cutOut)
    {
        int n = ClipOperandIndices(stencilIndex).Count;
        string what = cutOut ? "Cuts" : "Clips";
        string prep = cutOut ? "out of" : "to";
        return $"{what} the {Plural(n, "selected shape", "selected shapes")} {prep} {ClipStencilLabel(stencilIndex)}";
    }

    public void ApplyClip(int stencilIndex)   => ApplyClipCore(stencilIndex, cutOut: false, allShapes: false);
    public void ApplyCutOut(int stencilIndex) => ApplyClipCore(stencilIndex, cutOut: true,  allShapes: false);

    /// <summary>Clip All / Cut Out All — identical to the two above in every respect except which
    /// shapes are the operands (<see cref="ClipAllOperandIndices"/>). Same single undo entry, same
    /// untouched stencil, same per-operand independence.</summary>
    public void ApplyClipAll(int stencilIndex)   => ApplyClipCore(stencilIndex, cutOut: false, allShapes: true);
    public void ApplyCutOutAll(int stencilIndex) => ApplyClipCore(stencilIndex, cutOut: true,  allShapes: true);

    /// <summary>
    /// R-clip-7: ONE undo entry — a single <c>ReplaceShapesCommand</c> over the whole operand set,
    /// built and executed exactly once, exactly as every other method in this file does.
    ///
    /// <para><b>R-clip-2 — the stencil is NOT consumed.</b> It is a tool, not an operand, so it is
    /// neither removed nor re-added and the undo entry never touches it. That is what makes the common
    /// case work: clip Top Copper, then select Bottom Copper and clip again with the same rect.
    /// Deleting it afterwards is one keystroke; recreating it is not.</para>
    ///
    /// <para><b>R-clip-0 — the operands are the SELECTION and nothing else.</b> Unselected geometry is
    /// never touched, on any layer, including geometry on the stencil's own layer that the stencil
    /// overlaps. <paramref name="allShapes"/> is the one documented departure, and it is a SEPARATE
    /// menu entry rather than a mode: Clip All names its own scope, so neither command's meaning
    /// depends on state the user cannot see at the moment they click.</para>
    /// </summary>
    private void ApplyClipCore(int stencilIndex, bool cutOut, bool allShapes)
    {
        if (stencilIndex < 0 || stencilIndex >= Model.Shapes.Count) return;
        var stencil = Model.Shapes[stencilIndex];
        if (!LayoutBooleans.IsClipperOperand(stencil)) return;

        var indices = allShapes ? ClipAllOperandIndices(stencilIndex) : ClipOperandIndices(stencilIndex);
        if (indices.Count == 0) return;

        var operands = indices.Select(i => Model.Shapes[i]).ToList();
        string opName = cutOut ? "Cut Out" : "Clip";
        // Resolved BEFORE the commit: CommitReplace removes the operands, so the stencil's INDEX no
        // longer names the stencil afterwards — reporting from it would name the wrong shape, or
        // nothing at all.
        string stencilLabel = ClipStencilLabel(stencilIndex);

        var result = cutOut
            ? LayoutBooleans.CutOut(operands, stencil, Technology)
            : LayoutBooleans.Clip(operands, stencil, Technology);

        CommitReplace(indices, result.Shapes, opName);
        ReportClipOutcome(result, opName, cutOut, operands.Count, stencilLabel, allShapes);
        if (allShapes) ReportClipAllSkipped(opName, stencilIndex);
    }

    /// <summary>
    /// What Clip All did NOT touch, and why. A crop that silently left 189 vias standing outside the
    /// stencil is the defect R-clip-10 already had to fix once; a crop that silently leaves a whole
    /// hidden layer, or every instance on the board, is the same defect with a different cause. Neither
    /// is an error — both are correct — so this is Info, but it is never silent.
    ///
    /// <para>Counted, not guessed: the hidden/locked count is the shapes <see cref="SelectableShapeIndices"/>
    /// rejects, so it cannot drift from the filter it describes.</para>
    /// </summary>
    private void ReportClipAllSkipped(string opName, int stencilIndex)
    {
        if (_messageSink is null) return;

        var selectable = SelectableShapeIndices().ToHashSet();
        int hidden = 0, notGeometry = 0;
        for (int i = 0; i < Model.Shapes.Count; i++)
        {
            if (i == stencilIndex) continue;
            if (!selectable.Contains(i)) hidden++;
            else if (!LayoutBooleans.IsClipOperand(Model.Shapes[i])) notGeometry++;
        }

        var left = new List<string>();
        if (notGeometry > 0) left.Add(Plural(notGeometry, "bitmap", "bitmaps"));
        if (Model.Instances.Count > 0) left.Add(Plural(Model.Instances.Count, "instance", "instances"));
        if (left.Count > 0)
            _messageSink.Info($"{opName} All: {string.Join(" and ", left)} left untouched — {opName} applies to shape geometry.");

        if (hidden > 0)
            _messageSink.Info(
                $"{opName} All: {Plural(hidden, "shape", "shapes")} on hidden or non-selectable layers left untouched.");
    }

    /// <summary>R-clip-3's three sentences. The all-removed case gets its OWN sentence naming the
    /// cause, for the same reason R-clip-4 rewrites Intersect's: an empty result here is a legitimate
    /// outcome and must not read as a failure.</summary>
    private void ReportClipOutcome(LayoutClipResult result, string opName, bool cutOut, int operandCount,
                                   string stencil, bool allShapes)
    {
        // "all 1,284 shapes" vs "1,284 shapes" — the one word that says whether the selection was the
        // operand set, in the record the user reads AFTER the fact rather than the tooltip they read
        // before it.
        string scope = allShapes ? "all " : "";
        if (result.AnyCurvedOperand) WarnCurvedOperandOnce(opName);

        if (result.Shapes.Count == 0)
        {
            string which = allShapes ? "shape" : "selected shape";
            _messageSink?.Warning(cutOut
                ? $"{opName}: every {which} lay entirely inside {stencil} — all {operandCount} were removed."
                : $"{opName}: no {which} overlapped {stencil} — all {operandCount} were removed.");
            return;
        }

        string verb = cutOut ? "cut out of" : "clipped to";
        _messageSink?.Success(
            $"{opName}: {scope}{Plural(operandCount, "shape", "shapes")} {verb} {stencil} — " +
            $"{result.OperandsRemoved} removed, {result.OperandsChanged} changed, {result.OperandsUntouched} unchanged.");
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // ── Offset (§3) ────────────────────────────────────────────────────────────

    private long _offsetDbu;
    [ObservableProperty] private string _offsetText = "";

    /// <summary>Staged dimension field (§1 R6) — unlike the other typed fields on this VM, a negative
    /// value is valid and meaningful here (shrink), so this does not require <c>dbu &gt;= 0</c>.</summary>
    public void CommitOffsetText(string text)
    {
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu))
            _offsetDbu = dbu;
        OffsetText = LayoutUnits.Format(_offsetDbu, DisplayUnit, Model.DbuPerMicron);
    }

    /// <summary>Signed offset applied to each selected shape INDEPENDENTLY (§3) — one shape's result
    /// never depends on another's — folded into a single undo entry across the whole selection.</summary>
    public void ApplyOffsetToSelection()
    {
        var indices = GeometricSelectedIndices; // §3: a bitmap in the selection is silently excluded
        if (indices.Count == 0) return;

        var added = new List<LayoutShape>();
        bool anyCurved = false, anyAnnihilated = false;
        foreach (var i in indices)
        {
            var result = LayoutBooleans.Offset(Model.Shapes[i], _offsetDbu, Technology);
            added.AddRange(result.Shapes);
            anyCurved |= result.AnyCurvedOperand;
            if (result.Shapes.Count == 0) anyAnnihilated = true;
        }

        CommitReplace(indices, added, "Offset");

        if (anyCurved) WarnCurvedOperandOnce("Offset");
        if (anyAnnihilated)
            _messageSink?.Info("Offset removed one or more shapes entirely — the negative offset exceeded their extent.");
    }

    // ── Self-intersection repair (§4) ─────────────────────────────────────────

    /// <summary>A Clipper2 <c>Union</c> of the single shape against nothing — resolves crossings into
    /// a clean simple result, possibly several pieces, or one with holes (§0).</summary>
    public void RepairSelfIntersection(int shapeIndex)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return;
        var result = LayoutBooleans.Repair(Model.Shapes[shapeIndex], Technology);
        CommitReplace([shapeIndex], result.Shapes, "Repair Self-Intersection");
    }

    // ── Flatten to Polygon (§5, collapsed to one always-prompting entry — R-L1h-2; widened to text — R-lbl-4) ──

    /// <summary>The one dispatch point: a non-port <see cref="LabelShape"/> flattens via the
    /// SkiaSharp-glyph-outline + Clipper2-nesting pipeline (<see cref="LayoutTextFlatten"/>, 0..N
    /// polygons — R-lbl-6); everything else keeps using <see cref="LayoutFlattenToPolygon"/>'s single
    /// curved-primitive path (0..1 polygon/path). A port label always yields empty — R-lbl-5, and a
    /// defense-in-depth backstop in case a caller ever bypasses the <see cref="HasCurvedGeometryAt"/>
    /// filter that normally excludes it upstream.</summary>
    private IReadOnlyList<LayoutShape> FlattenOneShape(LayoutShape shape, long tolDbu, bool strokeLabelsAsPolygons = false)
    {
        if (shape is LabelShape label) return FlattenLabel(label, tolDbu, strokeLabelsAsPolygons);
        var flattened = LayoutFlattenToPolygon.FlattenToPolygon(shape, tolDbu);
        return flattened is null ? [] : [flattened];
    }

    /// <summary>
    /// A label as geometry. A STROKE label gives one round-ended path per pen stroke at its pen width by
    /// default, or the strokes' united outlines when <paramref name="strokeLabelsAsPolygons"/> — both from
    /// <c>StrokeText</c>, the geometry the canvas draws (brief-silkscreen-stroke-font.md D7, R-ssf-9). A Sans
    /// label gives its TrueType glyph outlines, as it always has.
    /// </summary>
    private IReadOnlyList<LayoutShape> FlattenLabel(LabelShape label, long tolDbu, bool strokeLabelsAsPolygons)
    {
        if (label.IsPort) return [];
        if (label.Font == LabelFont.Stroke)
            return strokeLabelsAsPolygons
                ? StrokeText.ToPolygons(label, tolDbu, Technology)
                : StrokeText.ToPaths(label, Technology);
        var contours = LayoutTextOutline.BuildGlyphContours(label);
        return LayoutTextFlatten.FlattenContoursToPolygons(contours, tolDbu, label.Layer, label.Net);
    }

    /// <summary>Live vertex-count preview for the "Flatten to Polygon…" dialog — single shape. For a
    /// label this is the TOTAL across every resulting polygon's outer ring + holes (running the full
    /// glyph-outline + Clipper2 pipeline live, same cost the eventual commit pays — the dialog already
    /// recomputes this on every tolerance keystroke for ordinary curved shapes).</summary>
    public int PreviewFlattenVertexCount(int shapeIndex, long tolDbu, bool strokeLabelsAsPolygons = false)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return 0;
        var shape = Model.Shapes[shapeIndex];
        if (shape is LabelShape label)
        {
            int total = 0;
            foreach (var g in FlattenLabel(label, tolDbu, strokeLabelsAsPolygons))
            {
                switch (g)
                {
                    case PolygonShape p:
                        total += p.Xy.Length / 2;
                        if (p.Holes is not null) foreach (var h in p.Holes) total += h.Length / 2;
                        break;
                    case PathShape path:
                        total += path.Xy.Length / 2;
                        break;
                }
            }
            return total;
        }
        return LayoutFlattenToPolygon.PreviewVertexCount(shape, tolDbu);
    }

    /// <summary>Per-shape + total vertex-count preview for the dialog's multi-selection breakdown
    /// (R-L1h-2) — only shapes that actually have something to flatten (<see cref="HasCurvedGeometryAt"/>,
    /// R-lbl-4 widened to include non-port labels) are included; the caller derives the skip count as
    /// <c>indices.Count - result.Count</c>.</summary>
    public IReadOnlyList<(int Index, int VertexCount)> PreviewFlattenVertexCounts(IReadOnlyList<int> shapeIndices, long tolDbu,
                                                                                bool strokeLabelsAsPolygons = false) =>
        shapeIndices.Where(HasCurvedGeometryAt).Select(i => (i, PreviewFlattenVertexCount(i, tolDbu, strokeLabelsAsPolygons))).ToList();

    /// <summary>Flattens every selected shape that has something to flatten, at <paramref name="tolDbu"/> —
    /// silently SKIPS shapes with nothing to flatten (§3.2 R9d), never an error. Port labels (R-lbl-5)
    /// are counted and reported separately — never silently dropped without a trace. One undo entry
    /// for the whole selection (a label expanding to N polygons is still exactly one removed→added
    /// pair within that same single <c>ReplaceShapesCommand</c> — R-lbl-6). <paramref name="tolDbu"/>
    /// is never optional at the call site any more (R-L1h-2: the dialog always prompts) — kept
    /// nullable here only because <c>null</c> is also the harmless "apply nothing, nothing selected"
    /// no-op shape every other Apply* method uses.</summary>
    public void FlattenSelectionToPolygon(long? tolDbu, bool strokeLabelsAsPolygons = false)
    {
        if (tolDbu is not { } tol) return;

        int portLabelsSkipped = _selectedIndices.Count(i =>
            i >= 0 && i < Model.Shapes.Count && Model.Shapes[i] is LabelShape { IsPort: true });

        var indices = _selectedIndices.Where(HasCurvedGeometryAt).OrderBy(i => i).ToList();
        if (indices.Count == 0)
        {
            if (portLabelsSkipped > 0) ReportPortLabelsSkipped(portLabelsSkipped);
            return;
        }

        var removed = new List<(int Index, LayoutShape Before)>();
        var added = new List<LayoutShape>();
        foreach (var i in indices)
        {
            var shape = Model.Shapes[i];
            var flattened = FlattenOneShape(shape, tol, strokeLabelsAsPolygons);
            if (flattened.Count == 0) continue;
            removed.Add((i, shape));
            added.AddRange(flattened);
        }
        if (removed.Count == 0)
        {
            if (portLabelsSkipped > 0) ReportPortLabelsSkipped(portLabelsSkipped);
            return;
        }

        int insertAt = removed.Min(r => r.Index);
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, added, "Flatten to Polygon"));
        SetSelection(Enumerable.Range(insertAt, added.Count));
        if (portLabelsSkipped > 0) ReportPortLabelsSkipped(portLabelsSkipped);
    }

    private void ReportPortLabelsSkipped(int count) =>
        _messageSink?.Warning(count == 1
            ? "Flatten: 1 port label was skipped — flattening a port would destroy it."
            : $"Flatten: {count} port labels were skipped — flattening a port would destroy it.");

    /// <summary>"Flatten All Curves" (§5) — on one layer (<paramref name="layerFilter"/> non-null) or
    /// the whole layout (null), for pre-export cleanup. Also routes through the shared dialog
    /// (R-L1h-2: "prompting once and applying the entered value to every affected shape") —
    /// <paramref name="tolDbuOverride"/> non-null means the dialog's value; null falls back to each
    /// shape's own resolved tolerance (the pre-dialog behavior, kept for callers that bypass the
    /// prompt entirely, e.g. a future keyboard shortcut).</summary>
    public void FlattenAllCurves(LayerKey? layerFilter, long? tolDbuOverride)
    {
        var indices = new List<int>();
        for (int i = 0; i < Model.Shapes.Count; i++)
        {
            if (layerFilter is { } lf && Model.Shapes[i].Layer != lf) continue;
            if (HasCurvedGeometryAt(i)) indices.Add(i);
        }
        if (indices.Count == 0) return;

        var removed = new List<(int Index, LayoutShape Before)>();
        var added = new List<LayoutShape>();
        foreach (var i in indices)
        {
            var shape = Model.Shapes[i];
            long tol = tolDbuOverride ?? LayoutFlattener.ResolveTolDbu(shape, Technology);
            var flattened = FlattenOneShape(shape, tol);
            if (flattened.Count == 0) continue;
            removed.Add((i, shape));
            added.AddRange(flattened);
        }
        if (removed.Count == 0) return;

        string desc = layerFilter is { } l ? $"Flatten All Curves ({LayerDisplayName(l)})" : "Flatten All Curves";
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, added, desc));
        _messageSink?.Success($"{desc}: {added.Count} shape(s) flattened.");
    }

    // ── Shared commit + reporting ─────────────────────────────────────────────

    private void CommitReplace(IReadOnlyList<int> removedIndices, IReadOnlyList<LayoutShape> added, string description)
    {
        var removed = removedIndices.Select(i => (i, Model.Shapes[i])).ToList();
        int insertAt = removed.Count > 0 ? removed.Min(r => r.i) : Model.Shapes.Count;
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, added, description));
        SetSelection(Enumerable.Range(insertAt, added.Count));
    }

    private void WarnCurvedOperandOnce(string opName)
    {
        if (_warnedCurvedOperandThisSession) return;
        _warnedCurvedOperandThisSession = true;
        _messageSink?.Warning($"{opName}: curved operand(s) were flattened to build this result.");
    }

    private void ReportOperandOutcome(LayoutBooleanResult result, string opName, IReadOnlyList<LayoutShape> operands)
    {
        if (opName == "Difference" && operands.Count > 0)
            _messageSink?.Info(
                $"Difference: {ShapeTypeName(operands[0])} · {LayerDisplayName(operands[0].Layer)} " +
                $"(selected first) minus {operands.Count - 1} other shape(s).");

        if (result.AnyCurvedOperand) WarnCurvedOperandOnce(opName);

        if (result.NetsDiffered)
            _messageSink?.Warning($"{opName}: operands were on different nets — net cleared.");

        if (result.Shapes.Count == 0)
            _messageSink?.Warning(EmptyResultReason(opName));
    }

    /// <summary>
    /// R-clip-4: an empty boolean result is a LEGITIMATE outcome and must not read as a failure.
    /// "Intersect produced no geometry." was <c>Info</c> severity and did not say why, which is how
    /// the reported gesture — draw a rect over the region of interest, select the whole copper layer,
    /// run Intersect — looked like a bug rather than the correct answer to the question it asked.
    /// Intersect is n-ary (<c>A ∩ B ∩ C ∩ …</c>), so it goes empty at the first disjoint pair and never
    /// reaches the rect; on the reported board 2,185 of 2,278 shape pairs had disjoint bounding boxes.
    /// Its sentence therefore names the cause AND points at <see cref="ApplyClip"/>, which is the
    /// operation that gesture actually wanted.
    /// </summary>
    private static string EmptyResultReason(string opName) => opName switch
    {
        "Intersect" =>
            "Intersect: the selected shapes have no region in common (Intersect is the region shared " +
            "by ALL of them). To keep the part of each shape inside one region, right-click that " +
            "region and use Clip.",
        "Difference" =>
            "Difference: nothing was left — the other selected shapes cover the first one completely.",
        "XOR" =>
            "XOR: the selected shapes cover exactly the same region, so their symmetric difference is empty.",
        _ => $"{opName} produced no geometry.",
    };
}
