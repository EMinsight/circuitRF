using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-27's benches: one <c>.cmodel</c> placed as a <c>ComposedFET</c> and its export placed as a
/// <c>VerilogA</c> component, on the same bench with the same instance parameters, compared at DC, in S-parameters and in
/// harmonic balance to MC-26's tolerances. The DC solves run to a residual of 1e-12 A, as MC-26's do.
/// </summary>
public static class VaBench
{
    public static readonly AnalysisSettings Tight = new() { NonlinearAbsTol = 1e-12 };

    private static readonly ConcurrentDictionary<string, (string Cmodel, string Va)> Written = new();

    /// <summary>The model's <c>.cmodel</c> and its export, once per run per name.</summary>
    public static (string Cmodel, string Va) Files(string name, ComposedModel model) => Written.GetOrAdd(name, _ =>
    {
        string cmodel = ComposedTestModels.WriteTemp(model, name + ".cmodel");
        string va = Path.ChangeExtension(cmodel, ".va");
        CmodelVerilogA.Export(cmodel, va);
        return (cmodel, va);
    });

    public static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The device on <c>g d 0</c>, or with its thermal pin on <paramref name="pin"/>.</summary>
    public static string ComposedFet(string cmodel, string instance = "", string? pin = null)
        => $"ComposedFET:X1 g d 0{(pin is null ? "" : " " + pin)} File=\"{cmodel}\" {instance}\n";

    public static string VerilogA(string va, string instance = "", string? pin = null)
        => $"VerilogA:X1 g d 0{(pin is null ? "" : " " + pin)} File=\"{va}\" Pins={(pin is null ? 3 : 4)} {instance}\n";

    public static string Bench(double vgs, double vds) => $"""
        Vdc:VG vg 0 V={N(vgs)}
        IProbe:IPG vg g
        Vdc:VD vd 0 V={N(vds)}
        IProbe:IPD vd d

        """;

    public static string SBench(double vgs, double vds) => $"""
        Port:P1 in 0 Num=1 Z=50 Ohm
        C:CB1 in g C=1e-6
        L:LB1 vg g L=1e-3
        Vdc:VG vg 0 V={N(vgs)}
        Port:P2 out 0 Num=2 Z=50 Ohm
        C:CB2 out d C=1e-6
        L:LB2 vd d L=1e-3
        Vdc:VD vd 0 V={N(vds)}

        """;

    public const double Vdd = 8, Rl = 50, Rs = 50;

    /// <summary>The 2 GHz amplifier bench. A model with trap states takes <paramref name="tol"/> = 1e-6 A: at 2 GHz the round-off of
    /// a state node's charge floors the HB residual near 1e-7 A (<c>RateTrapDynamics.NodeCapacitance</c>'s remarks).</summary>
    public static string Drive(double vs, double tol = 1e-12) => $"""
        V_1Tone:VS src 0 Vdc=-0.8 V={N(vs)} Freq=2e9 Phase=0
        R:RS src g R={N(Rs)}
        Vdc:VD vd 0 V={N(Vdd)}
        R:RL vd d R={N(Rl)}
        analysis HB1 type=hb Tone=2e9 MaxHarm=7 Tol={N(tol)}

        """;

