// ================================================================
//  Dbpsk.cs  —  the DBPSK carrier: differential binary PSK at a low bit
//  rate, the Sigfox uplink (ultra-narrowband, UNB): 100 b/s in the
//  868 MHz band (RC1)
//
//  Differential encoding: a 0 reverses the phase (180 degrees), a 1
//  keeps it. Each symbol (+1 or -1) is a raised-cosine pulse two bits
//  long centred on its bit, so two equal neighbours add to a flat 1 and
//  a reversal passes through zero along a cosine across the bit edge:
//  the envelope dips at each reversal and nowhere else. Hard phase
//  flips (a rectangular pulse) spread 99 % of the power over +-9 Rb;
//  this keeps it within +-0.7 Rb, the ~100 Hz a UNB uplink occupies.
//  The record is real (I only) and normalised to unit mean power.
//
//  The data is PN9, PN15 or a fixed pattern, as for FSK. The record is
//  one period of it — two when the period holds an odd number of 0s,
//  since the phase then ends reversed and the record would not repeat
//  (PN9 has 255 0s: 1,022 bits, 10.22 s at 100 b/s).
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>A DBPSK carrier's settings with every default applied: bit rate (b/s) and the data name as written.</summary>
    public sealed record DbpskShape(double BitRate, string Data)
    {
        /// <summary>Bits in the record: one data period, or two when it holds an odd number of 0s.</summary>
        public int Bits => Dbpsk.RecordBits(Data);
        public double RecordSeconds => Bits / BitRate;
    }

    public static class Dbpsk
    {
        public const double DefaultBitRate = 100;
        public const string DefaultData = "PN9";

        /// <summary>Samples per bit, the native rate's: the pulse's spectrum is ~-64 dB past 4 Rb, so 32 leave it far
        /// below the native Nyquist edge.</summary>
        public const int SamplesPerBit = 32;

        public static DbpskShape Resolve(CarrierSpec c)
            => new(c.BitRateBps ?? DefaultBitRate, (c.Data ?? DefaultData).ToUpperInvariant());

        /// <summary>Refuses a DBPSK carrier that cannot be generated, with a sentence naming the field; returns the shape.</summary>
        public static DbpskShape Validate(CarrierSpec c)
        {
            var s = Resolve(c);
            var inv = CultureInfo.InvariantCulture;
            if (!(s.BitRate > 0) || !double.IsFinite(s.BitRate))
                throw new FormatException($"DBPSK carrier: Rb must be a positive bit rate (got {s.BitRate.ToString(inv)} b/s)");
            if (s.Data is not ("PN9" or "PN15") && (s.Data.Length == 0 || s.Data.Any(ch => ch is not ('0' or '1'))))
                throw new FormatException($"DBPSK carrier: Data must be PN9, PN15 or a pattern of 0s and 1s (got '{c.Data}')");
            long n = (long)s.Bits * SamplesPerBit;
            if (n > Fsk.MaxSamples)
                throw new FormatException(string.Format(inv,
                    "DBPSK carrier: Data {0} makes a {1:N0}-bit record, {2:N0} samples, over the {3:N0} a record may hold; choose PN9 or a shorter pattern",
                    s.Data.Length > 12 ? s.Data[..12] + "…" : s.Data, s.Bits, n, Fsk.MaxSamples));
            return s;
        }

        /// <summary>The channel bandwidth: 2 Rb, the pulse's null-to-null main lobe (99 % of the power is in 1.4 Rb). The
        /// preview's span, the envelope rate and the ACPR bands follow from it.</summary>
        public static double BandwidthHz(DbpskShape s) => 2 * s.BitRate;

        public static double NativeRateHz(DbpskShape s) => SamplesPerBit * s.BitRate;

        public static int RecordBits(string data)
        {
            var bits = Fsk.DataBits(data);
            return bits.Count(b => b == 0) % 2 == 1 ? 2 * bits.Length : bits.Length;
        }

        /// <summary>The record's symbols, +1 or -1: the data (twice over when needed to close), differentially encoded
        /// from +1, a 0 reversing the phase.</summary>
        public static double[] Symbols(string data)
        {
            var bits = Fsk.DataBits(data);
            int n = RecordBits(data);
            var a = new double[n];
            double cur = 1;
            for (int i = 0; i < n; i++)
            {
                if (bits[i % bits.Length] == 0) cur = -cur;
                a[i] = cur;
            }
            return a;
        }

        /// <summary>One record at the native rate (<see cref="SamplesPerBit"/> per bit), real, unit mean power.</summary>
        public static Complex[] Generate(DbpskShape s)
        {
            var a = Symbols(s.Data);
            const int sps = SamplesPerBit;
            int n = a.Length * sps;
            // the pulse: cos^2 over two bits, peaking on a sample at the bit centre (sample m sits half a bit before the bit
            // starts), so a bit edge is a sample too: exactly 0 at a reversal, exactly 1 between equal symbols
            var p = new double[2 * sps];
            for (int m = 0; m < p.Length; m++)
            {
                double c = Math.Cos(Math.PI * (m - sps) / (2 * sps));
                p[m] = c * c;
            }
            var x = new double[n];
            for (int k = 0; k < a.Length; k++)
                for (int m = 0; m < p.Length; m++)
                    x[((k * sps - sps / 2 + m) % n + n) % n] += a[k] * p[m];
            double rms = Math.Sqrt(x.Average(v => v * v));
            return x.Select(v => new Complex(v / rms, 0)).ToArray();
        }
    }
}
