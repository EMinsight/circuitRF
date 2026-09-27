// ================================================================
//  StepConvertCliTests.cs — brief-em3d-68 R-em3d68-7: `circuitrf convert part.step -o <cell>/3d/part.c3d`, run as a
//  PROCESS. The .c3d it writes is byte for byte StepImport.Import's in process, and a comment-stripped scan of src/Cli
//  finds no import logic of its own — the rule Authoring.cs states for every verb that creates a document.
//  brief-em3d-69 R-em3d69-5: `convert <x.c3d | x.clay | cell | board…> -o out.step`, the same way round: the file is
//  StepExport.Export's bytes but for FILE_NAME's time-stamp, and src/Cli holds no export logic.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class StepConvertCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-stepcli68-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [KernelFact]
    public void Gate9_TheProcessWritesTheSameC3dBytesAsStepImportInProcess_AndCopiesTheFile()
    {
        string ws = Workspace();
        using var kernel = KernelForTests.New();
        var box = GeometryKernelTree.From(new C3dBox { Name = "body", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(500_000, 400_000, 300_000) }, 1000);
        var pin = GeometryKernelTree.From(new C3dCylinder { Name = "pin", Base = new C3dPoint3(250_000, 200_000, 300_000), Length = 200_000, Radius = 50_000 }, 1000);
        string src = Path.Combine(ws, "incoming", "part.step");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllBytes(src, kernel.Export([new(box, "body", [1, 0, 0]), new(pin, "Copper")], "step", "in"));

        string viaCli = Path.Combine(ws, "A", "3d", "A.c3d");
        var run = RunCli("convert", src, "-o", viaCli);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.Equal(Path.GetFullPath(viaCli), run.StdOut.Trim());

        string inProcess = Path.Combine(ws, "B", "3d", "B.c3d");
        var result = StepImport.Import(src, inProcess, new StepImportOptions(), kernel);

        Assert.Equal(File.ReadAllBytes(inProcess), File.ReadAllBytes(viaCli));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(ws, "A", "3d", "part.step")));
        var doc = C3dPersistence.LoadFromFile(viaCli);
        Assert.Equal([("body", "Brass"), ("Copper", "Copper")], doc.Objects.Cast<C3dStep>().Select(s => (s.Name, s.Material)));
        Assert.All(doc.Objects.Cast<C3dStep>(), s => Assert.Equal("inch", s.Unit));
        Assert.Equal("../../incoming/part.step", ((C3dStep)doc.Objects[0]).SourcePath);
        Assert.Equal(2, result.Objects.Count);

        // Importing into an existing document is writing its Step objects: the verb refuses, naming that.
        var again = RunCli("convert", src, "-o", viaCli);
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("reference topic=c3d", again.StdErr + again.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheKernel_ConvertRefusesInTheCapabilitysOwnWords_AndAStepFileIsRecognisedByItsHeader()
    {
        string ws = Workspace();
        string src = Path.Combine(ws, "part.dat");                // no telling extension: the header says what it is
        File.WriteAllText(src, "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
        string missing = Path.Combine(_root, "no-such-worker");
        var run = RunCli("convert", src, "-o", Path.Combine(ws, "A", "3d", "A.c3d"), env: new() { [GeometryKernel.EnvironmentVariable] = missing });

        Assert.Equal(1, run.ExitCode);
        var cap = new GeometryKernel(new GeometryKernelOptions
        {
            Locate = () => GeometryKernelLocator.Locate(missing, AppContext.BaseDirectory, GeometryKernelLocator.ProcessRid,
                                                        GeometryKernelRecipe.ShippedRids, OperatingSystem.IsWindows()),
        }).Probe();
        Assert.Contains(GeometryKernel.NeedsKernel("Import STEP", cap), run.StdErr + run.StdOut, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(ws, "A")));
    }

    [Fact]
    public void Gate9_SrcCliHoldsNoImportLogic_ItCallsStepImport()
    {
        string cli = Path.Combine(RepoRoot(), "src", "Cli");
        string all = string.Concat(Directory.GetFiles(cli, "*.cs", SearchOption.AllDirectories).Select(f => StripComments(File.ReadAllText(f))));
        foreach (string needle in (string[])["new C3dStep", "AutoMatch", "CopyInto", "ProposeName", ".ImportStep(", "SourceSpelling", "StepHash("])
            Assert.DoesNotContain(needle, all, StringComparison.Ordinal);
        Assert.Contains("StepImport.Import(", StripComments(File.ReadAllText(Path.Combine(cli, "LayoutConvert.cs"))), StringComparison.Ordinal);
    }

    // ── brief-em3d-69: STEP export ──────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d69 gate 1 — the process writes the bytes StepExport.Export writes in process, for a .c3d, a .clay and a cell
    /// folder, every line but FILE_NAME's time-stamp (the file carries it by design, as `em`'s gate exempts its provenance
    /// write-timestamp). OCCT's writer is byte-deterministic across processes once the worker renumbers each file's assembly
    /// occurrences from 1 (OCCT counts them for the life of the process), so no canonical re-read is needed.
    /// </summary>
    [KernelFact]
    public void Gate1_ConvertToStep_WritesTheSameBytesAsStepExportInProcess_ForAC3dALayoutAndACellFolder()
    {
        string ws = Workspace();
        string c3d = Path.Combine(ws, "Part", "3d", "Part.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
        C3dPersistence.SaveToFile(c3d, new C3dDocument
        {
            DisplayUnit = LayoutUnit.Mil,
            Objects =
            [
                new C3dBox { Name = "body", Material = "Brass", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(500_000, 400_000, 300_000) },
                new C3dBoolean
                {
                    Name = "pin", Op = C3dBooleanOp.Subtract,
                    Blank = new C3dBox { Material = "Copper", Min = new C3dPoint3(100_000, 100_000, 200_000), Size = new C3dPoint3(100_000, 100_000, 300_000) },
                    Tools = [new C3dCylinder { Name = "bore", Base = new C3dPoint3(150_000, 150_000, 0), Length = 600_000, Radius = 20_000 }],
                },
            ],
        });
        string example = Path.Combine(RepoRoot(), "examples", "3D Package");
        using var kernel = KernelForTests.New();
        (string Source, string[] Flags)[] cases =
            [(c3d, []), (Path.Combine(example, "Thru die", "layout", "Thru die.clay"), []), (Path.Combine(example, "Package"), ["--assembly"])];
        foreach (var (source, flags) in cases)
        {
            string viaCli = Path.Combine(_root, "cli" + Guid.NewGuid().ToString("N")[..6], "model.step");
            var run = RunCli(["convert", source, "-o", viaCli, .. flags], env: null);
            Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
            Assert.Equal(Path.GetFullPath(viaCli), run.StdOut.Trim());

            string inProcess = Path.Combine(_root, "in" + Guid.NewGuid().ToString("N")[..6], "model.step");
            StepExport.Export(source, inProcess, new StepExportOptions { Assembly = flags.Length > 0 }, kernel);
            Assert.Equal(StripTimestamp(File.ReadAllText(inProcess)), StripTimestamp(File.ReadAllText(viaCli)));
        }
        Assert.Contains("DRAUGHTING_PRE_DEFINED_COLOUR('red')", File.ReadAllText(Directory.GetFiles(_root, "model.step", SearchOption.AllDirectories)
                                                                       .First(f => File.ReadAllText(f).Contains("PRODUCT('body'"))), StringComparison.Ordinal);
    }

    /// <summary>R-em3d69-5a/-5c — a board file exports in one line; the refusals each name their remedy, in the words of the
    /// function that refused.</summary>
    [KernelFact]
    public void ConvertToStep_FromABoardInOneLine_AndItsRefusalsNameTheirRemedies()
    {
        string board = Path.Combine(RepoRoot(), "testdata", "pcb-samples", "stackup-present.kicad_pcb");
        var ok = RunCli(["convert", board, "-o", Path.Combine(_root, "board.step")], env: null);
        Assert.True(ok.ExitCode == 0, ok.StdErr + ok.StdOut);
        Assert.StartsWith("ISO-10303-21;", File.ReadAllText(Path.Combine(_root, "board.step")), StringComparison.Ordinal);

        // A board with no stackup: Em3dLayoutSolids.From's own sentence, verbatim.
        var flat = RunCli(["convert", Path.Combine(RepoRoot(), "testdata", "pcb-samples", "stackup-absent.kicad_pcb"), "-o", Path.Combine(_root, "flat.step")], env: null);
        Assert.Equal(1, flat.ExitCode);
        Assert.Contains("has no stackup layers", flat.StdErr, StringComparison.Ordinal);

        // A cell in no workspace: its layout's technology resolves to nothing.
        string cell = Path.Combine(_root, "loose", "Both");
        Directory.CreateDirectory(Path.Combine(cell, "3d"));
        Directory.CreateDirectory(Path.Combine(cell, "layout"));
        C3dPersistence.SaveToFile(Path.Combine(cell, "3d", "Both.c3d"), new C3dDocument
        {
            Objects = [new C3dBox { Name = "loose", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000, 1000, 1000) }],
        });
        LayoutPersistence.SaveToFile(Path.Combine(cell, "layout", "Both.clay"), new LayoutView { TechRef = "../../missing.ctech" });

        var both = RunCli(["convert", cell, "-o", Path.Combine(_root, "both.step")], env: null);
        Assert.Equal(1, both.ExitCode);
        Assert.Contains("--view 3d|layout", both.StdErr, StringComparison.Ordinal);

        var nothing = RunCli(["convert", cell, "--view", "3d", "-o", Path.Combine(_root, "nothing.step")], env: null);
        Assert.Equal(1, nothing.ExitCode);
        Assert.Contains("nothing to export", nothing.StdErr, StringComparison.Ordinal);

        var noTech = RunCli(["convert", cell, "--view", "layout", "-o", Path.Combine(_root, "notech.step")], env: null);
        Assert.Equal(1, noTech.ExitCode);
        Assert.Contains("--tech", noTech.StdErr, StringComparison.Ordinal);

        var misplaced = RunCli(["convert", board, "--assembly", "-o", Path.Combine(_root, "x.gds")], env: null);
        Assert.Equal(1, misplaced.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, "both.step")) || File.Exists(Path.Combine(_root, "nothing.step")) || File.Exists(Path.Combine(_root, "notech.step")));
    }

    [Fact]
    public void WithoutTheKernel_ConvertToStepRefusesInTheCapabilitysOwnWords()
    {
        string missing = Path.Combine(_root, "no-such-worker");
        string board = Path.Combine(RepoRoot(), "testdata", "pcb-samples", "stackup-present.kicad_pcb");
        var run = RunCli(["convert", board, "-o", Path.Combine(_root, "b.step")], env: new() { [GeometryKernel.EnvironmentVariable] = missing });
        Assert.Equal(1, run.ExitCode);
        var cap = new GeometryKernel(new GeometryKernelOptions
        {
            Locate = () => GeometryKernelLocator.Locate(missing, AppContext.BaseDirectory, GeometryKernelLocator.ProcessRid,
                                                        GeometryKernelRecipe.ShippedRids, OperatingSystem.IsWindows()),
        }).Probe();
        Assert.Contains(GeometryKernel.NeedsKernel("Export STEP", cap), run.StdErr + run.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "b.step")));
    }

    /// <summary>R-em3d69 gate 8 — src/Cli holds no export logic: no call into the worker, no STEP text; it calls StepExport.</summary>
    [Fact]
    public void Gate8_SrcCliHoldsNoExportLogic_ItCallsStepExport()
    {
        string cli = Path.Combine(RepoRoot(), "src", "Cli");
        string all = string.Concat(Directory.GetFiles(cli, "*.cs", SearchOption.AllDirectories).Select(f => StripComments(File.ReadAllText(f))));
        foreach (string needle in (string[])[".WriteStep(", "write-step", ".Export(new", "kernel.Export(", "ISO-10303", "FILE_NAME", "PRODUCT(",
                                             "Em3dPrecedence", "Em3dLayoutSolids.From("])
            Assert.DoesNotContain(needle, all, StringComparison.Ordinal);
        Assert.Contains("StepExport.Export(", StripComments(File.ReadAllText(Path.Combine(cli, "LayoutConvert.cs"))), StringComparison.Ordinal);
    }

    /// <summary>FILE_NAME's second field is the time the file was written; everything else must match.</summary>
    private static string StripTimestamp(string step) => Regex.Replace(step, @"FILE_NAME\('([^']*)','[^']*'", "FILE_NAME('$1',''");

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Brass", Sigma20 = 1.5e7, Color = "#ff0000" }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args) => RunCli(args, env: null);

    private (int ExitCode, string StdOut, string StdErr) RunCli(string a0, string a1, string a2, string a3, Dictionary<string, string> env)
        => RunCli([a0, a1, a2, a3], env);

    private (int ExitCode, string StdOut, string StdErr) RunCli(string[] args, Dictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(StepConvertCliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        // A throwaway per-user state directory: the child's geometry cache is its own, never the developer's.
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = Path.Combine(_root, "state");
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) return d.FullName;
        throw new InvalidOperationException("no source tree above the test assembly");
    }
}
