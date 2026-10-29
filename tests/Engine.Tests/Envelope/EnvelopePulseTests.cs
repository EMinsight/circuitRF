using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief ENV-PCW gates 3-6: an envelope run on a pulsed-CW source, on the Pulsed Radar example's radar-class ComposedFET at
/// its own thermal resistance (no gate overrides Rth to make a device heat). One 2 mm cell at 48 V into 50 Ω, an ideal
/// supply, so what droops is the device; draft accuracy and 50 µs pulses, the shortest records the claims allow.
/// </summary>
public sealed class EnvelopePulseTests(ITestOutputHelper output)
{
    private const double Ambient = 25;
    private const string Pulse = "PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us";

    private static string Bench(string switches, string analysis, double pin = 27) => $"""
        temp = {Ambient}
        Pin = {pin}
        MOD_Source:SRC1 n1 0 Fc=3e9 Pavg=Pin dBm Z=50 Ohm Carrier[1]="{Pulse}"
        C:C1 n1 g C=100 pF
        L:L1 g ng L=100 nH
        Vdc:V1 ng 0 Vdc=-2.6 V
        IProbe:IG g gx
        ComposedFET:X1 gx dx 0 File="{CmodelPath()}" N=8 {switches}
        IProbe:ID dx d
        L:L2 VDD d L=100 nH
        Vdc:V2 VDD 0 Vdc=48 V
        C:C2 d n5 C=100 pF
        IProbe:IL n5 nl
        R:RL nl 0 R=50 Ohm
        {analysis}
        """;

    private const string Env = "analysis ENV1 type=env Stop={0} Accuracy=draft MaxHarm=3 Segments=1 Probes=\"X1.t,n5\"";
    private const string Hb = "analysis HB1 type=hb Tone=3e9 MaxHarm=3";

