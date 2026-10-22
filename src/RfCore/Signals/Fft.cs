// ================================================================
//  Fft.cs  —  complex FFT for the modulated-signal library
//
//  Radix-2 iterative for powers of two; Bluestein (chirp-z through a
//  power-of-two convolution) for every other length, so a periodic
//  record of any whole number of samples can be resampled exactly.
//  Forward: X[k] = sum x[n] e^{-j 2 pi k n / N}.  Inverse divides by N.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace RfCore.Signals
{
    public static class Fft
    {
        private static readonly ConcurrentDictionary<int, Complex[]> TwiddleCache = new();

        public static Complex[] Forward(ReadOnlySpan<Complex> x)
        {
            var a = x.ToArray();
            Transform(a, inverse: false);
            return a;
        }

        public static Complex[] Inverse(ReadOnlySpan<Complex> x)
        {
            var a = x.ToArray();
            Transform(a, inverse: true);
            double s = 1.0 / a.Length;
            for (int i = 0; i < a.Length; i++) a[i] *= s;
            return a;
        }

        /// <summary>In-place unnormalised transform (inverse = conjugate kernel, no 1/N).</summary>
        public static void Transform(Complex[] a, bool inverse)
        {
            int n = a.Length;
            if (n <= 1) return;
            if ((n & (n - 1)) == 0) Radix2(a, inverse);
            else Bluestein(a, inverse);
        }

        private static Complex[] Twiddles(int n)
            => TwiddleCache.GetOrAdd(n, m =>
            {
                var w = new Complex[m / 2];
                for (int k = 0; k < m / 2; k++)
                    w[k] = Complex.FromPolarCoordinates(1.0, -2.0 * Math.PI * k / m);
                return w;
            });

        private static void Radix2(Complex[] a, bool inverse)
        {
            int n = a.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (a[i], a[j]) = (a[j], a[i]);
            }
            var tw = Twiddles(n);
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1, stride = n / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        Complex w = tw[k * stride];
                        if (inverse) w = Complex.Conjugate(w);
                        Complex u = a[i + k], v = a[i + k + half] * w;
                        a[i + k] = u + v;
                        a[i + k + half] = u - v;
                    }
                }
            }
        }

        private static void Bluestein(Complex[] a, bool inverse)
        {
            int n = a.Length;
            int m = 1;
            while (m < 2 * n - 1) m <<= 1;
            double sgn = inverse ? 1.0 : -1.0;
            var chirp = new Complex[n];
            for (int k = 0; k < n; k++)
            {
                // k^2 mod 2n keeps the phase argument small for long records
                long k2 = (long)k * k % (2L * n);
                chirp[k] = Complex.FromPolarCoordinates(1.0, sgn * Math.PI * k2 / n);
            }
            var A = new Complex[m];
            var B = new Complex[m];
            for (int k = 0; k < n; k++) A[k] = a[k] * chirp[k];
            B[0] = Complex.Conjugate(chirp[0]);
            for (int k = 1; k < n; k++) B[k] = B[m - k] = Complex.Conjugate(chirp[k]);
            Radix2(A, false);
            Radix2(B, false);
            for (int i = 0; i < m; i++) A[i] *= B[i];
            Radix2(A, true);
            double s = 1.0 / m;
            for (int k = 0; k < n; k++) a[k] = A[k] * s * chirp[k];
        }

        /// <summary>Exact band-limited resampling of a PERIODIC record by zero-padding / truncating its spectrum.</summary>
        public static Complex[] ResamplePeriodic(Complex[] x, int newLength)
        {
            int n = x.Length;
            if (newLength == n) return (Complex[])x.Clone();
            var X = Forward(x);
            var Y = new Complex[newLength];
            int keep = Math.Min(n, newLength);
            int pos = (keep + 1) / 2, neg = keep / 2;         // bins 0..pos-1 and the last `neg` bins
            for (int k = 0; k < pos; k++) Y[k] = X[k];
            for (int k = 1; k <= neg; k++) Y[newLength - k] = X[n - k];
            if (keep % 2 == 0 && newLength > n)               // split the Nyquist bin of an even record
            {
                Complex half = X[n / 2] * 0.5;
                Y[newLength - n / 2] = half;
                Y[n / 2] = half;
            }
            var y = Inverse(Y);
            double scale = (double)newLength / n;
            for (int i = 0; i < y.Length; i++) y[i] *= scale;
            return y;
        }
    }
}
