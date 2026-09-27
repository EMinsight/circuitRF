using System.Collections.Specialized;
using Avalonia.Input;
using CircuitRF.Design;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// Field report, 2026-09-27: the layout crashed opening a dropdown after footprints had been changed
/// (an ArgumentOutOfRange inside the dropdown's item panel). Picking a footprint used to re-point the
/// instance INSIDE the ComboBox's own two-way write-back, and the panel refresh that follows rebuilt
/// the very item list the ComboBox was selecting from; Avalonia throws there and the binding swallows
/// it, leaving the control half-updated. src/Ui/RESOLVED.md has the headless reproduction.
/// </summary>
public sealed class FootprintPickDeferredTests : IDisposable
{
    private readonly string _root;

    public FootprintPickDeferredTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-fp-pick-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The pick touches neither the document nor the rows until the ComboBox has finished —
    /// then it is applied exactly as before.</summary>
    [Fact]
    public void APickIsAppliedAfterTheComboBoxFinishes_NotInsideItsWriteBack()
    {
        var (vm, props) = PanelOver(LandPatternCell(SmtCaseTable.All[0]));
        var posted = new List<Action>();
        props.PostToUi = posted.Add;
        int rowChanges = 0;
        props.InstanceFootprintOptions.CollectionChanged += (_, _) => rowChanges++;

        var target = SmtCaseTable.All[1];
        props.InstanceFootprintIndex = 1;

        Assert.Equal(0, rowChanges);
        Assert.Equal(SmtCaseTable.All[0].Code, vm.SelectedInstanceFootprint?.Case.Code);
        Assert.Single(posted);

        posted[0]();
        Assert.Equal(target.Code, vm.SelectedInstanceFootprint?.Case.Code);
    }

    /// <summary>The rows are rebuilt only when they differ. The panel refreshes on every frame of a
    /// move drag, and a rebuild recycles every item container of the ComboBox.</summary>
    [Fact]
    public void AMoveDragLeavesTheFootprintRowsAlone()
    {
        var (vm, props) = PanelOver(LandPatternCell(SmtCaseTable.All[0]));
        int rowChanges = 0;
        props.InstanceFootprintOptions.CollectionChanged += (_, _) => rowChanges++;

        var (px, py) = PadPoint(vm);
        vm.OnPointerPressed(px, py, KeyModifiers.None, hitTolDbu: 1_000);
        vm.OnPointerMoved(px + 2_000_000, py, leftDown: true, KeyModifiers.None, hitTolDbu: 1_000);
        vm.OnPointerMoved(px + 3_000_000, py, leftDown: true, KeyModifiers.None, hitTolDbu: 1_000);
        vm.OnPointerReleased(px + 3_000_000, py, KeyModifiers.None);

        Assert.Equal(0, rowChanges);
    }

    private string LandPatternCell(SmtCase smtCase)
    {
        var reference = FootprintRef.For(smtCase);
        var tech = ShippedTechnologies.All.First(t => t.Id.StartsWith("pcb-", StringComparison.Ordinal));
        return GeneratedCellStore.GetOrCreate(
            _root, reference.ToString(), new Dictionary<string, PCellValue>(),
            ShippedTechnologies.Load(tech), tech.Id, PCellLayerSelection.Default);
    }

    private static (long X, long Y) PadPoint(LayoutEditorViewModel vm)
    {
        var view = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir).View!;
        return view.Shapes[0] switch
        {
            RectShape r    => ((r.X1 + r.X2) / 2, (r.Y1 + r.Y2) / 2),
            PolygonShape p => ((p.Xy.Where((_, i) => i % 2 == 0).Min() + p.Xy.Where((_, i) => i % 2 == 0).Max()) / 2,
                               (p.Xy.Where((_, i) => i % 2 == 1).Min() + p.Xy.Where((_, i) => i % 2 == 1).Max()) / 2),
            var other      => throw new InvalidOperationException($"unexpected pad shape {other.GetType().Name}"),
        };
    }

    private (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) PanelOver(string cellDir)
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_root, "Board", "layout", "main.clay"))
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0,
        });
        vm.SelectInstance(0);

        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }
}
