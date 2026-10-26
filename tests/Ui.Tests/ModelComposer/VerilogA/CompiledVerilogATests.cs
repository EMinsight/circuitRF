using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-26 §4, the compiled gates: each case's <c>.va</c>, compiled by the user's Verilog-A compiler and
/// placed as a <c>VerilogA</c> component, against the same <c>.cmodel</c> placed as a <c>ComposedFET</c> — one bench, one
/// bias network, the same instance parameters. Skips with a reason when no compiler or worker is reachable.
///
/// <para>The DC solves run to a residual of 1e-12 A rather than the default 1e-6: at a forward-biased gate an ampere of
/// gate current moves by tens of percent per volt, so the default stopping point alone puts two exact implementations
/// 1e-8 apart.</para>
/// </summary>
// Serialised with every class that mutates the process-wide ExternalDeviceRegistry or the worker's tools folder.
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class CompiledVerilogATests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public CompiledVerilogATests(ITestOutputHelper output)
    {
        _output = output;
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    public static IEnumerable<object[]> Names() => VaTestModels.All().Select(c => new object[] { c.Name });

    private static VaCase Case(string name) => VaTestModels.All().Single(c => c.Name == name);

    private static readonly AnalysisSettings Tight = new() { NonlinearAbsTol = 1e-12 };

    // ── DC I–V ────────────────────────────────────────────────────────────────

    [CompilerTheory]
    [MemberData(nameof(Names))]
    public void DcIv_AgreesToAPartInABillion_OverAGridReachingOutsideTheDomain(string name)
    {
        var c = Case(name);
        var (cmodel, va) = Files(c);
        int compared = 0;
        foreach (double vgs in c.GridVgs)
            foreach (double vds in c.GridVds)
            {
                var a = Dc(c, Bench(vgs, vds) + ComposedFet(c, cmodel));
                var b = Dc(c, Bench(vgs, vds) + VerilogA(c, va));
                foreach (var (what, x, y) in new[] { ("Id", a.Id, b.Id), ("Ig", a.Ig, b.Ig) })
                    Assert.True(Math.Abs(x - y) <= Math.Max(1e-9 * Math.Abs(x), 1e-15),
                                $"{name} {what} at vgs={vgs}, vds={vds}: ComposedFET {x:R}, VerilogA {y:R}");
                compared++;
            }
        Assert.Equal(c.GridVgs.Length * c.GridVds.Length, compared);
    }

    // ── S-parameters ──────────────────────────────────────────────────────────

    [CompilerTheory]
    [MemberData(nameof(Names))]
    public void SParameters_AgreeAtThreeBiases_1To20GHz(string name)
    {
        var c = Case(name);
        var (cmodel, va) = Files(c);
        double[] f = [1e9, 5e9, 10e9, 20e9];
        double worst = 0;
        foreach (var (vgs, vds) in c.SBiases)
        {
            var sa = Sp(c, SBench(vgs, vds) + ComposedFet(c, cmodel), f);
            var sb = Sp(c, SBench(vgs, vds) + VerilogA(c, va), f);
            for (int k = 0; k < f.Length; k++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                    {
                        Complex x = (Complex)sa[k, i, j], y = (Complex)sb[k, i, j];
                        worst = Math.Max(worst, (x - y).Magnitude);
                        // 1e-9, scaled by |S| where the device has gain: at 1 GHz circuitRF's own m = 2 (inductors divided
                        // by m) sits 8e-11·|S| from two parallel ComposedFETs, and |S21| reaches 12 here.
                        Assert.True((x - y).Magnitude <= 1e-9 * Math.Max(1, x.Magnitude),
                                    $"{name} S{i + 1}{j + 1} at ({vgs}, {vds}), {f[k]:G3} Hz: {x} vs {y}");
                    }
        }
        _output.WriteLine($"{name}: worst |ΔS| {worst:E2}");
    }

    // ── Harmonic balance ──────────────────────────────────────────────────────

    [CompilerTheory]
    [MemberData(nameof(Names))]
    public void HbPowerSweep_ToCompression_AgreesInPoutGainAndPae(string name)
    {
        var c = Case(name);
        var (cmodel, va) = Files(c);
        double first = double.NaN, last = double.NaN;
        for (double vs = 0.05; vs < 8; vs *= 1.5)
        {
            var a = Metrics(Hb(c, Drive(vs) + ComposedFet(c, cmodel)), vs);
            var b = Metrics(Hb(c, Drive(vs) + VerilogA(c, va)), vs);
            Assert.True(Math.Abs(a.Pout - b.Pout) <= 0.001, $"{name} Pout at {vs:G3} V: {a.Pout:F6} vs {b.Pout:F6} dBm");
            Assert.True(Math.Abs(a.Gain - b.Gain) <= 0.001, $"{name} gain at {vs:G3} V: {a.Gain:F6} vs {b.Gain:F6} dB");
            Assert.True(Math.Abs(a.Pae - b.Pae) <= 0.01, $"{name} PAE at {vs:G3} V: {a.Pae:F4} vs {b.Pae:F4} %");
            if (double.IsNaN(first)) first = a.Gain;
            last = a.Gain;
            if (last < first - 3) break;
        }
        // Not vacuous: the sweep reaches compression.
        Assert.True(last < first - 1, $"{name}: the gain fell only from {first:F2} to {last:F2} dB");
    }

    // ── The maximum-size network and the clamp warning ────────────────────────

    /// <summary>About 30 s on an Apple-silicon Mac the first time; the compiler's artefact is then cached on the source's
    /// content, so the opt-in tier is where this belongs.</summary>
    [CompilerFact]
    [Trait("Category", "Benchmark")]
    public void AMaximumSizeAnn_Compiles()
    {
        var c = new VaCase("MaximumAnn", VaTestModels.MaximumAnn());
        var (_, va) = Files(c);
        var watch = Stopwatch.StartNew();
        string osdi = VerilogASourceCompiler.Compile(va, out string note);
        _output.WriteLine($"4 x 64 ANN: {new FileInfo(va).Length / 1024} KiB of Verilog-A, compiled in {watch.Elapsed.TotalSeconds:F1} s. {note}");
        Assert.True(File.Exists(osdi));
    }

    [CompilerFact]
    public void ARangedModelRunBeyondItsRange_IsClampedAndSaysSo()
    {
        var c = Case("Range150Clamped");
        var (cmodel, va) = Files(c);
        var (lib, tb) = new CnlReader().Read(Globals(c) + Bench(-0.5, 6) + ComposedFet(c, cmodel));
        var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Contains(nl.Warnings, w => w.Contains("evaluated at 100 °C", StringComparison.Ordinal));
        // The module's own warning, said once at the first step.
        Assert.Contains("$warning(\"Range150Clamped.cmodel covers 0.0 to 100.0 degC; the device is at %g degC, so the model is evaluated at %g degC.\", Tdev, T);",
                        File.ReadAllText(va));
    }

    /// <summary>R-mc26-8: a constituent that is not finite stops the run naming it and the point — through the worker as a
    /// refused point carrying the model's own <c>$fatal</c> sentence, not as a worker that died.</summary>
    [CompilerFact]
    public void ANonFiniteConstituent_IsRefusedNamingItAndThePoint()
    {
        var m = ComposedTestModels.Model();
        var c = new VaCase("NotFinite", m with
        {
            Core = m.Core with
            {
                Id = Constituent.Equation(ComposedTestModels.IdEq + "+1e-3*(vgs+2)^0.5", VaTestModels.Inputs, ComposedTestModels.IdParams),
            },
        });
        var (cmodel, va) = Files(c);
        ElaboratedNetlist At(string device)
        {
            var (lib, tb) = new CnlReader().Read(Bench(-3, 4) + device);
            return new Elaborator(lib).Elaborate(tb);
        }

        // circuitRF's own core refuses at once.
        var composed = At(ComposedFet(c, cmodel));
        var ex = Assert.Throws<ComposedEvaluationException>(() => NonlinearDcEngine.Run(composed, Tight));
        Assert.Contains("constituent Id evaluated to NaN", ex.Message);

        // The module's point is refused by the worker with its own sentence; the DC engine takes a refused point as a step
        // to shorten, and when the solve fails its report carries that sentence.
        var placed = At(VerilogA(c, va));
        Assert.False(NonlinearDcEngine.Run(placed, Tight).Converged);
        string report = string.Join("\n", placed.Warnings);
        Assert.Contains("model fatal: NotFinite.cmodel: constituent Id evaluated to nan at vgs=", report);
        Assert.DoesNotContain("closed its output", report);
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, (string Cmodel, string Va)> Written = new();

    /// <summary>The case's <c>.cmodel</c> and its export, once per run. The <c>VerilogA</c> component compiles the
    /// <c>.va</c> on first use and the compiler's artefact is cached on the source's content.</summary>
    private static (string Cmodel, string Va) Files(VaCase c) => Written.GetOrAdd(c.Name, _ =>
    {
        string cmodel = ComposedTestModels.WriteTemp(c.Model, c.Name + ".cmodel");
        string va = Path.ChangeExtension(cmodel, ".va");
        CmodelVerilogA.Export(cmodel, va);
        return (cmodel, va);
    });

    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static string Globals(VaCase c) => c.TempC is { } t ? $"temp = {N(t)}\n" : "";

    private static string ComposedFet(VaCase c, string cmodel) => $"ComposedFET:X1 g d 0 File=\"{cmodel}\" {c.Instance}\n";

    private static string VerilogA(VaCase c, string va) => $"VerilogA:X1 g d 0 File=\"{va}\" Pins=3 {c.Instance}\n";

    private static string Bench(double vgs, double vds) => $"""
        Vdc:VG vg 0 V={N(vgs)}
        IProbe:IPG vg g
        Vdc:VD vd 0 V={N(vds)}
        IProbe:IPD vd d

        """;

    private static (double Ig, double Id) Dc(VaCase c, string cnl)
    {
        var (lib, tb) = new CnlReader().Read(Globals(c) + cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var r = NonlinearDcEngine.Run(nl, Tight);
        Assert.True(r.Converged, $"{c.Name}: the DC solve did not converge");
        return (r.ProbeCurrents["IPG"], r.ProbeCurrents["IPD"]);
    }

    private static string SBench(double vgs, double vds) => $"""
        Port:P1 in 0 Num=1 Z=50 Ohm
        C:CB1 in g C=1e-6
        L:LB1 vg g L=1e-3
        Vdc:VG vg 0 V={N(vgs)}
        Port:P2 out 0 Num=2 Z=50 Ohm
        C:CB2 out d C=1e-6
        L:LB2 vd d L=1e-3
        Vdc:VD vd 0 V={N(vds)}

        """;

    private static DataCube Sp(VaCase c, string cnl, double[] f)
    {
        var (lib, tb) = new CnlReader().Read(Globals(c) + cnl);
        return SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), f, Tight)["S"];
    }

    private const double Vdd = 8, Rl = 50, Rs = 50;

    private static string Drive(double vs) => $"""
        V_1Tone:VS src 0 Vdc=-0.8 V={N(vs)} Freq=2e9 Phase=0
        R:RS src g R={N(Rs)}
        Vdc:VD vd 0 V={N(Vdd)}
        R:RL vd d R={N(Rl)}
        analysis HB1 type=hb Tone=2e9 MaxHarm=7 Tol=1e-12

        """;

    private static DataSet Hb(VaCase c, string cnl)
    {
        var (lib, tb) = new CnlReader().Read(Globals(c) + cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hb = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        return (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hb, nl.ResolvedGlobals));
    }

    /// <summary>Output power at the fundamental into the load (dBm), transducer gain from the source's available power
    /// (dB), and power-added efficiency from the drain supply (%).</summary>
    private static (double Pout, double Gain, double Pae) Metrics(DataSet set, double vs)
    {
        var v = set["V"];
        Complex At(string node, int h) => (Complex)v[Array.FindIndex(v.Axes[0].Labels!, n => n == node), h];
        Complex vd1 = At("d", 1), vg1 = At("g", 1);
        double pout = vd1.Magnitude * vd1.Magnitude / (2 * Rl);
        double available = vs * vs / (8 * Rs);
        double pin = (vg1 * Complex.Conjugate((vs - vg1) / Rs)).Real / 2;
        double pdc = Vdd * (Vdd - At("d", 0).Real) / Rl;
        return (10 * Math.Log10(pout / 1e-3), 10 * Math.Log10(pout / available), 100 * (pout - pin) / pdc);
    }
}
