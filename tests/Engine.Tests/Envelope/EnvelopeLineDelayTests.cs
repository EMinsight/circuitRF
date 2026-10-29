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
/// Lines and Z(freq) blocks across the envelope band (brief ENV-FIT). A line is realized as its delay (EnvelopeLineDelay):
/// exact for a lossless line of any length, its loss taken to first order around each harmonic. A Z(freq) block is fitted
/// like a System Filter. Before, both were their value and slope at each harmonic, and a line was held shorted across the
/// baseband envelope. Each closed-form test below is one that treatment failed (src/Engine/RESOLVED.md, ENV-FIT).
/// </summary>
public sealed class EnvelopeLineDelayTests(ITestOutputHelper output)
{
    private const double F0 = 2.14e9;
    private const double C0 = 299792458.0;

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

    /// <summary>The envelope at <paramref name="node"/>, product <paramref name="k"/>, as a phasor at <paramref name="offHz"/>
    /// from it: the mean of v(t)·e^(−j2π·off·t) over whole periods of 20 ns after the first 100 ns.</summary>
    private static Complex Tone(DataSet ds, string node, int k, double offHz)
    {
        var v = ds["V"];
        var t = v.Axes[0].Values;
        int np = v.Axes[1].Length, nk = v.Axes[2].Length, p = Array.IndexOf(v.Axes[1].Labels!, node);
        double h = t[1] - t[0];
        int i0 = (int)Math.Ceiling(100e-9 / h), per = (int)Math.Round(20e-9 / h);
        Assert.True(Math.Abs(per * h - 20e-9) < 1e-15, "the step must divide 20 ns, or the window is not whole periods");
        int m = (t.Length - i0) / per * per;
        Complex acc = 0;
        for (int i = i0; i < i0 + m; i++)
            acc += v.ComplexValues[(i * np + p) * nk + k] * Complex.Exp(new Complex(0, -2 * Math.PI * offHz * t[i]));
        return acc / m;
    }

    /// <summary>A lossless line (Zc, delay τ) between 50 Ω terminations: S21 at <paramref name="hz"/>.</summary>
    private static Complex LineS21(double zc, double tau, double hz)
    {
        double g = (zc - 50) / (zc + 50);
        var H = Complex.Exp(new Complex(0, -2 * Math.PI * hz * tau));
        return H * (1 - g * g) / (1 - g * g * H * H);
    }

    private static string TwoCarriers(string body, string step = "0.4e-9", int harm = 1) => $"""
        MOD_Source:S1 in 0 Fc=2.14 GHz Pavg=0 Z=50 Ohm Record=0.5e-3 Carrier[1]="CW;Off=-50MHz;P=0" Carrier[2]="CW;Off=50MHz;P=0"
        {body}
        analysis ENV1 type=env Stop=1e-6 Step={step} Warmup=0.1e-6 MaxHarm={harm} Envelope=single Probes="in,out" Segments=1
        """;

    /// <summary>Two carriers 100 MHz apart through a 25 Ω, 300 mm line: each comes out with the line's own |S21| at its own
    /// frequency. Its value and slope at f0 gave −0.52 dB at −50 MHz, against the closed form's −0.67 dB.</summary>
    [Fact]
    public void TwoCarriersThroughALongLine_EachHasTheLinesGainAtItsOwnFrequency()
    {
        var line = Run(TwoCarriers("TLIN:TL1 in out Z=25 Ohm L=300 mm Eeff=1\nR:RL out 0 R=50 Ohm"), out var nl);
        var none = Run(TwoCarriers("R:Rz in out R=1e-6 Ohm\nR:RL out 0 R=50 Ohm"), out _);
        output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
        foreach (double off in new[] { -50e6, 50e6 })
        {
            double got = 20 * Math.Log10(Tone(line, "out", 1, off).Magnitude / Tone(none, "out", 1, off).Magnitude);
            double want = 20 * Math.Log10(LineS21(25, 0.3 / C0, F0 + off).Magnitude);
            output.WriteLine($"{off / 1e6:+0;-0} MHz: {got:0.0000} dB, closed form {want:0.0000} dB");
            Assert.True(Math.Abs(got - want) < 0.01, $"{off / 1e6} MHz: {got:0.0000} dB against {want:0.0000} dB");
        }
    }

