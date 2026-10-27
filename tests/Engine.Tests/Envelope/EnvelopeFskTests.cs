using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// An FSK source in an envelope run. Its record is one PN9 period, 511 bits at 100 kb/s = 5.11 ms, which no
/// 1.92 MHz x 2^k step divides: the run steps at the carrier's native rate x 2^k instead, as it does for an I/Q file.
/// A constant envelope into a matched load: 0 dB transducer gain.
/// </summary>
public sealed class EnvelopeFskTests(ITestOutputHelper output)
{
    [Fact]
    public void APn9Record_RunsIntoAMatchedLoadAtZeroDbGain()
    {
        var (lib, tb) = new CnlReader().Read("""
            MOD_Source:S1 in 0 Fc=868.95e6 Pavg=10 Z=50 Carrier[1]="FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0"
            R:RL in 0 R=50 Ohm
            analysis ENV1 type=env Stop=50e-6 Accuracy=draft MaxHarm=1 Probes="in" Segments=1
            """);
        ElaboratedNetlist Elaborate() => new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), Elaborate().ResolvedGlobals);
        var ds = EnvelopeRunner.Run(plan, Elaborate);

        double gt = ds["Gt_dB"].RealValues[0];
        output.WriteLine($"Gt {gt:0.00000} dB");
        Assert.True(Math.Abs(gt) < 1e-3, $"Gt {gt:0.0000} dB");
    }
}

/// <summary>
/// A 100 b/s DBPSK uplink in an envelope run. It needs ~3 kHz, so the run steps at the carrier's own 3.2 kHz rather than
/// at the 1.92 MHz the LTE / NR ladder starts from — 600 times fewer steps, which is what makes 50 ms (five bits) a
/// short run. Into a matched load the average transducer gain is 0 dB, though the envelope dips at each reversal.
/// </summary>
public sealed class EnvelopeDbpskTests(ITestOutputHelper output)
{
    [Fact]
    public void AUnbUplink_StepsAtItsOwnRate_AtZeroDbGain()
    {
        var (lib, tb) = new CnlReader().Read("""
            MOD_Source:S1 in 0 Fc=868.13e6 Pavg=14 Z=50 Carrier[1]="DBPSK;Rb=100bps;Data=PN9;Off=0;P=0"
            R:RL in 0 R=50 Ohm
            analysis ENV1 type=env Stop=50e-3 Accuracy=draft MaxHarm=1 Probes="in" Segments=1
            """);
        ElaboratedNetlist Elaborate() => new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), Elaborate().ResolvedGlobals);
        var ds = EnvelopeRunner.Run(plan, Elaborate);

        double rate = ds["__EnvSignalRate_Hz"].RealValues[0], gt = ds["Gt_dB"].RealValues[0];
        output.WriteLine($"signal rate {rate} Hz, Gt {gt:0.00000} dB");
        Assert.Equal(3200, rate);
        Assert.True(Math.Abs(gt) < 1e-3, $"Gt {gt:0.0000} dB");
    }
}
