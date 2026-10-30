using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Cli;

/// <summary>
/// <c>circuitrf import pdk</c> (brief-gdf-1 R-gdf1-4): File ▸ Import PDK with no window. It owns no
/// import logic — <see cref="PdkWorkspaceImport"/> does, and the view model calls the same function.
///
/// <para>The kit is synthetic: one record-format symbol, one subcircuit, and a parametric-cell package
/// written against the cell API, so the import has a declaration to write and generators to start.</para>
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class ImportPdkCliVerbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-importpdk-" + Guid.NewGuid().ToString("N")[..12]);

    private string StateDir => Path.Combine(_root, "state");

    public ImportPdkCliVerbTests()
    {
        Directory.CreateDirectory(StateDir);
        CircuitRF.Ui.AppDataRoot.RedirectTo(StateDir);
        PdkKitRegistry.ResetAllForTests();
    }

    public void Dispose()
    {
        ImportPdk.InterpreterFinder = null;
        PdkKitRegistry.ResetAllForTests();
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Kit()
    {
        string kit = Path.Combine(_root, "synthkit");
        void Write(string rel, string text)
        {
            string p = Path.Combine(kit, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        Write("libs.sym/widget.sym", """
            v {version=1.2}
            K {type=res
            template="name=X1 model=widget w=1u l=2u"}
            L 4 -20 -20 20 20 {}
            B 5 -2.5 -2.5 2.5 2.5 {name=a dir=inout}
            B 5 17.5 -2.5 22.5 2.5 {name=b dir=inout}
            """);
        Write("libs.spice/devices.lib", """
            .subckt widget a b w=1u l=2u
            R1 a b {w*l*1e12}
            .ends widget
            """);
        Write("libs.python/synth_cells/__init__.py", "");
        Write("libs.python/synth_cells/widget_code.py", "from cni.dlo import *\nclass widget(DloGen): pass\n");
        return kit;
    }

    private string Workspace(string name)
    {
        WorkspaceCreate.Create(_root, name, technologyId: null);
        return Path.Combine(_root, name);
    }

    // ── byte identity, and no second copy ────────────────────────────────────────────────────────

    /// <summary>The verb, run as a PROCESS, records the same <c>.cws</c> reference and writes the same
    /// cell declaration as the in-process function the GUI's import calls.</summary>
    [Fact]
    public void TheVerbWritesWhatTheGuisImportWrites()
    {
        string kit = Kit();
        string viaCli = Workspace("ws_cli"), inProcess = Workspace("ws_fn");

        var (exit, _, err) = RunCli("import", "pdk", kit, "--into", viaCli);
        Assert.True(exit == 0, err);
        PdkWorkspaceImport.Import(kit, Path.Combine(inProcess, WorkspacePersistence.FileName));

        string Refs(string ws) => JsonSerializer.Serialize(
            WorkspacePersistence.LoadFromFile(Path.Combine(ws, WorkspacePersistence.FileName)).PdkRefs);
        Assert.Equal(Refs(inProcess), Refs(viaCli));
        Assert.Contains("synthkit", Refs(viaCli), StringComparison.Ordinal);

        string folder = KitPCellLibrary.FolderNameFor("synthkit");
        foreach (string file in new[] { PCellGeneratorManifest.FileName, KitPCellLibrary.EntryScriptName })
            Assert.Equal(File.ReadAllBytes(Path.Combine(inProcess, folder, file)),
                         File.ReadAllBytes(Path.Combine(viaCli, folder, file)));
    }

    /// <summary>The view model calls the extracted function and kept no copy of what it does.</summary>
    [Fact]
    public void TheViewModelKeptNoSecondCopy()
    {
        string code = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));

        Assert.Contains("PdkWorkspaceImport.Read(", code, StringComparison.Ordinal);
        Assert.Contains("PdkWorkspaceImport.Commit(", code, StringComparison.Ordinal);
        Assert.Contains("PdkWorkspaceImport.ReadReferenced(", code, StringComparison.Ordinal);
        foreach (string gone in new[] { "PdkImporter.Import(", "PdkPartInstaller.Install(",
                                        "KitPCellLibrary.EnsureDeclared(", "new CwsPdkRef" })
            Assert.DoesNotContain(gone, code, StringComparison.Ordinal);
    }

    // ── consent ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Without consent no interpreter is even looked for, so no kit code can run; the import
    /// still succeeds and says why. <c>--allow-code</c> starts the kit's generators for this one run.</summary>
    [Fact]
    public void WithoutConsentNoInterpreterIsStarted()
    {
        string kit = Kit();
        int asked = 0;
        ImportPdk.InterpreterFinder = (_, _) => { asked++; return null; };

        var (exit, err) = Run("import", "pdk", kit, "--into", Workspace("ws_no"));
        Assert.Equal(0, exit);
        Assert.Equal(0, asked);
        Assert.Contains("were not started", err, StringComparison.Ordinal);

        (exit, _) = Run("import", "pdk", kit, "--into", Workspace("ws_yes"), "--allow-code");
        Assert.Equal(0, exit);
        Assert.Equal(1, asked);
    }

    // ── a placed kit part, headless ──────────────────────────────────────────────────────────────

    /// <summary>A schematic holding a part placed from an imported kit extracts with no window: the
    /// workspace's kits are loaded for the run, as a workspace open loads them.</summary>
    [Fact]
    public void APlacedKitPartExtractsHeadlessly()
    {
        string ws = Workspace("ws_run");
        Assert.Equal(0, Run("import", "pdk", Kit(), "--into", ws).Exit);
        PdkKitRegistry.ResetAllForTests();   // nothing in memory: what a fresh process has

        string csch = Path.Combine(ws, "bench.csch");
        var model = new SchematicEditModel { SchematicDirectory = ws };
        var comp = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor("synthkit", "widget"),
        };
        comp.Parameters.Add(new EditableParameter { Name = "w", Expression = "3E-06" });
        model.Components.Add(comp);
        SchematicPersistence.SaveToFile(csch, model, "bench");

        string cnl = Path.Combine(ws, "bench.cnl");
        var (exit, err) = Run("netlist", csch, "-o", cnl);
        Assert.True(exit == 0, err);
        // Resolved through the kit — without the load it is an unresolved reference and no instance.
        Assert.Matches(@"(?m)^ExtDevice:X1\s.*Provider=synthkit\s.*w=3E-06", File.ReadAllText(cnl));
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private static (int Exit, string Err) Run(params string[] args)
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

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = StateDir;

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ImportPdkCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
