// ================================================================
//  ModulatedSignal.cs  —  a generated multi-carrier signal
//
//  Holds every carrier SEPARATELY at its native rate (the envelope
//  engine gives widely spaced carriers their own fundamental and needs
//  each carrier's own narrow baseband), with relative powers applied
//  so the composite has unit mean power.  Any carrier, or the
//  composite, can be requested at any rate whose product with the
//  record length is a whole number of samples: resampling is exact
//  (band-limited) because every record is periodic.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace RfCore.Signals
{
    public sealed class CarrierRecord
    {
        public required CarrierSpec Spec { get; init; }
        /// <summary>Periodic baseband at <see cref="NativeRateHz"/>, scaled to the carrier's share of the unit total.</summary>
        public required Complex[] Native { get; init; }
        public required double NativeRateHz { get; init; }
        public required double OccupiedBwHz { get; init; }
        /// <summary>Carrier offset snapped to the record's frequency grid (1/record) so the composite stays periodic.</summary>
        public required double OffsetHz { get; init; }
        public double RelativePower { get; init; } = 1;
    }

    public sealed class ModulatedSignal
    {
        public SignalSpec Spec { get; }
        public IReadOnlyList<CarrierRecord> Carriers { get; }
        public double RecordSeconds { get; }

        private readonly ConcurrentDictionary<(int, long), Complex[]> _resampled = new();

        public ModulatedSignal(SignalSpec spec, IReadOnlyList<CarrierRecord> carriers, double recordSeconds)
        {
            Spec = spec; Carriers = carriers; RecordSeconds = recordSeconds;
        }

        public double InstantaneousBandwidthHz
        {
            get
            {
                double lo = Carriers.Min(c => c.OffsetHz - c.Spec.BandwidthHz / 2);
                double hi = Carriers.Max(c => c.OffsetHz + c.Spec.BandwidthHz / 2);
                return hi - lo;
            }
        }

        /// <summary>Samples in the record at <paramref name="rateHz"/>; throws if not a whole number.</summary>
        public int LengthAt(double rateHz)
        {
            double n = RecordSeconds * rateHz;
            long r = (long)Math.Round(n);
            if (Math.Abs(n - r) > 1e-6 * Math.Max(1, n))
                throw new ArgumentException($"rate {rateHz / 1e6:0.###} MHz does not give a whole number of samples over the {RecordSeconds * 1e3:0.###} ms record");
            return (int)r;
        }

        /// <summary>Carrier <paramref name="index"/> (at its own centre, no offset) resampled to <paramref name="rateHz"/>.</summary>
        public Complex[] CarrierAt(int index, double rateHz)
        {
            int n = LengthAt(rateHz);
            return _resampled.GetOrAdd((index, n), _ => Fft.ResamplePeriodic(Carriers[index].Native, n));
        }

        /// <summary>
        /// Sum of the carriers in <paramref name="indices"/> (all when null), each shifted by its offset minus
        /// <paramref name="centreHz"/>, at <paramref name="rateHz"/>.
        /// </summary>
        public Complex[] Composite(double rateHz, double centreHz = 0, IReadOnlyList<int>? indices = null)
        {
            int n = LengthAt(rateHz);
            var y = new Complex[n];
            foreach (int i in indices ?? Enumerable.Range(0, Carriers.Count).ToList())
            {
                var x = CarrierAt(i, rateHz);
                double w = 2 * Math.PI * (Carriers[i].OffsetHz - centreHz) / rateHz;
                for (int k = 0; k < n; k++)
                    y[k] += x[k] * Complex.FromPolarCoordinates(1.0, w * k);
            }
            return y;
        }

        /// <summary>
        /// A WINDOW of the composite: <paramref name="count"/> samples at <paramref name="rateHz"/> starting at record
        /// time <paramref name="startSample"/>/rate (any integer, negative or past the end: the record is periodic),
        /// each carrier shifted by its offset minus <paramref name="centreHz"/>. Interpolated from the carriers' native
        /// records with a Kaiser-windowed sinc (±<see cref="InterpHalfWidth"/> native samples, ~-100 dB stopband), so a
        /// short run never pays for resampling the whole record — the cost is O(count x taps), not O(record log record).
        /// </summary>
        public Complex[] CompositeWindow(double rateHz, double centreHz, long startSample, int count, IReadOnlyList<int>? indices = null)
        {
            var y = new Complex[count];
            foreach (int ci in indices ?? Enumerable.Range(0, Carriers.Count).ToList())
            {
                var c = Carriers[ci];
                var x = c.Native;
                int n = x.Length;
                double ratio = c.NativeRateHz / rateHz;
                double w = 2 * Math.PI * (c.OffsetHz - centreHz) / rateHz;
                double periodSamples = RecordSeconds * rateHz;
                bool exact = Math.Abs(ratio - Math.Round(ratio)) < 1e-12 && Math.Abs(ratio - 1) < 1e-12;
                for (int m = 0; m < count; m++)
                {
                    long gi = startSample + m;
                    double u = gi * ratio;                                     // position in native samples
                    u -= Math.Floor(u / n) * n;
                    Complex v;
                    if (exact) v = x[(int)Math.Round(u) % n];
                    else
                    {
                        int k0 = (int)Math.Floor(u);
                        double frac = u - k0;
                        if (frac < 1e-12) v = x[k0 % n];
                        else
                        {
                            v = Complex.Zero;
                            for (int j = -InterpHalfWidth + 1; j <= InterpHalfWidth; j++)
                            {
                                double d = frac - j;                            // u - (k0 + j)
                                double s = Math.Sin(Math.PI * d) / (Math.PI * d);
                                double kw = Kaiser(d / InterpHalfWidth);
                                int idx = ((k0 + j) % n + n) % n;
                                v += x[idx] * (s * kw);
                            }
                        }
                    }
                    // carrier offset; phase reduced modulo the record so long runs keep full precision
                    double ph = w * (gi - Math.Floor(gi / periodSamples) * periodSamples);
                    y[m] += v * Complex.FromPolarCoordinates(1.0, ph);
                }
            }
            return y;
        }

        public const int InterpHalfWidth = 24;
        private const double KaiserBeta = 10.0;
        private static readonly double KaiserNorm = BesselI0(KaiserBeta);

        private static double Kaiser(double r)
        {
            if (r <= -1 || r >= 1) return 0;
            return BesselI0(KaiserBeta * Math.Sqrt(1 - r * r)) / KaiserNorm;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 60; k++)
            {
                term *= q / (k * k);
                sum += term;
                if (term < 1e-17 * sum) break;
            }
            return sum;
        }

        /// <summary>Smallest 1.92 MHz x 2^k rate holding the composite plus +-2 channel bandwidths of regrowth.</summary>
        public double SuggestedCompositeRateHz()
        {
            double lo = Carriers.Min(c => c.OffsetHz - 2.5 * c.Spec.BandwidthHz);
            double hi = Carriers.Max(c => c.OffsetHz + 2.5 * c.Spec.BandwidthHz);
            double need = 2 * Math.Max(Math.Abs(lo), Math.Abs(hi));
            double r = 1.92e6;
            while (r < need) r *= 2;
            return Math.Max(r, Carriers.Max(c => c.NativeRateHz));
        }
    }

    public static class SignalComposer
    {
        public static ModulatedSignal Generate(SignalSpec spec, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (spec.Carriers.Count == 0) throw new ArgumentException("a signal needs at least one carrier");
            var file = spec.Carriers.FirstOrDefault(c => c.Standard == CarrierStandard.File);
            IqFile? iq = file is null ? null : IqFile.Read(file.FilePath!);
            double record = iq?.DurationSeconds ?? Math.Max(0.5e-3, Math.Round(spec.RecordSeconds / 0.5e-3) * 0.5e-3);
            int halfMs = (int)Math.Round(record / 0.5e-3);

            var raw = new List<(CarrierSpec s, Complex[] x, double rate, double occ)>();
            for (int i = 0; i < spec.Carriers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = spec.Carriers[i];
                switch (c.Standard)
                {
                    case CarrierStandard.Lte:
                    case CarrierStandard.Nr:
                    {
                        if (iq is not null && Math.Abs(iq.DurationSeconds / 0.5e-3 - Math.Round(iq.DurationSeconds / 0.5e-3)) > 1e-6)
                            throw new ArgumentException("an imported I/Q file mixed with generated carriers must last a whole number of 0.5 ms slots");
                        var nm = OfdmGenerator.Numerology(c);
                        var x = OfdmGenerator.Generate(c, halfMs, spec.WindowFraction, spec.ShapingFloorDbc);
                        if (spec.ClipPaprDb is double t)
                            OfdmGenerator.Clip(x, nm.SampleRateHz, nm.OccupiedBwHz, t);
                        raw.Add((c, x, nm.SampleRateHz, nm.OccupiedBwHz));
                        break;
                    }
                    case CarrierStandard.Cw:
                    {
                        double rate = 1.92e6;
                        int n = (int)Math.Round(record * rate);
                        raw.Add((c, Enumerable.Repeat(Complex.One, n).ToArray(), rate, 0));
                        break;
                    }
                    default:
                    {
                        var f = IqFile.Read(c.FilePath ?? throw new ArgumentException("a FILE carrier needs File=<path>"));
                        var x = (Complex[])f.Samples.Clone();
                        OfdmGenerator.Normalise(x);
                        raw.Add((c, x, f.SampleRateHz, f.ChannelBandwidthHz > 0 ? f.ChannelBandwidthHz * 0.9 : f.SampleRateHz / 2));
                        break;
                    }
                }
                progress?.Report((i + 1.0) / spec.Carriers.Count);
            }

            double totalLin = raw.Sum(r => Math.Pow(10, r.s.PowerDb / 10));
            var recs = raw.Select(r =>
            {
                double share = Math.Pow(10, r.s.PowerDb / 10) / totalLin;
                double a = Math.Sqrt(share);
                var x = r.x.Select(v => v * a).ToArray();
                double grid = 1.0 / record;
                return new CarrierRecord
                {
                    Spec = r.s, Native = x, NativeRateHz = r.rate, OccupiedBwHz = r.occ,
                    OffsetHz = Math.Round(r.s.OffsetHz / grid) * grid, RelativePower = share,
                };
            }).ToList();
            return new ModulatedSignal(spec, recs, record);
        }
    }

    /// <summary>
    /// I/Q text import: the Waveform_Generator format (<c># sample_rate_hz &lt;fs&gt;</c>, optional
    /// <c># channel_bw_hz</c>, then one <c>I Q</c> line per sample) or any two-column I Q text with a
    /// <c># sample_rate_hz</c> header.
    /// </summary>
    public sealed class IqFile
    {
        public required Complex[] Samples { get; init; }
        public required double SampleRateHz { get; init; }
        public double ChannelBandwidthHz { get; init; }
        public double DurationSeconds => Samples.Length / SampleRateHz;

        /// <summary>
        /// Writes <paramref name="samples"/> in the format <see cref="Read"/> takes: <paramref name="comments"/> as
        /// <c>#</c> lines, <c># sample_rate_hz</c>, optional <c># channel_bw_hz</c>, <c># samples</c>, <c># columns I Q</c>,
        /// then one <c>I Q</c> line per sample
        /// (9 significant digits, invariant culture), so an exported signal imports back as a FILE carrier.
        /// </summary>
        public static void Write(string path, ReadOnlySpan<Complex> samples, double sampleRateHz, double channelBwHz = 0,
                                 IEnumerable<string>? comments = null, IProgress<double>? progress = null,
                                 CancellationToken ct = default)
        {
            var inv = CultureInfo.InvariantCulture;
            using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false), 1 << 20) { NewLine = "\n" };
            foreach (var c in comments ?? Array.Empty<string>()) w.WriteLine("# " + c);
            w.WriteLine("# sample_rate_hz " + sampleRateHz.ToString("R", inv));
            if (channelBwHz > 0) w.WriteLine("# channel_bw_hz " + channelBwHz.ToString("R", inv));
            w.WriteLine("# samples " + samples.Length.ToString(inv));
            w.WriteLine("# columns I Q");
            int step = Math.Max(1, samples.Length / 100);
            for (int i = 0; i < samples.Length; i++)
            {
                w.Write(samples[i].Real.ToString("G9", inv));
                w.Write(' ');
                w.WriteLine(samples[i].Imaginary.ToString("G9", inv));
                if (i % step == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report((double)i / samples.Length);
                }
            }
            progress?.Report(1);
        }

        public static IqFile Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"I/Q file not found: {path}", path);
            double fs = 0, bw = 0;
            var list = new List<Complex>(1 << 16);
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t[0] == '#' || t[0] == '%')
                {
                    var p = t.TrimStart('#', '%').Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 2 && p[0].Equals("sample_rate_hz", StringComparison.OrdinalIgnoreCase))
                        fs = double.Parse(p[1], CultureInfo.InvariantCulture);
                    else if (p.Length >= 2 && p[0].Equals("channel_bw_hz", StringComparison.OrdinalIgnoreCase))
                        bw = double.Parse(p[1], CultureInfo.InvariantCulture);
                    continue;
                }
                var q = t.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (q.Length < 2) continue;
                list.Add(new Complex(double.Parse(q[0], CultureInfo.InvariantCulture), double.Parse(q[1], CultureInfo.InvariantCulture)));
            }
            if (fs <= 0) throw new FormatException($"{Path.GetFileName(path)}: no '# sample_rate_hz' header line");
            if (list.Count == 0) throw new FormatException($"{Path.GetFileName(path)}: no I Q samples");
            return new IqFile { Samples = list.ToArray(), SampleRateHz = fs, ChannelBandwidthHz = bw };
        }
    }
}
