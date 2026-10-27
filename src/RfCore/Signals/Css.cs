// ================================================================
//  Css.cs  —  the CSS carrier: chirp spread spectrum, the LoRa
//  modulation that LoRaWAN runs on (EU868 channels are 125 kHz wide)
//
//  A symbol is one chirp: the frequency sweeps the bandwidth BW once in
//  Ts = 2^SF / BW, up from -BW/2. A symbol value s (SF bits) starts the
//  sweep s / 2^SF of the way up and wraps from +BW/2 to -BW/2 when it
//  reaches the top, so the receiver reads s as the frequency left after
//  multiplying by a down-chirp. The phase is the running integral of
//  the frequency, as a synthesiser makes it: it never jumps, at a
//  wrap or a symbol edge. The envelope is constant (PAPR 0 dB).
//
//  The record is one packet: 8 preamble up-chirps, two sync symbols
//  (24 and 32: the public network sync word 0x34, eight times each
//  nibble), 2.25 down-chirps (the start-of-frame delimiter), and
//  32 payload symbols taken SF bits at a time from PN9. An RF front end
//  responds to the chirps, not to what the bits mean, so there is no
//  header, coding or CRC. The residual phase over the record is ramped
//  out as for FSK, moving the carrier by at most half a record bin.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>A CSS carrier's settings with every default applied: spreading factor and chirp bandwidth (Hz).</summary>
    public sealed record CssShape(int Sf, double BandwidthHz)
    {
        public int Chips => 1 << Sf;
        /// <summary>One symbol: 2^SF / BW.</summary>
        public double SymbolSeconds => Chips / BandwidthHz;
        /// <summary>Raw bit rate, before coding: SF x BW / 2^SF.</summary>
        public double RawBitRate => Sf * BandwidthHz / Chips;
        public double RecordSeconds => Css.PacketSymbols * SymbolSeconds;
    }

    public static class Css
    {
        public const int DefaultSf = 7, MinSf = 5, MaxSf = 12;
        public const double DefaultBandwidthHz = 125e3;
        public const int PreambleSymbols = 8, PayloadSymbols = 32;
        public static readonly int[] SyncSymbols = [24, 32];
        /// <summary>Preamble + sync + 2.25 down-chirps + payload.</summary>
        public const double PacketSymbols = PreambleSymbols + 2 + 2.25 + PayloadSymbols;

        /// <summary>Samples per chip, the native rate's: 8 BW holds the splatter of each wrap well inside its Nyquist edge.</summary>
        public const int SamplesPerChip = 8;

        public static CssShape Resolve(CarrierSpec c) => new(c.SpreadingFactor ?? DefaultSf, c.BandwidthHz);

        /// <summary>Refuses a CSS carrier that cannot be generated, with a sentence naming the field; returns the shape.</summary>
        public static CssShape Validate(CarrierSpec c)
        {
            var s = Resolve(c);
            var inv = CultureInfo.InvariantCulture;
            if (s.Sf is < MinSf or > MaxSf)
                throw new FormatException($"CSS carrier: SF must be {MinSf} to {MaxSf} (got {s.Sf.ToString(inv)})");
            if (!(s.BandwidthHz > 0) || s.BandwidthHz > 2e6 || !double.IsFinite(s.BandwidthHz))
                throw new FormatException($"CSS carrier: BW must be a chirp bandwidth up to 2 MHz (got {s.BandwidthHz.ToString(inv)} Hz)");
            return s;
        }

        public static double NativeRateHz(CssShape s) => SamplesPerChip * s.BandwidthHz;

        /// <summary>The 32 payload symbols: PN9 read SF bits at a time, most significant first.</summary>
        public static int[] Payload(int sf)
        {
            var bits = Fsk.DataBits("PN9");
            var p = new int[PayloadSymbols];
            for (int i = 0; i < p.Length; i++)
                for (int j = 0; j < sf; j++) p[i] = (p[i] << 1) | bits[(i * sf + j) % bits.Length];
            return p;
        }

        /// <summary>One packet at the native rate, unit magnitude everywhere, and the instantaneous frequency (Hz) it
        /// was integrated from.</summary>
        public static (Complex[] Samples, double[] FrequencyHz) Generate(CssShape s)
        {
            int ns = s.Chips * SamplesPerChip;
            double bw = s.BandwidthHz, rate = NativeRateHz(s);
            var f = new List<double>((int)Math.Round(PacketSymbols * ns));
            void Up(int value)
            {
                for (int m = 0; m < ns; m++)
                {
                    double u = (double)value / s.Chips + (double)m / ns;
                    f.Add(-bw / 2 + (u - Math.Floor(u)) * bw);
                }
            }
            void Down(int count)
            {
                for (int m = 0; m < count; m++) f.Add(bw / 2 - (double)(m % ns) / ns * bw);
            }
            for (int i = 0; i < PreambleSymbols; i++) Up(0);
            foreach (int v in SyncSymbols) Up(v % s.Chips);
            Down(2 * ns + ns / 4);
            foreach (int v in Payload(s.Sf)) Up(v);

            int n = f.Count;
            double total = 2 * Math.PI * f.Sum() / rate;
            double residual = total - 2 * Math.PI * Math.Round(total / (2 * Math.PI));
            var x = new Complex[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                x[i] = Complex.FromPolarCoordinates(1.0, phase - residual * i / n);
                phase += 2 * Math.PI * f[i] / rate;
            }
            return (x, f.ToArray());
        }
    }
}
