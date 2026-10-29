using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief ENV-MEAS-2 gates 1-4 and 7: <c>A.P("probe")</c> in a measure line reads the <c>Pprobe_*</c> cubes, through
/// any sweep, on envelope and on harmonic balance alike. The bench is <see cref="EnvelopeProbePowerTests"/>' weakly
/// nonlinear stage with an IProbe at its input, its load and its supply, CW and a couple of microseconds.
/// </summary>
public sealed class ProbePowerAccessorTests
{
    private static string Bench(string analysis, string extra = "") => $"""
        Pin = 10
        Vdd = 1
        MOD_Source:S1 n1 0 Fc=1e9 Pavg=Pin Z=50 Record=0.5e-3 Carrier[1]="CW;Off=0;P=0"
        IProbe:Iin n1 m
        R:RS m a R=5 Ohm
        SDD:X1 a 0 I[1,0]=2e-3*_v1^3
        IProbe:Iout a b
        R:RL b 0 R=25 Ohm
        Vdc:VDD vdd 0 Vdc=Vdd V
        IProbe:IDC vdd x
        L:LC x a L=1 uH
        {analysis}
        {extra}
        """;

    private const string Env = "analysis ENV1 type=env Stop=2e-6 Accuracy=draft MaxHarm=3 Segments=1 Warmup=0";

