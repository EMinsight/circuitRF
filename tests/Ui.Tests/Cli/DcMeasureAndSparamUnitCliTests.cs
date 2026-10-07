// ================================================================
//  DcMeasureAndSparamUnitCliTests.cs — brief-tuneopt-2 follow-up.
//
//  `dc` evaluates the bench's measure lines; `sparam` expands a frequency bound written as a
//  variable that carries its own unit once, not twice. Both run the verb as a PROCESS.
// ================================================================

using RfCore;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class DcMeasureAndSparamUnitCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-dcm-" + Guid.NewGuid().ToString("N")[..12]);

    public DcMeasureAndSparamUnitCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void Dc_PrintsTheBenchsMeasurements()
    {
        string cnl = Write("divider.cnl", """
            Vdc:V1 top 0 Vdc=2
            R:R1 top mid R=1k
            R:R2 mid 0 R=1k
            analysis DC1 type=dc
            measure Vmid_x3 = 3*DC1.V("mid")
            """);

        var run = Cli(["dc", cnl]);
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Contains("Measurements:\n  Vmid_x3 = 3\n", run.StdOut.Replace("\r", ""));
    }

    /// <summary>F1 = 2 GHz used as a start bound written in GHz: the grid is 2–3 GHz, as Simulate
    /// expands it, not 2e18 Hz.</summary>
    [Fact]
    public void Sparam_AVariableWithItsOwnUnit_IsNotScaledTwice()
    {
        string cnl = Write("unit.cnl", """
            F1 = 2 GHz
            Port:P1 a 0 Num=1 Z=50 Ohm
            R:R1 a 0 R=50 Ohm
            analysis SP1 type=sparam start="F1" startUnit=GHz stop="3" stopUnit=GHz npts=3
            """);

        string outPath = Path.Combine(_dir, "unit.s1p");
        var run = Cli(["sparam", cnl, "-o", outPath]);
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Equal([2e9, 2.5e9, 3e9], TouchstoneIO.ReadFile(outPath).Frequencies);
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) Cli(string[] args)
        => CircuitRF.Ui.Tests.Em3d.CliProcess.Run(RepoRoot(), [], args);

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}
