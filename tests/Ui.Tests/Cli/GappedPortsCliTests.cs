// ================================================================
//  GappedPortsCliTests.cs — brief DFR27-2 R-dfr27-2-3.
//
//  Touchstone numbers its ports 1…N by position, so a result whose Terms are 1, 2, 9, 10 cannot be
//  written as a .s4p without renumbering 9 and 10 to 3 and 4. Every Touchstone writer refuses it,
//  naming the missing numbers; .npy keeps the real numbers and is not refused.
// ================================================================

using System.Numerics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class GappedPortsCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-gap-" + Guid.NewGuid().ToString("N")[..12]);

    public GappedPortsCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private const string Bench = """
        Term:T1  c 0 Num=1  Z=50 Ohm
        Term:T2  d 0 Num=2  Z=50 Ohm
        Term:T9  x 0 Num=9  Z=50 Ohm
        Term:T10 y 0 Num=10 Z=50 Ohm
        R:R1 c d R=50 Ohm
        R:R2 x y R=100 Ohm
        analysis SP1 type=sparam start=1 GHz stop=2 GHz npts=2
        """;

    [Fact]
    public void Sparam_ToTouchstone_IsRefusedNamingTheMissingPorts()
    {
        string cnl = Path.Combine(_dir, "gap.cnl");
        File.WriteAllText(cnl, Bench);
        string snp = Path.Combine(_dir, "out.s4p");

        var run = Cli(["sparam", cnl, "-o", snp]);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("3–8", run.StdErr);
        Assert.Contains("1, 2, 9, 10", run.StdErr);
        Assert.False(File.Exists(snp));

        string npy = Path.Combine(_dir, "out.npy");
        var data = Cli(["sparam", cnl, "-o", npy]);
        Assert.True(data.ExitCode == 0, data.StdErr);
    }

    [Fact]
    public void TouchstoneExporter_RefusesAGappedResult()
    {
        var nums = new double[] { 1, 2, 9, 10 };
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube(
            [new Axis("freq", [1e9], "Hz"), new Axis("i", nums, "port"), new Axis("j", nums, "port")],
            new Complex[16]));

        var result = TouchstoneExporter.Export(ds, "SP1",
            new TouchstoneExportOptions(50, 6, 'g', RfCore.MatrixFormat.RI),
            new Dictionary<string, int>(), allSweepFiles: false, Path.Combine(_dir, "x"));

        Assert.Equal(TouchstoneExportStatus.GappedPorts, result.Status);
        Assert.Empty(result.WrittenPaths);
        Assert.Contains("3–8", result.Message);
    }

    private static (int ExitCode, string StdOut, string StdErr) Cli(string[] args)
        => CircuitRF.Ui.Tests.Em3d.CliProcess.Run(RepoRoot(), [], args);

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}
