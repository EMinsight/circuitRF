// A custom I/Q file as a MOD_Source signal (the Signal Designer's Load Custom).
//
//   C1  A file with no header plays at the rate given as Fs=, which survives the netlist text; its bandwidth is
//       measured from the file, not the 20 MHz a carrier defaults to; its record is N / fs.
//   C2  A record the 1.92 MHz ladder does not divide is sampled at the file's own rate x 2^k: whole samples.
//   C3  Clipping applies to a file as it does to a generated carrier.
//   C4  Rewriting the file changes the cache key, so an edited file is never served from the cache.
//   C5  Resolving the File= path leaves every other field of the description as written.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public sealed class CustomIqTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-customiq-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Band-limited noise, flat over +-<paramref name="edgeHz"/>, as two columns with NO header.</summary>
    internal static string WriteHeaderless(string dir, double fs, int n, double edgeHz, int seed = 1)
    {
        var rng = new Random(seed);
        var spec = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            double f = (k < (n + 1) / 2 ? k : k - n) * fs / n;
            if (Math.Abs(f) <= edgeHz) spec[k] = Complex.FromPolarCoordinates(1, 2 * Math.PI * rng.NextDouble());
        }
        var x = Fft.Inverse(spec);
        string path = Path.Combine(dir, $"capture{seed}.txt");
        File.WriteAllLines(path, x.Select(v => FormattableString.Invariant($"{v.Real:G9} {v.Imaginary:G9}")));
        return path;
    }

    private static SignalSpec Spec(string path, double? fs, double? clip = null)
        => new() { Carriers = [new CarrierSpec { Standard = CarrierStandard.File, FilePath = path, SampleRateHz = fs }], ClipPaprDb = clip };

    [Fact]
    public void C1_AHeaderlessFile_PlaysAtTheGivenRate_WithItsOwnBandwidth()
    {
        string path = WriteHeaderless(_dir, 100e6, 12_345, 15e6);
        Assert.Throws<FormatException>(() => SignalComposer.Generate(Spec(path, null)));

        var text = SignalSpec.FormatCarrier(Spec(path, 100e6).Carriers[0]);
        Assert.Contains(";Fs=100MHz", text);
        var back = SignalSpec.ParseCarrier(text);
        Assert.Equal(100e6, back.SampleRateHz);

        var sig = SignalComposer.Generate(Spec(path, 100e6));
        Assert.Equal(12_345 / 100e6, sig.RecordSeconds, 15);
        // 99 % of flat +-15 MHz noise lies within +-14.85 MHz: twice that over 0.9 is 33 MHz, not the 20 MHz default
        Assert.InRange(sig.Carriers[0].Spec.BandwidthHz, 32.5e6, 34e6);
    }

    [Fact]
    public void C2_AnOffLadderRecord_IsSampledAtTheFilesRateTimesAPowerOfTwo()
    {
        string path = WriteHeaderless(_dir, 100e6, 12_345, 15e6);
        var sig = SignalComposer.Generate(Spec(path, 100e6));
        Assert.False(SignalRate.Divides(sig, 122.88e6));
        Assert.Equal(200e6, SignalRate.AtLeast(150e6, [sig]));
        double r = sig.SuggestedCompositeRateHz();
        Assert.True(SignalRate.Divides(sig, r), $"{r / 1e6} MS/s");
        Assert.Equal(12_345 * (int)(r / 100e6), sig.LengthAt(r));
    }

    [Fact]
    public void C3_ClippingAppliesToAFile()
    {
        string path = WriteHeaderless(_dir, 100e6, 12_345, 15e6);
        var raw = SignalComposer.Generate(Spec(path, 100e6)).Carriers[0].Native;
        var clipped = SignalComposer.Generate(Spec(path, 100e6, clip: 7)).Carriers[0].Native;
        Assert.True(SignalMeasure.PaprDb(raw, 0) > 9, $"unclipped peak {SignalMeasure.PaprDb(raw, 0):0.00} dB");
        Assert.InRange(SignalMeasure.PaprDb(clipped, 0), 0, 7.05);
    }

    [Fact]
    public void C4_RewritingTheFile_ChangesTheCacheKey()
    {
        string path = WriteHeaderless(_dir, 100e6, 4096, 15e6);
        string before = Spec(path, 100e6).CacheKey();
        File.WriteAllLines(path, File.ReadAllLines(path).Append("0 0"));
        Assert.NotEqual(before, Spec(path, 100e6).CacheKey());
    }

    [Fact]
    public void C5_MapFilePath_ChangesOnlyThePath()
    {
        string mapped = SignalSpec.MapFilePath("FILE;File=iq/a.txt;Fs=61.44MHz;Off=5MHz;P=0;Seed=1", f => "/ws/" + f);
        Assert.Equal("FILE;File=/ws/iq/a.txt;Fs=61.44MHz;Off=5MHz;P=0;Seed=1", mapped);
    }
}
