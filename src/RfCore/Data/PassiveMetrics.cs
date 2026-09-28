// ================================================================
//  PassiveMetrics.cs  —  two-terminal part characterisation from an S-matrix
//
//  brief-passive-readouts-and-touchstone-check.md §2.
//
//  What this file is for. A capacitor, an inductor and a ferrite bead are all sold as an
//  IMPEDANCE versus frequency, and the numbers a designer works in are ESR, C_eff, L_eff, Q and
//  the self-resonance — not S-parameters. A vendor ships the part as a Touchstone file and as a
//  model, and the first thing anyone does is ask whether the two agree. Answering that from S11
//  in dB is possible and nobody does it.
//
//  Why it is not a member of NetworkMetric. Every metric in that enum is a stability or passivity
//  figure computed from one S-matrix and returning one real number, and EvaluateTwoPort loops
//  them over the sweep. These are also per-matrix, but they are functions of an IMPEDANCE that has
//  to be extracted first, and the extraction is a CHOICE the caller makes (see PassiveExtraction)
//  rather than something derivable from the data. Folding a required mode argument into
//  EvaluateTwoPort's switch would make five stability metrics carry a parameter that means nothing
//  to them. Group delay set the precedent for a derived quantity with its own entry point.
// ================================================================

using System;
using System.Numerics;
using NumFlat;

namespace RfCore.Data
{
    /// <summary>
    /// How the two-terminal impedance is recovered from the measured S-parameters. <b>This is a
    /// statement about the FIXTURE</b>, and no Touchstone header records it — but for a two-terminal
    /// part the data does: see <see cref="PassiveMetrics.InferExtraction"/>, which reads the
    /// series or shunt identity off the matrix and is null where it cannot tell.
    ///
    /// <para><b>Getting this wrong is the standard failure in this corner of the field, and the
    /// error is not where intuition puts it.</b> A vendor's 2-port capacitor file is measured
    /// shunt-through; reading its impedance off S11 as if it were a 1-port gives a curve that is
    /// smooth, finite and plausible everywhere. Working it through, the misread returns exactly
    /// <c>Z ∥ Z0</c> — so it is close to right wherever |Z| ≪ Z0, and SATURATES AT Z0 otherwise.
    /// A bulk decoupling capacitor is under an ohm across most of its band and survives the
    /// mistake; a 10 pF capacitor, an inductor or a bead comes back as ≈ 50 Ω, and its
    /// capacitance with it. Neither case announces itself.</para>
    ///
    /// <para>The reason the fixture exists in the LAB is a separate one and belongs to the
    /// instrument rather than to this arithmetic: at a few milliohms against 50 Ω, S11 sits within
    /// a part in 10⁴ of −1, where a vector network analyser's directivity and calibration
    /// residuals dominate, while the same impedance appears in S21 as a well-conditioned
    /// transmission measurement.</para>
    /// </summary>
    public enum PassiveExtraction
    {
        /// <summary>
        /// A 1-port reflection measurement — the DUT is the only thing at the port.
        /// <c>Z = Z0·(1 + S11)/(1 − S11)</c>. Correct for a genuine 1-port file, and the only
        /// possible reading of one.
        /// </summary>
        OnePort,

        /// <summary>
        /// The DUT is a SHUNT element bridging two through lines, the standard low-impedance
        /// fixture and the one nearly every vendor decoupling-capacitor file is measured in.
        /// <c>Z = (Z0/2)·S21/(1 − S21)</c> — 25·S21/(1 − S21) at a 50 Ω reference.
        /// </summary>
        ShuntThrough,

        /// <summary>
        /// The DUT is a SERIES element in the through line — the high-impedance fixture, used for
        /// chokes and beads well above their resonance. <c>Z = 2·Z0·(1 − S21)/S21</c>.
        /// </summary>
        SeriesThrough,
    }

    /// <summary>
    /// The quantities a passive part is specified and bought in. All are point functions of the
    /// extracted impedance <c>Z(f)</c> — no sweep derivative, no fit.
    /// </summary>
    public enum PassiveMetric
    {
        /// <summary>|Z| in ohms.</summary>
        MagZ,
        /// <summary>Equivalent series resistance, Re Z, in ohms.</summary>
        Esr,
        /// <summary>Reactance, Im Z, in ohms. Its upward zero crossing IS the self-resonance.</summary>
        Reactance,
        /// <summary>Effective capacitance in farads, −1/(ω·Im Z). Defined only where Im Z &lt; 0.</summary>
        Ceff,
        /// <summary>Effective inductance in henries, Im Z/ω. Defined only where Im Z &gt; 0.</summary>
        Leff,
        /// <summary>Quality factor |Im Z|/Re Z.</summary>
        Q,
    }

