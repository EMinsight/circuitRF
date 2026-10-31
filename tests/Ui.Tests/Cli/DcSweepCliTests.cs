// ================================================================
//  DcSweepCliTests.cs — `circuitrf dc` dispatches a parametric_sweep that wraps its DC analysis,
//  as sparam, hb, lp and lpp do theirs (brief MC-30 R-mc30-1). It ran one operating point and said
//  nothing about the axes it dropped. The real CLI, as a process, against the in-process engine.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class DcSweepCliTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-dc-sweep-" + Guid.NewGuid().ToString("N")[..12]);

    public DcSweepCliTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>An I–V family: Vbe stepped inside Vce, the shape a FET's Vgs-inside-Vds family takes.</summary>
    private string Netlist()
    {
        string path = Path.Combine(_root, "family.cnl");
        File.WriteAllText(path, """
            Vgs = 0.7
            Vds = 1
            Vdc:VG b 0 Vdc=Vgs
            Vdc:VD d 0 Vdc=Vds
            R:RB b bi R=1k
            BJT_NPN:Q1 d bi 0 Is=2e-17 Bf=95 Nf=1 Vaf=45 Ikf=3e-3
            analysis DC1 type=dc
            analysis SWG type=parametric_sweep Var=Vgs Values=0.6,0.7,0.8 Inner=DC1
            analysis SWD type=parametric_sweep Var=Vds Values=0.5,1,2,3 Inner=SWG
            measure Vbi = DC1.V("bi")
            """);
        return path;
    }

    [Fact]
    public void TwoAxisFamily_MatchesTheEngineInProcess()
    {
        string cnl = Netlist(), npy = Path.Combine(_root, "family.npy");
        var run = CliProcess(["dc", cnl, "-o", npy]);
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Contains("SWD", run.StdErr);

        var (ds, _) = DataSetImporter.Import(npy);
        var v = ds.Cubes["V"];
        Assert.Equal(["Vds", "Vgs"], v.Axes.Take(2).Select(a => a.Name));
        Assert.Equal([4, 3], v.Axes.Take(2).Select(a => a.Values.Length));
        Assert.Contains("Vbi", ds.CubesIn(RfCore.Data.DataSet.MeasurementsGroup).Keys);

        var (lib, tb) = CnlReader.ReadFile(cnl);
        var sweep = tb.Analyses.OfType<ParametricSweepAnalysis>().Single(a => a.Name == "SWD");
        var settings = new AnalysisSettings
        {
            NonlinearMaxIter = AnalysisSettings.Default.NonlinearMaxIter,
            DcBiasRampSteps = AnalysisSettings.Default.DcBiasRampSteps,
            Gmin = AnalysisSettings.Default.Gmin,
            ConductanceRegularization = RegularizationMode.Always,
        };
        var expected = ParametricSweepEngine.Run(sweep, lib, tb, settings, baseDirectory: _root)["V"];
        Assert.Equal(expected.RealValues, v.RealValues);
    }

    [Fact]
    public void UnsweptDc_IsUnchanged_AndRefusesAnUnknownExportExtension()
    {
        string cnl = Path.Combine(_root, "op.cnl");
        File.WriteAllText(cnl, """
            Vdc:V1 top 0 Vdc=2
            R:R1 top mid R=1k
            R:R2 mid 0 R=1k
            analysis DC1 type=dc
            """);
        var plain = CliProcess(["dc", cnl]);
        Assert.Equal(0, plain.ExitCode);
        Assert.Contains("Node voltages:", plain.StdOut);

        var refused = CliProcess(["dc", cnl, "-o", Path.Combine(_root, "op.s1p")]);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("names no format this verb writes", refused.StdErr);
    }

    private static (int ExitCode, string StdOut, string StdErr) CliProcess(string[] args)
        => CircuitRF.Ui.Tests.Em3d.CliProcess.Run(RepoRoot(), [], args);

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}
