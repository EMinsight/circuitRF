using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-53 — the Materials editor's view models: the one table, the .cmat document, the technology
//  editor's Materials tab, and the 3D editor's rename. Headless; pixels are the owner's check (§10).
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class MaterialsEditorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-em3d53ui-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    private string Library(string name, params TechMaterial[] materials)
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, name);
        MaterialLibraryPersistence.SaveToFile(path, materials);
        return path;
    }

    private string Technology(string name, IEnumerable<TechMaterial> own, string? entryMaterial = null, params string[] libraries)
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, name);
        TechPersistence.SaveToFile(path, new Technology
        {
            Name = Path.GetFileNameWithoutExtension(name),
            Materials = [.. own],
            MaterialLibraries = libraries.Length > 0 ? [.. libraries] : null,
            Stackup = new Stackup
            {
                Layers = [new StackupLayer { Name = "Sub", Kind = StackupKind.Dielectric, ThicknessDbu = 100_000, Epsr = 1, Mur = 1, Material = entryMaterial }],
            },
        });
        return path;
    }

    // ── 3. Null stays null; untouched stays untouched (§1e, §1f) ─────────────────────────────

    [Fact]
    public void Gate3_ABlankFieldIsAnOmittedKey_AndAFieldNotEditedIsWrittenBitForBit()
    {
        string path = Library("lib.cmat",
            new TechMaterial { Name = "Au", Sigma20 = 41234567.891 },
            new TechMaterial { Name = "Diel", Epsr = 4, EpsrTensor = [4, 4, 5] });
        var vm = new MaterialsEditorViewModel(path, MaterialLibraryPersistence.LoadFromFile(path));
        var au = vm.Table.Rows[0];
        var diel = vm.Table.Rows[1];

        au.TanDText = "0.001";                                                       // only tanδ edited
        Assert.Equal(41234567.891, vm.Working[0].Sigma20);
        au = vm.Table.Rows[0];
        au.TanDText = "";                                                            // cleared → not stated
        diel = vm.Table.Rows[1];
        diel.EpsrText = "";                                                          // clearing εr removes the key
        vm.Table.Rows[1].IsAnisotropic = false;                                      // … and the tensor
        vm.SaveCommand.Execute(null);

        string json = File.ReadAllText(path);
        Assert.Contains("\"Sigma20\": 41234567.891", json);
        var saved = MaterialLibraryPersistence.LoadFromFile(path);
        Assert.Equal(["Name", "Sigma20"], Stated(json, "Au"));
        Assert.Equal(["Name"], Stated(json, "Diel"));
        Assert.Equal(41234567.891, saved[0].Sigma20);

        // An SI prefix in the app's own spelling.
        vm.Table.Rows[0].Sigma20Text = "41M";
        Assert.Equal(41e6, vm.Working[0].Sigma20);

        static string[] Stated(string json, string name)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var rec = doc.RootElement.GetProperty("Materials").EnumerateArray().Single(e => e.GetProperty("Name").GetString() == name);
            return [.. rec.EnumerateObject().Select(p => p.Name)];
        }
    }

    // ── 4. The technology editor still opens a technology whose libraries refuse ───────────────

    [Fact]
    public void Gate4_TheTechnologyEditorOpensARefusedTechnology_AndShowsTheRefusalOnItsMaterialsTab()
    {
        Library("lib.cmat", new TechMaterial { Name = "Gold", Sigma20 = 3.3e7 });
        string ctech = Technology("t.ctech", [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }], null, "lib.cmat");
        Assert.Throws<MaterialLibraryException>(() => TechPersistence.LoadFromFile(ctech));

        var vm = new TechEditorViewModel(ctech, TechPersistence.LoadOwnFromFile(ctech));
        Assert.NotNull(vm.LibraryRefusal);
        Assert.Contains(vm.ValidationProblems, p => p.Area == TechProblemArea.Materials && p.Id == MaterialLibraries.ConflictId);
        Assert.StartsWith("Materials (", vm.MaterialsTabHeader);

        // Fixed where it is shown: the own row edited to agree, and the refusal is gone.
        vm.MaterialsTable.Rows[0].Sigma20Text = "3.3e7";
        Assert.Null(vm.LibraryRefusal);
        Assert.DoesNotContain(vm.ValidationProblems, p => p.Id == MaterialLibraries.ConflictId);
    }

    // ── 10. The Materials tab ───────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_LibraryRowsAreReadOnly_OpenLibraryNamesTheRow_NewLibraryIsOneUndoEntryThatLeavesTheFile()
    {
        string lib = Library("lab.cmat", new TechMaterial { Name = "Lid", Epsr = 3.9 });
        string ctech = Technology("t.ctech", [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }], null, "lab.cmat");
        var vm = new TechEditorViewModel(ctech, TechPersistence.LoadOwnFromFile(ctech));

        var row = Assert.Single(vm.MaterialsTable.Rows, r => r.IsLibrary);
        Assert.False(row.IsEditable);
        Assert.Equal("lab.cmat", row.SourceLabel);
        row.EpsrText = "9";                                                          // a library row is not edited here
        Assert.False(vm.IsDirty);

        (string Path, string? Name)? opened = null;
        vm.OpenLibraryRequested += (p, n) => opened = (p, n);
        vm.MaterialsTable.SelectedRow = row;
        vm.OpenSelectedRowLibraryCommand.Execute(null);
        Assert.Equal((lib, (string?)"Lid"), opened);

        string fresh = Path.Combine(_tmp, "new.cmat");
        Assert.True(vm.NewLibrary(fresh));
        Assert.True(File.Exists(fresh));
        Assert.Equal(["lab.cmat", "new.cmat"], vm.Working.MaterialLibraries!);
        vm.UndoRedo.Undo();
        Assert.Equal(["lab.cmat"], vm.Working.MaterialLibraries!);
        Assert.True(File.Exists(fresh));                                            // Undo removes the reference only
    }

    // ── 11. Rename and delete across files — one entry on each file's own stack ──────────────

    [Fact]
    public void Gate11_ARenameIsOneEntryOnEachFilesOwnStack_AndDeleteInUseIsRefusedWithTheUses()
    {
        string lib = Library("lib.cmat", new TechMaterial { Name = "Alumina", Epsr = 9.8 });
        string ctech = Technology("t.ctech", [], "Alumina", "lib.cmat");
        var library = new MaterialsEditorViewModel(lib, MaterialLibraryPersistence.LoadFromFile(lib));
        var tech = new TechEditorViewModel(ctech, TechPersistence.LoadOwnFromFile(ctech));
        var c3d = OpenC3d(ctech, "Alumina");

        // The workspace's rename, piece by piece: one entry on each of the three stacks, named alike.
        library.CommitEdit(l => l[0].Name = "Alumina 99.6%", "Rename material Alumina → Alumina 99.6%");
        Assert.True(tech.RenameMaterialReferences("Alumina", "Alumina 99.6%", renameOwn: false));
        Assert.True(c3d.RenameMaterial("Alumina", "Alumina 99.6%"));
        foreach (var stack in new[] { library.UndoRedo, tech.UndoRedo, c3d.UndoRedo })
            Assert.Contains("Rename material Alumina → Alumina 99.6%", stack.UndoDescription);
        Assert.Equal("Alumina 99.6%", tech.Working.Stackup.Layers[0].Material);
        Assert.Equal("Alumina 99.6%", c3d.Document.Objects[0].Material);

        // Undoing one side alone leaves the other renamed: a visible state `check` reports.
        tech.UndoRedo.Undo();
        Assert.Equal("Alumina", tech.Working.Stackup.Layers[0].Material);
        Assert.Equal("Alumina 99.6%", c3d.Document.Objects[0].Material);

        // Delete refuses a material in use and lists the use.
        tech.MaterialsTable.UsedBy = _ => ["stackup entry 'Sub'"];
        string own = Technology("own.ctech", [new TechMaterial { Name = "Sub", Epsr = 3 }], "Sub");
        var ownTech = new TechEditorViewModel(own, TechPersistence.LoadOwnFromFile(own));
        ownTech.MaterialsTable.SelectedRow = ownTech.MaterialsTable.Rows[0];
        ownTech.MaterialsTable.DeleteCommand.Execute(null);
        Assert.Contains("stackup entry 'Sub'", ownTech.MaterialsTable.Refusal);
        Assert.Single(ownTech.Working.Materials);
        Assert.False(ownTech.IsDirty);
    }

    [Fact]
    public void Gate11_TheTableRefusesAnAtInAName()
    {
        string lib = Library("lib.cmat", new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 });
        var vm = new MaterialsEditorViewModel(lib, MaterialLibraryPersistence.LoadFromFile(lib));
        vm.Table.Rows[0].NameText = "Gold@gaas";
        Assert.Equal("Gold", vm.Working[0].Name);
        Assert.Contains("'@' is reserved", vm.Table.Refusal);
        Assert.False(vm.IsDirty);
    }

    // ── 14. Add Generic Materials ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate14_AddGenericMaterialsCopiesTheLibraryAsOneUndoEntry_AndRefusesADifferentFileOfThatName()
    {
        string ctech = Technology("t.ctech", [], "Alumina 96%");
        var vm = new TechEditorViewModel(ctech, TechPersistence.LoadOwnFromFile(ctech));
        Assert.True(vm.CanAddGenericMaterials);
        vm.AddGenericMaterialsCommand.Execute(null);
        Assert.Equal([MaterialLibraries.GenericFileName], vm.Working.MaterialLibraries!);
        Assert.Equal(9.4, vm.Working.Stackup.Layers[0].Epsr);                       // the entry took the library's value
        Assert.False(vm.CanAddGenericMaterials);
        vm.UndoRedo.Undo();
        Assert.Null(vm.Working.MaterialLibraries);
        Assert.True(File.Exists(Path.Combine(_tmp, MaterialLibraries.GenericFileName)));

        File.WriteAllText(Path.Combine(_tmp, MaterialLibraries.GenericFileName), """{ "FormatVersion": 1, "Materials": [] }""");
        vm.AddGenericMaterialsCommand.Execute(null);
        Assert.Contains("already exists", vm.MaterialsRefusal);
        Assert.Null(vm.Working.MaterialLibraries);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    private C3dEditorViewModel OpenC3d(string ctech, string material)
    {
        string ws = _tmp;
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = Path.GetFileName(ctech) });
        string path = Path.Combine(ws, "cell", "3d", "cell.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "b", Material = material, Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000, 1000, 1000) }],
        });
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new PatchRecordingBackend(),
                                        () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        return vm;
    }
}
