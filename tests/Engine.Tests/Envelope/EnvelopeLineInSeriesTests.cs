using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// A line in series behind a DC block at a power amplifier's output (brief ENV-TLIN). The baseband product held the line
/// at its ω = 0 stamp, which is the resonance clamp of its 1/(jωL) pole: a purely imaginary ~2e7 S. Behind the real
/// projection the k = 0 envelope makes at the interface that is not passive, and the baseband state grew ×4.8 a step
/// until a step failed (~50 ns) at any length, loss, form or drive. A line is now held shorted at DC (EnvelopeBaseband).
/// </summary>
public sealed class EnvelopeLineInSeriesTests(ITestOutputHelper output)
{
    private const string Fet = """
        define TestPA_FET (gate drain)
          parameters Periphery_mm=1
          Sv = -0.837
          Sc = 0.71
          TV0 = 4.268
          TC = 1.507
          th = 0.001
          a = 0.176
          g = 0.089
          lam = 0.0012
          B = 1130
          C0 = 1.5e-12
          kc = 0.5
          sc = 1.0
          Vp = -2.0
          Cgd = 1.5e-13
          Cds = 8e-13
          SDD:X1  gate  0  drain  0  I[1,0]=_v1/50  I[2,0]=Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2  I[1,1]=C0*(_v1 + (kc/sc)*ln(cosh(sc*(_v1 - Vp)))) + Cgd*(_v1 - _v2)  I[2,1]=Cds*_v2 + Cgd*(_v2 - _v1)
        end TestPA_FET
        """;

    /// <summary>The brief's reproduction with <paramref name="line"/> between the output DC block (n10) and the load (Vout).</summary>
    private static string Pa(string line, double pinDbm, string stop) => Fet + $"""

        Pin = {pinDbm}
        VDD = 48
        MOD_Source:SRC1 n1 0 Fc=2140 MHz Pavg=Pin dBm Z=50 Ohm Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1" Record=0.0005 Shaping=-80
        C:Cin Vin n1 C=100 pF
        L:L1 n2 Vin L=1 uH
        Vdc:V1 n2 0 Vdc=-3.05 V
        TestPA_FET:X1 Vin Vd Periphery_mm=1
        L:Ld n4 Vd L=20 nH
        C:Cbp n4 n8 C=10 uF
        R:Resr n8 0 R=0.05 Ohm
        L:Lfeed n9 n4 L=100 nH
        R:Rfeed VDD n9 R=0.5 Ohm
        Vdc:V2 VDD 0 Vdc=VDD V
        C:Cout Vd n10 C=100 pF
        {line}
        R:RL Vout 0 R=50 Ohm
        R:Rb1 b1 0 R=50 Ohm
        R:Rb2 b2 0 R=50 Ohm
        analysis ENV1 type=env Stop={stop} Accuracy=standard MaxHarm=5 MaxMixOrder=7 Probes="Vin,Vd,Vout" Segments=1
        """;

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

    private static double[] Aclr(DataSet ds, string node)
    {
        var a = ds["ACLR"];
        int p = Array.IndexOf(a.Axes[0].Labels!, node), nc = a.Axes[1].Length;
        return [.. Enumerable.Range(0, 5).Select(m => a.RealValues[p * nc * 5 + m])];
    }

    /// <summary>Every form failed near step 23 before; one row per line family the cause reached (each is lossless at DC
    /// through the same clamp). The stripline is 5 mm: a lossy line near a resonance of its admittance at a harmonic is a
    /// different defect (src/Engine/RESOLVED.md, ENV-TLIN), which 20 mm of this one reaches at 5·f0.</summary>
    [Theory]
    [InlineData("TLIN:TL3 n10 Vout Z=50 Ohm L=3 mm")]
    [InlineData("TLIN:TL3 n10 Vout Z=50 Ohm L=300 mm")]
    [InlineData("MLIN:TL3 n10 Vout W=1.5 mm L=20 mm H=0.8 mm T=35 um Er=4.4 TanD=0.02 Sigma=5.8e7")]
    [InlineData("SLIN:TL3 n10 Vout W=0.5 mm L=5 mm")]
    [InlineData("CLIN:TL3 n10 Vout b1 b2 Ze=60 Ohm Zo=40 Ohm E=90 deg F=2.14 GHz")]
    public void TheReproduction_RunsToTheEnd(string line)
    {
        var ds = Run(Pa(line, -10, "1e-6"), out _);
        double pout = ds["Pout_avg_dBm"].RealValues[0];
        output.WriteLine($"{line}: Pout_avg {pout:0.0000} dBm");
        Assert.True(double.IsFinite(pout));
    }

    /// <summary>A 3 mm line is transparent: the same channel power and ACPR1 as a 1 mΩ resistor in its place (2 µs).</summary>
    [Fact]
    public void AShortLine_MatchesTheLineFreeCircuit()
    {
        var line = Run(Pa("TLIN:TL3 n10 Vout Z=50 Ohm L=3 mm", 14, "2e-6"), out _);
        var none = Run(Pa("R:Rz n10 Vout R=0.001 Ohm", 14, "2e-6"), out _);
        double dP = line["Pout_avg_dBm"].RealValues[0] - none["Pout_avg_dBm"].RealValues[0];
        double[] a = Aclr(line, "Vout"), b = Aclr(none, "Vout");
        output.WriteLine($"ΔPout {dP:0.0000} dB, ΔACPR1L {a[1] - b[1]:0.0000} dB, ΔACPR1U {a[2] - b[2]:0.0000} dB");
        Assert.True(Math.Abs(dP) < 0.05, $"Pout_avg {dP:0.0000} dB from the line-free circuit");
        Assert.True(Math.Abs(a[1] - b[1]) < 0.05 && Math.Abs(a[2] - b[2]) < 0.05, $"ACPR1 {a[1] - b[1]:0.0000} / {a[2] - b[2]:0.0000} dB");
    }

    /// <summary>The straight-line warning still names a line that bends across the band, and not a short one. 1 m is the
    /// first of 0.3/1/2/3 m above the threshold (12.8 % / 55.4 % / 75.3 % / 94.8 %); 300 mm is below it and is not named.</summary>
    [Fact]
    public void ALongLine_IsNamed_AShortOneIsNot()
    {
        Run(Pa("TLIN:TL3 n10 Vout Z=50 Ohm L=1000 mm", 14, "1e-6"), out var longNl);
        Run(Pa("TLIN:TL3 n10 Vout Z=50 Ohm L=3 mm", 14, "1e-6"), out var shortNl);
        output.WriteLine("long:  " + string.Join(" | ", longNl.Warnings));
        output.WriteLine("short: " + string.Join(" | ", shortNl.Warnings));
        Assert.Contains(longNl.Warnings, w => w.Contains("'TL3'") && w.Contains("straight line in frequency"));
        Assert.DoesNotContain(shortNl.Warnings, w => w.Contains("'TL3'"));
    }
}
