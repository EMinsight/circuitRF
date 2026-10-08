using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>TO-1 R-to1-7: one case per rule <c>check</c> applies to a tuning setup (TuningValidator).</summary>
public sealed class TuningCheckTests
{
    private const string Bench = """
        Rx = 25 Ohm
        Zs = 40+15j Ohm
        Wl = 300 um
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in out R=50 Ohm
        R:R2 out 0 R=Rx
        Port:P2 out 0 Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=3 npts=21 Unit=GHz
        """;

    [Theory]
    [InlineData("tune R1.R min=200 Ohm max=10 Ohm",                                          true,  "is not below max")]
    [InlineData("tune R1.R min=0 Ohm max=10 Ohm scale=log",                                  true,  "scale=log needs min above zero")]
    [InlineData("goal G = dB(SP9.S(2,1)) analysis=SP9 ge 0",                                 true,  "is not an analysis this design declares")]
    [InlineData("goal G = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=5 GHz hi=6 GHz ge 0",     true,  "holds no point of SP1's grid")]
    [InlineData("goal G = dB(SP1.S(2,1)) +* 3 analysis=SP1 ge 0",                             true,  "does not parse")]
    [InlineData("preset \"p\" R9.R=1 Ohm",                                                    false, "names nothing in this design")]
    [InlineData("tune R2.R min=1 Ohm max=2 Ohm",                                              true,  "cannot be tuned")]
    [InlineData("tune Zs min=1 Ohm max=2 Ohm",                                                true,  "its value is complex")]
    [InlineData("tune phase(Zs) min=0 deg max=400 deg",                                       true,  "more than one turn")]
    [InlineData("tune real(Zs) min=90 Ohm max=120 Ohm\ntune mag(Zs) min=20 Ohm max=80 Ohm",   true,  "no complex value lies inside")]
    [InlineData("optimize algorithm=de alg.populaton=40",                                     false, "alg.populaton is not an option of Differential evolution")]
    [InlineData("optimize algorithm=pso alg.topology=star",                                   true,  "alg.topology=star is not ring or global")]
    [InlineData("optimize algorithm=lm cost=minimax",                                         true,  "cannot use cost=minimax")]
    [InlineData("tune real(Zs) min=30 Ohm max=50 Ohm discrete=integer",                      true,  "is not offered on a part")]
    [InlineData("tune Wl min=100 um max=500 um discrete=preferred",                          true,  "has no preferred-value ladder")]
    public void EachRuleIsReported(string line, bool isError, string fragment)
    {
        var (lib, tb) = new CnlReader().Read(Bench + "\n" + line);
        using var nl = new Elaborator(lib).Elaborate(tb);

        var findings = TuningValidator.Validate(tb, TunableCatalog.FromNetlist(tb, lib), nl);

        var f = Assert.Single(findings);
        Assert.Equal(isError, f.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);
        Assert.Contains(fragment, f.Render());
    }
}