    /// <summary>
    /// Two-terminal impedance readouts from an S-matrix sweep — the numbers a passive part is
    /// specified in, computed from the file the vendor shipped.
    ///
    /// <para><b>Every method here presumes the DUT is a TWO-TERMINAL part.</b> ESR and C_eff of a
    /// transistor are arithmetic, not physics. Nothing in this class can detect that, so the
    /// caller states it by choosing to ask.</para>
    /// </summary>
    public static class PassiveMetrics
    {
        /// <summary>True when <paramref name="e"/> reads a transmission term and therefore needs an
        /// ordered port PAIR. <see cref="PassiveExtraction.OnePort"/> needs only the one port.</summary>
        public static bool NeedsPortPair(PassiveExtraction e) => e != PassiveExtraction.OnePort;

        /// <summary>The SI unit of <paramref name="m"/>, for a label or an axis.</summary>
        public static string Unit(PassiveMetric m) => m switch
        {
            PassiveMetric.MagZ      => "Ω",
            PassiveMetric.Esr       => "Ω",
            PassiveMetric.Reactance => "Ω",
            PassiveMetric.Ceff      => "F",
            PassiveMetric.Leff      => "H",
            PassiveMetric.Q         => "",
            _ => "",
        };

        // ── impedance extraction ─────────────────────────────────────────────

        /// <summary>
        /// The DUT impedance versus frequency, extracted from a full N-port S sweep under
        /// <paramref name="mode"/>.
        ///
        /// <para><b>Renormalised to a uniform REAL reference first, always</b> — the same rule
        /// <see cref="NetworkMetrics"/> follows and for the same reason (R-stb-2). All three
        /// closed forms below are derived for a real Z0; handing them a complex or per-port
        /// reference produces a number rather than an error, which is the failure to avoid.</para>
        /// </summary>
        /// <param name="full">Per-frequency N×N S matrices.</param>
        /// <param name="z0">Per-port reference impedances, length N.</param>
        /// <param name="mode">The fixture the DUT was measured in.</param>
        /// <param name="portA">1-based; the reflection port, or the input of the through pair.</param>
        /// <param name="portB">1-based; the output of the through pair. Ignored for OnePort.</param>
        public static Complex[] Impedance(
            Mat<Complex>[] full, Complex[] z0, PassiveExtraction mode, int portA, int portB)
        {
            if (mode == PassiveExtraction.OnePort)
            {
                var (s11, zRef1) = OnePortUniformReal(full, z0, portA);
                var z1 = new Complex[s11.Length];
                for (int f = 0; f < s11.Length; f++)
                {
                    var d = Complex.One - s11[f];
                    // S11 = 1 is an open circuit: infinite impedance, and no finite number is a
                    // better answer than NaN. Left to the plot to leave a gap.
                    z1[f] = d == Complex.Zero
                        ? new Complex(double.NaN, double.NaN)
                        : zRef1 * (Complex.One + s11[f]) / d;
                }
                return z1;
            }

            var mats = NetworkMetrics.TwoPortUniformReal(full, z0, portA, portB);
            double zRef = z0[portA - 1].Real;

            var z = new Complex[mats.Length];
            for (int f = 0; f < mats.Length; f++)
            {
                var s21 = mats[f][1, 0];
                z[f] = mode switch
                {
                    // Shunt Z across a through line: S21 = 2Z/(2Z + Z0)  ⇒  Z = (Z0/2)·S21/(1 − S21).
                    // S21 → 1 is the DUT removed (an open shunt): unbounded, so NaN.
                    PassiveExtraction.ShuntThrough =>
                        Complex.One - s21 == Complex.Zero
                            ? new Complex(double.NaN, double.NaN)
                            : 0.5 * zRef * s21 / (Complex.One - s21),

                    // Series Z in the line: S21 = 2Z0/(2Z0 + Z)  ⇒  Z = 2Z0·(1 − S21)/S21.
                    // S21 → 0 is an open series element: unbounded, so NaN.
                    _ => s21 == Complex.Zero
                            ? new Complex(double.NaN, double.NaN)
                            : 2.0 * zRef * (Complex.One - s21) / s21,
                };
            }
            return z;
        }