    public static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    /// <summary>A DC solve: the probe currents and every node's voltage by name.</summary>
    public static (double Ig, double Id, Dictionary<string, double> V) Dc(string cnl, string what)
    {
        var nl = Elaborate(cnl);
        var r = NonlinearDcEngine.Run(nl, Tight);
        Assert.True(r.Converged, $"{what}: the DC solve did not converge\n{string.Join("\n", nl.Warnings)}");
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 1; i < nl.Nodes.Count; i++) if (nl.Nodes.NameOf(i) is { } name) v[name] = r.NodeVoltages[i - 1];
        // The Verilog-A device's internal nodes by the model's own names: X1:ti for __extdev_X1_n<k>.
        foreach (var ext in nl.Components.Select(c => c.Model).OfType<ExternalDeviceModel>())
            foreach (var node in ext.Descriptor.Nodes)
                if (v.TryGetValue($"__extdev_X1_n{node.Index}", out double x)) v[$"X1:{node.Label}"] = x;
        return (r.ProbeCurrents["IPG"], r.ProbeCurrents["IPD"], v);
    }

    public static DataCube Sp(string cnl, double[] f) => Sp(cnl, f, out _);

    /// <summary>S, and what the run said — a regularised solve says so there.</summary>
    public static DataCube Sp(string cnl, double[] f, out string said)
    {
        var nl = Elaborate(cnl);
        var s = SParameterEngine.Run(nl, f, Tight)["S"];
        said = string.Join("\n", nl.Warnings);
        return s;
    }

    public static DataSet Hb(string cnl) => Hb(Elaborate(cnl), cnl);

    private static DataSet Hb(ElaboratedNetlist nl, string cnl)
    {
        var (_, tb) = new CnlReader().Read(cnl);
        var hb = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        return (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hb, nl.ResolvedGlobals));
    }

    /// <summary>Output power at the fundamental into the load (dBm), transducer gain from the source's available power
    /// (dB), and power-added efficiency from the drain supply (%).</summary>
    public static (double Pout, double Gain, double Pae) Metrics(DataSet set, double vs)
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

    // ── the comparisons ───────────────────────────────────────────────────────

    /// <summary>DC: both probe currents within max(1e-9 relative, 1e-15 A), and each named node within 1e-9 of the larger
    /// of its magnitude and 1 mV (or 1 mK).</summary>
    public static void DcAgrees(string what, string bench, string cf, string va, params (string Cf, string Va)[] nodes)
    {
        var a = Dc(bench + cf, what + " ComposedFET");
        var b = Dc(bench + va, what + " VerilogA");
        foreach (var (name, x, y) in new[] { ("Id", a.Id, b.Id), ("Ig", a.Ig, b.Ig) })
            Assert.True(Math.Abs(x - y) <= Math.Max(1e-9 * Math.Abs(x), 1e-15), $"{what} {name}: ComposedFET {x:R}, VerilogA {y:R}");
        foreach (var (nc, nv) in nodes)
        {
            double x = a.V[nc], y = b.V[nv];
            Assert.True(Math.Abs(x - y) <= 1e-9 * Math.Max(Math.Abs(x), 1e-3), $"{what} {nc}: ComposedFET {x:R}, VerilogA {y:R}");
        }
    }

    /// <summary>S at one bias: |ΔS| ≤ 1e-9·max(1, |S|) at every frequency. Returns the worst |ΔS|.</summary>
    public static double SAgrees(string what, string bench, string cf, string va, double[] f)
    {
        var sa = Sp(bench + cf, f, out string saidA);
        var sb = Sp(bench + va, f, out string saidB);
        double worst = 0;
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Complex x = (Complex)sa[k, i, j], y = (Complex)sb[k, i, j];
                    worst = Math.Max(worst, (x - y).Magnitude);
                    Assert.True((x - y).Magnitude <= 1e-9 * Math.Max(1, x.Magnitude),
                                $"{what} S{i + 1}{j + 1} at {f[k]:G3} Hz: {x} vs {y}\n{saidA}\n{saidB}");
                }
        return worst;
    }

    /// <summary>One HB point: Pout and gain within 0.001 dB, PAE within 0.01 pp.</summary>
    public static (double Pout, double Gain, double Pae) HbAgrees(string what, double vs, string cf, string va, double tol = 1e-12)
    {
        var sa = Hb(Drive(vs, tol) + cf);
        var sb = Hb(Drive(vs, tol) + va);
        Assert.True(sa["Converged"].RealValues.All(c => c == 1) && sb["Converged"].RealValues.All(c => c == 1),
                    $"{what}: HB did not converge at {vs:G3} V");
        var a = Metrics(sa, vs);
        var b = Metrics(sb, vs);
        Assert.True(Math.Abs(a.Pout - b.Pout) <= 0.001, $"{what} Pout at {vs:G3} V: {a.Pout:F6} vs {b.Pout:F6} dBm");
        Assert.True(Math.Abs(a.Gain - b.Gain) <= 0.001, $"{what} gain at {vs:G3} V: {a.Gain:F6} vs {b.Gain:F6} dB");
        Assert.True(Math.Abs(a.Pae - b.Pae) <= 0.01, $"{what} PAE at {vs:G3} V: {a.Pae:F4} vs {b.Pae:F4} %");
        return a;
    }

    /// <summary>
    /// The bias point circuitRF's own freeze settled on for the <c>ComposedFET</c> on <paramref name="cnl"/> — its
    /// <see cref="ComposedCoreModel.FrozenAt"/> after <see cref="BiasFreeze"/> — as the module's <c>VgsQ</c>, <c>VdsQ</c> and
    /// <c>TjQ</c>: the intrinsic voltages, and the device temperature plus the thermal node's rise.
    /// </summary>
    public static string FrozenPoint(string cnl, AnalysisSettings? settings = null)
    {
        var nl = Elaborate(cnl);
        BiasFreeze.Ensure(nl, settings);
        var core = nl.Components.Select(c => c.Model).OfType<ComposedCoreModel>().Single();
        var at = core.FrozenAt ?? throw new InvalidOperationException("the core was not frozen");
        double tj = core.TemperatureC + (core.HasThermalNode && core.Switches.SelfHeating ? at[core.ThermalPort] : 0.0);
        return $"VgsQ={N(at[0])} VdsQ={N(at[1])} TjQ={N(tj)}";
    }

    /// <summary>What the device worker serving <paramref name="va"/> has written to its error stream — a model's
    /// <c>$warning</c> lands there, once per distinct message.</summary>
    public static string WorkerOutput(ElaboratedNetlist nl)
    {
        var ext = nl.Components.Select(c => c.Model).OfType<ExternalDeviceModel>().First();
        return (ExternalDeviceRegistry.Find(ext.ProviderName) as DeviceWorkerProvider)?.RecentErrorOutput ?? "";
    }
}