    private static (DataSet ds, List<string> notes) RunEnv(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var first = new Elaborator(lib).Elaborate(tb);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return first; }
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), first.ResolvedGlobals);
        var notes = new List<string>();
        return (EnvelopeRunner.Run(plan, Elab, note: notes.Add), notes);
    }

    private static double ThermalAt(DataSet ds, int step) => ((Complex)ds["V"][step, Array.IndexOf(ds["V"].Axes[1].Labels!, "X1.t"), 0]).Real;

    /// <summary>A per-IProbe cube's row at the load's probe, <c>IL</c> (its first axis is the branch).</summary>
    private static double[] AtLoad(DataSet ds, string cube)
    {
        var c = ds[cube];
        int b = Array.IndexOf(c.Axes[0].Labels!, "IL"), n = c.RealValues.Length / c.Axes[0].Length;
        return c.RealValues[(b * n)..((b + 1) * n)];
    }

    [Fact]
    public void ThePulse_StartsAtTheQuiescentPoint()
    {
        // Gate 3: Delay > 0, so the first sample is the off-time and the run starts from the DC operating point.
        var (ds, _) = RunEnv(Bench("", string.Format(Env, "2e-6")));
        var (lib, tb) = new CnlReader().Read(Bench("", "analysis DC1 type=dc"));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);
        var (rows, names) = nl.Nodes.ResultRows(excludeInternal: false);
        double tq = dc.NodeVoltages[rows[Array.IndexOf(names, "X1.t")] - 1];
        output.WriteLine($"quiescent rise {tq:G6} K, envelope at t = 0 {ThermalAt(ds, 0):G6} K");
        Assert.True(tq > 1, "the class-AB quiescent point dissipates");
        Assert.Equal(tq, ThermalAt(ds, 0), 1e-6 * tq);
    }

    [Fact]
    public void NoHeatAndNoTraps_NoDroop()
    {
        // Gate 4.
        var (ds, notes) = RunEnv(Bench("SelfHeating=false DrainLag=false GateLag=false", string.Format(Env, "100e-6")));
        double droop = AtLoad(ds, "PulseDroop_dB").Single();
        output.WriteLine($"droop {droop:G4} dB; {string.Join(" | ", notes)}");
        Assert.True(Math.Abs(droop) <= 0.01, $"droop {droop} dB");
        Assert.Contains(notes, n => n.Contains("1 pulse measured, droop window 10-90 % of the top", StringComparison.Ordinal));
        Assert.False(ds.Contains("ACLR") || ds.Contains("CCDF") || ds.Contains("AMAM") || ds.Contains("NMSE_dB"));
    }

    /// <summary>
    /// Gate 5, the droop checked independently of the envelope engine, from harmonic balance alone: (a) the dissipation at
    /// pulse-top drive over the quiescent dissipation, each the power the device's two terminals take in at every harmonic,
    /// the pulse's at the window's mean temperature (refined once from the quiescent's); (b) the junction at the
    /// quiescent rise Pq·ΣRi plus the ladder's ΔT(t) = P·Σ Ri(1 − e^(−t/τi)) at the window's two ends, t from the rising
    /// edge's 50 % point. Leaving out the quiescent dissipation, or taking the pulse's at the ambient, overstated the droop
    /// by 15-17 %; (c) the output power
    /// at those two temperatures, from two HB runs with SelfHeating=false and Temp set to each. Traps off, so what droops
    /// is the heat alone. It holds even if the envelope's own thermal state were wrong.
    /// </summary>
    [Fact]
    public void TheDroop_IsTheLaddersHeat_ByHarmonicBalanceAlone()
    {
        const string still = "SelfHeating=false DrainLag=false GateLag=false";
        var (env, _) = RunEnv(Bench("DrainLag=false GateLag=false", string.Format(Env, "100e-6")));
        double droop = AtLoad(env, "PulseDroop_dB").Single();

        // IG flows into the gate, ID out of the drain (a branch current runs from its first node to its second)
        double Dissipation(double tc, double pin = 27) { var hb = Hb1(Bench($"{still} Temp={tc:R}", Hb, pin)); return Terminal(hb, "gx", "IG") - Terminal(hb, "dx", "ID"); }

        var shape = RfCore.Signals.PulsedCw.Resolve(RfCore.Signals.SignalSpec.ParseCarrier(Pulse));
        double top = shape.TopEnd - shape.TopStart, half = shape.TopStart - shape.RiseSpan / 2;
        double ts = shape.TopStart + shape.MeasureStart * top - half, te = shape.TopStart + shape.MeasureEnd * top - half;
        static double Rise(double p, double t) => p * Ladder.Sum(x => x.R * (1 - Math.Exp(-t / x.Tau)));
        // Before the pulse the junction sits at its quiescent rise, the bias's dissipation through the whole ladder; the
        // pulse adds its dissipation OVER the quiescent one. The device dissipates less as it heats, so that is taken at the
        // window's mean temperature: once from the ambient's, then at the temperatures that gives.
        double pq = Dissipation(Ambient, pin: -60), tq = Ambient + pq * Ladder.Sum(x => x.R);
        double p = Dissipation(tq) - pq;
        p = Dissipation(tq + 0.5 * (Rise(p, ts) + Rise(p, te))) - pq;
        double tStart = tq + Rise(p, ts), tEnd = tq + Rise(p, te);

        double Pout(double tc) => Dbm(Hb1(Bench($"{still} Temp={tc:R}", Hb)), "n5");
        double predicted = Pout(tStart) - Pout(tEnd);
        output.WriteLine($"P_diss {p:F3} W; T {tStart:F2} -> {tEnd:F2} °C; droop envelope {droop:F4} dB, HB {predicted:F4} dB");
        Assert.True(Math.Abs(droop - predicted) <= Math.Max(0.1 * Math.Abs(predicted), 0.02),
                    $"envelope {droop:F4} dB against harmonic balance {predicted:F4} dB");
    }

    [Fact]
    public void DrainLag_MakesThePulsesDiffer_AndCoolsTheOffTime()
    {
        // Gate 6: the traps capture at the pulse peak in nanoseconds and emit over a millisecond, so the quiescent current
        // after pulse 1 is lower than before it.
        var (ds, _) = RunEnv(Bench("", string.Format(Env, "200e-6")));
        double p2p = AtLoad(ds, "PulseToPulse_dB")[0];
        var time = ds["V"].Axes[0].Values;
        int beforeSecond = Array.FindLastIndex(time, t => t <= 109e-6);
        double t0 = ThermalAt(ds, 0), off = ThermalAt(ds, beforeSecond);
        output.WriteLine($"pulse to pulse {p2p:G4} dB; T rise at t = 0 {t0:F3} K, before pulse 2 {off:F3} K");
        Assert.True(p2p > 0);
        Assert.True(off < t0, $"off-time {off} K against the quiescent {t0} K");
    }

    // ── harmonic balance helpers ──────────────────────────────────────────────

    /// <summary>The ladder at N = 8, the model's base periphery (the truth's 9 K/W in three poles).</summary>
    private static readonly (double R, double Tau)[] Ladder = [(0.3 * 9, 1e-6), (0.4 * 9, 20e-6), (0.3 * 9, 0.5e-3)];

    private static DataSet Hb1(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var a = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var run = new HbEngine(nl, tb).Run(HbEngine.Resolve(a, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
        Assert.True(run.Converged);
        return run.DataSet;
    }

    private static Complex V(DataSet ds, string node, int k)
    {
        var v = ds["V"];
        int i = Array.IndexOf(v.Axes[0].Labels!, node);
        return (Complex)v[i, k];
    }

    /// <summary>The power a terminal takes in: V·I at DC plus ½ Re(V·I*) at each harmonic, the current through its probe.</summary>
    private static double Terminal(DataSet ds, string node, string probe)
    {
        var i = ds["I"];
        int b = Array.IndexOf(i.Axes[0].Labels!, probe);
        double p = 0;
        for (int k = 0; k < i.Axes[1].Values.Length; k++)
        {
            Complex vk = V(ds, node, k), ik = (Complex)i[b, k];
            p += k == 0 ? vk.Real * ik.Real : 0.5 * (vk * Complex.Conjugate(ik)).Real;
        }
        return p;
    }

    private static double Dbm(DataSet ds, string node) => 10 * Math.Log10(Math.Pow(V(ds, node, 1).Magnitude, 2) / (2 * 50) * 1e3);

    private static string CmodelPath([CallerFilePath] string here = "")
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "examples", "Pulsed Radar", "radar-gan", "radar_gan_8x250.cmodel");
    }
}