        /// <summary>
        /// The reflection coefficient at one port, renormalised to that port's own real reference.
        ///
        /// <para>Extracting S_aa from an N-port carries the same assumption every sub-matrix
        /// extraction here does — the other ports are terminated in their reference impedance
        /// (R-stb-4). For a genuine 1-port file there are no other ports and the extraction is
        /// exact.</para>
        /// </summary>
        private static (Complex[] S11, double ZRef) OnePortUniformReal(
            Mat<Complex>[] full, Complex[] z0, int port)
        {
            int nPorts = full.Length > 0 ? full[0].RowCount : z0.Length;
            if (port < 1 || port > nPorts)
                throw new ArgumentOutOfRangeException(nameof(port),
                    $"Port {port} is outside 1..{nPorts}.");

            int a = port - 1;
            var z0Old  = new[] { z0[a] };
            var target = new Complex(z0[a].Real, 0.0);
            bool identity = z0Old[0] == target;
            var z0New = new[] { target };

            var s11 = new Complex[full.Length];
            for (int f = 0; f < full.Length; f++)
            {
                if (identity) { s11[f] = full[f][a, a]; continue; }
                var sub = new Mat<Complex>(1, 1);
                sub[0, 0] = full[f][a, a];
                s11[f] = RFNetwork.SToS(sub, z0Old, z0New)[0, 0];
            }
            return (s11, target.Real);
        }

        // ── which fixture the data was measured in ───────────────────────────

        /// <summary>
        /// The fixture a two-port file of a TWO-TERMINAL part was measured in, read off the data —
        /// or null where the data does not say.
        ///
        /// <para><b>The fixture leaves an identity in the S-matrix.</b> A lumped impedance in SERIES
        /// between two ports at the same real reference gives S11 = Z/(Z + 2Z0) and
        /// S21 = 2Z0/(Z + 2Z0), so <c>S11 + S21 = 1</c> at every frequency, whatever Z is. A SHUNT
        /// impedance across a through line gives S11 = −Z0/(2Z + Z0) and S21 = 2Z/(2Z + Z0), so
        /// <c>S21 − S11 = 1</c>. A file satisfies one and misses the other by order one, so the
        /// test does not depend on the part's value. (Field report 2026-09-28: a vendor's
        /// series-thru capacitor files, read through the shunt-thru default, turned a 10 µF part
        /// into 4.6 pF; the series residual on them was 4e-16 and the shunt residual ≈ 2.)</para>
        ///
        /// <para>Measured data carries fixture and calibration residue, so the MEDIAN residual over
        /// the sweep is compared, and an answer is given only when one identity holds to
        /// <paramref name="tolerance"/> and the other misses by at least ten times as much. A
        /// one-port, a non-S file, a file of anything that is not a two-terminal part, and a file
        /// that is neither decisively are all null — the caller keeps its own default.</para>
        /// </summary>
        public static PassiveExtraction? InferExtraction(SNP snp, double tolerance = 0.1)
        {
            if (snp is null || snp.IsEmpty || snp.Ports != 2 || snp.Type != MatrixType.S) return null;

            Complex[] z0 = snp.Z0PerPort ?? new[] { snp.Z0, snp.Z0 };
            Mat<Complex>[] mats;
            try { mats = NetworkMetrics.TwoPortUniformReal(snp.Matrices, z0, 1, 2); }
            catch { return null; }

            int n = mats.Length;
            var series = new double[n];
            var shunt  = new double[n];
            for (int f = 0; f < n; f++)
            {
                Complex s11 = mats[f][0, 0], s21 = mats[f][1, 0];
                series[f] = (s11 + s21 - Complex.One).Magnitude;
                shunt[f]  = (s21 - s11 - Complex.One).Magnitude;
            }
            double rs = Median(series), rp = Median(shunt);
            if (double.IsNaN(rs) || double.IsNaN(rp)) return null;

            if (rs < tolerance && rs * 10.0 <= rp) return PassiveExtraction.SeriesThrough;
            if (rp < tolerance && rp * 10.0 <= rs) return PassiveExtraction.ShuntThrough;
            return null;

            static double Median(double[] v)
            {
                var sorted = (double[])v.Clone();
                Array.Sort(sorted);
                return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
            }
        }

        // ── the readouts ─────────────────────────────────────────────────────

