using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  Materials editor redesign (2026-09-29): every property on one form, the temperature tables editable, and the 3D
//  view's Materials dialog hosting the same editor over the technology's lists — edits applied to their files on OK.
//  And the generic library: its thermal-era materials had no electrical values, so they read as stating nothing.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class MaterialsEditorRedesignTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-matredesign-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    /// <summary>The dialog edits copies: an edit to a library material, and a duplicate renamed, reach ONLY the list they
    /// were made in, on OK; an existing material's rename (which would leave every file naming it dangling) is refused.</summary>
    [Fact]
    public void TheDialog_EditsCopiesOfEachList_AndHandsBackOnlyTheListsItChanged()
    {
        var library = new List<TechMaterial>
        {
            new() { Name = "Gold", Sigma20 = 4.1e7, ThermalK = 318 },
            new() { Name = "Gallium nitride", Epsr = 9.5, ThermalK = 165.3 },
        };
        var picker = new MaterialPickerViewModel(
            [new MaterialSourceSeed("t.ctech (the technology's own)", null, [], null),
             new MaterialSourceSeed("lib.cmat", "/x/lib.cmat", library, null)],
            "t.ctech", startNew: false, current: "Gallium nitride", objectCount: 1, refusal: null);
        var table = picker.Table;
        Assert.False(table.CanDelete);
        Assert.Equal("lib.cmat", table.TargetSource.Label);        // M9: the first library

        var gan = table.SelectedRow!;
        gan.ThermalKText = "150";
        gan.NameText = "GaN";                                      // an existing material: refused, name kept
        Assert.Equal("Gallium nitride", gan.Name);
        Assert.NotNull(table.Refusal);

        table.Duplicate();
        var copy = table.SelectedRow!;
        Assert.Equal("Gallium nitride copy", copy.Name);
        copy.NameText = "GaN epi";                                 // a new one is named here
        Assert.Equal("GaN epi", copy.Name);

        Assert.Equal(165.3, library[1].ThermalK);                  // the caller's list is untouched
        Assert.Null(picker.Accept());
        Assert.Equal("GaN epi", picker.ChosenName);
        var (seed, materials) = Assert.Single(picker.ChangedLists);
        Assert.Equal("/x/lib.cmat", seed.LibraryPath);
        Assert.Equal(150, materials.Single(m => m.Name == "Gallium nitride").ThermalK);
        Assert.Equal(150, materials.Single(m => m.Name == "GaN epi").ThermalK);
    }

    /// <summary>A k(T) table is edited point by point, each gesture one undo entry, kept sorted; a repeated temperature is
    /// refused and an empty table is written as no table.</summary>
    [Fact]
    public void ATemperatureTable_IsEditedPointByPoint_SortedAndOneUndoEntryEach()
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, "lib.cmat");
        MaterialLibraryPersistence.SaveToFile(path, [new TechMaterial { Name = "SiC", Epsr = 9.7, ThermalK = 383 }]);
        var vm = new MaterialsEditorViewModel(path, MaterialLibraryPersistence.LoadFromFile(path));
        var k = vm.Table.Rows[0].KTable;

        k.AddPointCommand.Execute(null);                            // 20 °C at the constant
        k.AddPointCommand.Execute(null);                            // 45 °C at the last value
        k.Points[1].ValueText = "331";
        k.Points[1].TempText = "20";                                // a second point at 20 °C: refused
        Assert.NotNull(vm.Table.Refusal);
        k.Points[1].TempText = "-10";                               // re-sorted to the front
        Assert.Equal([(-10.0, 331.0), (20.0, 383.0)], vm.Working[0].ThermalKVsTemp!.Select(p => (p.TempC, p.Value)));

        vm.UndoCommand.Execute(null);
        Assert.Equal([(20.0, 383.0), (45.0, 331.0)], vm.Working[0].ThermalKVsTemp!.Select(p => (p.TempC, p.Value)));
        k.ClearCommand.Execute(null);
        Assert.Null(vm.Working[0].ThermalKVsTemp);
    }

    /// <summary>Every generic material states what it is — a conductor or a dielectric — so none reads as "states nothing"
    /// in the editor, and GaN and SiC state their permittivity.</summary>
    [Fact]
    public void EveryGenericMaterial_StatesItsRole()
    {
        var lib = MaterialLibraries.LoadGeneric();
        Assert.All(lib, m => Assert.Contains(C3dMaterialRole.Implied(m), new[] { C3dImpliedRole.Conductor, C3dImpliedRole.Dielectric }));
        Assert.Equal(9.5, lib.Single(m => m.Name == "Gallium nitride").Epsr);
        Assert.Equal(9.7, lib.Single(m => m.Name == "Silicon carbide (4H, semi-insulating)").Epsr);
    }
}
