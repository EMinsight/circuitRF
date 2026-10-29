using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// A swept envelope splits each point into time segments, and every segment after the first elaborates its own
/// netlist. Those elaborations must take the run's BaseDirectory: a schematic's relative SnP path (kept relative,
/// unlike a .cnl's, which the reader anchors) resolved in segment 1 and was not found in segment 2.
/// </summary>
public sealed class SweptEnvelopeReferenceBaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-envbase-" + Guid.NewGuid().ToString("N"));

    public SweptEnvelopeReferenceBaseTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        File.WriteAllText(Path.Combine(_dir, "data", "load.s1p"), "# GHz S RI R 50\n0.5 0 0\n1.0 0 0\n2.0 0 0\n");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void EverySegment_ResolvesARelativeFile_AgainstTheBaseDirectory()
    {
        // No sourceDirectory: the File= path stays relative, as a schematic's does.
        var (lib, tb) = new CnlReader().Read("""
            Pin = 0
            MOD_Source:S1 n1 0 Fc=1e9 Pavg=Pin Z=50 Carrier[1]="CW;Off=0;P=0"
            IProbe:Iout n1 m
            SnP:X1 m 0 NumPorts=1 File="data/load.s1p"
            analysis ENV1 type=env Stop=40e-6 Accuracy=draft Probes="n1" Segments=2 Warmup=1e-6
            analysis SW1 type=parametric_sweep Var=Pin Values=0,3 Inner=ENV1
            """);
        var sweep = tb.Analyses.OfType<ParametricSweepAnalysis>().Single();

        var ds = ParametricSweepEngine.Run(sweep, lib, tb, baseDirectory: _dir);

        Assert.Equal(2.0, ds["__EnvSegments"].RealValues[0]);
        // 0 and 3 dBm available into the matched load the file describes.
        Assert.Equal([0.0, 3.0], ds["Pavg_dBm"].RealValues.Select(v => Math.Round(v, 2)));
    }
}
