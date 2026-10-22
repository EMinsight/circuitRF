using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Cli;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Engine;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.Lvs;

namespace CircuitRF.Ui.Tests;

// brief-dfr22-3-layout-round-trip.md — the layout round trip a designer took through a stub match: a body drag snaps
// pins and edges together (R-dfr22-3-1/2), a part on a plane is picked before the plane (R-dfr22-3-3), the pour's
// margin is a choice (R-dfr22-3-4), a ratsnest overlay guides placement (R-dfr22-3-5), and the tee is reviewed by
// the impedance analysis arm by arm (R-dfr22-3-6). One test per claim.
[Collection(LvsCliConsoleCollection.Name)]
public sealed class LayoutRoundTripRound22Tests : IDisposable
{
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Gnd = new(2, 0);
    private const long SnapTol = 3000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-dfr22-3-" + Guid.NewGuid().ToString("N")[..8]);

    public LayoutRoundTripRound22Tests()
    {
        Directory.CreateDirectory(_dir);
        CellLayoutResolver.InvalidateUnder(_dir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_dir);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static Technology Tech() => new()
    {
        Name = "round22",
        Layers =
        [
            new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
            new LayerDef { Key = Gnd, Name = "Plane", Purpose = "conductor" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(500), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Gnd] },
            ],
        },
    };

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>A 10 µm-long line cell with a pin at each end, like a generated MLIN — "Line" beside top.clay.</summary>
    private void WriteLineCell()
    {
        string cellDir = CellFolder.CreateCellFolder(_dir, "Line");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(Rect(Top, 0, -500, 10_000, 500));
        view.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 1000, OutwardDeg = 180, Layer = Top });
        view.Pins.Add(new LayoutPin { Name = "2", X = 10_000, Y = 0, WidthDbu = 1000, OutwardDeg = 0, Layer = Top });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
    }

    private LayoutEditorViewModel Vm(LayoutView model) =>
        new(model, Path.Combine(_dir, "top.clay")) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static LayoutView Model() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    // ── R-dfr22-3-1: a body drag snaps pin to pin ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 10_000)]    // pulled onto the stationary line's pin
    [InlineData(false, 11_000)]   // geometry snap off: where it was dropped, on the grid
    public void BodyDraggingALine_ToNearAPin_ClosesTheGap(bool geometrySnap, long expectedX)
    {
        WriteLineCell();
        var model = Model();
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 0, Y = 0, Mag = 1 });
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 20_000, Y = 0, Mag = 1 });
        var vm = Vm(model);
        vm.GeometrySnapEnabled = geometrySnap;

        // Pressed on the body (no marker within the press's own tolerance); dragged 9.4 µm left, which the grid
        // makes 9 µm — 1 µm short of the other line's pin.
        vm.OnPointerPressed(25_000, 200, KeyModifiers.None, 1, 40);
        Assert.Equal([1], vm.SelectedInstanceIndices);
        vm.OnPointerMoved(15_600, 500, true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(15_600, 500, KeyModifiers.None);

        Assert.Equal(expectedX, model.Instances[1].X);
        Assert.Equal(0, model.Instances[1].Y);
    }

    // ── R-dfr22-3-1/2: an edge lands flush on a facing parallel edge, at a bounded query count ─────────────

    [Fact]
    public void AnEdgeDraggedNearAParallelEdge_LandsOnIt_WithABoundedQueryCount()
    {
        var model = Model();
        model.Shapes.Add(Rect(Top, 0, 0, 10_000, 1000));
        model.Shapes.Add(Rect(Top, 20_000, 0, 30_000, 1000));
        var vm = Vm(model);

        vm.OnPointerPressed(25_000, 300, KeyModifiers.None, 1, 40);
        Assert.Equal([1], vm.SelectedIndices);
        vm.OnPointerMoved(15_700, 500, true, KeyModifiers.None, 40, 0, SnapTol);
        int queries = vm.BodySnapQueriesLastMove;
        vm.OnPointerReleased(15_700, 500, KeyModifiers.None);

        var moved = (RectShape)model.Shapes[1];
        Assert.Equal((10_000L, 0L), (moved.X1, moved.Y1));   // flush across the edge; along it, the pointer's grid delta
        Assert.InRange(queries, 1, LayoutEditorViewModel.BodySnapMaxPins + 1);
    }

    // ── R-dfr22-3-3: a part on a plane is picked first, the plane second ───────────────────────────────

    [Fact]
    public void APressOnALineOverAPlane_PicksTheLine_ThenThePlane()
    {
        WriteLineCell();
        var model = Model();
        model.Shapes.Add(Rect(Gnd, -50_000, -50_000, 50_000, 50_000));
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 0, Y = 0, Mag = 1 });
        var vm = Vm(model);
        vm.GeometrySnapEnabled = false;

        vm.OnPointerPressed(5000, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(5000, 0, KeyModifiers.None);
        Assert.Equal([0], vm.SelectedInstanceIndices);
        Assert.Empty(vm.SelectedIndices);

        vm.OnPointerPressed(5000, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(5000, 0, KeyModifiers.None);
        Assert.Equal([0], vm.SelectedIndices);
        Assert.Empty(vm.SelectedInstanceIndices);
    }

    // ── R-dfr22-3-4: the pour's margin ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePourMargin_IsFiveHeightsByDefault_TenWhenSet_AndAnOutlineStillWins()
    {
        const string fourLayer = "pcb-4layer_FR-4_62mil_1oz";
        var tech = ShippedTechnologies.Load(fourLayer);
        string cellDir = CellFolder.CreateCellFolder(_dir, "Stub");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);
        var schematic = new SchematicEditModel { SchematicDirectory = schematicDir };
        var mlin = new EditableComponent { InstanceName = "ML1", Symbol = SymbolKind.Mlin };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            mlin.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        schematic.Components.Add(mlin);

        GroundPour PourOf(double? heights, LayoutShape? outline = null)
        {
            var target = new LayoutView();
            if (outline is not null) target.Shapes.Add(outline);
            var run = heights is { } h
                ? SchematicToLayoutGenerator.Run(schematic, target, schematicDir, _dir, layoutDir, tech, null, null, pourMarginHeights: h)
                : SchematicToLayoutGenerator.Run(schematic, target, schematicDir, _dir, layoutDir, tech, null, null);
            return Assert.Single(run.GroundArtwork.Pours);
        }

        var byDefault = PourOf(null);
        var five = PourOf(GroundPourPlanner.DefaultMarginHeights);
        var ten = PourOf(10);
        Assert.Equal(5, GroundPourPlanner.DefaultMarginHeights);
        Assert.Equal(five.MarginDbu, byDefault.MarginDbu);
        Assert.Equal(Serialized(five.Shapes), Serialized(byDefault.Shapes));
        Assert.InRange(ten.MarginDbu, 2 * five.MarginDbu - 2, 2 * five.MarginDbu + 2);

        var outlineLayer = CircuitRF.Design.Layout.Em3d.Em3dGenerator.BoardOutlineLayers(tech).First();
        var bounded = PourOf(10, Rect(outlineLayer, -100_000_000, -100_000_000, 100_000_000, 100_000_000));
        Assert.True(bounded.BoundedByOutline);
        Assert.Equal(0, bounded.MarginDbu);
    }

    private static string Serialized(IEnumerable<LayoutShape> shapes)
    {
        var view = new LayoutView();
        view.Shapes.AddRange(shapes);
        return LayoutPersistence.Serialize(view);
    }

    // ── R-dfr22-3-5: the ratsnest ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRatsnest_JoinsUnjoinedPinsOfANet_UntilCopperDoes_AndIsNeverArtwork()
    {
        WriteLineCell();
        var model = Model();
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 0, Y = 0, Mag = 1, SchematicId = "TL1" });
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 20_000, Y = 0, Mag = 1, SchematicId = "TL2" });
        var vm = Vm(model);
        vm.ApplyTechResolution(new TechResolution(Tech(), null, TechResolutionSource.WorkspaceDefault, []));
        var nets = new PdnSchematicNets(null, new Dictionary<string, PdnSchematicPart>(StringComparer.OrdinalIgnoreCase)
        {
            ["TL1"] = new(["1", "2"], ["in", "mid"]),
            ["TL2"] = new(["1", "2"], ["mid", "out"]),
        }, []);

        vm.ShowRatsnestFrom(nets);
        var line = Assert.Single(vm.Overlay.Ratsnest);
        Assert.Equal(("mid", 10_000L, 0L, 20_000L, 0L), (line.Net, Math.Min(line.X0, line.X1), 0L, Math.Max(line.X0, line.X1), 0L));
        Assert.Empty(model.Shapes);

        string clay = Path.Combine(_dir, "top.clay");
        LayoutPersistence.SaveToFile(clay, model);
        Assert.Empty(LayoutPersistence.LoadFromFile(clay).Shapes);

        vm.Execute(new AddShapeCommand(model, Rect(Top, 10_000, -500, 20_000, 500)));
        Assert.Empty(vm.Overlay.Ratsnest);
    }

    /// <summary>
    /// Designer report, round 25: on a ten-layer board every placement froze the window for the whole copper read.
    /// The read runs off the UI thread now, and an answer read before an edit is never drawn: the edit's own read
    /// replaces it.
    /// </summary>
    [Fact]
    public void TheRatsnestRead_LeavesTheUiThread_AndAnAnswerForAnEditedDesignIsNotDrawn()
    {
        WriteLineCell();
        var model = Model();
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 0, Y = 0, Mag = 1, SchematicId = "TL1" });
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 20_000, Y = 0, Mag = 1, SchematicId = "TL2" });
        var vm = Vm(model);
        vm.ApplyTechResolution(new TechResolution(Tech(), null, TechResolutionSource.WorkspaceDefault, []));
        var reads = new System.Collections.Generic.List<(Action Work, System.Threading.Tasks.TaskCompletionSource Done)>();
        vm.ReadRatsnestOffThread = work =>
        {
            var done = new System.Threading.Tasks.TaskCompletionSource();
            reads.Add((work, done));
            return done.Task;
        };
        var nets = new PdnSchematicNets(null, new Dictionary<string, PdnSchematicPart>(StringComparer.OrdinalIgnoreCase)
        {
            ["TL1"] = new(["1", "2"], ["in", "mid"]),
            ["TL2"] = new(["1", "2"], ["mid", "out"]),
        }, []);

        vm.ShowRatsnestFrom(nets);
        Assert.Single(reads);
        Assert.Empty(vm.Overlay.Ratsnest);          // nothing read yet, and nothing waited for

        // Copper joins the two pins while the first read is out. Its answer (one line) describes the design
        // before the edit, so it is dropped and the redraw starts the edit's own read.
        vm.Execute(new AddShapeCommand(model, Rect(Top, 10_000, -500, 20_000, 500)));
        Assert.Single(reads);                       // one read at a time
        reads[0].Work();
        reads[0].Done.SetResult();
        Assert.Equal(2, reads.Count);
        Assert.Empty(vm.Overlay.Ratsnest);

        reads[1].Work();
        reads[1].Done.SetResult();
        Assert.Empty(vm.Overlay.Ratsnest);          // the joined design: no line
        Assert.Equal(2, vm.RatsnestRebuildCount);
    }

    // ── R-dfr22-3-6: the tee is reviewed arm by arm ─────────────────────────────────────────────────────

    /// <summary>A stub-match tee: a through line and an open stub, all one width.</summary>
    private static LayoutShape[] Tee(long w) =>
    [
        Rect(Top, Um(-6000), -w / 2, Um(6000), w / 2),
        Rect(Top, -w / 2, w / 2, w / 2, Um(6000)),
        Rect(Gnd, Um(-8000), Um(-8000), Um(8000), Um(8000)),
    ];

    [Fact]
    public void TheStubMatchTee_ReportsThreeArms_AtTheTarget_InTheCliTextAndJson()
    {
        long w = Um(950);
        var shapes = Tee(w);
        double z0 = TraceImpedanceProbe.Probe([Rect(Top, Um(-6000), -w / 2, Um(6000), w / 2), shapes[2]],
                                              Tech(), LayoutUnits.DefaultDbuPerMicron, 0, 0, Top).Z0Ohms;
        Assert.InRange(z0, 45, 55);

        var report = TraceImpedanceAnalysis.Analyze(shapes, Tech(), LayoutUnits.DefaultDbuPerMicron,
            new TraceImpedanceOptions { TargetOhms = 50, Layers = [Top] });
        var tee = Assert.Single(report.AllJunctions);
        Assert.Equal("tee", tee.Kind);
        Assert.Equal(3, tee.Arms.Count);
        foreach (var arm in tee.Arms)
        {
            Assert.Equal(z0, arm.Z0!.Value, z0 * 0.01);
            Assert.Equal(TraceVerdict.Pass, arm.Verdict);
        }
        Assert.Contains("and 1 junction reviewed", report.VerdictSentence, StringComparison.Ordinal);

        TechPersistence.SaveToFile(Path.Combine(_dir, "board.ctech"), Tech());
        var view = new LayoutView { TechRef = "board.ctech" };
        view.Shapes.AddRange(shapes);
        string clay = Path.Combine(_dir, "board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        Assert.Equal(0, InProcess("impedance", clay, "--layers", "Top"));
        Assert.Contains($"{tee.Id}    PASS  tee at", _last, StringComparison.Ordinal);
        InProcess("impedance", clay, "--layers", "Top", "--json");
        Assert.Contains("\"junctions\"", _last, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"tee\"", _last, StringComparison.Ordinal);
    }

    private string _last = "";

    private int InProcess(params string[] args)
    {
        var real = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            JsonRun.Reset();
            int exit = CliEntry.Run(args);
            _last = buffer.ToString();
            return exit;
        }
        finally { Console.SetOut(real); }
    }
}
