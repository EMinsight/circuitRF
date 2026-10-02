// ================================================================
//  NewViewMissingSubFolderTests.cs — the project tree's New Symbol / New Schematic / New Layout /
//  New 3D View on a cell that has never had that kind of view.
//
//  A cell made with only a layout has no schematic/ or symbol/ sub-folder, and the view model used to
//  refuse New Schematic there with "Schematic sub-folder not found" (New Symbol and New Layout had the
//  same guard). CellCreate's writers already create the sub-folder; the guard ran first and stopped
//  them. The commands open a modal name dialog, so the two halves are held separately: every writer
//  creates its missing sub-folder, and the view model holds no refusal in front of them.
// ================================================================

using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.Tests;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class NewViewMissingSubFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-newview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(ViewType.Schematic)]
    [InlineData(ViewType.Symbol)]
    [InlineData(ViewType.Layout)]
    [InlineData(ViewType.ThreeD)]
    public void EachWriterCreatesItsMissingSubFolder(ViewType type)
    {
        // A cell folder holding only the OTHER kinds of view — none of `type`'s sub-folder.
        string cellDir = Path.Combine(_root, "amp");
        Directory.CreateDirectory(cellDir);
        string sub = CellFolder.SubFolderPath(cellDir, type);
        Assert.False(Directory.Exists(sub));

        string written = type switch
        {
            ViewType.Schematic => CellCreate.WriteSchematicView(cellDir, "amp", "amp"),
            ViewType.Symbol    => CellCreate.WriteSymbolView(cellDir, "amp"),
            ViewType.Layout    => CellCreate.WriteLayoutView(cellDir, "amp", CellCreate.NewLayoutView(null)),
            ViewType.ThreeD    => CellCreate.WriteThreeDView(cellDir, "amp", new C3dDocument()),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        Assert.True(File.Exists(written));
        Assert.Equal(Path.GetFullPath(sub), Path.GetFullPath(Path.GetDirectoryName(written)!));
    }

    [Fact]
    public void TheViewModelDoesNotRefuseAMissingSubFolder()
    {
        string code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        code = Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        Assert.DoesNotContain("sub-folder not found", code, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        return dir;
    }
}