    private static DataSet RunEnv(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var first = new Elaborator(lib).Elaborate(tb);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return first; }
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), first.ResolvedGlobals);
        return EnvelopeRunner.Run(plan, Elab);
    }

    private static DataSet RunSweep(string cnl, string sweep)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(a => a.Name == sweep), lib, tb);
    }

    private static Value Eval(string expr, DataSet ds) =>
        new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["ENV1"] = ds })).Eval(expr, new Scope("t"));

    private static double Fund(DataSet ds, string probe) =>
        ds["Pprobe_fund_W"].RealValues[Array.IndexOf(ds["Pprobe_fund_W"].Axes[0].Labels!, probe)];

    private static double Dc(DataSet ds, string probe)
    {
        var c = ds["Pprobe_W"];
        return c.RealValues[Array.IndexOf(c.Axes[0].Labels!, probe) * c.Axes[1].Length];
    }

    private static readonly Lazy<DataSet> Single = new(() => RunEnv(Bench(Env)));

    private const string Sweeps = """
        analysis SW1 type=parametric_sweep Var=Pin Values=0,3,6 Inner=ENV1
        analysis SW2 type=parametric_sweep Var=Vdd Values=1,1.2 Inner=SW1
        """;
    private static readonly Lazy<DataSet> OneSweep = new(() => RunSweep(Bench(Env, Sweeps), "SW1"));
    private static readonly Lazy<DataSet> TwoSweeps = new(() => RunSweep(Bench(Env, Sweeps), "SW2"));

    [Fact]
    public void P_IsTheCube()
    {
        var ds = Single.Value;

        Assert.Equal(Fund(ds, "Iout"), Eval("ENV1.P(\"Iout\")", ds).AsReal());
        Assert.Equal(Dc(ds, "IDC"), Eval("ENV1.P(\"idc\", 0)", ds).AsReal());
    }

    [Fact]
    public void OneExpression_IsAScalar_ACurve_AndASurface_AsSweepsAreAdded()
    {
        const string text = "ENV1.P(\"Iout\")";
        foreach (var (ds, axes) in new[] { (Single.Value, Array.Empty<string>()), (OneSweep.Value, ["Pin"]),
                                           (TwoSweeps.Value, new[] { "Vdd", "Pin" }) })
        {
            var cube = ds["Pprobe_fund_W"];
            int b = Array.IndexOf(cube.Axes[^1].Labels!, "Iout"), nb = cube.Axes[^1].Length;
            var expected = cube.RealValues.Where((_, i) => i % nb == b).ToArray();

            var v = Eval(text, ds);
            if (axes.Length == 0) { Assert.Equal(expected.Single(), v.AsReal()); continue; }
            Assert.Equal(axes, v.AsCube().Axes.Select(a => a.Name));
            Assert.Equal(expected, v.AsCube().RealValues);
        }
    }

    [Fact]
    public void At_PicksOneSweepPoint()
    {
        var ds = OneSweep.Value;
        var curve = Eval("ENV1.P(\"Iout\")", ds).AsCube().RealValues;

        // at() leaves a rank-0 cube, the shape of any pinned slice
        Assert.Equal(curve[1], Eval("at(ENV1.P(\"Iout\"), \"Pin\", 1)", ds).AsCube().RealValues.Single());
    }

    [Fact]
    public void ThePaBlock_Evaluates_AndPaeIsTheClosedForm()
    {
        string cnl = Bench(Env, """
            measure Pout_dBm = dBm(ENV1.P("Iout"))
            measure Pin_dBm  = dBm(ENV1.P("Iin"))
            measure Pdc_W    = ENV1.P("IDC", 0)
            measure Gain_dB  = Pout_dBm - Pin_dBm
            measure DE_pct   = 100 * ENV1.P("Iout") / ENV1.P("IDC", 0)
            measure PAE_pct  = 100 * (ENV1.P("Iout") - ENV1.P("Iin")) / ENV1.P("IDC", 0)
            """);
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var env = Single.Value;
        var meas = new DataSet();

        var errors = new MeasurementEvaluator(tb, nl, new Dictionary<string, DataSet> { ["ENV1"] = env }).EvaluateInto(meas);

        Assert.Empty(errors);
        double pout = Fund(env, "Iout"), pin = Fund(env, "Iin"), pdc = Dc(env, "IDC");
        Assert.True(pout > 0 && pin > pout && pdc > 0);
        Assert.Equal(100 * (pout - pin) / pdc, meas["PAE_pct"].RealValues.Single(), 1e-12);
        Assert.Equal(10 * Math.Log10(pout / pin), meas["Gain_dB"].RealValues.Single(), 1e-9);
    }

    [Fact]
    public void OnHarmonicBalance_PMeansWhatItMeansOnEnvelope()
    {
        var (lib, tb) = new CnlReader().Read(Bench("analysis HB1 type=hb Tone=1e9 MaxHarm=3"));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var run = new HbEngine(nl, tb).Run(HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(),
                                                             nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
        Assert.True(run.Converged);
        double Dbm(string analysis, DataSet ds) =>
            10 * Math.Log10(new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { [analysis] = ds }))
                .Eval($"{analysis}.P(\"Iout\")", new Scope("t")).AsReal() * 1e3);

        Assert.Equal(Dbm("ENV1", Single.Value), Dbm("HB1", run.DataSet), 0.01);
    }

    [Fact]
    public void OnATwoToneHb_TheFundamentalBandIsTheTwoCarriers()
    {
        string cnl = Bench("", """
            analysis HB1 type=hb NumFreqs=2 Tone[1]=0.99e9 Tone[2]=1.01e9 MaxMixOrder=5 MaxHarm=3
            """).Replace("MOD_Source:S1 n1 0 Fc=1e9 Pavg=Pin Z=50 Record=0.5e-3 Carrier[1]=\"CW;Off=0;P=0\"",
                         "V_nTone:S1 s 0 NumFreqs=2 Freq[1]=0.99e9 V[1]=1 Freq[2]=1.01e9 V[2]=1\nR:RG s n1 R=50 Ohm");
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ds = new HbEngine(nl, tb).Run(HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(),
                                                            nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit)).DataSet;

        var pk = ds["Pprobe_W"];
        double At(string tag) => pk.RealValues[Array.IndexOf(pk.Axes[0].Labels!, "Iout") * pk.Axes[1].Length
                                              + Array.IndexOf(pk.Axes[1].Labels!, tag)];
        double carriers = At("(1,0)") + At("(0,1)");
        Assert.True(carriers > 0);
        Assert.Equal(carriers, Fund(ds, "Iout"), carriers * 1e-12);
    }
}
