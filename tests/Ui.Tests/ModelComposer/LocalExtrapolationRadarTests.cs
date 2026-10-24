using CircuitRF.Cli;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Examples;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-29 §2, gates 1-3: the Pulsed Radar device refitted from the grids it first had (Vgs −6 … +1 V, the
/// truth's lists truncated here, not in the example), exported under the local law and, from the same fit, the global one.
/// At 48 V and the design drive the intrinsic gate swings to about −8 V, past every grid; under the global law the gate
/// current ran away there (~1 A at −7 V) and clamped the swing. One fit, about eight minutes: opt-in.
/// <c>CRF_MC29_FIT</c> names a workspace a previous run fitted (kept, not deleted) to run the benches alone.
/// </summary>
public sealed class LocalExtrapolationRadarTests(ITestOutputHelper output)
{
    private const string Device = "ComposedFET:X1  g  d  0  tj  File=\"radar-gan/radar_gan_8x250.cmodel\"  N=48";

    private static readonly GanTruth Truth = SyntheticGan.Radar8x250 with
    {
        DcVgs = [.. SyntheticGan.Radar8x250.DcVgs.Where(v => v >= -6)],
        PulsedVgs = [.. SyntheticGan.Radar8x250.PulsedVgs.Where(v => v >= -6)],
    };

    private static readonly Lazy<(string Ws, string Local, string Global)> Fitted = new(Fit);

    private static (string Ws, string Local, string Global) Fit()
    {
        if (Environment.GetEnvironmentVariable("CRF_MC29_FIT") is { Length: > 0 } kept)
        {
            string d = Path.Combine(kept, PulsedRadarExampleAuthoring.DeviceDir);
            return (kept, Path.Combine(d, "local.cmodel").Replace('\\', '/'), Path.Combine(d, "global.cmodel").Replace('\\', '/'));
        }
        string root = Directory.CreateTempSubdirectory("crf-mc29-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, true); } catch (IOException) { } };
        string ws = WorkspaceCreate.Create(root, "mc29", null).WorkspaceDir;
        string dir = Path.Combine(ws, PulsedRadarExampleAuthoring.DeviceDir);
        SyntheticGan.WriteRaw(dir, Truth);
        string cmc = SyntheticGanDocument.Create(dir, Truth, PulsedRadarExampleAuthoring.Document);
        ModelComposerExampleAuthoring.UseNetworkCell(ws, cmc);
        SyntheticGanDocument.Configure(cmc, truth: Truth);
        _ = ModelCommands.Deembed(cmc).ToList();
        _ = ModelCommands.Fit(cmc, "all").ToList();
        string local = Path.Combine(dir, "local.cmodel"), global = Path.Combine(dir, "global.cmodel");
        var model = CmodelExport.Export(cmc, local).Model;
        Assert.Equal(ExtrapolationMode.Local, model.Extrapolation!.Mode);
        using (var f = File.Create(global))
            CmodelFile.Write(model with { Extrapolation = model.Extrapolation with { Mode = ExtrapolationMode.Global } }, f);
        return (ws, local.Replace('\\', '/'), global.Replace('\\', '/'));
    }

    /// <summary>Gate 1: a volt to three past the reverse edge, the gate current is the truth's to 1 mA (it was ~1 A).</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheReverseEdge_IsQuiet()
    {
        var (ws, local, global) = Fitted.Value;
        foreach (double vds in (double[])[50, 96])
            foreach (double vgs in (double[])[-7, -8, -9])
            {
                string bench = $"Vdc:VG vg 0 Vdc={vgs} V\nIProbe:IG vg g\nVdc:VD d 0 Vdc={vds} V\ntemp = 25\n";
                const string tail = "analysis DC1 type=dc\nmeasure Ig_A = DC1.I(\"IG\")\n";
                double m = Measure(ws, bench + Device.Replace("radar-gan/radar_gan_8x250.cmodel", local) + "\n" + tail, "Ig_A")[0];
                double was = Measure(ws, bench + Device.Replace("radar-gan/radar_gan_8x250.cmodel", global) + "\n" + tail, "Ig_A")[0];
                double t = Measure(ws, TruthGlobals(Truth) + SyntheticGan.Device(48, null, Truth) + bench + "gan:X1 g d 0\n" + tail, "Ig_A")[0];
                output.WriteLine($"vgs {vgs} V, vds {vds} V: Ig local {m:E3} A, global {was:E3} A, truth {t:E3} A");
                Assert.True(Math.Abs(m - t) <= 1e-3, $"vgs {vgs}, vds {vds}: Ig {m:E3} A against the truth's {t:E3} A");
            }
    }

    /// <summary>Gate 2, the brief's acceptance: on the CW bench, traps off and isothermal at 70 °C, Pout follows the truth
    /// to 0.2 dB from 29 to 34 dBm — where the swing passes the reverse edge from 30.5 dBm.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheSwingTheEdgeCaused_IsGone()
    {
        var (ws, local, _) = Fitted.Value;
        var (pin, m) = Sweep(ws, Model(local), 29, 34);
        var (_, t) = Sweep(ws, IsothermalTruth(), 29, 34);
        for (int k = 0; k < pin.Length; k++) output.WriteLine($"Pin {pin[k]}: Pout {t[k]:F3} truth / {m[k]:F3} model dBm");
        for (int k = 0; k < pin.Length; k++)
            Assert.True(Math.Abs(m[k] - t[k]) <= 0.2, $"Pin {pin[k]}: ΔPout {m[k] - t[k]:F3} dB");
    }

    /// <summary>
    /// Gate 3, on the example's own device, whose grids reach −10 V so the reverse gate edge lies beyond the swing. Driven
    /// past the forward gate edge (34.5-36 dBm), the gate currents' law changes Pout by no more than 0.05 dB, and both
    /// converge. The comparison holds <c>Id</c>'s added conductance out of both models (its <c>G_x</c> at 1e-12 S): at this
    /// drive the intrinsic point also leaves <c>Id</c>'s domain, through the oblique high-<c>vgs</c> edge and the 100 V top,
    /// where the global law's 11.4 S clamps the drain — measured 0.35-0.48 dB below the truth, against the local law's
    /// 0.04-0.06 dB. So the whole local model is also held to the truth, at gate 2's 0.2 dB.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheForwardEdge_IsUnchanged()
    {
        string ws = PulsedRadarExampleAuthoring.Workspace;
        string shipped = Path.Combine(ws, PulsedRadarExampleAuthoring.Model);
        ComposedModel model;
        using (var r = File.OpenRead(shipped)) model = CmodelFile.Read(r);
        Assert.Equal(ExtrapolationMode.Local, model.Extrapolation!.Mode);
        string dir = Directory.CreateTempSubdirectory("crf-mc29-gate3-").FullName;
        try
        {
            string Variant(string name, ExtrapolationMode mode)
            {
                var gx = new Dictionary<string, double>(model.Extrapolation.Gx) { ["Id"] = 1e-12 };
                string path = Path.Combine(dir, name + ".cmodel");
                using var f = File.Create(path);
                CmodelFile.Write(model with { Extrapolation = new ModelExtrapolation(gx) { Mode = mode } }, f);
                return path.Replace('\\', '/');
            }
            var (pin, l) = Sweep(ws, Model(shipped.Replace('\\', '/')), 34.5, 36);
            var (_, gl) = Sweep(ws, Model(Variant("gate-local", ExtrapolationMode.Local)), 34.5, 36);
            var (_, gg) = Sweep(ws, Model(Variant("gate-global", ExtrapolationMode.Global)), 34.5, 36);
            var (_, t) = Sweep(ws, IsothermalTruth(SyntheticGan.Radar8x250), 34.5, 36);
            for (int k = 0; k < pin.Length; k++)
                output.WriteLine($"Pin {pin[k]}: Pout {l[k]:F3} local / {t[k]:F3} truth dBm; gate law alone {gl[k]:F3} local / {gg[k]:F3} global dBm");
            for (int k = 0; k < pin.Length; k++)
            {
                Assert.True(Math.Abs(gl[k] - gg[k]) <= 0.05, $"Pin {pin[k]}: the gate currents' law moved Pout {gl[k] - gg[k]:F3} dB");
                Assert.True(Math.Abs(l[k] - t[k]) <= 0.2, $"Pin {pin[k]}: ΔPout {l[k] - t[k]:F3} dB from the truth");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── The benches ───────────────────────────────────────────────────────────

    /// <summary>The example's CW bench at 70 °C, its Pin swept from <paramref name="lo"/> to <paramref name="hi"/>.</summary>
    private static string Bench(double lo, double hi) => PulsedRadarExampleAuthoring.PulsedPA
        .Replace("labelednets in g d out VDD tj", "", StringComparison.Ordinal)
        .Replace("temp = 25", "temp = 70", StringComparison.Ordinal)
        .Replace("Start=20 Stop=34 Step=1", $"Start={lo} Stop={hi} Step=0.5", StringComparison.Ordinal);

    /// <summary>The fitted device with its traps following their DC state and its junction at the ambient.</summary>
    private static Func<string, string> Model(string path) => bench =>
        bench.Replace(Device, Device.Replace("radar-gan/radar_gan_8x250.cmodel", path) + "  SelfHeating=0  DrainLag=0", StringComparison.Ordinal);

    /// <summary>The truth the same way: its drain lag gone (the trap state at its DC value, phi2 = vds, shifts nothing) and
    /// its junction held at the ambient.</summary>
    private static Func<string, string> IsothermalTruth(GanTruth? truth = null)
    {
        var g = (truth ?? Truth) with { At = 0 };
        return bench => TruthGlobals(g) + SyntheticGan.Device(48, 0, g) + bench.Replace(Device, "gan:X1 g d 0", StringComparison.Ordinal);
    }

    /// <summary>The truth's constants; the bench states <c>temp</c>.</summary>
    private static string TruthGlobals(GanTruth g) => SyntheticGan.Globals(null, g).Replace("\ntemp", "\n;temp", StringComparison.Ordinal);

    private static (double[] Pin, double[] Pout) Sweep(string ws, Func<string, string> device, double lo, double hi)
    {
        string bench = Bench(lo, hi);
        Assert.Contains(Device, bench, StringComparison.Ordinal);
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(device(bench), ws, ws), new CircuitEvaluationRequest());
        Assert.True(r.Status == RunStatus.Success, $"{r.Status}: {r.StatusMessage}");
        double[] pin = [.. r.Results.First(x => x.Data.Contains("V")).Data["V"].Axes[0].Values];
        return (pin, r.GroupedResults!.CubesIn(DataSet.MeasurementsGroup)["Pout_dBm"].RealValues);
    }

    private static double[] Measure(string ws, string cnl, string name)
    {
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, ws, ws), new CircuitEvaluationRequest());
        Assert.True(r.Status == RunStatus.Success, $"{r.Status}: {r.StatusMessage}");
        return r.GroupedResults!.CubesIn(DataSet.MeasurementsGroup)[name].RealValues;
    }
}
