// ================================================================
//  SparamSweepCliTests.cs — `circuitrf sparam` dispatches a parametric_sweep that wraps its
//  S-parameter analysis, as hb, lp and lpp do theirs. It ran the inner analysis alone and wrote
//  one point, with nothing said (designer report, round 26). The real CLI, as a process.
// ================================================================

using System.Diagnostics;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class SparamSweepCliTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-sparam-sweep-" + Guid.NewGuid().ToString("N")[..12]);

    public SparamSweepCliTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Netlist()
    {
        string path = Path.Combine(_root, "shunt.cnl");
        File.WriteAllText(path, """
            Rs = 50 Ohm
            Port:P1 a 0 Num=1 Z=50 Ohm
            R:R1 a 0 R=Rs
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            analysis SW1 type=parametric_sweep Var=Rs Values=25,50,100 Inner=SP1
            """);
        return path;
    }

    [Fact]
    public void Sweep_WrapsTheSParameterRun_AndCarriesItsAxis()
    {
        string npy = Path.Combine(_root, "shunt.npy");
        var run = RunCli("sparam", Netlist(), "-o", npy);
        Assert.True(run.ExitCode == 0, run.StdErr);

        var (ds, _) = DataSetImporter.Import(npy);
        var s = ds.Cubes["S"];
        Assert.Equal("Rs", s.Axes[0].Name);
        Assert.Equal(3, s.Axes[0].Values.Length);
    }

    [Fact]
    public void Sweep_ToTouchstone_IsRefused()
    {
        var run = RunCli("sparam", Netlist(), "-o", Path.Combine(_root, "shunt.s1p"));
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("no axis for the sweep", run.StdErr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SparamSweepCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}
