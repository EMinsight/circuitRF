using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Core;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Brief DFR26-5 (designer feedback round 26): a paste follows the pointer until a click places it; deleting an
/// imported part takes the net label on its pin; and a part copied into a sheet in another folder keeps its symbol.
/// The tee/cross series cut is in <c>SeriesPartPlacementClearsWireTests</c>, the arm model in <c>Core.Tests</c>'
/// <c>JunctionArmTests</c>.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public sealed class SchematicEditRound26Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-dfr26-5-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static EditableComponent Resistor(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    /// <summary>A sheet holding R1 at the origin, and its copy fragment ready to paste.</summary>
    private static (SchematicEditModel Model, SchematicViewModel Vm, Action Paste) SheetWithACopy()
    {
        var model = new SchematicEditModel { GridSize = 100, GridSnap = true };
        model.Components.Add(Resistor("R1", 0, 0));
        var vm = new SchematicViewModel(model);
        string json = SchematicPersistence.SerializeSelection(model.Components.ToList(), [], [], model.GridSize);
        return (model, vm, () =>
        {
            var (comps, wires, cobjs, grid) = SchematicPersistence.DeserializeSelection(json);
            vm.PasteFragment(comps, wires, cobjs, grid);
        });
    }

    // ── R-dfr26-5-1: paste follows the pointer ────────────────────────────────

    [Fact]
    public void Paste_ArmsAGhostAtThePointer_AndAClickPlacesItThereOnGrid()
    {
        var (model, vm, paste) = SheetWithACopy();
        vm.PointerWorldProvider = () => (2030, 1480);

        paste();

        // Nothing lands — so nothing can connect — while the ghost floats; it sits at the pointer, on grid.
        Assert.True(vm.IsPasteArmed);
        Assert.Single(model.Components);
        var ghost = Assert.Single(vm.Overlay.DuplicateGhosts!);
        Assert.Equal((2000.0, 1500.0), (ghost.X, ghost.Y));

        vm.OnPointerMoved(3010, 990, leftDown: false);
        Assert.Equal((3000.0, 1000.0), (vm.Overlay.DuplicateGhosts![0].X, vm.Overlay.DuplicateGhosts[0].Y));

        vm.OnPointerPressed(3010, 990, KeyModifiers.None);

        Assert.False(vm.IsPasteArmed);
        Assert.Null(vm.Overlay.DuplicateGhosts);
        var placed = model.Components[1];
        Assert.Equal((3000.0, 1000.0), (placed.X, placed.Y));
        Assert.True(vm.Selection.IsSelected(placed.Id));
    }

    [Fact]
    public void Escape_DropsTheGhost_AndLeavesTheSheetUnchanged()
    {
        var (model, vm, paste) = SheetWithACopy();
        vm.PointerWorldProvider = () => (2000, 1500);
        paste();

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.IsPasteArmed);
        Assert.Null(vm.Overlay.DuplicateGhosts);
        Assert.Single(model.Components);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── R-dfr26-5-4: imported schematics ──────────────────────────────────────

    /// <summary>An imported sheet joins a pin it could not wire by a free label ON the pin. Deleting the part takes that
    /// label; one another part's pin also meets stays, still naming that pin. Undo brings it back.</summary>
    [Fact]
    public void DeletingAnImportedPart_TakesTheNetLabelOnItsPin()
    {
        var model = new SchematicEditModel { GridSize = 100, GridSnap = true };
        var r1 = Resistor("R1", 0, 0);       // pins at (0,-200) and (0,200)
        var r2 = Resistor("R2", 0, 400);     // pins at (0,200) and (0,600)
        model.Components.AddRange([r1, r2]);
        model.NetLabels.Add(new EditableNetLabel { Name = "N49", X = 0, Y = -200 });
        model.NetLabels.Add(new EditableNetLabel { Name = "N50", X = 0, Y = 200 });
        var vm = new SchematicViewModel(model);

        vm.Selection.SetAll([r1.Id]);
        vm.DeleteSelection();

        Assert.Equal(["N50"], model.NetLabels.Select(l => l.Name));
        vm.UndoRedo.Undo();
        Assert.Equal(["N49", "N50"], model.NetLabels.Select(l => l.Name).Order());
    }

    /// <summary>An imported part's symbol is a cell beside its source sheet, referenced relative to that sheet. Pasted into
    /// a sheet in another folder the same reference names nothing and the part drew as a placeholder; the clipboard now
    /// carries it absolute and the paste makes it relative to the destination, so it resolves there.</summary>
    [Fact]
    public void CopyingAnImportedPartToAnotherSheet_KeepsItsSymbol()
    {
        string sourceSheet = Path.Combine(_root, "import", "schematic");
        string destSheet   = Path.Combine(_root, "elsewhere", "deeper");
        Directory.CreateDirectory(sourceSheet);
        Directory.CreateDirectory(destSheet);
        var cell = CellCreate.Create(Path.Combine(_root, "import", "parts"), "U1", CellViews.Symbol);

        var part = new EditableComponent
        {
            InstanceName = "U1", Symbol = SymbolKind.Generic,
            CellRef = RefPath.ToStored(ExternalCellRef.MakeCellRef(sourceSheet, cell.CellDir)),
        };
        var resolvedAtSource = CellSymbolResolver.Resolve(part.CellRef!, sourceSheet).State;
        Assert.NotEqual(CellSymbolState.NotFound, resolvedAtSource);
        Assert.Equal(CellSymbolState.NotFound, CellSymbolResolver.Resolve(part.CellRef!, destSheet).State);

        string json = SchematicPersistence.SerializeSelection(
            SchematicClipboard.WithAbsoluteCellRefs([part], sourceSheet), [], [], 100);
        var (comps, _, _, _) = SchematicPersistence.DeserializeSelection(json);
        SchematicClipboard.RelativizeCellRefs(comps, destSheet);

        var pasted = Assert.Single(comps);
        Assert.False(Path.IsPathRooted(pasted.CellRef!));
        Assert.Equal(resolvedAtSource, CellSymbolResolver.Resolve(pasted.CellRef!, destSheet).State);
        Assert.Equal(Path.GetFullPath(cell.CellDir), ExternalCellRef.ResolveCellDir(pasted.CellRef, destSheet));
        Assert.Equal(Path.GetRelativePath(sourceSheet, cell.CellDir).Replace('\\', '/'), part.CellRef);   // source untouched
    }
}
