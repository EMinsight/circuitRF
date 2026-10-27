// ================================================================
//  EngineeringFormat.cs — SI-prefixed numbers for axes, tables and markers.
//
//  Why this exists. Every number format in the Data Display was written for quantities of order
//  one: S-parameter magnitudes, decibels, degrees, stability factors, ohms. The passive readouts
//  are not — an effective capacitance is 1e-12 to 1e-3 farads and an effective inductance 1e-12 to
//  1e-6 henries — and three separate pieces of the display failed on them, each silently:
//
//    * the table's default "F3" renders 100 pF as "0.000";
//    * an axis tick's "G3" renders 1e-7 as "1E-07", and every tick of a narrow window renders
//      IDENTICALLY, so the axis carries no information at all;
//    * worst, the axis label's near-zero snap was the ABSOLUTE constant 1e-12, so every tick of a
//      picofarad axis was snapped to exactly zero. The axis was not merely ugly, it was destroyed.
//
//  One formatter fixes all three, and the reason it can be turned on without auditing every
//  existing plot is that IT IS A NO-OP IN THE RANGE WHERE THE CURRENT OUTPUT IS ALREADY GOOD. See
//  ShouldPrefix: between a milli and a hundred thousand, this returns exactly what the plain format
//  string returns, character for character. A magnitude axis running 0…1 keeps "0.5"; it does not
//  become "500m".
// ================================================================

using System;

namespace CircuitRF.Render.DataDisplay
{
    /// <summary>Engineering notation — a mantissa and an SI prefix — for display only.</summary>
    public static class EngineeringFormat
    {
        /// <summary>SI prefixes from yocto to yotta, indexed by decade group / 3 + 8.</summary>
        private static readonly string[] Prefixes =
            ["y", "z", "a", "f", "p", "n", "µ", "m", "", "k", "M", "G", "T", "P", "E", "Z", "Y"];

        private const int MinGroup = -24, MaxGroup = 24;

        /// <summary>
        /// True when <paramref name="magnitude"/> is far enough from one that plain notation stops
        /// being readable.
        ///
        /// <para><b>The bounds are chosen to leave every existing plot untouched</b>, which is the
        /// whole basis for applying this everywhere rather than only to the traces that needed it.
        /// Below the top: decibels (−100…20), degrees (−180…180), stability factors (0…3), VSWR,
        /// ohms (0…500), S-parameter magnitudes (0…1) — all inside. Above the bottom: nothing the
        /// display drew before the passive readouts existed. What falls outside is exactly what was
        /// broken: farads, henries, and the milliohm floor of a decoupling capacitor.</para>
        /// </summary>
        public static bool ShouldPrefix(double magnitude) =>
            magnitude > 0 && double.IsFinite(magnitude) && (magnitude >= 1e5 || magnitude < 1e-3);

        /// <summary>
        /// The decade group (a multiple of three) an axis of this magnitude should be written in, or
        /// 0 for "no prefix, format as before".
        ///
        /// <para><b>One group for the whole axis, never one per tick.</b> Choosing per value puts
        /// "900p" and "1n" on adjacent gridlines of the same axis, which reads as a discontinuity
        /// in the data rather than in the labelling. The group comes from the LARGEST end of the
        /// window, so the ticks below it carry a sub-unity mantissa ("0.9n" under a 1.1 nF top) —
        /// which is what a bench instrument does and is the price of one prefix per axis.</para>
        /// </summary>
        public static int GroupFor(double magnitude)
        {
            if (!ShouldPrefix(magnitude)) return 0;
            int group = (int)Math.Floor(Math.Log10(magnitude) / 3.0) * 3;
            return Math.Clamp(group, MinGroup, MaxGroup);
        }

        /// <summary>The largest finite magnitude among the arguments; 0 when none is usable.</summary>
        public static double AxisMagnitude(params double[] values)
        {
            double m = 0;
            foreach (double v in values)
                if (double.IsFinite(v)) m = Math.Max(m, Math.Abs(v));
            return m;
        }

        /// <summary>The SI prefix for a decade group ("p", "n", "k", …); empty for group 0.</summary>
        public static string Prefix(int group)
        {
            if (group == 0) return "";
            int i = group / 3 + 8;
            return i >= 0 && i < Prefixes.Length ? Prefixes[i] : "";
        }

