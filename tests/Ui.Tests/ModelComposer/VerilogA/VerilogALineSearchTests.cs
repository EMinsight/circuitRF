using System;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.ModelComposer.VerilogA.VaBench;

namespace CircuitRF.Ui.Tests.ModelComposer.VerilogA;

/// <summary>
/// Brief MC-30: an exported model's internal nodes are opaque to the HB line search, so it accepts a trial that decreases
/// either the self-admittance-scaled norm or ‖F‖ in amperes (<see cref="HbNewton.JudgeFor"/>). Judged in amperes alone
/// the trap node vetoed step after step — 26 device evaluations per Newton iteration on the loadpull bench, against the
/// ComposedFET twin's 1.9 — and judged by the scaled norm alone the solve stalled short of a tight tolerance. A COUNTER,
/// not a clock: the same load on both placements, and the Verilog-A side may not need more than twice the twin's
/// evaluations.
/// </summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class VerilogALineSearchTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    public VerilogALineSearchTests(ITestOutputHelper output)
    {
        _output = output;
        if (CompilerAvailability.WorkerDirectory() is { } dir) DeviceWorkerManifest.ToolsDirectory = dir;
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.ResetResolved();
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
    }

    /// <summary>
    /// The bench the defect was found on: the shipped synthetic GaN model, its network, traps and self-heating inside the
    /// exported module, at one load of the GanLoadpull grid driven to 3 dB compression. Before MC-30 the Verilog-A side took
    /// 22,124 device evaluations for that one load against the ComposedFET's 248; it now takes fewer than the twin.
    /// </summary>
    [CompilerFact]
    public void OneLoadToCompression_TakesAboutTheTwinsEvaluations()
    {
        string root = RepoRoot(), dir = Directory.CreateTempSubdirectory("crf-mc30-ls-").FullName;
        try
        {
            string gam = Path.Combine(dir, "load.gam");
            File.WriteAllText(gam, "# gamma Z0=50 mag_ang\n0.2915  30.96\n");
            string models = Path.Combine(root, "examples", "Model Composer", "synthetic-gan");

            (int Evaluations, int Converged, int Rungs) Load(string device)
            {
                var nl = Elaborate($"""
                    RFfreq = 2 GHz
                    VGS = -0.8
                    VDS = 8
                    temp = 25
                    Tuner:SourceTuner1  Vin  0  Zdefault=1e-6  Z0=50  BiasTee="on"  Vbias=VGS  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6
                    {device}
                    Tuner:LoadTuner1  Vout  0  Zdefault=1e-6  Z0=50  BiasTee="on"  Vbias=VDS  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6
                    analysis LP1 type=loadpull Tone="RFfreq" ToneUnit=GHz MaxHarm=5 LoadTuner=LoadTuner1 SourceTuner=SourceTuner1 Grid="{gam}" Sweep=Load TuneHarm=1 Compression=3 GainType=Gt PinStart=-4 PinStep=1 PinMax=26 Tickle=-50 MaxIter=100 FFTOverSample=1 Tol=1e-6
                    """);
                var tb = Bench!;
                var p = LoadpullEngine.Resolve(tb.Analyses.OfType<CircuitRF.Core.Design.LoadpullAnalysis>().Single(),
                                               nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                var engine = new LoadpullEngine(nl, tb);
                var ctx = engine.PrepareContext(p);
                HbNewton.ResetEvaluations();
                var r = engine.RunOneTermination(p, ctx, p.Grid.Points[0].Z, 0);
                return (HbNewton.Evaluations, r.PinSteps.Count(s => s.Converged), r.PinSteps.Count);
            }

            var twin = Load($"ComposedFET:X1  Vin  Vout  0  File=\"{Path.Combine(models, "gan_4x125.cmodel")}\"  N=8");
            var exported = Load($"VerilogA:X1  Vin  Vout  0  File=\"{Path.Combine(models, "gan_4x125.va")}\"  Pins=3  N=8");
            _output.WriteLine($"one load: ComposedFET {twin}, Verilog-A {exported}");
            Assert.Equal(twin.Rungs, twin.Converged);
            Assert.Equal(exported.Rungs, exported.Converged);
            Assert.True(exported.Evaluations <= 2 * twin.Evaluations,
                        $"Verilog-A {exported.Evaluations} evaluations against the ComposedFET's {twin.Evaluations}");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    private CircuitRF.Core.Design.TestBench? Bench;

    private CircuitRF.Core.Elaboration.ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CircuitRF.Core.Netlist.CnlReader().Read(cnl);
        Bench = tb;
        return new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb);
    }

    private static string RepoRoot()
    {
        for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "circuitRF.slnx"))) return d;
        throw new InvalidOperationException("repo root not found");
    }

    [CompilerFact]
    public void TheJudge_IsScaledOrPlain_OnlyWhereAnExternalDeviceSits()
    {
        var model = VaStateModels.Trapped(true, true, ModelTemperature.Range(0, 150), ModelThermal.Single(50, 1e-9));
        var (cmodel, va) = Files("LineSearchTrapped", model);

        HbNewton.LineSearchJudge JudgeOf(string device)
        {
            var nl = Elaborate(Drive(1, 1e-9) + device);
            int[] all = [.. Enumerable.Range(1, nl.Nodes.Count)];
            return HbNewton.JudgeFor(nl, all);
        }

        Assert.Equal(HbNewton.LineSearchJudge.Scaled, JudgeOf(ComposedFet(cmodel)));
        Assert.Equal(HbNewton.LineSearchJudge.ScaledOrPlain, JudgeOf(VaBench.VerilogA(va)));
    }
}
