using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief ENV-MEAS-1 gates 2-9: the power through every IProbe of an envelope run, and the retired <c>Zmeas</c>. One
/// weakly nonlinear stage behind a series resistor, an IProbe in series with its load and, where the claim needs one,
/// a supply fed through its own IProbe and a choke. Draft accuracy and a few microseconds: nothing here needs more.
/// </summary>
public sealed class EnvelopeProbePowerTests
{
    private const string Nr = "NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1";
    private const string Cw = "Carrier[1]=\"CW;Off=0;P=0\"";
    private const string Rl50 = "R:RL b 0 R=50 Ohm";
    private const string Supply = """
        Vdc:VDD vdd 0 Vdc=1 V
        IProbe:IDC vdd x
        L:LC x a L=1 uH
        """;

    private static string Bench(string load, string env, string carriers = $"Carrier[1]=\"{Nr}\"", string extra = "") => $"""
        Pin = 10
        Rl = 25
        MOD_Source:S1 n1 0 Fc=1e9 Pavg=Pin Z=50 Record=0.5e-3 {carriers}
        R:RS n1 a R=5 Ohm
        SDD:X1 a 0 I[1,0]=2e-3*_v1^3
        IProbe:Iout a b
        {load}
        {extra}
        analysis ENV1 type=env Accuracy=draft MaxHarm=3 Segments=1 Warmup=0 Probes="a,b" {env}
        """;

