using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// Brief MC-30 R-mc30-6: a device worker that EXITS mid-run costs the point it exited at, not the run. The attempt is
/// reported with the worker's exit code and last error line, the worker is restarted and the point runs again; a second
/// exit at the same point stops the run with that message. The worker is <c>tools/DeviceWorkerExample</c>, told to die.
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class WorkerExitMidRunTests : IDisposable
{
    private const string ProviderName = "ExitingKit";

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "crf-exit-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        ExternalDeviceRegistry.Clear();
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A kit whose worker is the reference worker run with <paramref name="arguments"/>.</summary>
    private void Kit(params string[] arguments)
    {
        ExternalDeviceRegistry.Clear();
        string kitDir = Path.Combine(_workspace, "pdk", ProviderName);
        Directory.CreateDirectory(kitDir);
        string args = string.Join(", ", arguments.Select(a => System.Text.Json.JsonSerializer.Serialize(a)));
        File.WriteAllText(Path.Combine(kitDir, DeviceWorkerManifest.FileName), $$"""
            { "provider": "{{ProviderName}}",
              "workers": [ { "platform": "any", "command": {{System.Text.Json.JsonSerializer.Serialize(WorkerPath)}},
                             "arguments": [ {{args}} ] } ] }
            """);
        ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver([Path.Combine(_workspace, "pdk")]));
    }

    private static string WorkerPath
    {
        get
        {
            string dir = typeof(WorkerExitMidRunTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "DeviceWorkerExampleDir").Value!;
            return Path.Combine(Path.GetFullPath(dir),
                "DeviceWorkerExample" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
        }
    }

    private const string Bench = """
        Vg = 1
        Vdc:VG   g 0  Vdc=Vg
        Vdc:VD   dd 0 Vdc=5
        IProbe:IPd dd d
        ExtDevice:X1  g d 0  Provider=ExitingKit Type=example_fet_v1
        analysis DC1 type=dc
        analysis SW1 type=parametric_sweep Var=Vg Values=1,1.2,1.4,1.6,1.8,2,2.2,2.4 Inner=DC1
        """;

    private static (RfCore.Data.DataSet Data, ElaboratedNetlist Diagnostics) Sweep()
    {
        var (lib, tb) = new CnlReader().Read(Bench);
        var diagnostics = new Elaborator(lib).Elaborate(tb);
        var sweep = tb.Analyses.OfType<ParametricSweepAnalysis>().Single();
        return (ParametricSweepEngine.Run(sweep, lib, tb, diagnosticsInto: diagnostics), diagnostics);
    }

    [Fact]
    public void AnExit_FailsTheAttempt_RestartsTheWorker_AndTheRunCompletes()
    {
        Kit();
        var clean = Sweep().Data["I"].RealValues;

        Kit("--exit-on-eval", "12");
        var (data, diagnostics) = Sweep();

        Assert.All(data["Converged"].RealValues, c => Assert.Equal(1.0, c));
        Assert.Equal(clean, data["I"].RealValues);
        var notes = diagnostics.Warnings.Where(w => w.Contains("exited with code 3: crashed on eval 12", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(notes);
        Assert.All(notes, w => Assert.Contains("the worker was restarted and the point run again", w));
    }

    [Fact]
    public void ASecondExitAtTheSamePoint_StopsTheRun_WithTheWorkersLastLine()
    {
        Kit("--exit-when-vgs-above", "1.9");
        var ex = Assert.Throws<ExternalDeviceException>(() => Sweep());
        Assert.Contains("stopped at Vg = 2", ex.Message);
        Assert.Contains("exited with code 3: crashed: vgs above 1.9 V", ex.Message);
    }
}
