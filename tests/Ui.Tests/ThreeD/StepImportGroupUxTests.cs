// ================================================================
//  StepImportGroupUxTests.cs — brief-em3d-128 §5 gates 6-8: the group and member paths that already exist (C3dGroups, the
//  editor's group commands), proved on an imported package, so a regression is caught where the import is used. The
//  package is testdata/step/two-solids-one-product.step: 'lead' and an unnamed box, gathered as 'two-solids-one-product'.
//  Pixels were not seen: these read the model and the view model.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class StepImportGroupUxTests : IDisposable
{
    private const long Um = 1000;
    private const string Package = "two-solids-one-product";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-step128ux-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public StepImportGroupUxTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [KernelFact]
    public void Gate6And7_TheCommitSelectsTheGroupWhole_AViewPickOnAPieceTakesTheGroup_AndMoveMovesEveryPiece()
    {
        using var kernel = KernelForTests.New();
        var vm = Imported(kernel);
        Assert.Equal([new C3dGroupUnit(new C3dMemberRef(false, 0), Package)], vm.SelectedUnits());

        vm.Viewer.SetSelection([]);
        vm.Viewer.OnPicked(vm.SceneObject($"{Package}_2")!.Id, 0, Vector3.Zero, true);
        vm.Viewer.Click(false);
        Assert.Equal(["lead", $"{Package}_2"], vm.Viewer.SelectedObjects().Select(o => o.Name).Order());

        var cornerX = vm.Properties.Fields.Single(f => f.Label == "Corner x");
        cornerX.Text = "500";                                            // the package's corner: every piece by 500 µm
        vm.Properties.CommitField(cornerX);
        Assert.Equal([500 * Um, 500 * Um], vm.Document.Objects.Select(o => o.Placement.Origin.X));
    }

    [KernelFact]
    public void Gate8_AMembersTreeRowSetsThatMemberOnly_TheGroupRowSetsEvery_EachOneUndoEntry()
    {
        using var kernel = KernelForTests.New();
        var vm = Imported(kernel);
        var group = vm.Tree.Single(g => g.Role == C3dTreeGroupRole.Groups).Items.Single(i => i.IsGroup);
        Assert.Equal(["lead", $"{Package}_2"], group.Children.Select(c => c.Name));

        int entries = vm.UndoEntries;
        var lead = group.Children.Single(c => c.Name == "lead");
        vm.SelectedTreeItem = lead;
        vm.TreeMenuItems(lead).Single(i => i.Header == "Material").Children!.Single(m => m.Header == "Copper").Run!();
        Assert.Equal(["Copper", null], vm.Document.Objects.Select(o => o.Material));
        Assert.Equal(entries + 1, vm.UndoEntries);
        Settle(vm);

        vm.SelectedTreeItem = group;
        vm.Properties.Material = "Alumina";
        Assert.Equal(["Alumina", "Alumina"], vm.Document.Objects.Select(o => o.Material));
        Assert.Equal(entries + 2, vm.UndoEntries);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    /// <summary>An editor on an empty 3D view with the package imported by the dialog's OK, its defaults untouched.</summary>
    private C3dEditorViewModel Imported(CircuitRF.Design.ThreeD.Occ.GeometryKernel kernel)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(ws, "incoming"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Alumina", Epsr = 9.8 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string src = Path.Combine(ws, "incoming", Package + ".step");
        File.Copy(Path.Combine(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), "testdata", "step", Package + ".step"), src);
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument());

        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue, kernel: kernel);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        Assert.Null(vm.AcceptStepImport(vm.ReadStep(src, null)));
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
