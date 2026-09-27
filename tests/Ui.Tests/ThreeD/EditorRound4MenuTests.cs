// ================================================================
//  EditorRound4MenuTests.cs — the 3D editor's fourth round of owner feedback, the menu half: the top-level 3D menu is
//  shown only while a 3D document is the active one (bound, never added or removed — the macOS NativeMenu is fixed
//  for a window's life), and the .c3d canvas's right-click offers Copy, the one copy the read-only viewer uses.
//  WorkspaceWindow cannot be constructed headlessly, so the menu's wiring is read from the .axaml itself.
// ================================================================

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.Ui.Views.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class EditorRound4MenuTests
{
    [Fact]
    public void The3DMenu_ShowsOnlyForA3DDocument()
    {
        static object Blank(Type t) => RuntimeHelpers.GetUninitializedObject(t);
        Assert.False(WorkspaceViewModel.ShowsThreeDMenu(null));
        Assert.False(WorkspaceViewModel.ShowsThreeDMenu(Blank(typeof(SchematicDocument))));
        Assert.True(WorkspaceViewModel.ShowsThreeDMenu(Blank(typeof(C3dEditorDocument))));
        Assert.True(WorkspaceViewModel.ShowsThreeDMenu(Blank(typeof(Viewer3DDocument))));
    }

    [Fact]
    public void BothSpellingsOfThe3DMenu_BindTheirVisibility()
    {
        var xml = XDocument.Parse(ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml"));
        var menus = xml.Descendants()
            .Where(e => (e.Name.LocalName, (string?)e.Attribute("Header")) is ("NativeMenuItem", "3D") or ("MenuItem", "_3D"))
            .ToList();
        Assert.Equal(2, menus.Count);
        Assert.All(menus, m => Assert.Equal("{Binding IsThreeDMenuVisible}", (string?)m.Attribute("IsVisible")));
    }

    [Fact]
    public void TheCanvasMenus_OfferPlainCopy_InTheEditorAsInTheViewer()
    {
        Assert.Equal("Copy", Viewer3DPictureCopy.Header);
        // 3D editor bugs round 5 — the viewer IS the editor's view now, so one source holds both menus.
        foreach (string view in new[] { "src/Ui/Views/ThreeD/C3dEditorView.axaml.cs" })
        {
            string source = ReadRepoFile(view);
            Assert.Contains("Viewer3DPictureCopy.Item(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("× the window)\" }", source, StringComparison.Ordinal);
        }
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }
}
