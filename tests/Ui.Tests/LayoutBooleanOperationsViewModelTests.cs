using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests;

// ── Phase L1e gates 6, 7, 11, 12, 13: docs/sonnet-briefs/brief-L1e-clipper-operations.md §3/§4/§5
// VM-level: selection wiring, one-undo-entry, restore-at-original-index, Messages reporting.

public class LayoutBooleanOperationsViewModelTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel() => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
    };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Gate 13: one undo entry, restoring all operands at their original indices ───────────────

    [Fact]
    public void Union_OneUndoEntry_RestoresBothOperandsAtOriginalIndices_ByteIdentical()
    {
        var model = FreshModel();
        var before = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        var overlap = new RectShape { Layer = Layer1, X1 = 5000, Y1 = 5000, X2 = 15_000, Y2 = 15_000 };
        var untouched = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 };
        model.Shapes.Add(before);      // index 0
        model.Shapes.Add(overlap);     // index 1
        model.Shapes.Add(untouched);   // index 2

        var jsonBefore = LayoutPersistence.Serialize(model);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 2500, 2500);
        Click(vm, 12_000, 12_000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        vm.ApplyUnion();

        Assert.Equal(2, model.Shapes.Count); // merged shape + the untouched third rect
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();

        Assert.Equal(3, model.Shapes.Count);
        Assert.Same(before, model.Shapes[0]);
        Assert.Same(overlap, model.Shapes[1]);
        Assert.Same(untouched, model.Shapes[2]);
        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
    }

    // ── Remove All Holes (owner request, 2026-10-28) ─────────────────────────────────────────────

    /// <summary>The polygon under the right-click loses every hole and keeps its outline and its place
    /// in the drawing order; one undo puts the holes back. A polygon with none is offered the item
    /// disabled, and anything that is not a polygon is not offered it at all.</summary>
    [Fact]
    public void RemoveAllHoles_StripsTheRightClickedPolygon_InPlace_OneUndo()
    {
        var model = FreshModel();
        var rect  = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 };
        var holed = new PolygonShape
        {
            Layer = Layer1,
            Xy    = [0, 0, 30_000, 0, 30_000, 30_000, 0, 30_000],
            Holes = [[5000, 5000, 10_000, 5000, 10_000, 10_000, 5000, 10_000],
                     [20_000, 20_000, 25_000, 20_000, 25_000, 25_000, 20_000, 25_000]],
        };
        var plain = new PolygonShape { Layer = Layer1, Xy = [50_000, 0, 60_000, 0, 60_000, 10_000] };
        model.Shapes.Add(rect);    // 0
        model.Shapes.Add(holed);   // 1
        model.Shapes.Add(plain);   // 2
        var jsonBefore = LayoutPersistence.Serialize(model);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Assert.Null(vm.FindPolygonUnder(105_000, 5000, 40));                       // a rect: not offered
        Assert.False(vm.RemoveHolesAvailability(58_000, 2000, 40).CanExecute);     // no holes: disabled
        Assert.True(vm.RemoveHolesAvailability(15_000, 15_000, 40).CanExecute);

        vm.ApplyRemoveAllHoles(15_000, 15_000, 40);

        var stripped = Assert.IsType<PolygonShape>(model.Shapes[1]);
        Assert.Null(stripped.Holes);
        Assert.Equal(holed.Xy, stripped.Xy);
        Assert.Same(rect, model.Shapes[0]);
        Assert.Same(plain, model.Shapes[2]);

        vm.UndoRedo.Undo();
        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
    }

    // ── L1h gate 2 (brief-L1h-scale-and-context-menu.md R-L1h-1): Union groups by layer — this is
    // what "Merge" used to do; Merge is deleted, and this is now what Union itself does. ───────────

    [Fact]
    public void ApplyUnion_CrossLayerSelection_UnionsWithinEachLayer_OneShapePerLayer_OneUndoEntry()
    {
        var model = FreshModel();
        var layer2 = new LayerKey(2, 0);
        var a1 = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        var a2 = new RectShape { Layer = Layer1, X1 = 5_000, Y1 = 5_000, X2 = 15_000, Y2 = 15_000 };
        var b1 = new RectShape { Layer = layer2, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 };
        var b2 = new RectShape { Layer = layer2, X1 = 105_000, Y1 = 5_000, X2 = 115_000, Y2 = 15_000 };
        model.Shapes.Add(a1); model.Shapes.Add(a2); model.Shapes.Add(b1); model.Shapes.Add(b2);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.ApplyUnion();

        Assert.Equal(2, model.Shapes.Count); // one union'd shape per layer, both still on their own layer
        Assert.Contains(model.Shapes, s => s.Layer == Layer1);
        Assert.Contains(model.Shapes, s => s.Layer == layer2);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(4, model.Shapes.Count);
        Assert.Same(a1, model.Shapes[0]);
        Assert.Same(a2, model.Shapes[1]);
        Assert.Same(b1, model.Shapes[2]);
        Assert.Same(b2, model.Shapes[3]);
    }

    // ── L1h gate 5: an enabled Union that combines nothing (disjoint same-layer shapes) reports
    // through Messages rather than silently no-op'ing (R-L1h-3). ─────────────────────────────────

    [Fact]
    public void ApplyUnion_DisjointSameLayerShapes_PostsMessagesNote_NothingCombined()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 };
        var b = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 101_000, Y2 = 1_000 }; // far away, no overlap
        model.Shapes.Add(a); model.Shapes.Add(b);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Click(vm, 100_500, 500, KeyModifiers.Shift);

        Assert.True(vm.BooleanOpAvailability.CanExecute); // legitimately enabled — 2 shapes share a layer

        vm.ApplyUnion();

        Assert.Equal(2, model.Shapes.Count); // both shapes survive, untouched, just not combined
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Info && m.Text.Contains("did not overlap"));
    }

    // ── Gate 6: multiple disjoint results, undo restores the single original at its original index ──

    [Fact]
    public void Difference_SplitsIntoTwo_BothAtSensibleIndices_UndoRestoresOriginalAtOriginalIndex()
    {
        var model = FreshModel();
        var before = new RectShape { Layer = Layer1, X1 = -100_000, Y1 = -100_000, X2 = -90_000, Y2 = -90_000 };
        var bar = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 10_000 };
        var strip = new RectShape { Layer = Layer1, X1 = 45_000, Y1 = -10_000, X2 = 55_000, Y2 = 20_000 };
        model.Shapes.Add(before);  // index 0 — never touched by this op
        model.Shapes.Add(bar);     // index 1
        model.Shapes.Add(strip);   // index 2

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 25_000, 5000);              // selects bar (first)
        Click(vm, 50_000, 15_000, KeyModifiers.Shift); // adds strip

        vm.ApplyDifference();

        // before + two split pieces = 3 shapes; both pieces land at index 1/2 (the lowest removed
        // index), before stays at index 0.
        Assert.Equal(3, model.Shapes.Count);
        Assert.Same(before, model.Shapes[0]);
        Assert.IsType<PolygonShape>(model.Shapes[1]);
        Assert.IsType<PolygonShape>(model.Shapes[2]);

        vm.UndoRedo.Undo();

        Assert.Equal(3, model.Shapes.Count);
        Assert.Same(before, model.Shapes[0]);
        Assert.Same(bar, model.Shapes[1]);
        Assert.Same(strip, model.Shapes[2]);
    }

    // ── Gate 7: net propagation reporting ─────────────────────────────────────────

    [Fact]
    public void Union_DifferingNets_ReportsWarning()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, Net = "VCC", X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        var b = new RectShape { Layer = Layer1, Net = "GND", X1 = 5000, Y1 = 5000, X2 = 15_000, Y2 = 15_000 };
        model.Shapes.Add(a);
        model.Shapes.Add(b);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 2500, 2500);
        Click(vm, 12_000, 12_000, KeyModifiers.Shift);

        vm.ApplyUnion();

        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning && m.Text.Contains("net"));
        Assert.Null(model.Shapes[0].Net);
    }

    [Fact]
    public void CurvedOperandWarning_FiresOnlyOncePerSession()
    {
        var model = FreshModel();
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        void UnionCircleWithRect()
        {
            model.Shapes.Clear();
            model.Shapes.Add(new CircleShape { Layer = Layer1, Cx = 5000, Cy = 5000, R = 5000 });
            model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
            Click(vm, 5000, 5000);
            Click(vm, 1000, 1000, KeyModifiers.Shift);
            vm.ApplyUnion();
        }

        UnionCircleWithRect();
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning && m.Text.Contains("flattened"));
        int countAfterFirst = sink.Posted.Count(m => m.Text.Contains("flattened"));

        UnionCircleWithRect();
        int countAfterSecond = sink.Posted.Count(m => m.Text.Contains("flattened"));

        Assert.Equal(countAfterFirst, countAfterSecond); // warned once, not twice
    }

    // ── Offset via the VM's staged dimension field ────────────────────────────────

    [Fact]
    public void Offset_ViaStagedField_GrowsSelection_OneUndoEntry()
    {
        var model = FreshModel();
        var r = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        model.Shapes.Add(r);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 5000, 5000);
        Assert.True(vm.CanOffsetSelection);

        vm.CommitOffsetText("1um"); // 1 um = 1000 dbu at 1000 dbu/um
        vm.ApplyOffsetToSelection();

        var grown = Assert.IsType<PolygonShape>(Assert.Single(model.Shapes));
        Assert.Equal(new Bbox(-1000, -1000, 11_000, 11_000), LayoutGeometry.BboxOf(grown));

        vm.UndoRedo.Undo();
        Assert.Same(r, Assert.Single(model.Shapes));
    }

    [Fact]
    public void Offset_OverShrink_AnnihilatesAndReports()
    {
        var model = FreshModel();
        var r = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(r);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        vm.CommitOffsetText("-10um");
        vm.ApplyOffsetToSelection();

        Assert.Empty(model.Shapes);
        Assert.Contains(sink.Posted, m => m.Text.Contains("removed"));

        vm.UndoRedo.Undo();
        Assert.Same(r, Assert.Single(model.Shapes));
    }

    // ── Gate 12: repair via the VM, L1d self-intersection flag clears ─────────────

    [Fact]
    public void RepairSelfIntersection_ViaVm_ClearsTheFlag()
    {
        var model = FreshModel();
        var bowtie = new PolygonShape { Layer = Layer1, Xy = [0, 0, 100, 100, 100, 0, 0, 100] };
        model.Shapes.Add(bowtie);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 50, 50);
        Assert.True(vm.CanRepairSelected);

        vm.RepairSelfIntersection(vm.SelectedIndices[0]);

        Assert.All(model.Shapes, s => Assert.False(LayoutSelfIntersection.Test(s, null)));

        vm.UndoRedo.Undo();
        Assert.Same(bowtie, Assert.Single(model.Shapes));
    }

    // ── Gate 11: Flatten to Polygon ────────────────────────────────────────────────

    [Fact]
    public void FlattenSelection_Circle_BecomesPolygonWithinTolerance()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 100_000 };
        model.Shapes.Add(circle);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0, tolDbu: 200_000);

        vm.FlattenSelectionToPolygon(1000);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(model.Shapes));
        int n = poly.Xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double dx = poly.Xy[2 * i], dy = poly.Xy[2 * i + 1];
            double dist = System.Math.Sqrt(dx * dx + dy * dy);
            Assert.True(System.Math.Abs(dist - 100_000) <= 1001);
        }
    }

    [Fact]
    public void PreviewFlattenVertexCount_MatchesWhatTheCommandProduces()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 100_000 };
        model.Shapes.Add(circle);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0, tolDbu: 200_000);

        int preview = vm.PreviewFlattenVertexCount(vm.SelectedIndices[0], 1000);
        vm.FlattenSelectionToPolygon(1000);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(model.Shapes));

        Assert.Equal(preview, poly.Xy.Length / 2);
    }

    [Fact]
    public void FlattenSelection_MultiSelection_SilentlySkipsNonCurvedShapes()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        var rect = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 };
        model.Shapes.Add(circle);
        model.Shapes.Add(rect);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0, tolDbu: 20_000);
        Click(vm, 105_000, 5000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        vm.FlattenSelectionToPolygon(1000);

        Assert.Equal(2, model.Shapes.Count);
        Assert.IsType<PolygonShape>(model.Shapes[0]); // the circle flattened
        Assert.Same(rect, model.Shapes[1]);            // the rect untouched, same instance
    }

    [Fact]
    public void FlattenSelection_CurvedPath_StaysAPathShape_WidthAndEndStyleIntact()
    {
        var model = FreshModel();
        var path = new PathShape
        {
            Layer = Layer1, Xy = [0, 0, 10_000, 0, 20_000, 10_000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 }, new LayoutEdge { Kind = EdgeKind.Line }],
            Width = 2000, End = PathEndStyle.Round,
        };
        model.Shapes.Add(path);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 5000, 2000, tolDbu: 4000);

        vm.FlattenSelectionToPolygon(1000);

        var flattened = Assert.IsType<PathShape>(Assert.Single(model.Shapes));
        Assert.Equal(2000, flattened.Width);
        Assert.Equal(PathEndStyle.Round, flattened.End);
        Assert.True(flattened.Edges is null || flattened.Edges.All(e => e.Kind == EdgeKind.Line));
    }

    [Fact]
    public void CanFlattenSelection_FalseWhenNothingCurvedSelected()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Assert.False(vm.CanFlattenSelection);
    }

    [Fact]
    public void FlattenAllCurves_OnLayer_OnlyTouchesThatLayer()
    {
        var model = FreshModel();
        var layer2 = new LayerKey(2, 0);
        var circleOnLayer1 = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        var circleOnLayer2 = new CircleShape { Layer = layer2, Cx = 100_000, Cy = 0, R = 10_000 };
        model.Shapes.Add(circleOnLayer1);
        model.Shapes.Add(circleOnLayer2);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.FlattenAllCurves(Layer1, null);

        Assert.IsType<PolygonShape>(model.Shapes[0]);
        Assert.Same(circleOnLayer2, model.Shapes[1]);
    }

    // ── L1h gate 6: "Flatten to Polygon" collapsed to ONE always-prompting entry (R-L1h-2) ────────

    [Fact]
    public void FlattenSelectionToPolygon_Null_IsANoOp_TheNoDialogVariantIsGone()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        model.Shapes.Add(circle);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0);

        vm.FlattenSelectionToPolygon(null); // the removed no-dialog call shape — must do nothing

        Assert.Same(circle, model.Shapes[0]); // still a Circle, untouched
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── L1h gate 6b: pre-fill chain (own value vs. technology default) ────────────────────────────

    [Fact]
    public void OwnTolDbu_ReturnsShapesOwnValue_WhenSet_NullWhenInherited()
    {
        var withOwn = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 5000, FlattenTolDbu = 250 };
        var inherited = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 5000 };

        Assert.Equal(250, LayoutFlattener.OwnTolDbu(withOwn));
        Assert.Null(LayoutFlattener.OwnTolDbu(inherited));
    }

    [Fact]
    public void PreviewFlattenVertexCounts_SkipsNonCurvedShapes_TotalsMatchIndividualPreviews()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        var rect = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 }; // no curvature
        model.Shapes.Add(circle);
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        var counts = vm.PreviewFlattenVertexCounts(vm.SelectedIndices, 1000);

        var only = Assert.Single(counts); // the Rect is skipped — nothing to flatten
        Assert.Equal(0, only.Index);
        Assert.Equal(vm.PreviewFlattenVertexCount(0, 1000), only.VertexCount);
    }

    // ── Clip / Cut Out (brief-layout-clip-and-cut-out.md §10) ────────────────────
    //
    // The stencil is the shape under the RIGHT-CLICK (R-clip-1), which the canvas passes through as a
    // world point; these tests call FindClipStencil/ClipAvailability with that point directly, which is
    // exactly what LayoutCanvas.AddBooleanAndFlattenMenuItems does with the (wx, wy) it already has.

    private const long ClipTol = 40;

    private static string SerializeOne(LayoutShape shape)
    {
        var view = new LayoutView();
        view.Shapes.Add(shape);
        return LayoutPersistence.Serialize(view);
    }

    /// <summary>§10 gate 2, and the test for the question this whole design turns on — R-clip-0:
    /// GEOMETRY THAT IS NOT SELECTED IS BYTE-IDENTICAL AFTERWARDS, including geometry on the stencil's
    /// OWN layer that the stencil overlaps.</summary>
    [Fact]
    public void Clip_NeverTouchesUnselectedGeometry_EvenOnTheStencilsOwnLayerAndOverlappingIt()
    {
        var model = FreshModel();
        var operand = new RectShape { Layer = Layer1, X1 = -20_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 };
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        // Unselected, on the stencil's own layer, and squarely underneath it.
        var bystander = new RectShape { Layer = Layer1, X1 = 30_000, Y1 = 30_000, X2 = 40_000, Y2 = 40_000 };
        model.Shapes.Add(operand); model.Shapes.Add(stencil); model.Shapes.Add(bystander);
        string bystanderJson = SerializeOne(bystander);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, -10_000, 15_000);                 // select the operand only
        Assert.Equal([0], vm.SelectedIndices);

        vm.ApplyClip(stencilIndex: 1);

        Assert.Contains(bystander, model.Shapes);
        Assert.Equal(bystanderJson, SerializeOne(bystander));
    }

    /// <summary>§10 gate 3 — the stencil designation. Finding it mutates no selection; a stencil that
    /// happens to BE selected is excluded from the operand set rather than clipped against itself; and
    /// a right-click on empty space disables the command with its stated reason rather than guessing a
    /// stencil.</summary>
    [Fact]
    public void FindClipStencil_MutatesNoSelection_AndASelectedStencilIsExcludedFromItsOwnOperandSet()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, X1 = -20_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 };
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        model.Shapes.Add(a); model.Shapes.Add(stencil);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);                    // Select All sweeps the stencil up too
        var before = vm.SelectedIndices.ToList();

        Assert.Equal(1, vm.FindClipStencil(80_000, 80_000, ClipTol));
        Assert.Equal(before, vm.SelectedIndices);              // the query changed nothing

        Assert.True(vm.ClipAvailability(80_000, 80_000, ClipTol).CanExecute);
        vm.ApplyClip(stencilIndex: 1);

        // Only `a` was an operand; the stencil is not clipped against itself and survives (R-clip-2).
        Assert.Same(stencil, model.Shapes.Single(sh => ReferenceEquals(sh, stencil)));
        Assert.DoesNotContain(a, model.Shapes);
    }

    [Fact]
    public void ClipAvailability_RightClickOnEmptySpace_IsDisabledWithItsStatedReason_NotAGuess()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        var avail = vm.ClipAvailability(900_000, 900_000, ClipTol);

        Assert.False(avail.CanExecute);
        Assert.Equal("Right-click the shape to clip to", avail.DisabledReason);
    }

    [Fact]
    public void ClipAvailability_NothingSelected_AndStencilOnlySelection_EachNameTheirOwnRemedy()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var empty = vm.ClipAvailability(50_000, 50_000, ClipTol);
        Assert.False(empty.CanExecute);
        Assert.Equal("Select the shapes to clip, then right-click the shape to clip them to", empty.DisabledReason);

        Click(vm, 50_000, 50_000);   // now the stencil is the ONLY selected shape
        var onlyStencil = vm.ClipAvailability(50_000, 50_000, ClipTol);
        Assert.False(onlyStencil.CanExecute);
        Assert.Equal("Select the shapes to clip — the right-clicked shape is the stencil, not an operand",
            onlyStencil.DisabledReason);
    }

    /// <summary>§10 gate 4 / R-clip-2 — the stencil is a TOOL, not an operand: it survives the
    /// operation byte-identically, and the SAME stencil clips a second, disjoint selection on another
    /// layer in a second operation. That is the common case the rule exists for.</summary>
    [Fact]
    public void Clip_StencilIsNotConsumed_AndClipsASecondLayerInASecondOperation()
    {
        var model = FreshModel();
        var layer2 = new LayerKey(2, 0);
        var top = new RectShape { Layer = Layer1, X1 = -20_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 };
        var bottom = new RectShape { Layer = layer2, X1 = -20_000, Y1 = 60_000, X2 = 20_000, Y2 = 70_000 };
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        model.Shapes.Add(top); model.Shapes.Add(bottom); model.Shapes.Add(stencil);
        string stencilJson = SerializeOne(stencil);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, -10_000, 15_000);
        int stencilIndex = vm.FindClipStencil(80_000, 80_000, ClipTol)!.Value;
        vm.ApplyClip(stencilIndex);

        Assert.Contains(stencil, model.Shapes);
        Assert.Equal(stencilJson, SerializeOne(stencil));

        // Second operation, other layer, same stencil — which is only possible because it survived.
        Click(vm, -10_000, 65_000);
        int stencilIndex2 = vm.FindClipStencil(80_000, 80_000, ClipTol)!.Value;
        Assert.True(vm.ClipAvailability(80_000, 80_000, ClipTol).CanExecute);
        vm.ApplyClip(stencilIndex2);

        Assert.Contains(stencil, model.Shapes);
        Assert.Equal(stencilJson, SerializeOne(stencil));
        var clipped = model.Shapes.OfType<PolygonShape>().Where(sh => sh.Layer == layer2).ToList();
        Assert.Equal(new Bbox(0, 60_000, 20_000, 70_000), LayoutGeometry.BboxOf(Assert.Single(clipped)));
    }

    /// <summary>§10 gate 7 / §4 — one stencil, operands on three layers, ONE operation: each result on
    /// its own operand's layer, and <c>ClipAvailability</c> is enabled for a selection with no
    /// same-layer pair at all (which is exactly what <c>BooleanOpAvailability</c> would refuse).</summary>
    [Fact]
    public void Clip_ThreeLayersNoSameLayerPair_IsEnabled_AndKeepsEachResultOnItsOwnLayer()
    {
        var model = FreshModel();
        var layer2 = new LayerKey(2, 0);
        var layer3 = new LayerKey(3, 0);
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = -20_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 });
        model.Shapes.Add(new RectShape { Layer = layer2, X1 = -20_000, Y1 = 40_000, X2 = 20_000, Y2 = 50_000 });
        model.Shapes.Add(new RectShape { Layer = layer3, X1 = -20_000, Y1 = 70_000, X2 = 20_000, Y2 = 80_000 });
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        model.Shapes.Add(stencil);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, -10_000, 15_000);
        Click(vm, -10_000, 45_000, KeyModifiers.Shift);
        Click(vm, -10_000, 75_000, KeyModifiers.Shift);
        Assert.Equal(3, vm.SelectedIndices.Count);
        Assert.False(vm.BooleanOpAvailability.CanExecute);   // no same-layer pair — the other booleans refuse
        Assert.True(vm.ClipAvailability(80_000, 80_000, ClipTol).CanExecute);

        vm.ApplyClip(vm.FindClipStencil(80_000, 80_000, ClipTol)!.Value);

        var results = model.Shapes.OfType<PolygonShape>().ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal([Layer1, layer2, layer3], results.Select(r => r.Layer));
    }

    /// <summary>§10 gate 8 / R-clip-7 — ONE undo entry restoring every operand at its original index,
    /// byte-identical. The stencil is neither removed nor re-added, so the undo entry never touches
    /// it.</summary>
    [Fact]
    public void Clip_OneUndoEntry_RestoresEveryOperandAtItsOriginalIndex_ByteIdentical()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = -20_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = -20_000, Y1 = 40_000, X2 = 20_000, Y2 = 50_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });  // stencil
        string jsonBefore = LayoutPersistence.Serialize(model);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, -10_000, 15_000);
        Click(vm, -10_000, 45_000, KeyModifiers.Shift);

        vm.ApplyClip(stencilIndex: 2);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();

        Assert.False(vm.UndoRedo.CanUndo);   // exactly one entry, not two
        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
    }

    /// <summary>§10 gate 9 / R-clip-3 — Messages names the stencil by kind AND layer, reports the three
    /// counts, and the all-removed case gets its own sentence instead of reading as a failure.</summary>
    [Fact]
    public void Clip_Messages_NameTheStencilAndReportTheThreeCounts()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = -20_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 }); // straddles
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 30_000, Y1 = 30_000, X2 = 40_000, Y2 = 40_000 });  // inside
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 500_000, Y1 = 0, X2 = 510_000, Y2 = 10_000 });     // outside
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });          // stencil

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, -10_000, 15_000);
        Click(vm, 35_000, 35_000, KeyModifiers.Shift);
        Click(vm, 505_000, 5_000, KeyModifiers.Shift);

        vm.ApplyClip(stencilIndex: 3);

        var posted = Assert.Single(sink.Posted);
        Assert.Equal(MessageLevel.Success, posted.Level);
        Assert.Contains("Rect · ", posted.Text);                       // the stencil, named by kind and layer
        Assert.Contains("1 removed, 1 changed, 1 unchanged.", posted.Text);
    }

    [Fact]
    public void Clip_NothingOverlapped_PostsItsOwnSentence_NotAGenericFailure()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 500_000, Y1 = 0, X2 = 510_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });   // stencil

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 505_000, 5_000);

        vm.ApplyClip(stencilIndex: 1);

        var posted = Assert.Single(sink.Posted);
        Assert.Contains("no selected shape overlapped Rect · ", posted.Text);
        Assert.Contains("all 1 were removed.", posted.Text);
    }

    /// <summary>R-clip-4 — Intersect's empty result is a legitimate outcome, so it now names its CAUSE
    /// and points at Clip instead of reading as a failure. §1's semantics are untouched: it is still
    /// empty.</summary>
    [Fact]
    public void Intersect_DisjointShapes_ReportsWhyItIsEmptyAndPointsAtClip()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 500_000, Y1 = 0, X2 = 501_000, Y2 = 1_000 });

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Click(vm, 500_500, 500, KeyModifiers.Shift);

        vm.ApplyIntersect();

        Assert.Empty(model.Shapes);
        var posted = Assert.Single(sink.Posted);
        Assert.Equal(MessageLevel.Warning, posted.Level);
        Assert.Contains("no region in common", posted.Text);
        Assert.Contains("use Clip", posted.Text);
    }

    /// <summary>§10 gate 10 / R-clip-8, VM half — the LIVE CRASH §8 found: a label or a via selected
    /// together with geometry on the same layer, and any boolean run, threw
    /// <c>ArgumentOutOfRangeException</c> out of a context-menu click. Both kinds, every operation that
    /// builds an operand set.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Booleans_WithALabelOrAViaInTheSelection_DoNotThrow_AndSkipThatShape(bool useLabel)
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 5_000, Y1 = 5_000, X2 = 15_000, Y2 = 15_000 });
        LayoutShape nonRegion = useLabel
            ? new LabelShape { Layer = Layer1, X = 20_000, Y = 20_000, Text = "N1" }
            : new ViaShape { Layer = Layer1, X = 20_000, Y = 20_000, PadSize = 2_000, DrillSize = 1_000 };
        model.Shapes.Add(nonRegion);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(3, vm.SelectedIndices.Count);
        Assert.True(vm.CanBooleanOp);

        vm.ApplyIntersect();                       // used to throw here
        vm.UndoRedo.Undo();
        vm.SelectAllCommand.Execute(null);
        vm.ApplyUnion();
        vm.UndoRedo.Undo();
        vm.SelectAllCommand.Execute(null);
        vm.ApplyXor();
        vm.UndoRedo.Undo();
        vm.SelectAllCommand.Execute(null);
        vm.ApplyDifference();
        vm.UndoRedo.Undo();
        vm.SelectAllCommand.Execute(null);
        vm.ApplyOffsetToSelection();
        vm.UndoRedo.Undo();

        // The non-region shape was skipped, not consumed — it is still in the model, untouched.
        Assert.Contains(nonRegion, model.Shapes);
    }

    /// <summary>
    /// R-clip-10, VM half — the owner-reported defect, at the gesture level: select everything,
    /// right-click a rect, Clip. Before the fix the vias were not in the operand set, so every one of
    /// them survived wherever it sat and the Messages count did not even mention them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clip_OverASelectionOfViasAndPolygons_RemovesTheViasOutsideTheStencil(bool cutOut)
    {
        var model = FreshModel();
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        model.Shapes.Add(stencil);
        var inside = new ViaShape { Layer = Layer1, X = 50_000, Y = 50_000, PadSize = 20_000, DrillSize = 10_000 };
        var outside = new ViaShape { Layer = Layer1, X = 900_000, Y = 900_000, PadSize = 20_000, DrillSize = 10_000 };
        model.Shapes.Add(inside);
        model.Shapes.Add(outside);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);
        int stencilIndex = vm.FindClipStencil(50_000, 50_000, ClipTol) ?? -1;
        Assert.Equal(0, stencilIndex);
        Assert.True(vm.ClipAvailability(50_000, 50_000, ClipTol).CanExecute);

        if (cutOut) vm.ApplyCutOut(stencilIndex); else vm.ApplyClip(stencilIndex);

        Assert.Contains(stencil, model.Shapes);                        // R-clip-2: the stencil survives
        Assert.Equal(cutOut, !model.Shapes.Contains(inside));
        Assert.Equal(cutOut, model.Shapes.Contains(outside));

        // R-clip-7: one undo entry puts both vias back.
        vm.UndoRedo.Undo();
        Assert.Contains(inside, model.Shapes);
        Assert.Contains(outside, model.Shapes);
    }

    /// <summary>A via-only selection is a legitimate clip, not a disabled command — before the fix its
    /// operand set was empty and the menu item said "select the shapes to clip" over a selection of
    /// 189 of them.</summary>
    [Fact]
    public void Clip_WithOnlyViasSelected_IsEnabled()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        model.Shapes.Add(new ViaShape { Layer = Layer1, X = 900_000, Y = 900_000, PadSize = 20_000, DrillSize = 10_000 });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);   // the stencil is swept up too and excluded (R-clip-1)

        Assert.True(vm.ClipAvailability(50_000, 50_000, ClipTol).CanExecute);

        vm.ApplyClip(stencilIndex: 0);
        Assert.Single(model.Shapes);         // only the stencil is left; the outside via is gone
    }

    // ── Clip All / Cut Out All (owner, 2026-09-15) ───────────────────────────────
    //
    // The selection step removed: on an imported board it IS the cost of the operation and the answer
    // is nearly always "all of them". Separate menu entries, not a fallback on an empty selection —
    // so neither command's meaning depends on off-screen state at the moment of the click.

    private static readonly LayerKey Layer2 = new(2, 0);

    /// <summary>The gate that separates Clip All from Clip: it ignores the selection ENTIRELY — both
    /// what is selected and what is not — and the stencil is still a tool, not an operand.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipAll_IgnoresTheSelection_AndStillNeverConsumesTheStencil(bool cutOut)
    {
        var model = FreshModel();
        var inside  = new RectShape { Layer = Layer1, X1 = 10_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 };
        var outside = new RectShape { Layer = Layer2, X1 = 500_000, Y1 = 500_000, X2 = 510_000, Y2 = 510_000 };
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        model.Shapes.Add(inside); model.Shapes.Add(outside); model.Shapes.Add(stencil);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Assert.Empty(vm.SelectedIndices);                                    // nothing selected at all
        Assert.True(vm.ClipAllAvailability(80_000, 80_000, ClipTol).CanExecute);
        Assert.Equal(2, vm.FindClipStencil(80_000, 80_000, ClipTol));

        if (cutOut) vm.ApplyCutOutAll(2); else vm.ApplyClipAll(2);

        Assert.Contains(stencil, model.Shapes);                              // R-clip-2 still holds
        if (cutOut)
        {
            Assert.DoesNotContain(inside, model.Shapes);                     // wholly inside → cut away
            Assert.Contains(outside, model.Shapes);                          // disjoint → untouched
        }
        else
        {
            Assert.Contains(inside, model.Shapes);                           // wholly inside → kept
            Assert.DoesNotContain(outside, model.Shapes);                    // disjoint → cropped away
        }
    }

    /// <summary>The selection-scoped Clip is unchanged by the arrival of the All variants: a selection
    /// of one still clips exactly one, with the other shape byte-identical afterwards. Both commands
    /// coexist on the same stencil; neither is the other's fallback.</summary>
    [Fact]
    public void Clip_AndClipAll_AreSeparateCommands_OnTheSameStencil()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, X1 = -50_000, Y1 = 10_000, X2 = 10_000, Y2 = 20_000 };
        var b = new RectShape { Layer = Layer1, X1 = -50_000, Y1 = 30_000, X2 = 10_000, Y2 = 40_000 };
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        model.Shapes.Add(a); model.Shapes.Add(b); model.Shapes.Add(stencil);
        string bJson = SerializeOne(b);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, -20_000, 15_000);                                          // select `a` only
        Assert.Equal([0], vm.SelectedIndices);

        vm.ApplyClip(stencilIndex: 2);
        Assert.Equal(bJson, SerializeOne(b));                                // R-clip-0: `b` untouched

        // …and Clip All, on the same stencil, does reach it.
        int stencil2 = vm.FindClipStencil(80_000, 80_000, ClipTol)!.Value;
        vm.ApplyClipAll(stencil2);
        Assert.DoesNotContain(b, model.Shapes);
    }

    /// <summary>The whole reason the operand set is <c>SelectableShapeIndices</c> and not
    /// <c>Model.Shapes</c>: Clip All must be exactly Select All then Clip, so artwork on a hidden or
    /// non-selectable layer is off limits to it — the rule the 2026-09-09 Select All fix installed.
    /// And it is not silent about it.</summary>
    [Theory]
    [InlineData(true, false)]   // visible, not selectable
    [InlineData(false, true)]   // hidden, selectable
    public void ClipAll_LeavesHiddenAndNonSelectableLayersAlone_AndSaysSo(bool visible, bool selectable)
    {
        var model = FreshModel();
        var offLimits = new RectShape { Layer = Layer2, X1 = 500_000, Y1 = 500_000, X2 = 510_000, Y2 = 510_000 };
        var stencil = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        var ordinary = new RectShape { Layer = Layer1, X1 = 600_000, Y1 = 0, X2 = 610_000, Y2 = 10_000 };
        model.Shapes.Add(offLimits); model.Shapes.Add(stencil); model.Shapes.Add(ordinary);
        string offLimitsJson = SerializeOne(offLimits);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.Technology = new Technology
        {
            Layers =
            {
                new LayerDef { Key = Layer1, Name = "M1" },
                new LayerDef { Key = Layer2, Name = "M2", Visible = visible, Selectable = selectable },
            },
        };

        vm.ApplyClipAll(vm.FindClipStencil(80_000, 80_000, ClipTol)!.Value);

        Assert.Contains(offLimits, model.Shapes);
        Assert.Equal(offLimitsJson, SerializeOne(offLimits));       // byte-identical, not merely present
        Assert.DoesNotContain(ordinary, model.Shapes);              // an ordinary layer WAS cropped
        Assert.Contains(sink.Posted, p => p.Text.Contains("1 shape on hidden or non-selectable layers"));
    }

    /// <summary>An instance in the DOCUMENT must not disable Clip All — that would disable it on every
    /// real board. It is left untouched and REPORTED, which is the opposite trade from the
    /// selection-scoped command, where an instance in the SELECTION is genuinely ambiguous and refuses
    /// (brief-L3a-followups.md R-fix-2).</summary>
    [Fact]
    public void ClipAll_WithInstancesInTheDocument_RunsAnyway_AndReportsThemAsUntouched()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 500_000, Y1 = 500_000, X2 = 510_000, Y2 = 510_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 4_000, Y = 6_000, Mag = 1.0 });

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);                                   // instance IS selected now

        // The selection-scoped command refuses on it…
        var selAvail = vm.ClipAvailability(80_000, 80_000, ClipTol);
        Assert.False(selAvail.CanExecute);
        Assert.Contains("1 instance selected", selAvail.DisabledReason);

        // …and Clip All does not.
        Assert.True(vm.ClipAllAvailability(80_000, 80_000, ClipTol).CanExecute);
        vm.ApplyClipAll(1);

        Assert.Single(model.Instances);
        Assert.Contains(sink.Posted, p => p.Text.Contains("1 instance left untouched"));
    }

    /// <summary>R-clip-3's rule applied to the new entries: the count IS the safety signal, so the
    /// enabled tooltip carries it and the Messages sentence says "all" rather than reading as though a
    /// selection had been used. A disabled entry still names its own remedy.</summary>
    [Fact]
    public void ClipAllTooltipAndMessage_NameTheScopeAndTheCount()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 10_000, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 30_000, Y1 = 30_000, X2 = 40_000, Y2 = 40_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Assert.Equal("Clips all 2 shapes to Rect · L1/0 — the selection is ignored", vm.ClipAllTooltip(2, cutOut: false));
        Assert.Equal("Cuts all 2 shapes out of Rect · L1/0 — the selection is ignored", vm.ClipAllTooltip(2, cutOut: true));

        vm.ApplyClipAll(2);
        Assert.Contains(sink.Posted, p => p.Text.Contains("Clip: all 2 shapes clipped to Rect · L1/0"));

        // Right-click on empty space names the same remedy the selection-scoped command does.
        var avail = vm.ClipAllAvailability(900_000, 900_000, ClipTol);
        Assert.False(avail.CanExecute);
        Assert.Equal("Right-click the shape to clip to", avail.DisabledReason);
    }

    /// <summary>One undo entry over the whole document, exactly as the selection-scoped command gives
    /// one over the selection — Clip All is not N operations.</summary>
    [Fact]
    public void ClipAll_IsOneUndoEntry_RestoringEveryShapeByteIdentically()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 500_000, Y1 = 0, X2 = 510_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = Layer2, X1 = 600_000, Y1 = 0, X2 = 610_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = -20_000, Y1 = -20_000, X2 = 20_000, Y2 = 20_000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        string jsonBefore = LayoutPersistence.Serialize(model);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.ApplyClipAll(3);
        Assert.NotEqual(jsonBefore, LayoutPersistence.Serialize(model));

        vm.UndoCommand.Execute(null);
        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
    }

    [Fact]
    public void ClipStencil_IsNeverALabelAViaOrABitmap()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "N1" });
        model.Shapes.Add(new ViaShape { Layer = Layer1, X = 0, Y = 0, PadSize = 20_000, DrillSize = 10_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Assert.Null(vm.FindClipStencil(0, 0, ClipTol));
    }

    /// <summary>§10 gate 11 / R-clip-9 — <c>Slice</c> was listed as a shipped command in both the design
    /// note and the user reference and has never existed anywhere in <c>src/Ui</c> or <c>src/Design</c>.
    /// A knife-cut Slice is a real, different operation (a line that divides shapes, conserving area) and
    /// spending the name on "remove what is inside a region" would make it unnameable later.</summary>
    [Fact]
    public void Docs_NoLongerPromiseASliceCommand_AndListClipAndCutOutInstead()
    {
        foreach (var relative in new[] { "docs/design/layout-view.md", "docs/user/src/reference/layout-editor.md" })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), relative));
            Assert.DoesNotContain("Slice", text);
            Assert.Contains("Clip", text);
            Assert.Contains("Cut Out", text);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void FlattenSelectionToPolygon_WithExplicitTolerance_NeverWritesTheToleranceBackOntoAnyShape()
    {
        // R-L1h-2a: the dialog's chosen value is applied via FlattenSelectionToPolygon directly — it
        // must never appear written onto any surviving/replaced shape's own FlattenTolDbu field,
        // because every shape it touches stops being curved (there's nothing left for it to govern).
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        model.Shapes.Add(circle);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0);

        vm.FlattenSelectionToPolygon(1234);

        var flattened = Assert.IsType<PolygonShape>(model.Shapes[0]);
        Assert.NotSame(circle, flattened); // the Circle is gone
        // PolygonShape has no FlattenTolDbu field at all — there is structurally nowhere for the
        // dialog's value to have been "written back" to.
    }
}
