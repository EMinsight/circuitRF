// ================================================================
//  MaterialHoverAndEditMaterialTests.cs — brief-em3d-94. The hover shows the material properties that apply to what the object
//  is to the solve (MaterialHover, one row per role); a By-material header and an object row offer Edit Material…; the
//  workspace opens the file the material is defined in (a .cmat, or the technology's Materials tab) on its row, one session per
//  path; and the Materials table scrolls its selected row into view. No window is shown: the scroll is read from the view's
//  source, so its pixels were not seen.
// ================================================================

using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class MaterialHoverAndEditMaterialTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-editmat-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public MaterialHoverAndEditMaterialTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. the hover ────────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 1: one row per role. A conductor shows σ (μr only when not 1) and never εr or tanδ; a dielectric εr (or its
    /// tensor) and tanδ, σ only when above 0; a Role override decides over an ambiguous material; air says Air; an object with
    /// no values says why; a record stating nothing says so; a thermal setup adds k, and ρ and c when it is transient.</summary>
    [Fact]
    public void TheHover_ShowsWhatAppliesToTheRole()
    {
        var copper = new Em3dMaterial("Copper", 1, null, 0, 1, 5.8e7);
        var nickel = new Em3dMaterial("Nickel", 1, null, 0, 600, 1.43e7);
        var fr4 = new Em3dMaterial("FR4", 4.4, null, 0.02, 1, 0);
        var sapphire = new Em3dMaterial("Sapphire", 9.4, [9.4, 9.4, 11.6], 1e-4, 1, 0);
        var silicon = new Em3dMaterial("Silicon", 11.9, null, 0.005, 1, 10);      // σ AND εr: ambiguous without a Role
        var thermal = new MaterialHoverContext(Thermal: new MaterialHoverThermal(25, Transient: true,
            new TechMaterial { Name = "FR4", Epsr = 4.4, ThermalK = 0.3, DensityKgM3 = 1850, SpecificHeat = 1100 }));

        (Scene3DKind Kind, Em3dRole? Role, Em3dMaterial? Values, MaterialHoverContext? Context, string[] Expected)[] rows =
        [
            (Scene3DKind.Conductor, Em3dRole.Conductor, copper, null, ["Copper: σ 5.8E+07 S/m at 20 °C"]),
            (Scene3DKind.Sheet, Em3dRole.Conductor, nickel, null, ["Nickel: σ 1.43E+07 S/m at 20 °C, μr 600"]),
            (Scene3DKind.Dielectric, Em3dRole.Dielectric, fr4, null, ["FR4: εr 4.4, tanδ 0.02"]),
            (Scene3DKind.Dielectric, Em3dRole.Dielectric, sapphire, null, ["Sapphire: εr (9.4, 9.4, 11.6), tanδ 0.0001"]),
            (Scene3DKind.Body, Em3dRole.Dielectric, silicon, null, ["Silicon: εr 11.9, tanδ 0.005, σ 10 S/m at 20 °C"]),
            (Scene3DKind.Body, Em3dRole.Conductor, silicon, null, ["Silicon: σ 10 S/m at 20 °C"]),
            (Scene3DKind.Air, Em3dRole.Air, new Em3dMaterial("Air", 1, null, 0, 1, 0), null, ["Air"]),
            (Scene3DKind.Body, null, null, null, [MaterialHover.NoMaterial]),
            (Scene3DKind.Body, Em3dRole.Conductor, new Em3dMaterial("Blank", 1, null, 0, 1, 0), new MaterialHoverContext(new TechMaterial { Name = "Blank" }),
             ["Blank: states nothing (solved as a conductor with σ 0)"]),
            (Scene3DKind.Dielectric, Em3dRole.Dielectric, fr4, thermal,
             ["FR4: εr 4.4, tanδ 0.02", "k 0.3 W/(m·K) at 25 °C", "ρ 1850 kg/m³, c 1100 J/(kg·K)"]),
        ];
        foreach (var r in rows)
            Assert.Equal(r.Expected, MaterialHover.Lines(r.Kind, r.Role, r.Values, 20, r.Context));
    }

    // ── 2. the tree ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 2: by material, a material's header offers Edit Material… and No material's does not; an object row offers
    /// Edit Material 'X'…, and choosing it raises the request with the material and the document's technology.</summary>
    [Fact]
    public void TheTree_OffersEditMaterial_OnAMaterialHeaderAndAnObjectRow()
    {
        var doc = new C3dDocument { Objects = [Box("trace", 0, "Copper"), Box("board", 200, "FR4"), Box("bare", 400, null)] };
        var (vm, ws) = Open(doc);
        (string Material, string? Tech)? asked = null;
        vm.EditMaterialRequested = (m, t) => asked = (m, t);

        Assert.Equal(C3dTreeGrouping.Material, vm.TreeGrouping);
        Assert.Contains(vm.TreeGroupMenuItems(vm.Tree.Single(g => g.Header == "FR4")), i => i.Header == "Edit Material…");
        Assert.DoesNotContain(vm.TreeGroupMenuItems(vm.Tree.Single(g => g.Header == C3dEditorViewModel.NoMaterialHeader)),
                              i => i.Header.StartsWith("Edit Material", StringComparison.Ordinal));

        var row = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "board");
        vm.SelectedTreeItem = row;
        var edit = Assert.Single(vm.TreeMenuItems(row), i => i.Header == "Edit Material 'FR4'…");
        edit.Run!();
        Assert.Equal("FR4", asked!.Value.Material);
        Assert.Equal(Path.Combine(ws, "tech.ctech"), Path.GetFullPath(asked.Value.Tech!));
    }

    // ── 3. the workspace handler ────────────────────────────────────────────────────────────────

    /// <summary>Gate 3: a material from a .cmat opens that library's document on its row — again after the row was moved, in the
    /// same session — and one of the technology's own opens the technology editor on its Materials tab with the row selected.</summary>
    [Fact]
    public void TheWorkspace_OpensWhereTheMaterialIsDefined_OnItsRow_OneSessionPerPath()
    {
        string ws = Workspace();
        string tech = Path.Combine(ws, "tech.ctech"), cmat = Path.Combine(ws, "lib.cmat");
        var workspace = new WorkspaceViewModel();

        workspace.EditMaterial("FR4", tech);
        var library = Assert.IsType<MaterialsDocument>(workspace.FindOpenDocument(cmat));
        Assert.Equal("FR4", library.ViewModel.Table.SelectedRow?.Name);

        library.ViewModel.Table.Select("Laminate");
        workspace.EditMaterial("FR4", tech);
        Assert.Same(library, workspace.FindOpenDocument(cmat));
        Assert.Equal("FR4", library.ViewModel.Table.SelectedRow?.Name);

        workspace.EditMaterial("Copper", tech);
        var editor = Assert.IsType<TechDocument>(workspace.FindOpenDocument(tech)).ViewModel;
        Assert.Equal(TechEditorViewModel.MaterialsTabIndex, editor.SelectedTabIndex);
        Assert.Equal("Copper", editor.MaterialsTable.SelectedRow?.Name);
    }

    // ── 4. the scroll ───────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 4 (no window; pixels not seen): the one Materials table view both hosts use scrolls its list to the selected
    /// row when SelectedRow changes, and again once attached, so a table opened on a row reveals it.</summary>
    [Fact]
    public void TheMaterialsTable_ScrollsTheSelectedRowIntoView()
    {
        string src = StripComments(File.ReadAllText(RepoFile("src/Ui/Views/Materials/MaterialsTableView.axaml.cs")));
        Assert.Contains("nameof(MaterialsTableViewModel.SelectedRow)) RevealSelected()", src);
        Assert.Contains("AttachedToVisualTree += (_, _) => RevealSelected()", src);
        Assert.Contains("RowList.ScrollIntoView(row)", src);
        Assert.Contains("x:Name=\"RowList\"", File.ReadAllText(RepoFile("src/Ui/Views/Materials/MaterialsTableView.axaml")));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, long x, string? material)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) };

    /// <summary>A workspace whose technology defines Copper itself and names lib.cmat, which defines FR4 and Laminate.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        MaterialLibraryPersistence.SaveToFile(Path.Combine(ws, "lib.cmat"),
            [new TechMaterial { Name = "FR4", Epsr = 4.4, TanD = 0.02 }, new TechMaterial { Name = "Laminate", Epsr = 3.66, TanD = 0.004 }]);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }], MaterialLibraries = ["lib.cmat"],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private (C3dEditorViewModel Vm, string Ws) Open(C3dDocument doc)
    {
        string ws = Workspace();
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
        return (vm, ws);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }
}
