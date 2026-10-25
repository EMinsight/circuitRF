using CircuitRF.Cli;
using CircuitRF.Ui.Tests.Lvs;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  A loose schematic (a Known File outside the workspace folder) and the workspace it is run from.
//
//  A cell reference is relative to the schematic holding it, so `../../FET` written from inside a
//  cell names nothing once the file lives elsewhere. ExternalCellRef.FindInHostWorkspace looks for
//  it in the host workspace, LAST: a stored path that resolves always wins, and a run says so when
//  the host offered another cell of that name.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class LooseSchematicHostWorkspaceTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;
    private readonly string _loose;

    public LooseSchematicHostWorkspaceTests()
    {
        _root  = Path.Combine(Path.GetTempPath(), "crf_loose_" + Guid.NewGuid().ToString("N")[..8]);
        _ws    = Path.Combine(_root, "ws");
        _loose = Path.Combine(_root, "loose");
        Directory.CreateDirectory(_ws);
        Directory.CreateDirectory(_loose);
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile());
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    internal static string CreateCell(string parent, string name)
    {
        string cellDir = CellFolder.CreateCellFolder(parent, name);
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol(
                primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
                pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
                portCount:  2));
        var schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        SchematicPersistence.SaveToFile(Path.Combine(schDir, name + ".csch"), new SchematicEditModel());
        return cellDir;
    }

    private static SchematicEditModel LooseModel(string dir, string cellRef, string? host)
    {
        Directory.CreateDirectory(dir);
        var model = new SchematicEditModel { SchematicDirectory = dir, HostWorkspaceRoot = host };
        model.Components.Add(new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = cellRef });
        return model;
    }

    [Fact]
    public void ALooseReferenceThatNamesNothing_ResolvesToTheHostWorkspacesCell()
    {
        CreateCell(_ws, "FET");
        string dir = Path.Combine(_loose, "Top", "schematic");

        Assert.Equal(CellSymbolState.NotFound, CellSymbolResolver.Resolve("../../FET", dir).State);
        Assert.Equal(CellSymbolState.Resolved, CellSymbolResolver.Resolve("../../FET", dir, _ws).State);

        var model = LooseModel(dir, "../../FET", _ws);
        Assert.NotNull(HierarchyResolver.ResolvePrimaryPath(model.Components[0], model));

        var choice = Assert.Single(NetExtractor.Extract(model, "tb", DiskCellResolver.Instance).HostCellChoices);
        Assert.False(choice.IsWarning);
        Assert.EndsWith($"used {Path.Combine(_ws, "FET")} from workspace 'ws'.", choice.Text);
    }

    [Fact]
    public void ASchematicInsideAWorkspace_IsNeverReadAgainstAnotherWorkspace()
    {
        CreateCell(_ws, "FET");
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        WorkspacePersistence.SaveToFile(Path.Combine(other, ".cws"), new CwsFile());
        WorkspaceRootFinder.InvalidateCache();

        string dir = Path.Combine(other, "Top", "schematic");
        Directory.CreateDirectory(dir);
        Assert.Equal(CellSymbolState.NotFound, CellSymbolResolver.Resolve("../../FET", dir, _ws).State);
    }

    [Fact]
    public void TwoHostCellsOfTheName_AreReportedAndNeitherIsUsed()
    {
        CreateCell(Path.Combine(_ws, "lib1"), "FET");
        CreateCell(Path.Combine(_ws, "lib2"), "FET");
        var model = LooseModel(Path.Combine(_loose, "Top", "schematic"), "../../FET", _ws);

        var result = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);

        Assert.Equal(CellSymbolState.NotFound,
            CellSymbolResolver.Resolve("../../FET", model.SchematicDirectory, _ws).State);
        var choice = Assert.Single(result.HostCellChoices);
        Assert.True(choice.IsWarning);
        Assert.Contains("2 cells named 'FET'", choice.Text);
        Assert.EndsWith("Neither was used.", choice.Text);
    }

    [Fact]
    public void AStoredPathThatResolves_WinsOverTheHost_AndTheRunSaysWhichItUsed()
    {
        string literal = CreateCell(_loose, "FET");
        string hosted  = CreateCell(_ws, "FET");
        var model = LooseModel(Path.Combine(_loose, "designs"), "../FET", _ws);

        var result = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);

        Assert.Contains(Path.GetFullPath(literal), result.CellKeys.Values);
        Assert.DoesNotContain(Path.GetFullPath(hosted), result.CellKeys.Values);
        var choice = Assert.Single(result.HostCellChoices);
        Assert.True(choice.IsWarning);
        Assert.Contains("found two cells named 'FET'", choice.Text);
        Assert.Contains(hosted, choice.Text);
        Assert.EndsWith($"Used {literal}.", choice.Text);
    }

    [Fact]
    public void SaveAs_RespellsAResolvingCellRef_AndLeavesAMissingOneAsWritten()
    {
        string fet = CreateCell(_ws, "FET");
        string topSch = CellFolder.SubFolderPath(CreateCell(_ws, "Top"), ViewType.Schematic);
        var model = LooseModel(topSch, "../../FET", null);
        model.Components.Add(new EditableComponent { InstanceName = "X2", Symbol = SymbolKind.Generic, CellRef = "../../Gone" });

        SchematicPersistence.SaveToFile(Path.Combine(_loose, "top.csch"), model);

        var (reread, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(_loose, "top.csch"));
        Assert.Equal(Path.GetFullPath(fet), ExternalCellRef.ResolveCellDir(reread.Components[0].CellRef, _loose));
        Assert.Equal("../../Gone", reread.Components[1].CellRef);
    }
}

/// <summary>The CLI's <c>--workspace</c>: the same lookup, the same sentence on stderr.</summary>
[Collection(LvsCliConsoleCollection.Name)]
public sealed class LooseSchematicHostWorkspaceCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_loosecli_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (int Exit, string Err) Run(params string[] args)
    {
        var (realOut, realErr) = (Console.Out, Console.Error);
        var err = new StringWriter();
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(err);
            JsonRun.Reset();
            return (CliEntry.Run(args), err.ToString());
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
    }

    [Fact]
    public void Netlist_WithWorkspace_UsesTheWorkspacesCell_AndSaysSo()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile());
        LooseSchematicHostWorkspaceTests.CreateCell(ws, "FET");
        string dir = Path.Combine(_root, "loose");
        Directory.CreateDirectory(dir);
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../../FET" });
        string csch = Path.Combine(dir, "top.csch");
        SchematicPersistence.SaveToFile(csch, model);
        CellSymbolResolver.InvalidateAll();

        var (exit, err) = Run("netlist", csch, "--workspace", ws);

        Assert.Equal(0, exit);
        Assert.Contains($"note: Cell instance 'X1': '../../FET' is not at the schematic's stored path; used {Path.Combine(ws, "FET")}", err);
    }

    [Fact]
    public void Workspace_NamingNoWorkspace_IsRefused()
    {
        Directory.CreateDirectory(_root);
        var (exit, err) = Run("netlist", Path.Combine(_root, "top.csch"), "--workspace", _root);

        Assert.Equal(1, exit);
        Assert.Contains("not a workspace", err);
    }
}