/// <summary>The devices the MC-27 gates place: <see cref="ComposedTestModels"/>' device with each kind of state.</summary>
public static class VaStateModels
{
    private static readonly string[] Inputs = ["vgs", "vds"];

    /// <summary>The drain current with a drain-lag and a gate-lag threshold shift, saturating, each zero at DC (MC-10's
    /// trapped device, both directions), and optionally falling with temperature.</summary>
    public static string IdTrapped(bool gate, bool drain, bool thermal)
    {
        string shift = (drain ? "-At*tanh((phi2-vds)/Ct)" : "") + (gate ? "-Ag*tanh((vgs-phi1)/Ct)" : "");
        return (thermal ? "beta*(1-kT*(T-25))*" : "beta*") + $"(ln(1+exp((vgs{shift}-Vto)/Vs))*Vs)^2*(1+lambda*vds)*tanh(alpha*vds)";
    }

    public static readonly (string, double)[] IdTrappedParams = [.. ComposedTestModels.IdParams, ("At", 0.3), ("Ag", 0.2), ("Ct", 5.0), ("kT", 0.004)];

    private static (string, double)[] ParamsOf(string eq) =>
        [.. IdTrappedParams.Where(p => System.Text.RegularExpressions.Regex.IsMatch(eq, $@"\b{p.Item1}\b"))];

