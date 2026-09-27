// 3D editor bugs round 5 — one 3D view. A .cem's Show 3D View is the 3D editor's own view, built around the setup's
// Viewer3DViewModel with nothing to edit (C3dEditorViewModel.IsViewOnly). One test per claim:
//   * the setup's view IS the editor's view, and nothing in it can edit;
//   * its tree lists the scene the setup's generator built, and a tick is the view's visibility, never an edit;
//   * a regeneration (what a setup edit, a layout save or a technology save causes) rebuilds that tree.

using System.Runtime.CompilerServices;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(Viewer3DCollection.Name)]
public sealed class EditorRound5ViewOnlyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r5view-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>The 3D view of a setup, as OpenOrActivate3DView builds it: the viewer, then the document around it.</summary>
    private Viewer3DDocument Open(Viewer3DInputs inputs)
        => new(new Viewer3DViewModel(Path.Combine(_root, "caseA.cem"), () => inputs, () => new RecordingBackend(), () => null, a => a()));

    private static void Settle(C3dEditorViewModel editor, long generation)
        => Assert.True(SpinWait.SpinUntil(() => editor.AdoptedGeneration >= generation, TimeSpan.FromSeconds(30)), "the scene was never adopted");

    private static List<C3dTreeItem> Rows(C3dEditorViewModel editor) => [.. editor.Tree.SelectMany(g => g.Items)];

    [Fact]
    public void ShowThreeDView_IsTheEditorsView_WithNothingToEdit()
    {
        Directory.CreateDirectory(_root);
        var (setup, source) = PalaceProgressTests.CaseA(_root);
        var doc = Open(new Viewer3DInputs(setup.Clone(), source, null, ColorTheme.BuiltIn, ColorVariant.Light));
        using var viewer = doc.ViewModel;
        var editor = doc.Editor.ViewModel;

        Assert.Same(viewer, editor.Viewer);
        Assert.True(editor.IsViewOnly);
        Assert.False(editor.IsEditable);
        Assert.True(editor.ShowsStatusPane);                 // the setup's refusals and notes are the point
        Assert.Null(viewer.EditHost);                        // no tool, gizmo, delete key or drop can reach an edit
        Assert.IsNotAssignableFrom<IUndoableDocument>(doc);  // the shell's Undo/Save never resolve to it

        // The shell hands the document's editor model to the editor's own view; the editing chrome is hidden in it.
        Assert.Contains("<t3v:C3dEditorView DataContext=\"{Binding Editor}\"/>", ReadRepoFile("src/Ui/Views/Viewer3D/Viewer3DView.axaml"));
        var xaml = ReadRepoFile("src/Ui/Views/ThreeD/C3dEditorView.axaml").Split('\n');
        foreach (string binding in new[] { "ViewModel.IsBoxArmed", "ViewModel.IsWireArmed", "ViewModel.Viewer.SnapEnabled}\"",
                                           "ViewModel.ShowPropertiesPanelCommand", "ViewModel.AirBoxShown", "ViewModel.OpenSetupAnalysesCommand",
                                           "ViewModel.ShowVariables}\" ToolTip", "ViewModel.SimulateActiveCommand" })
        {
            string line = xaml.First(l => l.Contains(binding, StringComparison.Ordinal));
            Assert.Contains("IsVisible=\"{Binding ViewModel.IsEditable}\"", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheTree_ListsTheScene_AndATick_IsTheViewsVisibility_NeverAnEdit()
    {
        Directory.CreateDirectory(_root);
        var (setup, source) = PalaceProgressTests.CaseA(_root);
        var doc = Open(new Viewer3DInputs(setup.Clone(), source, null, ColorTheme.BuiltIn, ColorVariant.Light));
        using var viewer = doc.ViewModel;
        var editor = doc.Editor.ViewModel;
        viewer.Regenerate();
        Settle(editor, 1);

        var scene = viewer.Scene;
        Assert.Equal(scene.Objects.Select(o => o.Name).Order(), Rows(editor).Select(r => r.Name).Order());

        // A tick hides the object in the view — nothing is written, nothing is undoable.
        var conductor = scene.Objects.First(o => o.Kind is not (Scene3DKind.Dielectric or Scene3DKind.Air or Scene3DKind.Port or Scene3DKind.Boundary));
        Rows(editor).Single(r => r.Name == conductor.Name).IsVisible = false;
        Assert.False(viewer.View.IsVisible(conductor.Id));
        Assert.Equal(0, editor.UndoEntries);
        Assert.False(editor.IsDirty);

        // The view's own switches reach the ticks.
        viewer.ShowDielectrics = false;
        var dielectrics = scene.Objects.Where(o => o.Kind == Scene3DKind.Dielectric).Select(o => o.Name).ToHashSet();
        Assert.NotEmpty(dielectrics);
        Assert.All(Rows(editor).Where(r => dielectrics.Contains(r.Name)), r => Assert.False(r.IsVisible));

        // A click in the scene selects its row; the row's menu offers only what a view can do.
        viewer.SetSelection([Scene3DItem.OfObject(conductor.Id)]);
        var row = Assert.IsType<C3dTreeItem>(editor.SelectedTreeItem);
        Assert.Equal(conductor.Name, row.Name);
        var menu = editor.TreeMenuItems(row).Select(i => i.Header).ToList();
        Assert.Contains("Show All", menu);
        Assert.DoesNotContain(menu, h => h.StartsWith("Delete") || h.StartsWith("Duplicate") || h.StartsWith("Rename") || h == "Properties");
    }

    [Fact]
    public void ARegeneration_RebuildsTheTree_FromTheNewScene()
    {
        Directory.CreateDirectory(_root);
        var (setupA, sourceA) = PalaceProgressTests.CaseA(_root);
        var (setupB, sourceB) = Em3dGeneratorTests.CaseB(plated: true);
        var inputs = new Viewer3DInputs(setupA.Clone(), sourceA, null, ColorTheme.BuiltIn, ColorVariant.Light);
        using var viewer = new Viewer3DViewModel(Path.Combine(_root, "caseA.cem"), () => inputs, () => new RecordingBackend(), () => null, a => a());
        var editor = new Viewer3DDocument(viewer).Editor.ViewModel;
        viewer.Regenerate();
        Settle(editor, 1);
        var first = Rows(editor).Select(r => r.Name).ToHashSet();

        inputs = new Viewer3DInputs(setupB.Clone(), sourceB, null, ColorTheme.BuiltIn, ColorVariant.Light);
        viewer.Regenerate();
        Settle(editor, 2);
        var second = Rows(editor).Select(r => r.Name).ToHashSet();

        Assert.Equal(viewer.Scene.Objects.Select(o => o.Name).ToHashSet(), second);
        Assert.False(first.SetEquals(second));
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }
}
