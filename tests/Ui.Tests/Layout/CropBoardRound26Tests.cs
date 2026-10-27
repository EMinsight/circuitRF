using System.Linq;
using Clipper2Lib;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-dfr26-2: Crop to Rectangle (every layer, one undo entry, holes kept, vias and instances by their anchor, the
/// outline rebuilt as the intersection) and Group into Cell's technology reference and left-out shapes; brief-dfr27-6:
/// Crop to New Cell, the plane named first, and Include them.
/// </summary>
[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class CropBoardRound26Tests : IDisposable
{
    private static readonly LayerKey Shown   = new(1, 0);
    private static readonly LayerKey Hidden  = new(2, 0);
    private static readonly LayerKey Locked  = new(3, 0);
    private static readonly LayerKey Outline = new(4, 0);

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crfCrop26_" + Guid.NewGuid().ToString("N")[..8]);

    public CropBoardRound26Tests()
    {
        Directory.CreateDirectory(_ws);
        CellLayoutResolver.InvalidateUnder(_ws);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_ws);
        if (Directory.Exists(_ws)) Directory.Delete(_ws, recursive: true);
    }

    private sealed class Sink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static Technology Tech() => new()
    {
        Name = "T",
        Layers =
        [
            new LayerDef { Key = Shown,   Name = "Top" },
            new LayerDef { Key = Hidden,  Name = "Plane", Visible = false },
            new LayerDef { Key = Locked,  Name = "Inner", Selectable = false },
            new LayerDef { Key = Outline, Name = "Edge",
                           Interchange = new InterchangeMapping(null, null, null, null, null, PcbLayerName: "Edge.Cuts") },
        ],
    };

    private static (LayoutEditorViewModel Vm, LayoutView Model, Sink Sink) Editor(
        Action<LayoutView> populate, string? clayPath = null)
    {
        var model = new LayoutView { DbuPerMicron = 1000 };
        populate(model);
        var sink = new Sink();
        var vm = new LayoutEditorViewModel(model, clayPath, sink) { Technology = Tech() };
        return (vm, model, sink);
    }

    private static double Area(LayoutShape s) => Math.Abs(Clipper.Area(LayoutClipper.ToClipperPaths(s, 1)));

    [Fact]
    public void Crop_CutsHiddenAndLockedLayersToo_InOneUndoEntry()
    {
        var (vm, model, _) = Editor(m =>
        {
            foreach (var layer in new[] { Shown, Hidden, Locked })
                m.Shapes.Add(new RectShape { Layer = layer, X1 = 0, Y1 = 0, X2 = 2000, Y2 = 500 });
        });
        var before = model.Shapes.ToList();

        vm.CropToRectangle(0, 0, 1000, 1000);

        Assert.Equal(3, model.Shapes.Count);
        Assert.All(model.Shapes, s => Assert.Equal(1000, LayoutGeometry.BboxOf(s).MaxX));
        Assert.Equal(new[] { Shown, Hidden, Locked }, model.Shapes.Select(s => s.Layer));
        Assert.Single(vm.UndoRedo.Entries);

        vm.UndoRedo.Undo();
        Assert.Equal(before, model.Shapes);   // the same objects, in order: one entry undid all three layers
    }

    [Fact]
    public void Crop_APlaneHalfInside_KeepsTheHoleOnTheKeptSide()
    {
        var (vm, model, _) = Editor(m => m.Shapes.Add(new PolygonShape
        {
            Layer = Hidden,
            Xy = [0, 0, 2000, 0, 2000, 1000, 0, 1000],
            Holes = [[200, 400, 400, 400, 400, 600, 200, 600], [1500, 400, 1700, 400, 1700, 600, 1500, 600]],
        }));

        vm.CropToRectangle(0, 0, 1000, 1000);

        var plane = Assert.IsType<PolygonShape>(Assert.Single(model.Shapes));
        Assert.Single(plane.Holes!);
        Assert.Equal(1000.0 * 1000 - 200 * 200, Area(plane));
    }

    [Fact]
    public void Crop_ViasAndInstancesFollowTheirAnchor()
    {
        var (vm, model, sink) = Editor(m =>
        {
            m.Shapes.Add(new ViaShape { Layer = Shown, X = 500, Y = 500, DrillSize = 100 });
            m.Shapes.Add(new ViaShape { Layer = Shown, X = 1500, Y = 500, DrillSize = 100 });
            m.Instances.Add(new LayoutInstance { CellRef = "In",  X = 900,  Y = 500, Mag = 1 });   // origin in, body past the edge
            m.Instances.Add(new LayoutInstance { CellRef = "Out", X = 1100, Y = 500, Mag = 1 });   // origin out, body over the edge
        });
        var inside = model.Shapes[0];

        vm.CropToRectangle(0, 0, 1000, 1000);

        Assert.Same(inside, Assert.Single(model.Shapes));
        Assert.Equal("In", Assert.Single(model.Instances).CellRef);
        Assert.Contains(sink.Posted, p => p.Text.Contains("instances 1 kept, 1 removed"));
    }

    /// <summary>brief-dfr28-1 R-dfr28-1-2 — a via kept on the new edge is counted, in the confirmation and the result.</summary>
    [Fact]
    public void Crop_SaysWhenTheNewEdgeCutsThroughAKeptViasDrill()
    {
        var (vm, model, sink) = Editor(m =>
        {
            m.Shapes.Add(new ViaShape { Layer = Shown, X = 500, Y = 1000, DrillSize = 100 });   // centre on the edge
            m.Shapes.Add(new ViaShape { Layer = Shown, X = 500, Y = 500, DrillSize = 100 });    // wholly inside
        });

        var plan = vm.PlanCropToRectangle(0, 0, 1000, 1000);
        Assert.Equal(1, plan.EdgeCutVias);
        Assert.Contains("1 via is kept with the new edge through its drill", vm.CropSummary(plan));

        vm.ApplyCrop(plan);

        Assert.Equal(2, model.Shapes.Count);
        Assert.Contains(sink.Posted, p => p.Text.Contains("1 via is kept with the new edge through its drill"));
    }

    [Fact]
    public void Crop_RedrawsTheOutlineAsItsIntersectionWithTheRectangle()
    {
        // A Gerber outline: one stroke round a 10 mm square, on the edge.
        var (vm, model, _) = Editor(m => m.Shapes.Add(new PathShape
        {
            Layer = Outline, Width = 200, End = PathEndStyle.Round,
            Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000, 0, 0],
        }));

        vm.CropToRectangle(5_000, 2_000, 15_000, 8_000);

        var line = Assert.IsType<PathShape>(Assert.Single(model.Shapes));
        Assert.Equal(200, line.Width);
        Assert.Equal(new Bbox(5_000, 2_000, 10_000, 8_000), LayoutGeometry.BboxOf(new PolygonShape { Xy = line.Xy }));
        Assert.Equal((line.Xy[0], line.Xy[1]), (line.Xy[^2], line.Xy[^1]));   // closed
    }

    [Fact]
    public void GroupIntoCell_FromACellTwoFoldersDeep_WritesATechRefThatResolvesToTheParentsTechnology()
    {
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{}");
        string techPath = Path.Combine(_ws, "tech", "board.ctech");
        Directory.CreateDirectory(Path.GetDirectoryName(techPath)!);
        TechPersistence.SaveToFile(techPath, Tech());

        string parentCell = CellFolder.CreateCellFolder(Path.Combine(_ws, "boards", "rf"), "Board");
        string clay = Path.Combine(CellFolder.SubFolderPath(parentCell, ViewType.Layout), "Board.clay");
        var model = new LayoutView { DbuPerMicron = 1000, TechRef = "../../../../tech/board.ctech" };
        model.Shapes.Add(new RectShape { Layer = Shown, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        LayoutPersistence.SaveToFile(clay, model);

        var vm = new LayoutEditorViewModel(model, clay, new Sink());
        var (resolution, _) = TechnologyResolver.ResolveForDocument(model.TechRef, clay, null, new TechnologyCache());
        Assert.NotNull(resolution.Tech);
        vm.ApplyTechResolution(resolution);
        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.CommitGroupIntoCell(_ws, "Part"));

        string newClay = Path.Combine(_ws, "Part", "layout", "Part.clay");
        var grouped = LayoutPersistence.LoadFromFile(newClay);
        var (again, _) = TechnologyResolver.ResolveForDocument(grouped.TechRef, newClay, null, new TechnologyCache());
        Assert.Equal(Path.GetFullPath(techPath), again.ResolvedPath);
        Assert.NotNull(again.Tech);
    }

    [Fact]
    public void GroupIntoCell_OverASelectionHoldingAGroundFill_KeepsTheFill()
    {
        var (vm, model, _) = Editor(m =>
        {
            m.Shapes.Add(new PolygonShape
            {
                Layer = Shown, Net = "GND",
                Xy = [0, 0, 4000, 0, 4000, 4000, 0, 4000], Holes = [[1000, 1000, 2000, 1000, 2000, 2000, 1000, 2000]],
            });
            m.Shapes.Add(new RectShape { Layer = Shown, X1 = 1200, Y1 = 1200, X2 = 1800, Y2 = 1800 });
        }, Path.Combine(_ws, "top.clay"));
        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.CommitGroupIntoCell(_ws, "Cut"));

        var grouped = LayoutPersistence.LoadFromFile(Path.Combine(_ws, "Cut", "layout", "Cut.clay"));
        var fill = Assert.Single(grouped.Shapes.OfType<PolygonShape>());
        Assert.Equal("GND", fill.Net);
        Assert.Single(fill.Holes!);
    }

    [Fact]
    public void GroupIntoCell_SaysWhichOverlappingShapesWereNotSelected()
    {
        var (vm, model, sink) = Editor(m =>
        {
            m.Shapes.Add(new PolygonShape { Layer = Hidden, Xy = [0, 0, 9000, 0, 9000, 9000, 0, 9000] });   // a fill on a hidden layer
            m.Shapes.Add(new RectShape { Layer = Shown, X1 = 0, Y1 = 1500, X2 = 9000, Y2 = 1600 });         // reaches past the box
            m.Shapes.Add(new RectShape { Layer = Shown, X1 = 1000, Y1 = 1000, X2 = 2000, Y2 = 2000 });
        }, Path.Combine(_ws, "top.clay"));
        // The hidden fill is copper: a stackup conductor draws on its layer.
        var tech = Tech();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "L2", DrawingLayers = [Hidden] });
        vm.Technology = tech;
        MarqueeSelect(vm, 500, 500, 2500, 2500);
        Assert.Equal(new[] { 2 }, vm.SelectedIndices);

        Assert.True(vm.CommitGroupIntoCell(_ws, "Part"));

        var note = Assert.Single(sink.Posted, p => p.Level == MessageLevel.Info && p.Text.StartsWith("Group into Cell:"));
        Assert.StartsWith("Group into Cell: The plane on Plane stays behind", note.Text);   // R-dfr27-6-1: first
        Assert.Contains("1 on hidden or locked layers", note.Text);
        Assert.Contains("1 reaching past", note.Text);
    }

    /// <summary>Armed from the context menu with Select still active, the crop drag has to say so with the crosshair:
    /// the canvas asks <see cref="LayoutEditorViewModel.IsCanvasPickArmed"/> on every notification
    /// <see cref="LayoutEditorViewModel.IsCanvasPickProperty"/> names, so arming and releasing must both raise one.</summary>
    [Fact]
    public void CropDrag_ArmsTheCrosshairUntilTheBoxIsReleased()
    {
        var (vm, _, _) = Editor(m => m.Shapes.Add(new RectShape { Layer = Shown, X1 = 0, Y1 = 0, X2 = 2000, Y2 = 500 }));
        int notified = 0;
        vm.PropertyChanged += (_, e) => { if (LayoutEditorViewModel.IsCanvasPickProperty(e.PropertyName)) notified++; };

        vm.ArmCropToNewCell(_ => Task.CompletedTask);
        Assert.True(vm.IsCanvasPickArmed);
        Assert.Equal(1, notified);

        vm.OnPointerPressed(100, 100, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerReleased(100, 100, Avalonia.Input.KeyModifiers.None);
        Assert.True(vm.IsCanvasPickArmed);   // a click encloses nothing: still armed, still the crosshair

        vm.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerMoved(1000, 1000, leftDown: true, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerReleased(1000, 1000, Avalonia.Input.KeyModifiers.None);
        Assert.False(vm.IsCanvasPickArmed);
        Assert.Equal(2, notified);

        vm.ArmCropToRectangle(null);
        Assert.True(vm.IsCanvasPickArmed);
        vm.OnKeyDown(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None);
        Assert.False(vm.IsCanvasPickArmed);
        Assert.Equal(4, notified);

        string canvas = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Controls", "LayoutCanvas.cs"));
        Assert.Contains("_viewModel?.IsCanvasPickArmed == true) { SetCursor(StandardCursorType.Cross)", canvas, StringComparison.Ordinal);
        Assert.Contains("LayoutEditorViewModel.IsCanvasPickProperty(e.PropertyName)", canvas, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    private static void MarqueeSelect(LayoutEditorViewModel vm, double x1, double y1, double x2, double y2)
    {
        vm.OnPointerPressed(x1, y1, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerMoved(x2, y2, leftDown: true, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerReleased(x2, y2, Avalonia.Input.KeyModifiers.None);
    }

    [Fact]
    public void GroupIntoCell_IncludeThem_TakesTheLeftOutShapesToo()
    {
        var (vm, _, _) = Editor(m =>
        {
            m.Shapes.Add(new PolygonShape { Layer = Hidden, Xy = [0, 0, 9000, 0, 9000, 9000, 0, 9000] });
            m.Shapes.Add(new RectShape { Layer = Shown, X1 = 0, Y1 = 1500, X2 = 9000, Y2 = 1600 });
            m.Shapes.Add(new RectShape { Layer = Shown, X1 = 1000, Y1 = 1000, X2 = 2000, Y2 = 2000 });
        }, Path.Combine(_ws, "top.clay"));
        MarqueeSelect(vm, 500, 500, 2500, 2500);

        vm.IncludeGroupIntoCellLeftOut();
        Assert.True(vm.CommitGroupIntoCell(_ws, "Part"));

        Assert.Equal(3, LayoutPersistence.LoadFromFile(Path.Combine(_ws, "Part", "layout", "Part.clay")).Shapes.Count);
    }

    [Fact]
    public void CropToNewCell_CopiesWhatCropWouldKeep_AndLeavesTheBoardByteIdentical()
    {
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{}");
        string techPath = Path.Combine(_ws, "tech", "board.ctech");
        Directory.CreateDirectory(Path.GetDirectoryName(techPath)!);
        TechPersistence.SaveToFile(techPath, Tech());

        string parentCell = CellFolder.CreateCellFolder(Path.Combine(_ws, "boards"), "Board");
        string clay = Path.Combine(CellFolder.SubFolderPath(parentCell, ViewType.Layout), "Board.clay");
        var model = new LayoutView { DbuPerMicron = 1000, TechRef = "../../../tech/board.ctech" };
        model.Shapes.Add(new PolygonShape   // a ground fill spanning the box, on a hidden layer
        {
            Layer = Hidden, Net = "GND", Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000],
            Holes = [[1000, 1000, 2000, 1000, 2000, 2000, 1000, 2000]],
        });
        model.Shapes.Add(new RectShape { Layer = Locked, X1 = 3000, Y1 = 3000, X2 = 8000, Y2 = 3500 });
        model.Shapes.Add(new RectShape { Layer = Shown, X1 = 500, Y1 = 500, X2 = 900, Y2 = 900 });
        model.Shapes.Add(new PathShape
        {
            Layer = Outline, Width = 200, End = PathEndStyle.Round, Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000, 0, 0],
        });
        LayoutPersistence.SaveToFile(clay, model);
        byte[] before = File.ReadAllBytes(clay);
        string serialized = LayoutPersistence.Serialize(model);

        LayoutEditorViewModel Open(LayoutView m)
        {
            var (resolution, _) = TechnologyResolver.ResolveForDocument(m.TechRef, clay, null, new TechnologyCache());
            resolution.Tech!.Layers.Single(l => l.Key == Hidden).Visible = false;
            resolution.Tech.Layers.Single(l => l.Key == Locked).Selectable = false;
            var v = new LayoutEditorViewModel(m, clay, new Sink());
            v.ApplyTechResolution(resolution);
            return v;
        }
        var vm = Open(model);
        string? opened = null;
        vm.OpenCreatedLayout = p => { opened = p; return Task.CompletedTask; };

        string? newClay = vm.CommitCropToNewCell(vm.PlanCropToRectangle(0, 0, 5000, 5000), vm.CropToNewCellParentDir!, "Corner");

        Assert.Equal(Path.Combine(_ws, "boards", "Corner", "layout", "Corner.clay"), newClay);   // beside the board's cell
        Assert.Equal(newClay, opened);
        Assert.Equal(serialized, LayoutPersistence.Serialize(model));
        Assert.Equal(before, File.ReadAllBytes(clay));
        Assert.Empty(vm.UndoRedo.Entries);

        var cropped = LayoutPersistence.Deserialize(serialized);
        Open(cropped).CropToRectangle(0, 0, 5000, 5000);
        var copy = LayoutPersistence.LoadFromFile(newClay!);
        static Dictionary<LayerKey, (int, double)> PerLayer(LayoutView v) => v.Shapes.GroupBy(s => s.Layer)
            .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(Area)));
        Assert.Equal(PerLayer(cropped), PerLayer(copy));
        Assert.Equal(4, PerLayer(copy).Count);

        var (again, _) = TechnologyResolver.ResolveForDocument(copy.TechRef, newClay, null, new TechnologyCache());
        Assert.Equal(Path.GetFullPath(techPath), again.ResolvedPath);
    }
}