    /// <summary>The same carriers make a 100 MHz beat through a square law, carried to a load by a 25 Ω, 300 mm line in the
    /// bias path: the beat at the load follows the line's closed form at 100 MHz. Held shorted, the line passed it as a
    /// short does: 0.00 dB against −0.77 dB.</summary>
    [Fact]
    public void ABeatAcrossTheBaseband_FollowsTheLine()
    {
        string Bench(string feed) => TwoCarriers($"""
            R:Rin in 0 R=50 Ohm
            SDD:X1 in 0 b 0 I[1,0]=0 I[2,0]=-0.01*_v1*_v1
            R:Rb b 0 R=50 Ohm
            {feed}
            R:Rc out 0 R=50 Ohm
            """, harm: 2);
        var line = Run(Bench("TLIN:TB b out Z=25 Ohm L=300 mm Eeff=1"), out var nl);
        var none = Run(Bench("R:Rz b out R=1e-6 Ohm"), out _);
        output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
        // a current I into b: V(out) = I·(Rb ∥ Zin)·Rc/(A·Rc + B) with the line's ABCD at 100 MHz; shorted, I·25 Ω
        double th = 2 * Math.PI * 100e6 * 0.3 / C0, zc = 25, r = 50;
        Complex A = Math.Cos(th), B = new Complex(0, zc * Math.Sin(th)), C = new Complex(0, Math.Sin(th) / zc);
        var zin = (A * r + B) / (C * r + A);
        var ratio = r * zin / (r + zin) * r / (A * r + B) / 25.0;
        double want = 20 * Math.Log10(ratio.Magnitude);
        double got = 20 * Math.Log10(Tone(line, "out", 0, 100e6).Magnitude / Tone(none, "out", 0, 100e6).Magnitude);
        output.WriteLine($"100 MHz beat: {got:0.0000} dB, closed form {want:0.0000} dB");
        Assert.True(Math.Abs(got - want) < 0.01, $"{got:0.0000} dB against {want:0.0000} dB");
    }

    /// <summary>A Z(freq) parallel resonator (Q 20 at f0) in series, one carrier 50 MHz off f0, where its impedance turns
    /// fastest: the carrier comes out with 100/(100 + Z(f)). Under its value and slope at f0 the run went to NaN.</summary>
    [Fact]
    public void AZFreqResonator_AtTheBandEdge_HasItsClosedFormGain()
    {
        string Bench(string block) => $"""
            MOD_Source:S1 in 0 Fc=2.14 GHz Pavg=0 Z=50 Ohm Record=0.5e-3 Carrier[1]="CW;Off=50MHz;P=0"
            {block}
            R:RL out 0 R=50 Ohm
            analysis ENV1 type=env Stop=1e-6 Step=0.16e-9 Warmup=0.1e-6 MaxHarm=1 Probes="in,out" Segments=1
            """;
        var tank = Run(Bench("Z_Port:ZT in out Z[1,1]=50*freq*2.14e9/(freq*2.14e9+j*20*(freq*freq-2.14e9*2.14e9))"), out var nl);
        var none = Run(Bench("R:Rz in out R=1e-6 Ohm"), out _);
        output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
        double f = F0 + 50e6;
        var z = 50 * f * F0 / new Complex(f * F0, 20 * (f * f - F0 * F0));
        double want = 20 * Math.Log10((100 / (100 + z)).Magnitude);
        double got = 20 * Math.Log10(Tone(tank, "out", 1, 50e6).Magnitude / Tone(none, "out", 1, 50e6).Magnitude);
        output.WriteLine($"+50 MHz: {got:0.0000} dB, closed form {want:0.0000} dB");
        Assert.True(Math.Abs(got - want) < 0.01, $"{got:0.0000} dB against {want:0.0000} dB");
        Assert.Contains(nl.Notes, n => n.Contains("'ZT'") && n.Contains("rational fit"));
    }

