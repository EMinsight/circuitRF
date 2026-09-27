// ================================================================
//  StepConvertCliTests.cs — brief-em3d-68 R-em3d68-7: `circuitrf convert part.step -o <cell>/3d/part.c3d`, run as a
//  PROCESS. The .c3d it writes is byte for byte StepImport.Import's in process, and a comment-stripped scan of src/Cli
//  finds no import logic of its own — the rule Authoring.cs states for every verb that creates a document.
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
