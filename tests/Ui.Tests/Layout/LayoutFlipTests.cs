using CircuitRF.Design.Layout.Text;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-bsp-2 — Flip in the layout editor: a placed part (and the loose shapes selected with it) to the
/// other face of the board, through Mirror's own rigid-body path. One test per claim.
/// </summary>
public sealed class LayoutFlipTests : IDisposable
{
    private readonly string _ws;

    public LayoutFlipTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), "crfFlip_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_ws);
        CellLayoutResolver.InvalidateUnder(_ws);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_ws);
        if (Directory.Exists(_ws)) Directory.Delete(_ws, recursive: true);
    }

    private const string FourLayer = "pcb-4layer_FR-4_62mil_1oz";

    private sealed class Sink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static LayerKey Key(Technology t, string name) => t.Layers.Single(l => l.Name == name).Key;

    private static RectShape Rect(LayerKey layer, long x1, long x2) => new() { Layer = layer, X1 = x1, Y1 = -500, X2 = x2, Y2 = 500 };

    /// <summary>A two-land chip part: lands 1000..2000 and 3000..4000 on top copper, mask and silk over them.</summary>
    private void CreateChip(Technology t, string name = "Chip")
    {
        var cellDir = CellFolder.CreateCellFolder(_ws, name);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1 };
        view.Shapes.Add(Rect(Key(t, "Top Copper"), 1000, 2000));
        view.Shapes.Add(Rect(Key(t, "Top Copper"), 3000, 4000));
        view.Shapes.Add(Rect(Key(t, "Soldermask Top"), 1000, 4000));
        view.Shapes.Add(Rect(Key(t, "Silk Top"), 900, 4100));
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
    }

    private (LayoutEditorViewModel Vm, Sink Sink) Editor(Technology t, LayoutView board)
    {
        var sink = new Sink();
        var vm = new LayoutEditorViewModel(board, Path.Combine(_ws, "board.clay"), sink)
        {
            Technology = t, ActiveTool = LayoutEditorViewModel.Tool.Select,
        };
        return (vm, sink);
    }

    private static LayoutView Board(params LayoutInstance[] parts)
    {
        var board = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1 };
        board.Instances.AddRange(parts);
        return board;
    }

    // ── R-bsp2-2 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlippedChip_LandsOnBottomCopper_MirroredAboutThePivot()
    {
        var t = ShippedTechnologies.Load(FourLayer);
        CreateChip(t);
        var (vm, _) = Editor(t, Board(new LayoutInstance { CellRef = "Chip" }));
        vm.SelectInstance(0);

        Assert.True(vm.FlipSelection());

        var inst = vm.Model.Instances[0];
        Assert.Equal((PlacementSide.Bottom, true), (inst.Side, inst.MirrorX));
        var lands = LayoutFlatten.FlattenOneLevel(inst, _ws, t)!.Shapes
            .Where(s => s.Layer == Key(t, "Bottom Copper")).Cast<RectShape>()
            .Select(r => (Math.Min(r.X1, r.X2), Math.Max(r.X1, r.X2))).OrderBy(x => x).ToList();
        // The part spanned 900..4100 (its silk), pivot 2500: land 1000..2000 lands at 3000..4000 and back.
        Assert.Equal(new[] { (1000L, 2000L), (3000L, 4000L) }, lands);
        Assert.Equal(5000, inst.X);
    }

    [Fact]
    public void FlipTwice_IsTheIdentity_ByteForByteOnTheSavedFile()
    {
        var t = ShippedTechnologies.Load(FourLayer);
        CreateChip(t);
        var board = Board(new LayoutInstance { CellRef = "Chip", X = 1234, Y = -77, RotationDegrees = 30 });
        board.Shapes.Add(new PathShape { Layer = Key(t, "Top Copper"), Width = 300, Xy = [0, 0, 7000, 2100] });
        var (vm, _) = Editor(t, board);
        string before = Path.Combine(_ws, "before.clay"), after = Path.Combine(_ws, "after.clay");
        LayoutPersistence.SaveToFile(before, board);

        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.FlipSelection());
        Assert.True(vm.FlipSelection());

        LayoutPersistence.SaveToFile(after, vm.Model);
        Assert.Equal(File.ReadAllBytes(before), File.ReadAllBytes(after));
    }

    [Fact]
    public void SelectedTrace_GoesToBottomCopperWithThePart_InnerLayerShapeStays()
    {
        var t = ShippedTechnologies.Load(FourLayer);
        CreateChip(t);
        var board = Board(new LayoutInstance { CellRef = "Chip" });
        board.Shapes.Add(new PathShape { Layer = Key(t, "Top Copper"), Width = 300, Xy = [4000, 0, 9000, 0] });
        board.Shapes.Add(Rect(Key(t, "Inner 1"), 0, 9000));
        var (vm, _) = Editor(t, board);

        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.FlipSelection());

        Assert.Equal(PlacementSide.Bottom, vm.Model.Instances[0].Side);
        Assert.Equal(Key(t, "Bottom Copper"), vm.Model.Shapes[0].Layer);
        Assert.Equal(Key(t, "Inner 1"), vm.Model.Shapes[1].Layer);
    }

    // ── R-bsp2-3 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SingleSidedTechnology_FlipIsRefused_AndNothingMoves()
    {
        var t = new Technology
        {
            Name = "One Side",
            Layers = [new LayerDef { Key = new(1, 0), Name = "Top Copper" }],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "M1", DrawingLayers = [new(1, 0)] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", Epsr = 4.0 },
                ],
            },
        };
        var board = Board(new LayoutInstance { CellRef = "Chip", X = 10 });
        var (vm, sink) = Editor(t, board);
        vm.SelectInstance(0);

        Assert.False(vm.FlipAvailability.CanExecute);
        Assert.False(vm.FlipSelection());
        Assert.Equal((10L, PlacementSide.Top, false), (board.Instances[0].X, board.Instances[0].Side, board.Instances[0].MirrorX));
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Error && m.Text.Contains("no bottom copper"));
    }

    [Fact]
    public void MissingBottomSilk_IsReportedForAPart_ButRefusedForASelectedLooseSilkShape()
    {
        var full = ShippedTechnologies.Load(FourLayer);
        CreateChip(full);
        var t = TechPersistence.Clone(full);
        t.Layers.RemoveAll(l => l.Name == "Silk Bottom");

        var board = Board(new LayoutInstance { CellRef = "Chip", RefDes = "R12" });
        var (vm, sink) = Editor(t, board);
        vm.SelectInstance(0);
        Assert.True(vm.FlipSelection());
        Assert.Contains(sink.Posted, m => m.Text ==
            "R12: no bottom silkscreen in this technology, so its silkscreen is not drawn while it is on the bottom.");

        board.Shapes.Add(Rect(Key(t, "Silk Top"), 0, 100));
        vm.SelectAllCommand.Execute(null);
        Assert.False(vm.FlipAvailability.CanExecute);
        Assert.Contains("Silk Top", vm.FlipAvailability.DisabledReason);
        Assert.False(vm.FlipSelection());
        Assert.Equal(PlacementSide.Bottom, board.Instances[0].Side);
    }

    // ── R-bsp2-4 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mirror_CarriesALabelByItsCentre_AndKeepsItReadable()
    {
        var layer = new LayerKey(1, 0);
        var board = Board();
        board.Shapes.Add(new RectShape { Layer = layer, X1 = 0, Y1 = 0, X2 = 10000, Y2 = 1000 });
        var label = new LabelShape { Layer = layer, X = 1000, Y = 2000, Text = "R1", Height = 1000, RotationDegrees = 30 };
        board.Shapes.Add(label);
        var vm = new LayoutEditorViewModel(board) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        static (double X, double Y) Centre(LabelShape l)
        {
            var (dx, dy) = LayoutTextOutline.CentreOffset(l);
            var (c, s) = LayoutAngle.CosSin(l.RotationDegrees);
            return (l.X + dx * c - dy * s, l.Y + dx * s + dy * c);
        }
        var c0 = Centre(label);

        vm.SelectShapes(board.Shapes.ToList());
        vm.MirrorSelection(horizontal: true);

        var rect = Assert.IsType<RectShape>(vm.Model.Shapes[0]);
        var moved = Assert.IsType<LabelShape>(vm.Model.Shapes[1]);
        // The rect 0..10000 reflects to 2a-10000..2a, which gives the axis a; the centre reflects about it.
        double twoAxis = (rect.X1 + rect.X2 + 10000) / 2.0;
        var c1 = Centre(moved);
        Assert.InRange(Math.Abs(twoAxis - c0.X - c1.X), 0, 1.5);
        Assert.InRange(Math.Abs(c0.Y - c1.Y), 0, 1.5);
        // The 30 deg direction reflects to 150, which reads left to right as -30.
        Assert.Equal(330.0, moved.RotationDegrees, 6);
        Assert.Null(moved.HAlign);
    }

    // ── R-bsp2-5 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SideCombo_FlipsThePartAboutItsOwnOrigin()
    {
        var t = ShippedTechnologies.Load(FourLayer);
        CreateChip(t);
        var board = Board(new LayoutInstance { CellRef = "Chip", X = 7000, Y = 300, RotationDegrees = 30 },
                          new LayoutInstance { CellRef = "Chip", X = 20000, Side = PlacementSide.Bottom, MirrorX = true });
        var (vm, _) = Editor(t, board);
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(-1, props.InstanceSideIndex);   // mixed
        vm.SelectInstance(0);
        Assert.Equal(0, props.InstanceSideIndex);
        Assert.Equal("Flip to Bottom", vm.FlipHeader);

        props.InstanceSideIndex = 1;

        var inst = vm.Model.Instances[0];
        Assert.Equal((7000L, 300L, 330.0, true, PlacementSide.Bottom),
                     (inst.X, inst.Y, inst.RotationDegrees, inst.MirrorX, inst.Side));
        Assert.Equal("Flip to Top", vm.FlipHeader);
    }

    // ── R-bsp2-7 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BottomPartsDesignator_OnSilkBottom_Readable_OnTheSameSideOfTheBody()
    {
        var t = ShippedTechnologies.Load(FourLayer);
        CreateChip(t);
        var board = Board(new LayoutInstance { CellRef = "Chip", X = 50000, RefDes = "R1", LabelDx = 2000, LabelDy = 3000 });
        var (vm, _) = Editor(t, board);
        var top = Assert.Single(FootprintLabel.ShapesFor(board, _ws, t, null));

        vm.SelectInstance(0);
        Assert.True(vm.SetSelectedInstancesSide(PlacementSide.Bottom));
        var bottom = Assert.Single(FootprintLabel.ShapesFor(vm.Model, _ws, t, null));

        Assert.Equal(Key(t, "Silk Top"), top.Layer);
        Assert.Equal(Key(t, "Silk Bottom"), bottom.Layer);
        Assert.Equal(0.0, bottom.RotationDegrees);                         // reads left to right
        Assert.Equal((48000L, 3000L), (bottom.X, bottom.Y));                // the offset mirrored with the part
        Assert.Equal((52000L, 3000L), (top.X, top.Y));
    }

    // ── R-bsp2-8 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HidingSilkBottom_HidesABottomPartsDesignator_AndOnlyThat()
    {
        var t = TechPersistence.Clone(ShippedTechnologies.Load(FourLayer));
        CreateChip(t);
        var board = Board(new LayoutInstance
        {
            CellRef = "Chip", RefDes = "R1", Side = PlacementSide.Bottom, MirrorX = true,
            LabelDx = 2500, LabelDy = 3000, LabelHeight = 2000,   // in frame and legible at this zoom
        });
        string Picture(bool shown)
        {
            board.Instances[0].ShowRefDes = shown ? null : false;
            using var stream = new SKDynamicMemoryWStream();
            var vp = new LayoutViewport(-10_000, -10_000, 400.0 / 20_000, 400, 400);
            using (var canvas = SKSvgCanvas.Create(SKRect.Create(0, 0, 400, 400), stream))
                LayoutRenderer.Draw(canvas, board, t, vp,
                    new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _ws });
            using var data = stream.DetachAsData();
            // Skia's SVG device numbers clip paths per process, so two identical frames differ only there.
            return System.Text.RegularExpressions.Regex.Replace(
                System.Text.Encoding.UTF8.GetString(data.ToArray()), @"cl_\d+", "cl");
        }

        Assert.NotEqual(Picture(shown: true), Picture(shown: false));   // drawn while Silk Bottom is visible
        t.Layers.Single(l => l.Name == "Silk Bottom").Visible = false;
        Assert.Equal(Picture(shown: true), Picture(shown: false));      // and gone with it
    }
}
