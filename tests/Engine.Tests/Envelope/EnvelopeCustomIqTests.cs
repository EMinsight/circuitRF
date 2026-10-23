using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// A custom I/Q file driving an envelope run, stored as the Signal Designer stores it: a WORKSPACE-relative File= with
/// its sample rate in Fs=. 1001 samples at 10 MS/s last 100.1 µs, which no 1.92 MHz x 2^k step divides — the run used to
/// refuse that record; it now steps at the file's own rate x 2^k. Into a matched load the transducer gain is 0 dB.
/// </summary>
public sealed class EnvelopeCustomIqTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-envcustomiq-").FullName;

    public void Dispose() => Directory.Delete(_ws, recursive: true);

    [Fact]
    public void ARelativeOffLadderFile_RunsIntoAMatchedLoadAtZeroDbGain()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "iq"));
        var rng = new Random(3);
        var lines = Enumerable.Range(0, 1001).Select(_ =>
        {
            var v = Complex.FromPolarCoordinates(0.5 + rng.NextDouble(), 2 * Math.PI * rng.NextDouble());
            return FormattableString.Invariant($"{v.Real:G9} {v.Imaginary:G9}");
        });
        File.WriteAllLines(Path.Combine(_ws, "iq", "capture.txt"), lines);

        var (lib, tb) = new CnlReader().Read("""
            MOD_Source:S1 in 0 Fc=1e9 Pavg=0 Z=50 Carrier[1]="FILE;File=iq/capture.txt;Fs=10MHz;Off=0;P=0;Seed=1"
            R:RL in 0 R=50 Ohm
            analysis ENV1 type=env Stop=20e-6 Accuracy=draft MaxHarm=1 Probes="in" Segments=1
            """);
        ElaboratedNetlist Elaborate() => new Elaborator(lib) { BaseDirectory = _ws }.Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), Elaborate().ResolvedGlobals);
        var ds = EnvelopeRunner.Run(plan, Elaborate);

        double gt = ds["Gt_dB"].RealValues[0];
        output.WriteLine($"Gt {gt:0.00000} dB");
        Assert.True(Math.Abs(gt) < 1e-3, $"Gt {gt:0.0000} dB");
    }
}
