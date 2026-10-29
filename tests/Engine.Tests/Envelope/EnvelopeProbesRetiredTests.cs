using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief ENV-MEAS-3 gates 2-8: a labeled net records its voltage, an IProbe its power, and nothing assumes an impedance.
/// One weakly nonlinear stage behind a series resistor and an IProbe in series with its load, as brief 1's tests use.
/// Draft accuracy and a few microseconds unless the claim needs more. Gate 5 as the brief states it (the min/max spread
/// is zero without memory) is not a property of a 0.5 dB bin: the gain's own slope across the bin is in the spread
/// (src/Engine/RESOLVED.md); what is gated is that the spread is the cloud's own extremes, bin by bin.
/// </summary>
public sealed class EnvelopeProbesRetiredTests(ITestOutputHelper output)
{
    private const string Nr = "Carrier[1]=\"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1\"";
    private const string Cw = "Carrier[1]=\"CW;Off=0;P=0\"";
    private static readonly double To50Ohm = 10 * Math.Log10(1000.0 / 50);

    private static string Bench(string load, string env, string carriers = Nr, string extra = "") => $"""
        Pin = 10
        MOD_Source:S1 n1 0 Fc=1e9 Pavg=Pin Z=50 Record=0.5e-3 {carriers}
        R:RS n1 a R=5 Ohm
        SDD:X1 a 0 I[1,0]=2e-3*_v1^3
        IProbe:Iout a b
        {load}
        {extra}
        analysis ENV1 type=env Accuracy=draft MaxHarm=3 Segments=1 Warmup=0 {env}
        """;