    private static DataSet Run(string cnl, out ElaboratedNetlist first)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        first = nl;
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        object gate = new();
        ElaboratedNetlist Elab() { lock (gate) { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; } }
        return EnvelopeRunner.Run(plan, Elab);
    }

    private static DataSet Run(string cnl)
    {
        var ds = Run(cnl, out var first);
        first.Dispose();
        return ds;
    }

    /// <summary>The first step the built-in scalars average from: after the settle skip of a cold run.</summary>
    private static int T0(DataSet ds) =>
        (int)Math.Round(ds["__EnvMeasureFrom_s"].RealValues[0] / ds["__EnvStep_s"].RealValues[0]);

    private static double Fund(DataSet ds, string probe)
    {
        var c = ds["Pprobe_fund_W"];
        return c.RealValues[Array.IndexOf(c.Axes[0].Labels!, probe)];
    }

    private static double ProbeW(DataSet ds, string probe, string product)
    {
        var c = ds["Pprobe_W"];
        int b = Array.IndexOf(c.Axes[0].Labels!, probe);
        int k = c.Axes[1].Labels is { } l ? Array.IndexOf(l, product) : int.Parse(product);
        return c.RealValues[b * c.Axes[1].Length + k];
    }

    /// <summary>The mean fundamental power |V_b|²/(2R) at the load's node, over the built-in scalars' window.</summary>
    private static double NodePower(DataSet ds, double ohms)
    {
        var v = ds["V"];
        int nt = v.Axes[0].Length, np = v.Axes[1].Length, nk = v.Axes[2].Length, b = Array.IndexOf(v.Axes[1].Labels!, "b");
        var vv = v.ComplexValues;
        int t0 = T0(ds);
        double sum = 0;
        for (int t = t0; t < nt; t++) { var x = vv[(t * np + b) * nk + 1]; sum += x.Real * x.Real + x.Imaginary * x.Imaginary; }
        return sum / (nt - t0) / (2 * ohms);
    }

    [Fact]
    public void AtATrue50OhmLoad_TheProbePowerIsTheNodePowerInto50Ohm_AndPavgSaysIt()
    {
        var ds = Run(Bench(Rl50, "Stop=10e-6"));

        double expected = NodePower(ds, 50);
        Assert.Equal(expected, Fund(ds, "Iout"), expected * 1e-9);
        Assert.Equal(10 * Math.Log10(Fund(ds, "Iout") * 1e3), ds["Pavg_dBm"].RealValues[0], 1e-9);
    }

    [Fact]
    public void AtA25OhmLoad_TheProbeIsExact()
    {
        var ds = Run(Bench("R:RL b 0 R=25 Ohm", "Stop=10e-6"));

        double expected = NodePower(ds, 25);
        Assert.Equal(expected, Fund(ds, "Iout"), expected * 1e-9);
    }

    [Fact]
    public void IntoAReactiveLoad_TheProbePowerIsWhatTheResistorTakes()
    {
        var ds = Run(Bench("R:RL b c R=25 Ohm\nL:LL c 0 L=3 nH", "Stop=2e-6", Cw));

        var i = ds["I"];
        int nt = i.Axes[0].Length, nb = i.Axes[1].Length, nk = i.Axes[2].Length;
        var iv = i.ComplexValues;
        int t0 = T0(ds);
        double sum = 0;
        for (int t = t0; t < nt; t++) { var x = iv[(t * nb + 0) * nk + 1]; sum += x.Real * x.Real + x.Imaginary * x.Imaginary; }
        double expected = sum / (nt - t0) * 25 / 2;

        Assert.Equal(expected, Fund(ds, "Iout"), expected * 1e-9);
    }

    [Fact]
    public void AProbeBetweenTheOnlySupplyAndTheCircuit_ReadsPdc()
    {
        var ds = Run(Bench(Rl50, "Stop=10e-6", extra: Supply));

        double pdc = ds["Pdc_W"].RealValues[0];
        Assert.Equal(pdc, ProbeW(ds, "IDC", "0"), Math.Abs(pdc) * 1e-9);
    }

    [Fact]
    public void OnTheLattice_TheFundamentalBandIsTheCarrierProducts_AndTheSupplyHasADcProduct()
    {
        var ds = Run(Bench(Rl50, "Stop=2e-6", "Carrier[1]=\"CW;Off=-50MHz;P=0\" Carrier[2]=\"CW;Off=50MHz;P=0\"", Supply));

        Assert.Equal(2, ds["__EnvFundamentals"].RealValues[0]);
        double carriers = ProbeW(ds, "Iout", "(1,0)") + ProbeW(ds, "Iout", "(0,1)");
        Assert.Equal(carriers, Fund(ds, "Iout"), carriers * 1e-12);
        Assert.True(ProbeW(ds, "IDC", "(0,0)") > 0);
    }

    [Fact]
    public void InsideSweeps_TheProbePowersStackLikeAnyScalar()
    {
        string cnl = Bench("R:RL b 0 R=Rl", $"Stop=2e-6", Cw) + """

            analysis SW1 type=parametric_sweep Var=Pin Values=0,3 Inner=ENV1
            analysis SW2 type=parametric_sweep Var=Rl Values=25,50 Inner=SW1
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var sweeps = tb.Analyses.OfType<ParametricSweepAnalysis>().ToDictionary(s => s.Name);

        var one = ParametricSweepEngine.Run(sweeps["SW1"], lib, tb)["Pprobe_fund_W"];
        var two = ParametricSweepEngine.Run(sweeps["SW2"], lib, tb)["Pprobe_fund_W"];

        Assert.Equal(["Pin", "branch"], one.Axes.Select(a => a.Name));
        Assert.Equal(["Rl", "Pin", "branch"], two.Axes.Select(a => a.Name));
    }

    [Fact]
    public void WithoutWaveforms_TheAveragesStay_AndTheTimeCubesGo()
    {
        var ds = Run(Bench(Rl50, "Stop=2e-6 SaveWaveforms=no", Cw));

        Assert.True(ds.Contains("Pprobe_W"));
        Assert.True(ds.Contains("Pprobe_fund_W"));
        Assert.False(ds.Contains("I"));
        Assert.False(ds.Contains("Pprobe_t_W"));
    }

    [Fact]
    public void Zmeas_IsRetired_50IsSilent_AnythingElseChangesNothingWithOneWarning()
    {
        string Swept(string z) => Bench(Rl50, $"Stop=2e-6 Zmeas={z}", Cw) + "\nanalysis SW1 type=parametric_sweep Var=Pin Values=0,3 Inner=ENV1\n";
        (DataSet ds, List<string> warnings, TestBench tb) RunSwept(string z)
        {
            var (lib, tb) = new CnlReader().Read(Swept(z));
            using var diag = new Elaborator(lib).Elaborate(tb);
            var ds = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb, diagnosticsInto: diag);
            return (ds, diag.Warnings.Where(w => w.Contains("Zmeas", StringComparison.Ordinal)).ToList(), tb);
        }

        var (at50, quiet, _) = RunSwept("50");
        var (at25, warned, tb25) = RunSwept("25");

        Assert.Empty(quiet);
        Assert.Single(warned);
        Assert.Empty(tb25.ReadWarnings);                    // the reader (and so check) accepts the key
        Assert.Equal(at50["Pavg_dBm"].RealValues, at25["Pavg_dBm"].RealValues);
    }
}
