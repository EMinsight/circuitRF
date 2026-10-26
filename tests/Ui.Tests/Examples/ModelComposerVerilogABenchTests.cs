// ================================================================
//  ModelComposerVerilogABenchTests.cs — brief-modelcomposer-28's acceptance: the shipped example's GanVerilogA bench,
//  which places the GaN model as a ComposedFET and its committed Verilog-A export as a VerilogA on identical bias
//  networks, gives the same DC sweep, S-parameters and HB drive sweep from both — with the switches at their defaults,
//  with each lag and self-heating off, and with LinearC on at the bias point circuitRF's own freeze settled on.
//
//  The bench's own netlist (its schematic, extracted as Simulate extracts it) is run through the engines with the DC
//  residual at 1e-12 A, as MC-26's gates run theirs: the bench's 1e-6 A default would let two halves of one solve stop
//  further apart than the comparison allows. Skipped, with the reason, where no compiler or device worker is reachable.
// ================================================================

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Ui.Tests.ModelComposer;
using CircuitRF.Ui.Tests.ModelComposer.VerilogA;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class ModelComposerVerilogABenchTests : IDisposable
{
    private static readonly string Ws = Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder);

    private static readonly Lazy<string> Bench = new(() => SchematicCircuit.CnlTextOf(
        Path.Combine(Ws, ModelComposerExampleAuthoring.VerilogABench, "schematic", ModelComposerExampleAuthoring.VerilogABench + ".csch")));

    private const double Vdd = 8;

    private readonly ITestOutputHelper _output;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public ModelComposerVerilogABenchTests(ITestOutputHelper output)
    {
        _output = output;
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    [CompilerTheory]
    [InlineData("")]
    [InlineData("GateLag=0")]
    [InlineData("DrainLag=0")]
    [InlineData("SelfHeating=0")]
    [InlineData("LinearC=1")]
    public void TheComposedFetAndItsVerilogA_AgreeOnTheBench(string switches)
    {
        string what = switches.Length == 0 ? "defaults" : switches;
        // The module takes the bias point as given; circuitRF's freeze finds it from the bench's own DC solution.
        string point = switches.Contains("LinearC", StringComparison.Ordinal) ? FrozenPoint(Instances(switches, "")) : "";
        string cnl = Instances(switches, $"{switches} {point}");
        if (point.Length > 0) _output.WriteLine($"{what}: {point}");

        // DC: the bench's gate sweep.
        var dc = Find<ParametricSweepAnalysis>(cnl, "DC1_sweep_VGS");
        foreach (double vgs in dc.SweepValues)
        {
            var r = NonlinearDcEngine.Run(Elaborate(Set(cnl, dc.SweepVarName, vgs)), VaBench.Tight);
            Assert.True(r.Converged, $"{what}: DC did not converge at VGS = {vgs:G3}");
            foreach (var (a, b) in new[] { ("ID1", "ID2"), ("IG1", "IG2") })
            {
                double x = r.ProbeCurrents[a], y = r.ProbeCurrents[b];
                Assert.True(Math.Abs(x - y) <= Math.Max(1e-9 * Math.Abs(x), 1e-15), $"{what} {a} at VGS = {vgs:G3}: {x:R} vs {y:R}");
            }
        }

        // S at the bias, over the bench's band (0.5 to 20 GHz in 40 points): ports 1-2 against 3-4.
        Assert.Equal(40, Find<SParameterAnalysis>(cnl, "SP1").Freq.NumPoints);
        double[] f = [.. Enumerable.Range(0, 40).Select(k => 0.5e9 + k * 19.5e9 / 39)];
        var s = SParameterEngine.Run(Elaborate(cnl), f, VaBench.Tight)["S"];
        double worstS = 0;
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Complex x = (Complex)s[k, i, j], y = (Complex)s[k, i + 2, j + 2];
                    worstS = Math.Max(worstS, (x - y).Magnitude);
                    Assert.True((x - y).Magnitude <= 1e-9 * Math.Max(1, x.Magnitude), $"{what} S{i + 1}{j + 1} at {f[k]:G3} Hz: {x} vs {y}");
                }

        // HB: the bench's drive sweep.
        var hb = Find<ParametricSweepAnalysis>(cnl, "HB1_sweep_Pin");
        double dPout = 0, dGain = 0, dPae = 0;
        foreach (double pin in hb.SweepValues)
        {
            var set = Hb(Set(cnl, hb.SweepVarName, pin));
            Assert.True(set["Converged"].RealValues.All(c => c == 1), $"{what}: HB did not converge at Pin = {pin:G3} dBm");
            var a = Metrics(set, pin, "o1", "a1", "IIN1", "ID1");
            var b = Metrics(set, pin, "o2", "a2", "IIN2", "ID2");
            (dPout, dGain, dPae) = (Math.Max(dPout, Math.Abs(a.Pout - b.Pout)), Math.Max(dGain, Math.Abs(a.Gain - b.Gain)),
                                    Math.Max(dPae, Math.Abs(a.Pae - b.Pae)));
            Assert.True(Math.Abs(a.Pout - b.Pout) <= 0.001, $"{what} Pout at Pin = {pin:G3}: {a.Pout:F6} vs {b.Pout:F6} dBm");
            Assert.True(Math.Abs(a.Gain - b.Gain) <= 0.001, $"{what} gain at Pin = {pin:G3}: {a.Gain:F6} vs {b.Gain:F6} dB");
            Assert.True(Math.Abs(a.Pae - b.Pae) <= 0.01, $"{what} PAE at Pin = {pin:G3}: {a.Pae:F4} vs {b.Pae:F4} %");
        }
        _output.WriteLine($"{what}: worst |dS| {worstS:E2}; HB largest difference Pout {dPout:E2} dB, gain {dGain:E2} dB, PAE {dPae:E2} pp");
    }

    // ── the bench ─────────────────────────────────────────────────────────────

    /// <summary>The bench with <paramref name="x1"/> on the ComposedFET's line and <paramref name="x2"/> on the VerilogA's.</summary>
    private static string Instances(string x1, string x2)
    {
        string cnl = Bench.Value;
        foreach (var (prefix, extra) in new[] { ("ComposedFET:X1 ", x1), ("VerilogA:X2 ", x2) })
        {
            var m = Regex.Match(cnl, $"^{Regex.Escape(prefix)}.*$", RegexOptions.Multiline);
            Assert.True(m.Success, $"the bench has no {prefix}line\n{cnl}");
            cnl = cnl.Remove(m.Index, m.Length).Insert(m.Index, (m.Value + " " + extra).TrimEnd());
        }
        return cnl;
    }

    /// <summary>The bench with one global set: its <c>name = value</c> line rewritten.</summary>
    private static string Set(string cnl, string name, double value)
    {
        var m = Regex.Match(cnl, $@"^{Regex.Escape(name)}\s*=.*$", RegexOptions.Multiline);
        Assert.True(m.Success, $"the bench has no global {name}");
        return cnl.Remove(m.Index, m.Length).Insert(m.Index, $"{name} = {value.ToString("R", CultureInfo.InvariantCulture)}");
    }

    private static (Library Lib, TestBench Tb) Read(string cnl) => new CnlReader().Read(cnl, ModelComposerExampleAuthoring.VerilogABench, Ws);

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = Read(cnl);
        return new Elaborator(lib) { BaseDirectory = Ws }.Elaborate(tb);
    }

    private static T Find<T>(string cnl, string name) where T : Analysis
        => Read(cnl).Tb.Analyses.OfType<T>().Single(a => a.Name == name);

    private static DataSet Hb(string cnl)
    {
        var (lib, tb) = Read(cnl);
        var nl = new Elaborator(lib) { BaseDirectory = Ws }.Elaborate(tb);
        var hb = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        return (DataSet)new HbEngine(nl, tb, null, null, lib, Ws).Run(HbEngine.Resolve(hb, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
    }

    /// <summary>The ComposedFET's bias point as circuitRF's freeze settled on it, as the module's <c>VgsQ</c>, <c>VdsQ</c>,
    /// <c>TjQ</c>: the intrinsic voltages, and the device temperature plus the thermal node's rise.</summary>
    private static string FrozenPoint(string cnl)
    {
        var nl = Elaborate(cnl);
        BiasFreeze.Ensure(nl, VaBench.Tight);
        var core = nl.Components.Select(c => c.Model).OfType<ComposedCoreModel>().Single();
        var at = core.FrozenAt ?? throw new InvalidOperationException("the core was not frozen");
        double tj = core.TemperatureC + (core.HasThermalNode && core.Switches.SelfHeating ? at[core.ThermalPort] : 0.0);
        return $"VgsQ={VaBench.N(at[0])} VdsQ={VaBench.N(at[1])} TjQ={VaBench.N(tj)}";
    }

    /// <summary>Output power into the 50 Ω port (dBm), transducer gain from the available drive (dB), and power-added
    /// efficiency from the drain supply (%), as the bench's own measures define them.</summary>
    private static (double Pout, double Gain, double Pae) Metrics(DataSet set, double pin, string output, string input,
                                                                    string inputProbe, string drainProbe)
    {
        Complex At(DataCube c, string label, int h) => (Complex)c[Array.FindIndex(c.Axes[0].Labels!, n => n == label), h];
        var v = set["V"];
        var i = set["I"];
        Complex vo = At(v, output, 1);
        double pout = (vo * Complex.Conjugate(vo)).Real / 100;
        double delivered = (0.5 * At(v, input, 1) * Complex.Conjugate(At(i, inputProbe, 1))).Real;
        double pdc = Vdd * At(i, drainProbe, 0).Real;
        double dbm = 10 * Math.Log10(pout / 1e-3);
        return (dbm, dbm - pin, 100 * (pout - delivered) / pdc);
    }
}
