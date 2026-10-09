using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tests.Optimization;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-yield-15: the review's fixes to the statistics core and the run services, one gate per claim.</summary>
public sealed class ReviewCoreFixesTests
{
    private const string Bench = """
        Zp = polar(50,30) Ohm
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in out R=50 Ohm
        R:R2 out 0 R=100 Ohm
        Port:P2 out 0 Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=3 npts=5 Unit=GHz
        measure G = dB(SP1.S(2,1))
        """;

    private static IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Check(string lines)
    {
        var (lib, tb) = new CnlReader().Read(Bench + "\n" + lines);
        return TuningValidator.Validate(tb, TunableCatalog.FromNetlist(tb, lib));
    }

    // ── R-ya15-1 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("confidence=100%", "confidence=100%")]
    [InlineData("confidence=0%",   "confidence=0%")]
    [InlineData("target=0%",       "target=0%")]
    [InlineData("target=101%",     "target=101%")]
    [InlineData("trials=0",        "trials=0")]
    [InlineData("sigmascale=0",    "sigmascale=0")]
    [InlineData("parallel=0",      "parallel=0")]
    public void AnOutOfRangeSetting_IsRefusedByTheValidator(string setting, string named)
    {
        var f = Assert.Single(Check("tune R1.R dist=gauss sd=2%\nstatistics " + setting), d => d.Id == "yield.statistics.range");
        Assert.Equal(CircuitRF.Diagnostics.DiagnosticSeverity.Error, f.Severity);
        Assert.Contains(named, f.Render());
    }

    [Fact]
    public void AHundredPercentConfidence_RefusesTheRun_InTheValidatorsSentence()
    {
        var run = StatisticalRun.Create(PreparedCircuit.FromText(YieldCircuits.Divider("trials=20 confidence=100%"), null, null),
                                        new StatisticalOptions { Mode = StatisticalMode.Yield });
        Assert.Equal("yield.statistics.range", run.Refusal?.Id);
        Assert.Equal(StatisticalOutcome.Refused, run.Run().Outcome);
    }

    [Fact]
    public void TheCliConfidenceFlag_RefusesOneHundredPercent()
    {
        string cnl = YieldCli.Divider(OptCli.Dir(), statistics: "trials=20");
        var (exit, _, stderr) = OptCli.Run("yield", "estimate", cnl, "--confidence", "100%");
        Assert.Equal(1, exit);
        Assert.Contains("below 100", stderr);
    }

    // ── R-ya15-3 ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACornerSavedFromAStoppedLhsRun_ReplaysTheStoppedRunsTrial()
    {
        string cnl = YieldCircuits.Divider("trials=20 sampling=lhs");
        StatisticalRun? run = null;
        run = StatisticalRun.Create(PreparedCircuit.FromText(cnl, null, null), new StatisticalOptions
        {
            Mode = StatisticalMode.Yield, BatchSize = 1, Progress = new Inline(p => { if (p.Trials == 7) run!.Stop(); }),
        });
        var stopped = run.Run();
        Assert.Equal(7, stopped.Trials);

        var corner = TrialReplay.CornerOf(stopped.Data!, "Worst", 3);
        Assert.Equal(20, corner.Trials);                     // the plan, not the 7 it reached

        // No .yield.npy at hand: the trial is drawn afresh from the corner's own seed, sampling and trial count.
        var replay = StatisticalCorner.Replay(PreparedCircuit.FromText(cnl, null, null), null, corner, (DataSet?)null);
        Assert.Null(replay.Refusal);
        foreach (var (key, text) in stopped.Records[2].Values) Assert.Equal(text, replay.Values[key]);
    }

    // ── R-ya15-4 ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReRunningATrial_AfterTheSeedChanged_DrawsTheResultsOwnTrial()
    {
        string dir = OptCli.Dir();
        string cnl = YieldCli.Divider(dir, statistics: "trials=12 seed=3");
        Assert.Equal(0, OptCli.Run("yield", "estimate", cnl).Exit);
        string npy = Path.Combine(dir, "div.yield.npy");
        var (full, _) = DataSetImporter.Import(npy);
        File.WriteAllText(cnl, File.ReadAllText(cnl).Replace("seed=3", "seed=9"));

        var (data, refusal) = TrialReplay.Run(npy, 7, StatisticalMode.Yield);
        Assert.Null(refusal);

        var again = data!["DC1.V"].RealValues;
        var ran = full["DC1.V"].RealValues.Skip(6 * again.Length).Take(again.Length);
        Assert.Equal(ran, again);
    }

    // ── R-ya15-5 ─────────────────────────────────────────────────────────────

    [Fact]
    public void APhaseEntrysTrialCube_IsInRadians_AndSaysSo()
    {
        Assert.Equal("rad", Units.BaseUnit("deg"));
        Assert.Equal(1.0, Units.Scale(Units.BaseUnit("deg")));

        var r = StatisticalRun.Create(PreparedCircuit.FromText(Bench + "\ntune phase(Zp) dist=gauss sd=1 deg\nstatistics trials=4", null, null),
                                      new StatisticalOptions { Mode = StatisticalMode.MonteCarlo }).Run();
        var trials = r.Data!["trials.stat:phase(Zp)"];
        var nominal = r.Data["nominal.trials.stat:phase(Zp)"];
        Assert.Equal("rad", trials.Unit);
        Assert.Equal("rad", nominal.Unit);
        Assert.Equal(Math.PI / 6, nominal.RealValues[0], 12);
        Assert.All(trials.RealValues, v => Assert.InRange(v, Math.PI / 6 - 0.1, Math.PI / 6 + 0.1));   // 30° ± 1° is 0.52 ± 0.02
    }

    // ── R-ya15-6 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheAutoStopLabel_UsesAPeriod_UnderACommaDecimalCulture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var r = YieldCircuits.Create(YieldCircuits.Divider("trials=400 sampling=sobol autostop=1 target=50% confidence=99.5%")).Run();
            Assert.NotNull(r.AutoStop);
            string label = r.Data!["yield.stopped"].Axes[0].Labels![0];
            Assert.Contains("the 99.5 % interval", label);
            Assert.Contains("the 50 % target", label);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    // ── R-ya15-7 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheNonPhysicalWarning_AppliesSigmaScale()
    {
        // σ = 10 %, doubled: zero sits 5σ below the mean, P ≈ 2.9e-7 — above the 1e-9 threshold.
        Assert.DoesNotContain(Check("tune R1.R dist=gauss sd=10%"), d => d.Id == "yield.dist.nonphysical");
        Assert.Contains(Check("tune R1.R dist=gauss sd=10%\nstatistics sigmascale=2"), d => d.Id == "yield.dist.nonphysical");
    }

    private sealed class Inline(Action<StatisticalProgress> report) : IProgress<StatisticalProgress>
    {
        public void Report(StatisticalProgress value) => report(value);
    }
}
