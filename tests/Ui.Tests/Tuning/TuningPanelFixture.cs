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

    /// <param name="extraVar">One more VAR row on the bench's VAR block — a complex value, say.</param>
    public TuningPanelFixture(string? workspaceRoot = null, string? x2Rbias = null,
                              (string Name, string Expr, string Unit)? extraVar = null)
    {
        var top = TuningFixture.Top(x2Rbias: x2Rbias);
        if (extraVar is { } v)
            top.Components.Single(c => c.InstanceName == "VAR1").Parameters
               .Add(new EditableParameter { Name = v.Name, Expression = v.Expr, Unit = v.Unit });
        Top = new SchematicViewModel(top);
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
