using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests.Layout;

// brief-dfr26-4-layout-ratsnest.md — the ratsnest follows a part through copy, paste and rotate. Two linked parts in
// series, R1 (in, mid) and C1 (mid, out), each a 10 µm line with a pin at each end, and one rat between them on "mid".
// One test per claim.
public sealed class RatsnestRound26Tests : IDisposable
{
    private static readonly LayerKey Top = new(1, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-dfr26-4-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Sink _sink = new();

    private sealed class Sink : IMessageSink
    {
        public List<string> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add(text);
        public void Clear() => Posted.Clear();
    }

    public RatsnestRound26Tests()
    {
        Directory.CreateDirectory(_dir);
        CellLayoutResolver.InvalidateUnder(_dir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_dir);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Technology Tech() => new()
    {
        Name = "round26",
        Layers = [new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" }],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Open,
            Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [Top] }],
        },
    };

    private void WriteLineCell()
    {
        string cellDir = CellFolder.CreateCellFolder(_dir, "Line");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = -500, X2 = 10_000, Y2 = 500 });
        view.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 1000, OutwardDeg = 180, Layer = Top });
        view.Pins.Add(new LayoutPin { Name = "2", X = 10_000, Y = 0, WidthDbu = 1000, OutwardDeg = 0, Layer = Top });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
    }

    private (LayoutView Model, LayoutEditorViewModel Vm) Board(Func<Action, System.Threading.Tasks.Task>? reads = null)
    {
        WriteLineCell();
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 0, Y = 0, Mag = 1, SchematicId = "R1" });
        model.Instances.Add(new LayoutInstance { CellRef = "Line", X = 20_000, Y = 0, Mag = 1, SchematicId = "C1" });
        var vm = new LayoutEditorViewModel(model, Path.Combine(_dir, "top.clay"), _sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        if (reads is not null) vm.ReadRatsnestOffThread = reads;
        vm.OpenSiblingSchematicNames = _ => ["R1", "C1"];   // the cell's schematic, as an open one answers
        vm.ApplyTechResolution(new TechResolution(Tech(), null, TechResolutionSource.WorkspaceDefault, []));
        vm.ShowRatsnestFrom(new PdnSchematicNets(null, new Dictionary<string, PdnSchematicPart>(StringComparer.OrdinalIgnoreCase)
        {
            ["R1"] = new(["1", "2"], ["in", "mid"]),
            ["C1"] = new(["1", "2"], ["mid", "out"]),
        }, []));
        return (model, vm);
    }

    /// <summary>The rats drawn, each with its ends in a fixed order.</summary>
    private static (string Net, long X0, long Y0, long X1, long Y1)[] Rats(LayoutEditorViewModel vm) =>
    [
        .. vm.Overlay.Ratsnest
            .Select(l => (l.X0, l.Y0).CompareTo((l.X1, l.Y1)) <= 0 ? (l.Net, l.X0, l.Y0, l.X1, l.Y1) : (l.Net, l.X1, l.Y1, l.X0, l.Y0))
            .OrderBy(l => l),
    ];

    private static readonly (string, long, long, long, long)[] Start = [("mid", 10_000, 0, 20_000, 0)];

    /// <summary>The schematic names C1 too — it is the component the part is linked to, and is no second claim on
    /// the name. Counting it as one re-minted every Cut then Paste in a cell with a schematic (designer report).</summary>
    [Fact]
    public void CutThenPaste_IsTheSamePart_AndKeepsItsRats()
    {
        var (model, vm) = Board();
        vm.SelectInstance(1);
        var payload = vm.BuildCopyPayload()!;
        vm.CutSelectionAfterCopy();
        Assert.Empty(Rats(vm));

        vm.PasteInstancesInPlace(LayoutFragment.Translate(vm.RebaseFragmentInstances(payload), 0, 30_000));

        Assert.Equal("C1", model.Instances[^1].SchematicId);
        Assert.Equal([("mid", 10_000, 0, 20_000, 30_000)], Rats(vm));
    }

    [Fact]
    public void CopyThenPaste_NamesTheCopyAnew_SaysItHasNoRats_AndLeavesTheOriginalsRats()
    {
        var (model, vm) = Board();
        vm.SelectInstance(1);
        var payload = vm.BuildCopyPayload()!;

        vm.PasteInstancesInPlace(LayoutFragment.Translate(vm.RebaseFragmentInstances(payload), 0, 30_000));

        var copy = model.Instances[^1];
        Assert.Null(copy.SchematicId);
        Assert.Equal("C2", copy.DisplayRefDes);
        Assert.Contains("C1 copied as C2 — not in the schematic, so it has no rats", _sink.Posted);
        Assert.Equal(Start, Rats(vm));
    }

    [Fact]
    public void Rotate_EndsTheRatsOnTheRotatedPins()
    {
        var (_, vm) = Board();
        vm.SelectInstance(0);
        vm.RotateSelection();   // R1 turns about its centre (5, 0) µm: its "mid" pin goes from (10, 0) to (5, 5)

        Assert.Equal([("mid", 5_000, 5_000, 20_000, 0)], Rats(vm));
    }

    [Fact]
    public void UndoOfAPaste_ARotateAndAMove_RestoresTheRats()
    {
        var (model, vm) = Board();
        vm.SelectInstance(1);
        var payload = vm.BuildCopyPayload()!;
        vm.CutSelectionAfterCopy();
        vm.PasteInstancesInPlace(LayoutFragment.Translate(vm.RebaseFragmentInstances(payload), 0, 30_000));
        vm.UndoLast();
        vm.UndoLast();
        Assert.Equal(Start, Rats(vm));

        vm.SelectInstance(0);
        vm.RotateSelection();
        vm.UndoLast();
        Assert.Equal(Start, Rats(vm));

        vm.OnPointerPressed(25_000, 200, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(25_000, 10_200, true, KeyModifiers.None, 40, 0, 3000);
        vm.OnPointerReleased(25_000, 10_200, KeyModifiers.None);
        Assert.Equal(10_000, model.Instances[1].Y);
        vm.UndoLast();
        Assert.Equal(Start, Rats(vm));
    }

    /// <summary>
    /// The stuck rats. On a ten-layer board the copper read takes ~20 s off the UI thread, and the last answer's pins
    /// used to be drawn where they were read for all of it: a moved part's rat stayed behind, a rotated part's ended on
    /// its old pin, a deleted part's was still drawn. Here every read is held back, as a slow one would be.
    /// </summary>
    [Fact]
    public void WhileARead_IsOut_TheRatsFollowTheParts_AndAPartThatIsGoneDrawsNone()
    {
        var reads = new List<(Action Work, System.Threading.Tasks.TaskCompletionSource Done)>();
        var (model, vm) = Board(work =>
        {
            var done = new System.Threading.Tasks.TaskCompletionSource();
            reads.Add((work, done));
            return done.Task;
        });
        reads[0].Work();
        reads[0].Done.SetResult();
        Assert.Equal(Start, Rats(vm));

        vm.SelectInstance(0);
        vm.RotateSelection();
        Assert.Equal(2, reads.Count);                         // the rotation's own read is out, and not answered
        Assert.Equal([("mid", 5_000, 5_000, 20_000, 0)], Rats(vm));

        vm.Execute(new CircuitRF.Ui.Commands.Layout.MoveInstancesCommand(model, [1], 0, 10_000));
        Assert.Equal([("mid", 5_000, 5_000, 20_000, 10_000)], Rats(vm));

        vm.SelectInstance(1);
        vm.CutSelectionAfterCopy();   // the editor's delete (the view writes the clipboard before it)
        Assert.Empty(Rats(vm));
    }
}
