using System.IO;
using CircuitRF.Design.Cells;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A double-click on a cell opens its schematic, else its layout, else its 3D view — the primary, or
/// the alphabetically first file when none is primary — and its .ccell only when it has none; Edit
/// Parameters still opens the .ccell.
/// </summary>
public class CellOpenOrderTests : IDisposable
{
    private readonly string _cell;

    public CellOpenOrderTests()
    {
        _cell = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}", "amp");
        Directory.CreateDirectory(_cell);
        File.WriteAllText(Path.Combine(_cell, CellFolder.CcellFileName), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_cell)!, recursive: true); } catch { }
    }

    private void AddView(ViewType view, string name)
    {
        var dir = CellFolder.SubFolderPath(_cell, view);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + CellFolder.ViewExtension(view)), "{}");
    }

    [Theory]
    [InlineData("sch,sym,lay,3d", ViewType.Schematic)]
    [InlineData("sym,lay,3d",     ViewType.Layout)]
    [InlineData("sym,3d",         ViewType.ThreeD)]
    [InlineData("sym",            null)]
    [InlineData("",               null)]
    public void OpensTheFirstViewPresent(string present, ViewType? expected)
    {
        foreach (var v in present.Split(',', StringSplitOptions.RemoveEmptyEntries))
            AddView(v switch { "sch" => ViewType.Schematic, "sym" => ViewType.Symbol,
                               "lay" => ViewType.Layout,    _     => ViewType.ThreeD }, "amp");

        var got = CellOpenOrder.Resolve(_cell);

        Assert.Equal(expected, got?.View);
        if (got is { } g) Assert.True(File.Exists(g.Path), g.Path);
    }

    /// <summary>Several views of each kind and no primary named anywhere: the first schematic,
    /// alphabetically, still opens.</summary>
    [Fact]
    public void NoPrimaryNamed_OpensTheAlphabeticallyFirstSchematic()
    {
        AddView(ViewType.Schematic, "c");
        AddView(ViewType.Schematic, "A");
        AddView(ViewType.Schematic, "b");
        AddView(ViewType.Layout, "a");
        AddView(ViewType.Layout, "b");

        var got = CellOpenOrder.Resolve(_cell);

        Assert.Equal(ViewType.Schematic, got?.View);
        Assert.Equal("A.csch", Path.GetFileName(got?.Path));
    }

    /// <summary>Edit Parameters shared the double-click's command; it must keep opening the .ccell.</summary>
    [Fact]
    public void EditParameters_DoesNotBindTheDoubleClickCommand()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx"))) dir = Path.GetDirectoryName(dir);
        var axaml = File.ReadAllText(Path.Combine(dir!, "src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml"));

        int i = axaml.IndexOf("Header=\"Edit Parameters\"", StringComparison.Ordinal);
        Assert.True(i > 0, "Edit Parameters item not found");
        Assert.Contains("Command=\"{Binding EditParametersCommand}\"", axaml[i..(i + 200)], StringComparison.Ordinal);
    }
}