        /// <summary>
        /// One metric versus frequency from an already-extracted impedance.
        ///
        /// <para><b>Where a metric is undefined it is NaN, never a clamped or wrapped value.</b>
        /// C_eff above the self-resonance and L_eff below it are the routine cases: the part is
        /// inductive and asking for its capacitance has no answer, so the trace ends. A negative
        /// capacitance plotted on a log axis, or a folded-back positive one, would read as data.
        /// </para>
        /// </summary>
        public static double[] Evaluate(Complex[] z, double[] freqs, PassiveMetric metric)
        {
            if (freqs.Length != z.Length)
                throw new ArgumentException(
                    $"Frequency axis ({freqs.Length}) and impedance array ({z.Length}) differ in length.",
                    nameof(freqs));

            var v = new double[z.Length];
            for (int f = 0; f < z.Length; f++)
            {
                double re = z[f].Real, im = z[f].Imaginary;
                double w  = 2.0 * Math.PI * freqs[f];

                v[f] = metric switch
                {
                    PassiveMetric.MagZ      => z[f].Magnitude,
                    PassiveMetric.Esr       => re,
                    PassiveMetric.Reactance => im,
                    // Capacitive branch only. ω = 0 has no C_eff either — at DC an ideal capacitor
                    // is an open and Im Z is already infinite.
                    PassiveMetric.Ceff      => im < 0.0 && w > 0.0 ? -1.0 / (w * im) : double.NaN,
                    PassiveMetric.Leff      => im > 0.0 && w > 0.0 ?  im / w         : double.NaN,
                    // Q of a lossless element is unbounded, and of a negative-resistance one is
                    // not a Q at all. Both are NaN rather than a large or signed number.
                    PassiveMetric.Q         => re > 0.0 ? Math.Abs(im) / re : double.NaN,
                    _ => double.NaN,
                };
            }
            return v;
        }

        /// <summary>Extraction and evaluation in one step — the form a plotted trace uses.</summary>
        public static double[] Evaluate(
            Mat<Complex>[] full, Complex[] z0, double[] freqs,
            PassiveExtraction mode, PassiveMetric metric, int portA, int portB)
            => Evaluate(Impedance(full, z0, mode, portA, portB), freqs, metric);

        // ── self-resonance ───────────────────────────────────────────────────

        /// <summary>
        /// The self-resonant frequency in hertz — the lowest frequency at which the reactance
        /// crosses zero going CAPACITIVE to INDUCTIVE — or null when the sweep contains no such
        /// crossing.
        ///
        /// <para><b>Direction matters and an unsigned zero-crossing search is wrong.</b> A real
        /// part has several crossings: the series resonance where |Z| is a minimum, then the
        /// mounting/package parallel resonance above it where |Z| is a MAXIMUM and the reactance
        /// crosses the other way. The number everyone means by "SRF" is the first of those, and
        /// the sign of the crossing is what separates them.</para>
        ///
        /// <para>Interpolated in log f, because these sweeps are logarithmic and a linear
        /// interpolation across a decade-wide gap places the crossing visibly wrong. Returns null
        /// rather than the band edge when the reactance never changes sign: "this sweep does not
        /// contain the resonance" is the honest answer, and a clamped band edge would be read as a
        /// measurement.</para>
        /// </summary>
        public static double? SelfResonance(double[] freqs, Complex[] z)
        {
            for (int i = 1; i < z.Length && i < freqs.Length; i++)
            {
                double x0 = z[i - 1].Imaginary, x1 = z[i].Imaginary;
                if (double.IsNaN(x0) || double.IsNaN(x1)) continue;
                if (!(x0 < 0.0 && x1 >= 0.0)) continue;

                double f0 = freqs[i - 1], f1 = freqs[i];
                if (x1 == x0) return f1;
                if (f0 <= 0.0 || f1 <= 0.0) return f0 + (f1 - f0) * (-x0 / (x1 - x0));

                double t = -x0 / (x1 - x0);
                return Math.Exp(Math.Log(f0) + t * (Math.Log(f1) - Math.Log(f0)));
            }
            return null;
        }

        /// <summary>Self-resonance straight from a sweep, under <paramref name="mode"/>.</summary>
        public static double? SelfResonance(
            Mat<Complex>[] full, Complex[] z0, double[] freqs,
            PassiveExtraction mode, int portA, int portB)
            => SelfResonance(freqs, Impedance(full, z0, mode, portA, portB));
    }
}
