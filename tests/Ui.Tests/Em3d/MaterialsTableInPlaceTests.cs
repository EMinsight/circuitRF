using System.Collections.Specialized;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Theming;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  3D editor round 3 — the Materials table (a .cmat document and the technology editor's Materials tab):
//  a committed field updates its row IN PLACE rather than rebuilding the list (the row flashed on every
//  focus move), a focus move that changes nothing does nothing, and the colour picker commits #rrggbb.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class MaterialsTableInPlaceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-mattable-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    private MaterialsEditorViewModel Library(params TechMaterial[] materials)
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, "lib.cmat");
        MaterialLibraryPersistence.SaveToFile(path, materials);
        return new MaterialsEditorViewModel(path, MaterialLibraryPersistence.LoadFromFile(path));
    }

    [Fact]
    public void ACommittedField_KeepsEveryRowAndTheSelection_AndANoChangeLeaveWritesNothing()
    {
        var vm = Library(new TechMaterial { Name = "Au", Sigma20 = 41e6 }, new TechMaterial { Name = "Diel", Epsr = 4 });
        var rows = vm.Table.Rows.ToArray();
        vm.Table.SelectedRow = rows[1];
        var actions = new List<NotifyCollectionChangedAction>();
        vm.Table.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        // Focus leaves a field with its text untouched: nothing is written and nothing is rebuilt.
        rows[1].EpsrText = rows[1].EpsrText;
        rows[1].NameText = rows[1].NameText;
        rows[1].ColorText = rows[1].ColorText;
        Assert.False(vm.UndoRedo.CanUndo);

        // A real edit, then its undo and redo: the same row objects, re-pointed at the snapshot's records.
        rows[1].TanDText = "0.002";
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        Assert.Empty(actions);
        Assert.Equal(rows, vm.Table.Rows);
        Assert.Same(rows[1], vm.Table.SelectedRow);
        Assert.Same(vm.Working[1], rows[1].Material);
        Assert.Equal("0.002", rows[1].TanDText);

        // A rename keeps the selected row selected (by position once its old name is gone).
        rows[1].NameText = "Sub";
        Assert.Same(rows[1], vm.Table.SelectedRow);
        Assert.Equal("Sub", vm.Working[1].Name);
    }

    [Fact]
    public void LeavingALibraryRowsReadOnlyField_RaisesNoRefusal()
    {
        var own = new List<TechMaterial>();
        var table = new MaterialsTableViewModel(() => own, (mutate, _) => mutate(), "this technology");
        table.Rebuild([new LibraryMaterial(new TechMaterial { Name = "Cu", Sigma20 = 58e6, Epsr = 1 }, "/x/generic.cmat")]);
        var row = table.Rows.Single();

        row.Sigma20Text = row.Sigma20Text;
        row.EpsrText = row.EpsrText;
        Assert.Null(table.Refusal);

        row.Sigma20Text = "1";                                        // a real edit is still refused
        Assert.NotNull(table.Refusal);
        Assert.Equal(58e6, row.Material.Sigma20);
    }

    [Fact]
    public void ThePickedColour_CommitsAsHex_AndThePickedColourAlreadyStatedWritesNothing()
    {
        var vm = Library(new TechMaterial { Name = "Au", Sigma20 = 41e6 });
        var row = vm.Table.Rows[0];
        Assert.Equal(Avalonia.Media.Colors.Transparent, row.SwatchColor);

        row.ApplyPickedColour(null);                                   // Cancel
        Assert.False(vm.UndoRedo.CanUndo);

        row.ApplyPickedColour(new Rgba(0x12, 0x34, 0xAB, 0x80));       // alpha is not a material's
        Assert.Equal("#1234ab", vm.Working[0].Color);
        Assert.Equal(new Avalonia.Media.Color(255, 0x12, 0x34, 0xAB), row.SwatchColor);
        Assert.Same(row, vm.Table.Rows[0]);

        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoRedo.CanUndo);
        vm.RedoCommand.Execute(null);
        row.ApplyPickedColour(new Rgba(0x12, 0x34, 0xAB));
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoRedo.CanUndo);                             // the repeat pick pushed no entry
    }
}
