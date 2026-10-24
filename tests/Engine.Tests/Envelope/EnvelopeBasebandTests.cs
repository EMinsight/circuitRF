using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// A line in the baseband envelope. The k = 0 product took its C from stamps at 1 Hz and 2 Hz, where a line's Y stamp
/// goes as 1/(jωL): a 25-ohm line of 90-450° gave a slope of ~1e15 and the LU found no pivot. Such an element was then held
/// at its DC stamp for the baseband product (EnvelopeBaseband); since brief ENV-FIT a line is stepped as its delay
/// (EnvelopeLineDelay), its DC short at ω = 0 and exact across the baseband when lossless.
/// </summary>
public sealed class EnvelopeBasebandTests(ITestOutputHelper output)
{
    private static DataSet Run(string cnl, out ElaboratedNetlist nl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var first = new Elaborator(lib).Elaborate(tb);
        nl = first;
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), first.ResolvedGlobals);
        bool used = false;
        object gate = new();
        return EnvelopeRunner.Run(plan, () => { lock (gate) { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return first; } });
    }

    private static string Line(double eDeg, string extra = "") => $"""
        MOD_Source:S1 in 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 Carrier[1]="CW;Off=0;P=0"
        TLIN:TL1 in out Z=25 Ohm E={eDeg} deg F=1 GHz
        R:RL out 0 R=50 Ohm
        {extra}
        analysis ENV1 type=env Stop=2e-6 Accuracy=draft MaxHarm=1 Probes="in,out" Segments=1
        """;

    /// <summary>The lengths that failed run, and a CW tone through the line has the line's closed-form gain: a 25-ohm line
    /// in 50 ohm at θ = 90°·(odd) passes (1 − Γ²)²/(1 + Γ²)² with Γ = −1/3, −1.94 dB.</summary>
    [Theory]
    [InlineData(90.0)]
    [InlineData(270.0)]
    [InlineData(450.0)]
    [InlineData(810.0)]
    public void QuarterWaveMultiples_RunAndPassTheLinesGain(double eDeg)
    {
        var ds = Run(Line(eDeg), out var nl);
        double g2 = 1.0 / 9, want = 20 * Math.Log10((1 - g2) / (1 + g2));
        double gt = ds["Gt_dB"].RealValues[0];
        output.WriteLine($"E {eDeg}: Gt {gt:0.00000} dB (closed form {want:0.00000}); " + string.Join(" | ", nl.Warnings));
        Assert.True(Math.Abs(gt - want) < 1e-3, $"Gt {gt:0.0000} dB against {want:0.0000} dB");
    }

    /// <summary>Held at its DC stamp, a line in the bias path is the short it is at DC: 1 V through it into 10 ohm draws 0.1 W
    /// at every step of the run, not the open or the ringing a degenerate baseband slope would make of it.</summary>
    [Fact]
    public void ABiasFeedThroughALine_DrawsItsDcCurrent()
    {
        var ds = Run(Line(90, """
            Vdc:VB vb 0 Vdc=1
            TLIN:TB vb d Z=25 Ohm E=90 deg F=1 GHz
            R:RD d 0 R=10 Ohm
            """), out _);
        double pdc = ds["Pdc_W"].RealValues[0];
        output.WriteLine($"Pdc {pdc:R} W");
        Assert.True(Math.Abs(pdc - 0.1) < 1e-6, $"Pdc {pdc} W, not 0.1 W");
    }

    /// <summary>A line 0.4 wavelengths long at the edge of a 50 MHz baseband was named for being held at its DC value there
    /// (147 %); stepped as its delay it is not held, and nothing names it.</summary>
    [Fact]
    public void ALongLine_IsNotHeldAcrossTheBaseband()
    {
        Run(Line(2970).Replace("CW;Off=0;P=0", "NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1"), out var longNl);
        output.WriteLine("long:  " + string.Join(" | ", longNl.Notes.Concat(longNl.Warnings)));
        Assert.DoesNotContain(longNl.Warnings, w => w.Contains("'TL1'"));
        Assert.Contains(longNl.Notes, n => n.Contains("'TL1'") && n.Contains("stepped as its delay"));
    }
}
