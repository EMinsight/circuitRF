using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>YA-1 R-ya1-4: one case per error and warning <c>check</c> reports on a setup's statistical part,
/// through <see cref="TuningValidator"/> — the function a run refuses on.</summary>
public sealed class StatisticsCheckTests
{
    private const string Bench = """
        Vneg = -2
        Zs = 40+15j Ohm
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in out R=50 Ohm
        R:R2 out 0 R=100 Ohm
        R:R3 out 0 R=1 kOhm m=2
        C:C1 out 0 C=2 pF
        Port:P2 out 0 Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=3 npts=21 Unit=GHz
        """;

    private const string R1 = "tune R1.R dist=gauss sd=2%\n";
    private const string R12 = R1 + "tune R2.R dist=gauss sd=2%\n";

    [Theory]
    // Errors
    [InlineData("tune R9.R dist=gauss sd=2%",                                    "yield.dist.not-tunable", true)]
    [InlineData("tune R1.R dist=gauss tol=5%",                                   "yield.spread.missing", true)]
    [InlineData("tune R1.R dist=gauss sd=2% lo=1 Ohm",                           "yield.spread.extra", true)]
    [InlineData("tune R1.R dist=gauss sd=0%",                                    "yield.spread.not-positive", true)]
    [InlineData("tune R1.R dist=gauss sd=3 pF",                                  "yield.spread.wrong-unit", true)]
    [InlineData("tune R1.R dist=unif lo=60 Ohm hi=40 Ohm",                       "yield.spread.inverted", true)]
    [InlineData("tune R1.R dist=discrete lo=40 Ohm hi=60 Ohm by=0 Ohm",          "yield.spread.not-positive", true)]
    [InlineData("tune R1.R dist=discrete lo=40 Ohm hi=60 Ohm by=7 Ohm",          "yield.spread.step", true)]
    [InlineData("tune Vneg dist=lognorm sd=10%",                                 "yield.dist.lognorm-nonpositive", true)]
    [InlineData("tune R3.m dist=gauss sd=1",                                     "yield.dist.integer", true)]
    [InlineData("tune real(Zs) dist=gauss sd=1 Ohm\ntune mag(Zs) dist=gauss sd=1 Ohm", "yield.complex.mixed", true)]
    [InlineData("tune real(Zs) dist=gauss sd=1 Ohm\ntune imag(Zs) dist=gauss sd=1 Ohm\ntune mag(Zs) dist=gauss sd=1 Ohm",
                                                                                 "yield.complex.too-many", true)]
    [InlineData(R1 + "correlate R1.R R2.R rho=0.5",                              "yield.correlate.not-stat", true)]
    [InlineData(R1 + "correlate R1.R R1.R rho=0.5",                              "yield.correlate.same-key", true)]
    [InlineData(R12 + "correlate R1.R R2.R rho=1",                               "yield.correlate.rho", true)]
    [InlineData(R1 + "statistics sampling=lhs autostop=1 target=95%",            "yield.statistics.lhs-autostop", true)]
    [InlineData(R1 + "statistics autostop=1",                                    "yield.statistics.autostop-target", true)]
    [InlineData("corner Hot R9.R=5 Ohm",                                         "yield.corner.unknown-key", true)]
    [InlineData("corner Hot temp=warm",                                          "yield.corner.temp", true)]
    [InlineData("corner Worst trial=4 seed=7",                                   "yield.corner.trial-incomplete", true)]
    // Warnings
    [InlineData("tune R1.R dist=gauss sd=20%",                                   "yield.dist.nonphysical", false)]
    [InlineData("tune R1.R dist=gauss sd=2% stat=0",                             "yield.dist.off", false)]
    [InlineData("goal G = dB(SP1.S(2,1)) analysis=SP1 ge -10 use=yield",         "yield.goal.nothing-varies", false)]
    [InlineData(R12 + "tune C1.C dist=gauss sd=2%\ncorrelate R1.R R2.R rho=0.9\ncorrelate R2.R C1.C rho=0.9\ncorrelate R1.R C1.C rho=-0.9",
                                                                                 "yield.correlate.repair", false)]
    public void EachRuleIsReported(string lines, string code, bool isError)
    {
        var (lib, tb) = new CnlReader().Read(Bench + "\n" + lines);

        var findings = TuningValidator.Validate(tb, TunableCatalog.FromNetlist(tb, lib));

        var f = Assert.Single(findings, d => d.Id == code);
        Assert.Equal(isError, f.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ACompleteSetup_IsClean()
    {
        var (lib, tb) = new CnlReader().Read(Bench + "\n" + """
            tune R1.R dist=gauss sd=20% trunc=3
            tune R2.R dist=gauss sd=2%
            tune C1.C dist=unif tol=0.1 pF
            correlate R1.R R2.R rho=0.9
            statistics trials=500 sampling=sobol autostop=1 target=95%
            goal G = dB(SP1.S(2,1)) analysis=SP1 ge -10 use=yield
            corner Hot temp=85 Vneg=-3 R1.R=47 Ohm
            corner Worst trial=4 seed=7 sampling=sobol trials=500
            """);

        Assert.Empty(TuningValidator.Validate(tb, TunableCatalog.FromNetlist(tb, lib)));
    }
}
