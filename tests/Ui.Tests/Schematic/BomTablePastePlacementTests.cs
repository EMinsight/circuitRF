using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Placing a pasted parts table (<see cref="SchematicViewModel.PlaceBomTable"/>): the parts are the
/// ones a palette drop builds, the table's value and case replace the defaults, the whole table is
/// ONE undo step, and it lands below what is already on the sheet in one row per type.
/// </summary>
public sealed class BomTablePastePlacementTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-bompaste-" + Guid.NewGuid().ToString("N"));

    public BomTablePastePlacementTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private SchematicViewModel VmOnABoard() => new(new SchematicEditModel
    {
        SchematicDirectory = WorkspaceCreate.Create(_root, "ws", "pcb-2layer_FR-4_70mil_1oz").WorkspaceDir,
    });

    [Fact]
    public void ATable_PlacesItsParts_AsOneUndoStep_BelowTheSheet_OneRowPerType()
    {
        var vm = VmOnABoard();
        vm.CommitPlacement(SymbolKind.Capacitor, 0, SymbolRotation.R0, 0, 0);   // "C1", already here
        double existingBottom = vm.EditModel.Components.Max(c => c.Y);

        const string table =
            "Reference\tType\tValue\tSize\n" +
            "C1,C2\tCapacitor\t100 pF\t0603\n" +
            "R1\tResistor\t49.9 Ω\t402\n" +
            "C3\tCapacitor\t10 nF\t805\n";

        Assert.True(vm.PlaceBomTable(table, "the clipboard"));

        var placed = vm.EditModel.Components.Skip(1).ToList();
        // Only the C1 already on the sheet clashes, and only it is renumbered — past every name
        // the table itself uses, so the table's own C2 and C3 keep theirs.
        Assert.Equal(["C4", "C2", "R1", "C3"], placed.Select(c => c.InstanceName));

        var r1 = placed.Single(c => c.InstanceName == "R1");
        var rValue = r1.Parameters.Single(p => p.Name == "R");
        Assert.Equal(("49.9", "Ω"), (rValue.Expression, rValue.Unit));
        Assert.Equal("smt:0402@N", r1.Footprint);
        Assert.Equal("smt:0805@N", placed.Single(c => c.InstanceName == "C3").Footprint);

        // Below the sheet; capacitors on one row, the resistor on the next.
        Assert.All(placed, c => Assert.True(c.Y > existingBottom));
        Assert.Single(placed.Where(c => c.Symbol == SymbolKind.Capacitor).Select(c => c.Y).Distinct());
        Assert.True(r1.Y > placed.First(c => c.Symbol == SymbolKind.Capacitor).Y);

        vm.UndoRedo.Undo();
        Assert.Single(vm.EditModel.Components);
    }

    [Fact]
    public void TheFootprintLabel_IsShownOnlyWhereTheTableNamedTheCase()
    {
        var vm = VmOnABoard();
        const string table = "Reference\tValue\tSize\nC1\t100 pF\t0603\nC2\t10 nF\t\n";

        Assert.True(vm.PlaceBomTable(table, "the clipboard"));

        Assert.True(vm.EditModel.Components.Single(c => c.InstanceName == "C1").ShowFootprintLabel);
        Assert.False(vm.EditModel.Components.Single(c => c.InstanceName == "C2").ShowFootprintLabel);
    }

    [Fact]
    public void TextThatIsNotATable_IsNotPlaced_AndTheSheetIsUntouched()
    {
        var vm = VmOnABoard();
        Assert.False(vm.PlaceBomTable("tune the output match first", "the clipboard"));
        Assert.Empty(vm.EditModel.Components);
    }
}
