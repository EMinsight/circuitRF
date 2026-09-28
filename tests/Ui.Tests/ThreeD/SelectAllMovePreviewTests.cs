using System.Numerics;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>
/// Select All Objects, then a gizmo drag: every object the commit will move is in the live preview. The 3D Connector's
/// housing is a Boolean that KEEPS its bore as a solid of its own; the bore moved on the drop and stood still while dragged.
/// </summary>
public sealed class SelectAllMovePreviewTests
{
    [KernelFact]
    public void AGizmoDragAfterSelectAll_PreviewsEverySelectedObject_AndAKeptTool()
    {
        string ws = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Connector");
        string c3d = Path.Combine(ws, "Launch", "3d", "Launch.c3d");
        using var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => new PatchRecordingBackend(),
                                              () => Path.Combine(ws, ".cws"), a => a());
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(120)));
        vm.Viewer.FitCommand.Execute(null);

        vm.Viewer.SelectAllObjects();
        var bore = vm.SceneObject("bore")!;
        Assert.Contains(vm.Viewer.View.Selection, s => s.Object == bore.Id);
        vm.Viewer.Hover(200, 150);
        Assert.True(vm.GizmoDrag(GizmoHandle.AxisX));
        vm.Viewer.Hover(260, 150);
        vm.Viewer.OnPicked(0, 0, Vector3.Zero, false);

        var preview = Assert.IsType<Scene3DPreview>(vm.Viewer.View.Preview);
        Assert.All(vm.Viewer.View.Selection, s => Assert.True(preview.IsMoving(s.Object), vm.Viewer.Scene.Objects[s.Object - 1].Name));
        Assert.True(preview.IsMoving(bore.Id));
        vm.GizmoCancel();
    }
}
