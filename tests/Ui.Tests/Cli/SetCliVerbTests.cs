// ================================================================
//  SetCliVerbTests.cs — `--set` on the two run verbs that lacked it, `sparam` and `dc`.
//
//  Each verb is run as a PROCESS twice on one netlist, once as written and once with a global
//  overridden, and the answer must move to the value the override implies. A flag that parsed and
//  was then not applied would leave both answers equal, which is the failure this gate is for.
// ================================================================

using RfCore;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class SetCliVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-set-" + Guid.NewGuid().ToString("N")[..12]);

    public SetCliVerbTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    /// <summary>A series resistor between two 50 Ω ports: |S21| = 100 / (100 + R). The netlist says
    /// 50 Ω (|S21| = 2/3); <c>--set Rs=25</c> must give 0.8. (Not 0 Ω: the engine regularises a zero
    /// resistor, so a through would read 0.999.)</summary>
    [Fact]
    public void Sparam_AppliesSet_BeforeElaboration()
    {
        string cnl = Write("series.cnl", """
            Rs = 50 Ohm
            Port:P1 a 0 Num=1 Z=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            R:R1 a b R=Rs
            analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
            """);

        double S21(params string[] extra)
        {
            string outPath = Path.Combine(_dir, $"out{Guid.NewGuid():N}.s2p");
            var run = Cli(["sparam", cnl, .. extra, "-o", outPath]);
            Assert.True(run.ExitCode == 0, run.StdErr);
            return TouchstoneIO.ReadFile(outPath).Matrices[0][1, 0].Magnitude;
        }

        Assert.Equal(2.0 / 3.0, S21(), 6);
        Assert.Equal(0.8, S21("--set", "Rs=25"), 6);
    }

    /// <summary>A divider of two equal resistors from a supply global: the midpoint is half of it,
    /// and <c>--set Vs=4</c> must move it from 1 V to 2 V.</summary>
    [Fact]
    public void Dc_AppliesSet_BeforeElaboration()
    {
        string cnl = Write("divider.cnl", """
            Vs = 2
            Vdc:V1 top 0 Vdc=Vs
            R:R1 top mid R=1k
            R:R2 mid 0 R=1k
            """);

        double Mid(params string[] extra)
        {
            var run = Cli(["dc", cnl, .. extra]);
            Assert.True(run.ExitCode == 0, run.StdErr);
            string line = run.StdOut.Split('\n').Single(l => l.TrimStart().StartsWith("mid ", StringComparison.Ordinal));
            return double.Parse(line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1],
                                System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(1.0, Mid(), 6);
        Assert.Equal(2.0, Mid("--set", "Vs=4"), 6);
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