    /// <summary>The device with a <c>CaptureEmission</c> block over the given states.</summary>
    public static ComposedModel Trapped(bool gate, bool drain, ModelTemperature? temperature = null, ModelThermal? thermal = null,
                                        params (string Name, double Value)[] parameters)
    {
        bool hasT = temperature is { Mode: TemperatureMode.Range };
        var m = ComposedTestModels.Model(temperature);
        string eq = IdTrapped(gate, drain, hasT);
        string[] inputs = [.. Inputs, .. (hasT ? (string[])["T"] : []), .. (gate ? (string[])["phi1"] : []), .. (drain ? (string[])["phi2"] : [])];
        return m with
        {
            Thermal = thermal,
            Traps = new ModelTraps
            {
                Model = CaptureEmissionTraps.ModelName,
                States = [.. (gate ? (string[])[CaptureEmissionTraps.GateLag] : []), .. (drain ? (string[])[CaptureEmissionTraps.DrainLag] : [])],
                Parameters = [.. parameters.Select(p => new KeyValuePair<string, double>(p.Name, p.Value))],
            },
            Core = m.Core with { Id = Constituent.Equation(eq, inputs, ParamsOf(eq)) },
        };
    }

    /// <summary>The device with DC/RF dispersion: an RF current of a different shape, and τ = 1 µs.</summary>
    public static ComposedModel Dispersive(ModelTemperature? temperature = null, ModelThermal? thermal = null)
    {
        var m = SelfHeating(temperature ?? ModelTemperature.Single(25), thermal);
        return m with
        {
            Core = m.Core with
            {
                IdRF = Constituent.Equation(ComposedTestModels.IdEq, Inputs,
                                            [("beta", 0.05), ("Vto", -1.2), ("Vs", 0.15), ("lambda", -0.01), ("alpha", 2.5)]),
                Dispersion = new ModelDispersion(1e-6),
            },
        };
    }

    /// <summary>The device whose drain current and charges fall with temperature, with <paramref name="thermal"/>.</summary>
    public static ComposedModel SelfHeating(ModelTemperature temperature, ModelThermal? thermal)
    {
        var m = ComposedTestModels.Model(temperature);
        if (temperature.Mode != TemperatureMode.Range) return m with { Thermal = thermal };
        string id = IdTrapped(false, false, true);
        return m with
        {
            Thermal = thermal,
            Core = m.Core with
            {
                Id = Constituent.Equation(id, ["vgs", "vds", "T"], ParamsOf(id)),
                Qg = Constituent.Equation("C0*(1+kq*(T-25))*vgs+C1*(vgs-vds)+Cx*vgs*tanh(vds)", ["vgs", "vds", "T"],
                                          [.. ComposedTestModels.QgParams, ("kq", 0.003)]),
            },
        };
    }

    /// <summary>A <c>Custom</c> drain-lag state written as <c>CaptureEmission</c>'s own law.</summary>
    public static ComposedModel Custom()
    {
        const string u = "((vds-phi2-Xref)/Vc)";
        var m = Trapped(false, true);
        return m with
        {
            Traps = new ModelTraps
            {
                Model = CustomTraps.ModelName, States = ["phi2"],
                Parameters = [new("TauEmit2", 1e-3), new("TauCapt2", 1e-8), new("Xref", 1), new("Vc", 0.05)],
                Expressions =
                [
                    new("phi2.Dynamics", $"(vds-phi2)/TauEmit2+(Xref/TauCapt2)*(if({u}<20,exp({u}),exp(20)*(1+{u}-20))-exp(-Xref/Vc))"),
                    new("phi2.Dc", "vds"),
                ],
            },
        };
    }

    /// <summary>Both states through the <c>Shift</c> coupling: Id takes none of them.</summary>
    public static ComposedModel Shift() => ComposedTestModels.Model() with
    {
        Traps = new ModelTraps
        {
            Model = CaptureEmissionTraps.ModelName, Coupling = ModelTraps.Shift,
            States = [CaptureEmissionTraps.GateLag, CaptureEmissionTraps.DrainLag],
            Parameters = [new(CaptureEmissionTraps.ShiftDrain, 0.05), new(CaptureEmissionTraps.ShiftGate, 0.1)],
        },
    };
}
