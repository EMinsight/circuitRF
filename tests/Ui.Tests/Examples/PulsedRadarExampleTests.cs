using CircuitRF.Cli;
using CircuitRF.Design.Circuit;
using CircuitRF.Ui.Tests.ModelComposer;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

/// <summary>
/// Brief ENV-PCW gate 10 and §5's device acceptance, on <c>examples/Pulsed Radar</c> as a user opens it: the pulse bench
/// reproduces the numbers its README states, and the radar device follows the truth that made its data on the CW bench.
/// Minutes in Debug (the pulse run is 65,000 envelope steps): opt-in.
/// </summary>
public sealed class PulsedRadarExampleTests(ITestOutputHelper output)
{
    private static string Ws => PulsedRadarExampleAuthoring.Workspace;

    [Fact]
    [Trait("Category", "Benchmark")]
    public void ThePulseBench_ReproducesItsReadme()
    {
        string sch = Path.Combine(Ws, PulsedRadarExampleAuthoring.PulseBench, "schematic", PulsedRadarExampleAuthoring.PulseBench + ".csch");
        string dir = Directory.CreateTempSubdirectory("crf-radar-run-").FullName;
        try
        {
            string npy = Path.Combine(dir, "run.npy");
            Assert.Equal(0, CliEntry.Run(["env", sch, "-o", npy]));
            using var stream = File.OpenRead(npy);
            var ds = DataSetImporter.ImportNpy(stream);
            double[] top = Cube(ds, "PulseTop_dBm"), droop = Cube(ds, "PulseDroop_dB");
            var v = Get(ds, "V");
            double h = v.Axes[0].Values[1] - v.Axes[0].Values[0];
            int tj = Array.IndexOf(v.Axes[1].Labels!, "tj");
            double Rise(double t) => ((System.Numerics.Complex)v[(int)Math.Round(t / h), tj, 0]).Real;
            double heat = Rise(110e-6) - Rise(9e-6);
            output.WriteLine($"top {string.Join(", ", top.Select(x => x.ToString("F3")))} dBm; droop {string.Join(", ", droop.Select(x => x.ToString("F4")))} dB; " +
                             $"pulse 1 heats {heat:F1} K; pulse to pulse {Cube(ds, "PulseToPulse_dB")[0]:F4} dB");
            // the README's numbers
            Assert.Equal(3, top.Length);
            Assert.Equal(Readme.TopDbm, top[0], 0.05);
            Assert.Equal(Readme.Droop1Db, droop[0], 0.01);
            Assert.Equal(Readme.Droop3Db, droop[2], 0.01);
            Assert.Equal(Readme.HeatK, heat, 1.0);
            // §5: a 100 µs pulse heats the junction 30-60 K across the top
            Assert.InRange(heat, 30, 60);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>
    /// The CW bench with the model and with the truth in its place: Pout within 0.5 dB and PAE within 3 points, the first
    /// model's own acceptance, from small signal to the 34 dBm design drive (measured: 0.23 dB and 0.8 points at worst).
    /// The design drive is where the gate swings past every grid's edge and the drain runs hot with its traps charged; the
    /// data reaches both (src/Ui/RESOLVED.md, ENV-PCW), and a refit that loses either is seen here.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheRadarDevice_FollowsItsTruth_OnTheCwBench()
    {
        string model = Path.Combine(Ws, PulsedRadarExampleAuthoring.Model).Replace('\\', '/');
        string bench = PulsedRadarExampleAuthoring.PulsedPA.Replace("labelednets in g d out VDD tj", "", StringComparison.Ordinal);
        const string device = "ComposedFET:X1  g  d  0  tj  File=\"radar-gan/radar_gan_8x250.cmodel\"  N=48";
        Assert.Contains(device, bench, StringComparison.Ordinal);
        var g = SyntheticGan.Radar8x250;
        var m = Sweep(bench.Replace("File=\"radar-gan/radar_gan_8x250.cmodel\"", $"File=\"{model}\"", StringComparison.Ordinal));
        var t = Sweep(SyntheticGan.Globals(null, g).Replace("\ntemp", "\n;temp", StringComparison.Ordinal) + SyntheticGan.Device(48, null, g) +
                      bench.Replace(device, "gan:X1 g d 0", StringComparison.Ordinal));
        for (int k = 0; k < t.Pin.Length; k++)
            output.WriteLine($"Pin {t.Pin[k]}: Pout {t.Pout[k]:F2} / {m.Pout[k]:F2} dBm  PAE {t.Pae[k]:F1} / {m.Pae[k]:F1} %");
        for (int k = 0; k < t.Pin.Length; k++)
        {
            Assert.True(Math.Abs(m.Pout[k] - t.Pout[k]) <= 0.5, $"Pin {t.Pin[k]}: ΔPout {m.Pout[k] - t.Pout[k]:F2} dB");
            Assert.True(Math.Abs(m.Pae[k] - t.Pae[k]) <= 3, $"Pin {t.Pin[k]}: ΔPAE {m.Pae[k] - t.Pae[k]:F1} pp");
        }
    }

    private (double[] Pin, double[] Pout, double[] Pae) Sweep(string cnl)
    {
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, Ws, Ws), new CircuitEvaluationRequest());
        Assert.True(r.Status == RunStatus.Success, $"{r.Status}: {r.StatusMessage}");
        var ds = r.Results.First(x => x.Data.Contains("V")).Data;
        double[] pin = [.. ds["V"].Axes[0].Values];
        double[] Measure(string name) => r.GroupedResults!.CubesIn(DataSet.MeasurementsGroup)[name].RealValues;
        return (pin, Measure("Pout_dBm"), Measure("PAE"));
    }

    /// <summary>A cube of a run read back from its file (grouped under its analysis) or straight from the run.</summary>
    private static DataCube Get(DataSet ds, string name) => ds.Contains(name) ? ds[name] : ds["ENV1." + name];

    private static double[] Cube(DataSet ds, string name) => Get(ds, name).RealValues;

    /// <summary>What the README states, measured on the merged build.</summary>
    private static class Readme
    {
        public const double TopDbm = 49.07, Droop1Db = 0.259, Droop3Db = 0.303, HeatK = 47.4;
    }
}
