using System.Collections.Generic;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>
/// TO-4's panel over <see cref="TuningFixture"/>'s design, with the shell's three hooks played by the
/// test: discovery through the stub resolver, the DUT drawing's own session, and a record of which
/// drawings a Push asked to have a tab.
/// </summary>
internal sealed class TuningPanelFixture
{
    public readonly SchematicEditModel   DutModel = TuningFixture.Dut();
    public readonly SchematicViewModel   Top;
    public readonly SchematicViewModel   Dut;
    public readonly TuningPanelViewModel Panel = new();
    public readonly List<SchematicEditModel> TabsOpened = [];

    public TuningPanelFixture(string? workspaceRoot = null, string? x2Rbias = null)
    {
        Top = new SchematicViewModel(TuningFixture.Top(x2Rbias: x2Rbias));
        Dut = new SchematicViewModel(DutModel);
        var resolver = TuningFixture.Resolver(DutModel);
        Panel.Discover          = vm => TunableCatalog.Discover(vm.EditModel, resolver, workspaceRoot);
        Panel.SessionForDrawing = d => { TabsOpened.Add(d); return ReferenceEquals(d, DutModel) ? Dut : null; };
        Top.Tuning = Panel;
        Dut.Tuning = Panel;
        Panel.SetActiveSchematic(Top, "tb.csch");
    }

    public TuningRowViewModel Row(string key) => Panel.Rows.Single(r => r.Key == key);
}