        /// <summary>
        /// One tick label, written in <paramref name="group"/>. A group of 0 is the plain
        /// <c>G{digits}</c> this replaced, so the caller never needs to branch.
        /// </summary>
        /// <summary>
        /// The significant digits a linear axis's labels need so that neighbouring ticks never read the same: at least
        /// <paramref name="digits"/>, and as many as it takes for the step to show. A flat trace at −250 dB is framed
        /// −250.3…−249.7 with a 0.1 step, and three digits labelled every tick "-250" (designer round 28).
        /// </summary>
        public static int DigitsToResolve(double lo, double hi, double step, int group, int digits)
        {
            double scale = Math.Pow(10.0, group);
            double m = Math.Max(Math.Abs(lo), Math.Abs(hi)) / scale, st = Math.Abs(step) / scale;
            if (!(m > 0) || !(st > 0) || !double.IsFinite(m) || !double.IsFinite(st)) return digits;
            int needed = (int)Math.Floor(Math.Log10(m)) - (int)Math.Floor(Math.Log10(st) + 1e-9) + 1;
            return Math.Max(digits, Math.Min(needed, 15));
        }

        public static string Tick(double value, int group, int digits)
        {
            if (group == 0) return value.ToString($"G{digits}");

            double mantissa = value / Math.Pow(10.0, group);
            // The mantissa of a well-chosen group lies in 1…1000, so three or four significant
            // digits is always enough and never produces an exponent of its own.
            return mantissa.ToString($"G{Math.Max(digits, 3)}") + Prefix(group);
        }

        /// <summary>
        /// The decade group for a single value written in FULL engineering notation — a mantissa in
        /// 1…1000 — rather than under <see cref="GroupFor"/>'s conservative axis rule.
        ///
        /// <para><b>The two rules differ on purpose, and the difference is opt-in versus
        /// automatic.</b> <see cref="GroupFor"/> governs axis ticks, which are formatted this way on
        /// EVERY plot whether or not anyone asked, so it stays silent unless the plain notation is
        /// actually unreadable. This one governs a cell whose trace is explicitly set to
        /// Engineering — where the user, or the passive-readout default, has asked for engineering
        /// notation and should get it: 5 mΩ is "5.000 m", not "0.005", because "0.005" throws away
        /// two of the three digits that were asked for.</para>
        /// </summary>
        public static int EngineeringGroup(double magnitude)
        {
            if (!(magnitude > 0) || !double.IsFinite(magnitude)) return 0;
            if (magnitude >= 1.0 && magnitude < 1000.0) return 0;
            return Math.Clamp((int)Math.Floor(Math.Log10(magnitude) / 3.0) * 3, MinGroup, MaxGroup);
        }

        /// <summary>
        /// A single value in engineering notation — for a table cell or a marker readout, where
        /// there is no axis to share a group with.
        ///
        /// <para>Only ever reached when the trace's format is <c>PrecisionFormat.S</c>, which no
        /// existing trace is: the default is unchanged, and the passive readouts set it because
        /// "F3" renders 100 pF as "0.000".</para>
        /// </summary>
        public static string Value(double value, int digits)
        {
            if (!double.IsFinite(value)) return "NaN";
            if (value == 0) return 0.0.ToString($"F{digits}");

            int group = EngineeringGroup(Math.Abs(value));
            if (group == 0) return value.ToString($"F{digits}");

            double mantissa = value / Math.Pow(10.0, group);
            return mantissa.ToString($"F{digits}") + " " + Prefix(group);
        }

        /// <summary>
        /// A tick that is zero to within the axis's own resolution.
        ///
        /// <para><b>Relative, and that is the entire fix for the picofarad axis.</b> The test used
        /// to be the absolute constant <c>1e-12</c>, which exists to stop floating-point tick
        /// accumulation printing "−3.5E-17" where zero belongs — a real problem, solved with a
        /// number that silently assumes the data is of order one. On an axis whose ticks ARE
        /// picofarads it snapped every one of them to zero. Measured against the tick spacing it
        /// does the job it was written for at any scale.</para>
        /// </summary>
        public static double SnapNearZero(double value, double tick)
        {
            double epsilon = double.IsFinite(tick) && tick > 0 ? Math.Abs(tick) * 1e-6 : 1e-12;
            return Math.Abs(value) < epsilon ? 0.0 : value;
        }
    }
}