    /// <summary>A Z(freq) block whose fit is not passive (the resonator above with its sign turned, |S21| = 2 at f0) is not
    /// stepped as that fit: it keeps its value and slope at each harmonic, and the run warns naming it and the reason.</summary>
    [Fact]
    public void AZFreqBlockWhoseFitIsNotPassive_IsRefused_AndNamed()
    {
        var (lib, tb) = new CnlReader().Read("""
            R:R1 in 0 R=50 Ohm
            Z_Port:ZA in out Z[1,1]=-50*freq*2.14e9/(freq*2.14e9+j*20*(freq*freq-2.14e9*2.14e9))
            R:RL out 0 R=50 Ohm
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var before = nl.Components.Single(c => c.InstancePath == "ZA").Model;
        using (EnvelopeSnpFit.Apply(nl, F0 + 61.44e6, [F0], 61.44e6))
        {
            output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
            Assert.Same(before, nl.Components.Single(c => c.InstancePath == "ZA").Model);
            Assert.Contains(nl.Warnings, w => w.Contains("'ZA'") && w.Contains("not passive")
                                              && w.Contains("approximated to first order around each harmonic (its value and slope there)"));
        }
    }

    /// <summary>The reference bench (brief ENV-TLIN's PA) with a 300 mm line at its output: it runs, says what it did with the
    /// line, and names it in no warning.</summary>
    [Fact]
    public void TheReferenceBench_StepsItsLineAsADelay()
    {
        var ds = EnvelopeLineInSeriesTests.Run(EnvelopeLineInSeriesTests.Pa("TLIN:TL3 n10 Vout Z=50 Ohm L=300 mm", 14, "1e-6"), out var nl);
        output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
        Assert.True(double.IsFinite(ds["Pavg_dBm"].RealValues[0]));
        Assert.Contains(nl.Notes, n => n.Contains("'TL3'") && n.Contains("stepped as its delay"));
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("'TL3'"));
    }

    /// <summary>A coupled line is not a delay and is not fitted (a long one cannot be): it keeps its value and slope at each
    /// harmonic, and the run says so in those words, naming it.</summary>
    [Fact]
    public void ACoupledLine_KeepsTheFirstOrderTreatment_AndIsNamed()
    {
        Run("""
            MOD_Source:S1 a 0 Fc=2.14 GHz Pavg=0 Z=50 Ohm Record=0.5e-3 Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1"
            MCLIN:TC a b c d W=1 mm S=0.3 mm L=300 mm H=0.8 mm T=35 um Er=4.4 TanD=0.02 Sigma=5.8e7
            R:RL b 0 R=50 Ohm
            R:Rc c 0 R=50 Ohm
            R:Rd d 0 R=50 Ohm
            analysis ENV1 type=env Stop=0.2e-6 MaxHarm=5 Probes="a,b" Segments=1 Warmup=0
            """, out var nl);
        output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
        Assert.Contains(nl.Warnings, w => w.Contains("'TC'") && w.Contains("approximated to first order around each harmonic (its value and slope there)"));
    }

    /// <summary>The multi-fundamental engine steps a line the same way: two carriers 200 MHz apart, each its own fundamental,
    /// through the 25 Ω, 300 mm line.</summary>
    [Fact]
    public void TheLatticeEngine_StepsALineAsADelay()
    {
        var ds = Run("""
            MOD_Source:S1 in 0 Fc=2.14 GHz Pavg=0 Z=50 Ohm Record=0.5e-3 Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=-100MHz;P=0;Seed=1" Carrier[2]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=100MHz;P=0;Seed=2"
            TLIN:TL1 in out Z=25 Ohm L=300 mm Eeff=1
            IProbe:Iout out o
            R:RL o 0 R=50 Ohm
            analysis ENV1 type=env Stop=0.5e-6 MaxMixOrder=3 Probes="in,out" Segments=1 Warmup=0
            """, out var nl);
        output.WriteLine(string.Join(" | ", nl.Notes.Concat(nl.Warnings)));
        Assert.Equal(2, ds["__EnvFundamentals"].RealValues[0]);
        Assert.True(double.IsFinite(ds["Pavg_dBm"].RealValues[0]));
        Assert.Contains(nl.Notes, n => n.Contains("'TL1'") && n.Contains("stepped as its delay"));
    }
}
