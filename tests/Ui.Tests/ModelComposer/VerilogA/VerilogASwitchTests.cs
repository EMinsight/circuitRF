using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Tests.Composed;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.ModelComposer.VerilogA.VaBench;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// brief-modelcomposer-27 R-mc27-4: the switches that need no bias point, each alone and the two lags together, set the
/// same on both placements — DC, S at one bias and one HB point — and a switch naming a part the model lacks changes
/// nothing.
/// </summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class VerilogASwitchTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public VerilogASwitchTests(ITestOutputHelper output)
    {
        _output = output;
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    private static readonly ModelTemperature Range = ModelTemperature.Range(0, 150);

    /// <summary>Both trap states and self-heating; dispersion is never combined with traps, so it has its own device.</summary>
    private static readonly Dictionary<string, ComposedModel> Models = new()
    {
        ["Trapped"] = VaStateModels.Trapped(true, true, Range, ModelThermal.Single(50, 1e-9)),
        ["Dispersive"] = VaStateModels.Dispersive(Range, ModelThermal.Single(50, 1e-9)),
    };

    [CompilerTheory]
    [InlineData("Trapped", "Charge=0")]
    [InlineData("Trapped", "SelfHeating=0")]
    [InlineData("Trapped", "GateLag=0")]
    [InlineData("Trapped", "DrainLag=0")]
    [InlineData("Trapped", "GateLag=0 DrainLag=0")]
    [InlineData("Dispersive", "Dispersion=0")]
    public void ASwitch_DoesWhatTheComposedFetDoes(string model, string switches)
    {
        var (cmodel, va) = Files("Switch" + model, Models[model]);
        string cf = ComposedFet(cmodel, switches), placed = VaBench.VerilogA(va, switches);
        string what = $"{model} {switches}";
        DcAgrees(what, Bench(-0.6, 6), cf, placed, ("X1.t", "X1:ti"));
        double worst = SAgrees(what, SBench(-0.6, 6), cf, placed, [1e4, 1e9, 1e10]);
        var hb = HbAgrees(what, 1.5, cf, placed, Models[model].Traps is null ? 1e-12 : 1e-6);

        // Not vacuous: the switch moves the answer.
        var on = Metrics(Hb(Drive(1.5, 1e-6) + ComposedFet(cmodel)), 1.5);
        Assert.True(Math.Abs(on.Pout - hb.Pout) > 0.01 || Math.Abs(on.Pae - hb.Pae) > 0.1,
                    $"{what}: Pout {hb.Pout:F4} dBm and PAE {hb.Pae:F3} % against {on.Pout:F4} and {on.Pae:F3} with every switch on");
        _output.WriteLine($"{what}: worst |ΔS| {worst:E2}; Pout {hb.Pout:F3} dBm (on: {on.Pout:F3})");
    }

    /// <summary>A switch naming a part the model lacks exists on the module and changes nothing (the counterpart of
    /// R-mc21-2).</summary>
    [CompilerTheory]
    [InlineData("Trapped", "Dispersion=0")]
    [InlineData("Equation", "SelfHeating=0 GateLag=0 DrainLag=0 Dispersion=0")]
    public void ASwitchForAPartTheModelLacks_ChangesNothing(string model, string switches)
    {
        var m = model == "Equation" ? ComposedTestModels.Model() : Models[model];
        var (cmodel, va) = Files("Switch" + model, m);
        DcAgrees($"{model} {switches}", Bench(-0.6, 6), ComposedFet(cmodel), VaBench.VerilogA(va, switches));
        SAgrees($"{model} {switches}", SBench(-0.6, 6), ComposedFet(cmodel), VaBench.VerilogA(va, switches), [1e9, 1e10]);
    }
}
