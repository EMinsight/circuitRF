using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using Xunit;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// The points of an envelope sweep run side by side: each is elaborated with its own value in the testbench, and only
/// its simulation overlaps the others'. A point must come out bit for bit as it does run alone.
/// </summary>
public sealed class SweptEnvelopeConcurrentPointsTests
{
    private static string Bench(string pin) => $"""
        Pin = {pin}
        MOD_Source:S1 n1 0 Fc=1e9 Pavg=Pin Z=50 Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1" Record=0.001
        SDD:X1 n1 0 I[1,0]=_v1/50 + 2e-3*_v1^3
        analysis ENV1 type=env Stop=2e-6 Accuracy=draft Probes="n1" Segments=1 Warmup=0
        analysis SW1 type=parametric_sweep Var=Pin Values=0,10,20 Inner=ENV1
        """;

    [Fact]
    public void PointsRunSideBySide_AndEachMatchesItsOwnRunAlone()
    {
        var (lib, tb) = new CnlReader().Read(Bench("0"));
        var swept = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb);

        Assert.Equal(Math.Min(3, Environment.ProcessorCount), ParametricSweepEngine.LastEnvelopeConcurrency);
        var sweptV = swept["V"].ComplexValues;
        int per = sweptV.Length / 3;
        string[] pins = ["0", "10", "20"];
        for (int i = 0; i < 3; i++)
        {
            var (l1, t1) = new CnlReader().Read(Bench(pins[i]));
            using var nl = new Elaborator(l1).Elaborate(t1);
            var alone = EnvelopeRunner.Run(EnvelopeRunner.Resolve(t1.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals),
                                           () => nl)["V"].ComplexValues;
            Assert.Equal(alone, sweptV.Skip(i * per).Take(per));
        }
        Assert.Equal("0", tb.GlobalVariables.Single(v => v.Name == "Pin").Expression);
    }
}
