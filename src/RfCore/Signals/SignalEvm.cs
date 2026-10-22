// ================================================================
//  SignalEvm.cs  —  EVM of a generated LTE / NR carrier
//
//  Demodulates a carrier's own native record (the one the source
//  plays: after clipping and spectral shaping) symbol by symbol — FFT
//  over the useful part right after each cyclic prefix, where the
//  WOLA ramps do not reach — and compares every resource element with
//  the QAM point the generator put there (data-aided: the reference is
//  regenerated from the carrier's seed, not decided).  One complex
//  gain is fitted over the whole record (no per-subcarrier equaliser),
//  so EVM reads the in-band distortion the clipping added and nothing
//  else; an unclipped record reads ~0 %.
//
//  EVM rms = sqrt(sum |r/g - s|^2 / sum |s|^2), peak = max |r/g - s|
//  over the rms of the ideal points.  Limits are the base-station EVM
//  requirements per modulation (QPSK 17.5 %, 16QAM 12.5 %, 64QAM 8 %,
//  256QAM 3.5 %, 1024QAM 2.5 %).
// ================================================================

using System;
using System.Numerics;

namespace RfCore.Signals
{
    public sealed record EvmResult(double RmsPercent, double PeakPercent, double LimitPercent,
                                   Complex[] Received, Complex[] Ideal, int Symbols, int Subcarriers)
    {
        public double RmsDb => 20 * Math.Log10(Math.Max(RmsPercent, 1e-12) / 100);
        public bool Passes => RmsPercent <= LimitPercent;
    }

    public static class SignalEvm
    {
        public static double LimitPercent(QamOrder m) => m switch
        {
            QamOrder.Qpsk => 17.5,
            QamOrder.Qam16 => 12.5,
            QamOrder.Qam64 => 8.0,
            QamOrder.Qam256 => 3.5,
            _ => 2.5,
        };

        /// <summary>EVM of an LTE / NR carrier's <paramref name="native"/> record (any scale) against its ideal symbols.</summary>
        public static EvmResult Measure(CarrierSpec c, ReadOnlySpan<Complex> native, double recordSeconds)
        {
            var nm = OfdmGenerator.Numerology(c);
            int halfMs = (int)Math.Round(recordSeconds / 0.5e-3);
            if (native.Length != nm.SamplesPerHalfMs * halfMs)
                throw new ArgumentException($"{c.Label}: the record holds {native.Length} samples, not the {nm.SamplesPerHalfMs * halfMs} its numerology gives");
            var ideal = OfdmGenerator.IdealSymbols(c, halfMs);
            int n = nm.Nfft, nsc = nm.NSc, total = native.Length;
            var rx = new Complex[ideal.Length * nsc];
            var id = new Complex[rx.Length];
            var buf = new Complex[n];
            int pos = 0;
            for (int s = 0; s < ideal.Length; s++)
            {
                int cp = s % nm.SymbolsPerHalfMs == 0 ? nm.CpLong : nm.CpNormal;
                for (int i = 0; i < n; i++) buf[i] = native[(pos + cp + i) % total];
                Fft.Transform(buf, inverse: false);
                for (int i = 0; i < nsc; i++)
                {
                    rx[s * nsc + i] = buf[OfdmGenerator.BinOf(nm, i)];
                    id[s * nsc + i] = ideal[s][i];
                }
                pos += cp + n;
            }
            Complex num = Complex.Zero; double den = 0;
            for (int k = 0; k < rx.Length; k++) { num += rx[k] * Complex.Conjugate(id[k]); den += id[k].Real * id[k].Real + id[k].Imaginary * id[k].Imaginary; }
            var g = num / den;
            double err = 0, peak = 0;
            for (int k = 0; k < rx.Length; k++)
            {
                rx[k] /= g;
                double e = (rx[k] - id[k]).Magnitude;
                err += e * e;
                peak = Math.Max(peak, e);
            }
            double rmsRef = Math.Sqrt(den / rx.Length);
            return new EvmResult(100 * Math.Sqrt(err / den), 100 * peak / rmsRef, LimitPercent(c.Modulation),
                                 rx, id, ideal.Length, nsc);
        }
    }
}
