// ================================================================
//  PhaseUnwrap.cs  —  a phase continued across ±180°
//
//  GitHub discussion 27. A wrapped phase jumps by 360° wherever it crosses ±180°, which hides the
//  slope a delay or an electrical length is read from. Unwrapping removes each jump: wherever two
//  neighbouring samples differ by more than half a turn, every later sample is moved by the whole
//  turns that bring the step back inside it.
//
//  One implementation, two callers: the expression engine's unwrap() and the Data Display's
//  "Unwrap°" transform, so a goal written against unwrap(...) and the curve the user is
//  looking at cannot disagree.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RfCore.Data
{
    /// <summary>Unwraps a phase in degrees along one sequence of samples.</summary>
    public static class PhaseUnwrap
    {
        /// <summary>The phase of each sample, in degrees, continued across ±180°.</summary>
        public static double[] Degrees(IReadOnlyList<Complex> values)
        {
            var p = new double[values.Count];
            for (int i = 0; i < p.Length; i++) p[i] = values[i].Phase * (180.0 / Math.PI);
            UnwrapInPlace(p, 0, 1, p.Length);
            return p;
        }

        /// <summary><paramref name="degrees"/> continued across ±180°; the input is not changed.</summary>
        public static double[] Degrees(IReadOnlyList<double> degrees)
        {
            var p = new double[degrees.Count];
            for (int i = 0; i < p.Length; i++) p[i] = degrees[i];
            UnwrapInPlace(p, 0, 1, p.Length);
            return p;
        }

        /// <summary>
        /// Unwraps every line of a row-major buffer of degrees along axis <paramref name="dim"/> of
        /// <paramref name="shape"/>, in place. Each line is independent, so a swept run unwraps each
        /// sweep point's own curve.
        /// </summary>
        public static void DegreesAlong(double[] values, IReadOnlyList<int> shape, int dim)
        {
            int stride = 1;
            for (int d = shape.Count - 1; d > dim; d--) stride *= shape[d];
            int length = shape[dim];
            int block = stride * length;
            for (int outer = 0; outer + block <= values.Length; outer += block)
                for (int inner = 0; inner < stride; inner++)
                    UnwrapInPlace(values, outer + inner, stride, length);
        }

        /// <summary>
        /// The first sample stays where it is. A NaN stays NaN and is stepped over: the next present
        /// sample is compared with the last present one, so one missing point does not reset the curve.
        /// </summary>
        private static void UnwrapInPlace(double[] p, int start, int step, int count)
        {
            double offset = 0, previous = double.NaN;
            for (int k = 0; k < count; k++)
            {
                int i = start + k * step;
                double raw = p[i];
                if (double.IsNaN(raw)) continue;
                // The nearest whole number of turns, a tie going toward zero: a step of exactly half a turn is
                // ambiguous, and is left as it is. (MidpointRounding.ToZero would truncate every step, not just ties.)
                if (!double.IsNaN(previous))
                {
                    double turns = (raw - previous) / 360.0;
                    offset -= 360.0 * Math.Sign(turns) * Math.Ceiling(Math.Abs(turns) - 0.5);
                }
                previous = raw;
                p[i] = raw + offset;
            }
        }
    }
}