    private static (EnvelopeRunner.Plan plan, ElaboratedNetlist nl) Load(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals), nl);
    }

    private static DataSet Run(string cnl, out ElaboratedNetlist nl)
    {
        var (plan, first) = Load(cnl);
        nl = first;
        return EnvelopeRunner.Run(plan, () => first);
    }

    /// <summary>The engine's own record and the DataSet built from it, with no settle skip: what the old definitions are
    /// recomputed from.</summary>
    private static (EnvelopeRunResult r, DataSet ds) Record(string cnl)
    {
        var (plan, nl) = Load(cnl);
        using (nl)
        {
            var r = new EnvelopeEngine(nl).Run(plan.Params);
            return (r, EnvelopeDataSet.Build(r, plan));
        }
    }

    /// <summary>A cube's row at one label of its first axis (node or branch).</summary>
    private static double[] Row(DataSet ds, string cube, string label)
    {
        var c = ds[cube];
        int i = Array.IndexOf(c.Axes[0].Labels!, label), n = c.RealValues.Length / c.Axes[0].Length;
        Assert.True(i >= 0, $"{label} is not on {cube}'s {c.Axes[0].Name} axis");
        return c.RealValues[(i * n)..((i + 1) * n)];
    }

    // ── gate 2: what a run records ──────────────────────────────────────────

    [Fact]
    public void TwoLabeledNetsAndOneIProbe_RecordExactlyThose()
    {
        var ds = Run(Bench("R:RL b 0 R=50 Ohm", "Stop=2e-6", Cw, "labelednets a b"), out var nl);
        using (nl)
        {
            Assert.Equal(["a", "b"], ds["V"].Axes[1].Labels!);
            Assert.Equal(["a", "b"], ds["VSD"].Axes[0].Labels!);
            Assert.Equal(["Iout"], ds["I"].Axes[1].Labels!);
            Assert.DoesNotContain(nl.Notes, n => n.Contains("no IProbe", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NeitherLabelsNorIProbes_RecordTheSourceNode_WithOneNote()
    {
        var ds = Run("""
            MOD_Source:S1 n1 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 Carrier[1]="CW;Off=0;P=0"
            R:RL n1 0 R=50 Ohm
            analysis ENV1 type=env Stop=2e-6 Accuracy=draft MaxHarm=1 Segments=1 Warmup=0
            """, out var nl);
        using (nl)
        {
            Assert.Equal(["n1"], ds["V"].Axes[1].Labels!);
            Assert.False(ds.Contains("Gt_dB") || ds.Contains("PSD") || ds.Contains("Pavg_dBm"));
            Assert.Single(nl.Notes, n => n.Contains("no IProbe is placed", StringComparison.Ordinal));
        }
    }

    // ── gate 3: the two spectra ─────────────────────────────────────────────

    [Fact]
    public void Vsd_IsTheOldNodePsd_LessOnlyTheUnit()
    {
        var (r, ds) = Record(Bench("R:RL b 0 R=50 Ohm", "Stop=10e-6"));
        var vsd = ds["VSD"];
        var freq = vsd.Axes[1].Values;
        double fs = 1 / r.StepS, df = freq[1] - freq[0];
        int nseg = (int)Math.Round(fs / df);
        foreach (string node in vsd.Axes[0].Labels!)
        {
            int p = Array.IndexOf(r.ProbeNames, node);
            var (f, d) = SignalMeasure.Welch([.. r.Probe.Select(x => x[p][1])], fs, nseg);   // the old PSD: into 50 ohm
            var old = f.Select((x, i) => (f: r.F0Hz + x, dbm: 10 * Math.Log10(Math.Max(d[i], 1e-300) / (2 * 50) * 1e3)))
                       .Where(x => x.f > 0).ToArray();
            var got = Row(ds, "VSD", node);
            Assert.Equal(old.Length, got.Length);
            for (int i = 0; i < got.Length; i++)
            {
                Assert.Equal(old[i].f, freq[i], 1e-3);
                Assert.True(Math.Abs(got[i] + To50Ohm - old[i].dbm) < 1e-9, $"{node} bin {i}: {got[i] + To50Ohm} against {old[i].dbm}");
            }
        }
    }

    [Fact]
    public void AtA50OhmResistor_TheProbesPsd_IsItsNodesVsdInto50Ohm()
    {
        var ds = Run(Bench("R:RL b 0 R=50 Ohm", "Stop=10e-6", extra: "labelednets b"), out var nl);
        nl.Dispose();
        double[] psd = Row(ds, "PSD", "Iout"), vsd = Row(ds, "VSD", "b");
        Assert.Equal(vsd.Length, psd.Length);
        for (int i = 0; i < psd.Length; i++)
            Assert.True(Math.Abs(psd[i] - (vsd[i] + To50Ohm)) < 1e-9, $"bin {i}: {psd[i]} against {vsd[i] + To50Ohm}");
    }

    /// <summary>Into 25 Ω the delivered spectrum integrates to the probe's fundamental power, within the 0.05 dB the
    /// channel-power comparisons of EnvelopeSnpTests allow a Welch band power. A CW drive: a modulated record this short
    /// is one Welch segment, whose windowed power is a sample of the signal's, not its mean.</summary>
    [Fact]
    public void AtA25OhmLoad_ThePsdIntegratesToTheProbePower()
    {
        var ds = Run(Bench("R:RL b 0 R=25 Ohm", "Stop=10e-6", Cw), out var nl);
        nl.Dispose();
        var freq = ds["PSD"].Axes[1].Values;
        double df = freq[1] - freq[0];
        double band = Row(ds, "PSD", "Iout").Where(double.IsFinite).Sum(x => Math.Pow(10, x / 10) / 1e3) * df;
        double fund = Row(ds, "Pprobe_fund_W", "Iout")[0];
        output.WriteLine($"integrated {10 * Math.Log10(band * 1e3):F4} dBm, Pprobe_fund {10 * Math.Log10(fund * 1e3):F4} dBm");
        Assert.True(Math.Abs(10 * Math.Log10(band / fund)) < 0.05);
    }

    // ── gate 4: AM-AM and AM-PM at an IProbe ────────────────────────────────

    /// <summary>The old AM-AM / AM-PM, recomputed: |V|²/(2·50) at the node against the available drive, aligned to the
    /// node's own envelope, binned as the engine bins.</summary>
    private static (double[] am, double[] pm) OldAmAm(EnvelopeRunResult r, string node, Func<Complex, Complex, double>? delivered = null)
    {
        int p = Array.IndexOf(r.ProbeNames, node), nm = r.Time.Length;
        var drive = new Complex[nm];
        for (int t = 0; t < nm; t++) foreach (var d in r.Drive) drive[t] += d[t];
        Complex[] y = [.. r.Probe.Select(x => x[p][1])];
        Complex[] cur = [.. r.IProbeCurrent.Select(x => x[0][1])];
        var (g, lag, _) = SignalMeasure.Align(drive, y, 16);
        double gphase = Math.Atan2(g.Imaginary, g.Real);
        var bins = new SortedDictionary<int, (double am, double pm, int n)>();
        for (int t = 0; t + lag < nm; t++)
        {
            double pi = (drive[t].Real * drive[t].Real + drive[t].Imaginary * drive[t].Imaginary) / (8 * r.DriveRe[0]);
            var yo = y[t + lag];
            double po = delivered?.Invoke(yo, cur[t + lag]) ?? (yo.Real * yo.Real + yo.Imaginary * yo.Imaginary) / (2 * 50);
            if (pi <= 0 || po <= 0) continue;
            double ph = (Complex.Log(yo / drive[t]).Imaginary - gphase) * 180 / Math.PI;
            ph = ((ph + 540) % 360) - 180;
            int b = (int)Math.Floor(10 * Math.Log10(pi * 1e3) / 0.5);
            var e = bins.GetValueOrDefault(b);
            bins[b] = (e.am + 10 * Math.Log10(po / pi), e.pm + ph, e.n + 1);
        }
        int b0 = (int)Math.Round(EnvelopeDataSet.AmGridLoDbm / 0.5), nb = (int)Math.Round(EnvelopeDataSet.AmGridHiDbm / 0.5) - b0;
        double[] Col(Func<(double am, double pm, int n), double> f) =>
            [.. Enumerable.Range(b0, nb).Select(b => bins.TryGetValue(b, out var e) && e.n >= EnvelopeDataSet.AmMinSamples ? f(e) : double.NaN)];
        return (Col(e => e.am / e.n), Col(e => e.pm / e.n));
    }

    private static void SameInEveryFilledBin(double[] expected, double[] got, double tol)
    {
        Assert.Equal(expected.Length, got.Length);
        Assert.Contains(expected, double.IsFinite);
        for (int i = 0; i < got.Length; i++)
        {
            Assert.Equal(double.IsFinite(expected[i]), double.IsFinite(got[i]));
            if (double.IsFinite(got[i])) Assert.True(Math.Abs(got[i] - expected[i]) <= tol, $"bin {i}: {got[i]} against {expected[i]}");
        }
    }

    [Fact]
    public void AtA50OhmResistor_TheProbesAmAmAndAmPm_AreTheOldDefinition()
    {
        var (r, ds) = Record(Bench("R:RL b 0 R=50 Ohm", "Stop=10e-6", extra: "labelednets a"));
        var (am, pm) = OldAmAm(r, "a");
        SameInEveryFilledBin(am, Row(ds, "AMAM", "Iout"), 1e-9);
        SameInEveryFilledBin(pm, Row(ds, "AMPM", "Iout"), 1e-9);
    }

    [Fact]
    public void IntoAReactiveLoad_TheProbesAmAm_IsTheDeliveredPowerOverTheAvailable()
    {
        var (r, ds) = Record(Bench("R:RL b c R=25 Ohm\nL:LL c 0 L=3 nH", "Stop=10e-6", extra: "labelednets a"));
        var (am, _) = OldAmAm(r, "a", (v, i) => 0.5 * (v.Real * i.Real + v.Imaginary * i.Imaginary));
        SameInEveryFilledBin(am, Row(ds, "AMAM", "Iout"), 1e-9);
    }

    // ── gate 6: the cloud ───────────────────────────────────────────────────

    /// <summary>Gates 5-6: the cloud is the samples AM-AM and AM-PM bin. Binned on the same grid, Gt_t_dB's mean is AMAM
    /// and PM_t_deg's is AMPM, and the min/max cubes are the extremes of the same samples, bin by bin.</summary>
    [Fact]
    public void TheCloud_BinnedOnThePinGrid_IsAmAmAndAmPm_WithTheirSpread()
    {
        var ds = Run(Bench("R:RL b c R=25 Ohm\nL:LL c 0 L=3 nH", "Stop=10e-6"), out var nl);
        nl.Dispose();
        var pin = ds["Pin_inst_t_dBm"].RealValues;
        int b0 = (int)Math.Round(EnvelopeDataSet.AmGridLoDbm / 0.5), nbin = ds["AMAM"].Axes[1].Length;
        foreach (var (cloud, cube) in new[] { ("Gt_t_dB", "AMAM"), ("PM_t_deg", "AMPM") })
        {
            var y = ds[cloud].RealValues;                          // [time, branch], one branch
            Assert.Equal(pin.Length, y.Length);
            var bins = new SortedDictionary<int, List<double>>();
            for (int t = 0; t < pin.Length; t++)
            {
                if (!double.IsFinite(pin[t]) || !double.IsFinite(y[t])) continue;
                int b = (int)Math.Floor(pin[t] / 0.5);
                if (!bins.TryGetValue(b, out var l)) bins[b] = l = [];
                l.Add(y[t]);
            }
            double[] Col(Func<List<double>, double> f) => [.. Enumerable.Range(0, nbin)
                .Select(i => bins.TryGetValue(b0 + i, out var l) && l.Count >= EnvelopeDataSet.AmMinSamples ? f(l) : double.NaN)];
            SameInEveryFilledBin(Col(l => l.Sum() / l.Count), Row(ds, cube, "Iout"), 0);
            SameInEveryFilledBin(Col(l => l.Min()), Row(ds, cube + "_min", "Iout"), 0);
            SameInEveryFilledBin(Col(l => l.Max()), Row(ds, cube + "_max", "Iout"), 0);
        }
    }

    /// <summary>A record longer than the cap: decimated to at most <see cref="EnvelopeDataSet.CloudMaxSamples"/>, and the
    /// same length at every point, so a Pin sweep stacks.</summary>
    [Fact]
    public void TheCloud_IsCapped_AndStacksOverASweep()
    {
        var (lib, tb) = new CnlReader().Read(Bench("R:RL b 0 R=50 Ohm", "Stop=25e-6 Step=1e-9 MaxHarm=1", Cw) +
                                            "\nanalysis SW1 type=parametric_sweep Var=Pin Values=0,10 Inner=ENV1\n");
        var ds = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb);
        var gt = ds["Gt_t_dB"];
        Assert.Equal(["Pin", "time", "branch"], gt.Axes.Select(a => a.Name));
        Assert.InRange(gt.Axes[1].Length, EnvelopeDataSet.CloudMaxSamples / 2, EnvelopeDataSet.CloudMaxSamples);
        Assert.Equal(gt.Axes[1].Length, ds["Pin_inst_t_dBm"].Axes[1].Length);
    }

    // ── gate 7: a probe that carries no RF ──────────────────────────────────

    [Fact]
    public void ASupplyProbe_IsNaNInTheOutputRows_ButItsDcPowerIsNot()
    {
        var ds = Run(Bench("R:RL b 0 R=50 Ohm", "Stop=10e-6", extra: """
            Vdc:VDD vdd 0 Vdc=1 V
            IProbe:IDC vdd x
            L:LC x a L=1 uH
            """), out var nl);
        nl.Dispose();
        foreach (var cube in new[] { "AMAM", "AMPM", "Gt_dB", "NMSE_dB", "DE_pct", "Gt_t_dB" })
        {
            var c = ds[cube];
            int b = Array.IndexOf(c.Axes[cube == "Gt_t_dB" ? 1 : 0].Labels!, "IDC");
            Assert.True(b >= 0, cube);
        }
        Assert.All(Row(ds, "AMAM", "IDC"), v => Assert.True(double.IsNaN(v)));
        Assert.True(double.IsNaN(Row(ds, "Gt_dB", "IDC")[0]));
        Assert.True(double.IsNaN(Row(ds, "NMSE_dB", "IDC")[0]));
        Assert.Contains(Row(ds, "AMAM", "Iout"), double.IsFinite);
        Assert.True(Row(ds, "Pprobe_W", "IDC")[0] > 0);
    }

    // ── gate 8: an old netlist's Probes ─────────────────────────────────────

    [Fact]
    public void AnOldProbesList_RecordsItsNodes_NotesOnce_AndIsNeverWritten()
    {
        string cnl = Bench("R:RL b 0 R=50 Ohm", "Stop=2e-6 Probes=\"a,b\"", Cw);
        var (lib, tb) = new CnlReader().Read(cnl);
        Assert.Empty(tb.ReadWarnings);                       // the reader (and so check) accepts the key

        var ds = Run(cnl, out var nl);
        using (nl)
        {
            Assert.Equal(["a", "b"], ds["V"].Axes[1].Labels!);
            Assert.Single(nl.Notes, n => n.Contains("Probes is no longer a setting", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("Probes", CnlWriter.Write(tb, lib), StringComparison.Ordinal);
    }
}
