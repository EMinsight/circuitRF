// ================================================================
//  NewThreeDViewOpensTests.cs — 3D editor round 1: creating a 3D view OPENS it, orthographic, and a
//  cell's context menu offers Open 3D View. The open path needs a live dock and a GPU backend, so the
//  creation half is held on the source, comments stripped.
// ================================================================

using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class NewThreeDViewOpensTests
{
    /// <summary>Every creation path — New 3D View, New 3D View from Layout and File ▸ New 3D Design — opens what it wrote as a
    /// NEWLY CREATED view, and only that flag turns perspective off.</summary>
    [Fact]
    public void EveryCreationPath_OpensTheNewView_Orthographic()
    {
        string ws = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        int start = ws.IndexOf("public async Task New3DViewAsync(", StringComparison.Ordinal);
        string body = ws[start..ws.IndexOf("private string? CreateThreeDViewFile(", start, StringComparison.Ordinal)];
        Assert.Contains("OpenOrActivateC3dEditor(created, newlyCreated: true)", body);

        // File ▸ New ▸ New 3D Design: a new cell whose .c3d is written by the same function and opened
        // the same way.
        start = ws.IndexOf("internal async Task<string?> CreateCellHoldingViewAsync(", StringComparison.Ordinal);
        body = ws[start..ws.IndexOf("public async Task NewSymbolAsync(", start, StringComparison.Ordinal)];
        Assert.Matches(@"ViewType\.ThreeD\)\s*\{\s*if \(CreateThreeDViewFile\(newCellDir, name\) is \{ \} created\) OpenOrActivateC3dEditor\(created, newlyCreated: true\);", body);
        Assert.Contains("CreateCellHoldingViewAsync(name, ViewType.ThreeD)", ws);

        string td = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs"));
        start = td.IndexOf("private async Task NewThreeDViewFromLayoutAsync(", StringComparison.Ordinal);
        Assert.Contains("OpenOrActivateC3dEditor(path, newlyCreated: true)", td[start..]);
        Assert.Contains("if (newlyCreated) vm.Viewer.IsPerspective = false;", td);
    }

    /// <summary>The cell menu's Open 3D View sits with the other Open items and runs the tree's command.</summary>
    [Fact]
    public void CellMenu_OffersOpen3DView()
    {
        string axaml = Src("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");
        int layout = axaml.IndexOf("Header=\"Open Layout\"", StringComparison.Ordinal);
        int threeD = axaml.IndexOf("Header=\"Open 3D View\"", StringComparison.Ordinal);
        Assert.True(layout > 0 && threeD > layout, "Open 3D View must follow Open Layout.");
        Assert.Contains("Command=\"{Binding OpenThreeDCommand}\"", axaml[threeD..(threeD + 200)]);
    }

    private static string Src(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static string StripComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
