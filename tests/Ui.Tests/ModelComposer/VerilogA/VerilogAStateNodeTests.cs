using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.ModelComposer.VerilogA.VaBench;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-27 R-mc27-1, R-mc27-2: the dispersion node and the trap states as the module writes them, each
/// against the same <c>.cmodel</c> placed as a <c>ComposedFET</c> — DC, S at one bias and one HB point. Skips with a reason
/// when no compiler or worker is reachable.
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class VerilogAStateNodeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public VerilogAStateNodeTests(ITestOutputHelper output)
    {
        _output = output;
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    private static readonly Dictionary<string, (ComposedModel Model, string Globals)> Cases = new()
    {
        ["Dispersion"] = (VaStateModels.Dispersive(), ""),
        ["GateLag"] = (VaStateModels.Trapped(gate: true, drain: false), ""),
        ["DrainLag"] = (VaStateModels.Trapped(gate: false, drain: true), ""),
        // Both states, thermally activated emission, over a temperature range and run away from Tref.
        ["BothActivated"] = (VaStateModels.Trapped(true, true, ModelTemperature.Range(0, 150), null,
                                                   ("Ea1", 0.3), ("Ea2", 0.5), ("Tref", 40)), "temp = 60\n"),
        ["Custom"] = (VaStateModels.Custom(), ""),
        ["Shift"] = (VaStateModels.Shift(), ""),
    };

    public static IEnumerable<object[]> Names() => Cases.Keys.Select(k => new object[] { k });

    [CompilerTheory]
    [MemberData(nameof(Names))]
    public void Dc_SAndHb_AgreeWithTheComposedFet(string name)
    {
        var (model, globals) = Cases[name];
        var (cmodel, va) = Files("States" + name, model);
        string cf = ComposedFet(cmodel), placed = VaBench.VerilogA(va);

        foreach (double vgs in (double[])[-1.5, -0.6, 0.3])
            foreach (double vds in (double[])[0.5, 6])
                DcAgrees($"{name} ({vgs}, {vds})", globals + Bench(vgs, vds), cf, placed);

        // 10 kHz and 10 GHz lie on the two sides of the dispersion's 1/τ and above every trap's emission rate.
        double worst = SAgrees(name, globals + SBench(-0.6, 6), cf, placed, [1e4, 1e9, 1e10]);
        var hb = HbAgrees(name, 1.5, globals + cf, globals + placed, model.Traps is null ? 1e-12 : 1e-6);
        _output.WriteLine($"{name}: worst |ΔS| {worst:E2}; at 1.5 V Pout {hb.Pout:F3} dBm, gain {hb.Gain:F3} dB");
    }
}
