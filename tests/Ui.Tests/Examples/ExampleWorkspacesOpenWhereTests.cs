// ================================================================
//  ExampleWorkspacesOpenWhereTests.cs — where Tools ▸ Examples opens the copy
//  (brief-examples-open-here-when-empty.md, R-ex1-1).
//
//  An EMPTY window — no workspace, no torn-off document window, no unsaved docked tab — takes the
//  copy itself; any other window hands it to a new one. Each test runs the real command from after
//  the folder picker (stubbed to a temp directory) through the real copy, and observes the new-window
//  opener through its seam, since App.OpenWorkspaceInNewWindow is static.
//
//  In this collection because opening a workspace pushes it to Open Recent, which is written to the
//  per-user preferences file — redirected here, so no run of this class touches the real one.
// ================================================================

using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(UserStateDirectoryCollection.Name)]
public sealed class ExampleWorkspacesOpenWhereTests : IDisposable
{
    // The smallest shipped example: the rule is about where it opens, not what is in it.
    private const string Example = "Smith Chart";

    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-example-open-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly List<string> _newWindowOpens = [];

    public ExampleWorkspacesOpenWhereTests()
    {
        Directory.CreateDirectory(_tmp);
        AppDataRoot.RedirectTo(Path.Combine(_tmp, "state"));
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    private WorkspaceViewModel NewVm(string parentDir)
    {
        Directory.CreateDirectory(parentDir);
        return new WorkspaceViewModel
        {
            ExampleParentDirPicker     = _ => Task.FromResult<string?>(parentDir),
            OpenExampleInNewWindowHook = _newWindowOpens.Add,
        };
    }

    private static string CwsOf(string parentDir) => Path.Combine(parentDir, Example, ".cws");

    [Fact]
    public async Task AnEmptyWindowOpensTheCopyItself()
    {
        string parent = Path.Combine(_tmp, "here");
        var vm = NewVm(parent);
        Assert.True(vm.WindowIsEmptyForExample());

        await vm.OpenExampleCommand.ExecuteAsync(Example);

        Assert.Equal(CwsOf(parent), vm.CurrentWorkspacePath);
        Assert.Empty(_newWindowOpens);
        // Opened through the funnel every opened workspace goes through, so it is in Open Recent.
        Assert.Equal(CwsOf(parent), AppPreferencesIo.Load().RecentWorkspaces?.FirstOrDefault());
    }

    [Fact]
    public async Task AWindowWithAWorkspaceHandsTheCopyToANewWindow()
    {
        string first = Path.Combine(_tmp, "first");
        var vm = NewVm(first);
        await vm.OpenExampleCommand.ExecuteAsync(Example);
        Assert.Equal(CwsOf(first), vm.CurrentWorkspacePath);

        string second = Path.Combine(_tmp, "second");
        Directory.CreateDirectory(second);
        vm.ExampleParentDirPicker = _ => Task.FromResult<string?>(second);
        await vm.OpenExampleCommand.ExecuteAsync(Example);

        Assert.Equal(CwsOf(first), vm.CurrentWorkspacePath);
        Assert.Equal([CwsOf(second)], _newWindowOpens);
    }

    /// <summary>The launch action's untouched scratch schematic is not a reason to open a second
    /// window: it is a clean DOCKED tab, and the example replaces it as it would the Welcome tab.</summary>
    [Fact]
    public async Task AWindowWithOnlyACleanScratchDocumentOpensTheCopyItself()
    {
        string parent = Path.Combine(_tmp, "doc");
        var vm = NewVm(parent);
        await vm.NewScratchSchematicCommand.ExecuteAsync(null);
        Assert.True(vm.WindowIsEmptyForExample());

        await vm.OpenExampleCommand.ExecuteAsync(Example);

        Assert.Equal(CwsOf(parent), vm.CurrentWorkspacePath);
        Assert.Empty(_newWindowOpens);
    }
}
