// ================================================================
//  TuningSliderMapping.cs  —  a tunable's value ↔ its slider position
//  (brief-tuneopt-4 R-to4-4, overview §3 keyboard)
//
//  Positions run 0..1 across [min, max]. A log scale spaces them by
//  ratio, a linear one by difference; Auto is decided by the range
//  (TunableValue.Effective). Values are in the tunable's own unit, so
//  1.8 on a pF row is 1.8 pF and nothing here converts units.
// ================================================================

using System;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tuning;

/// <summary>How far one key press moves a slider (overview §3).</summary>
public enum TuningNudge
{
    /// <summary>An arrow: one step.</summary>
    Step,
    /// <summary>Shift + arrow: ten steps.</summary>
    TenSteps,
    /// <summary>Page Up / Page Down: a tenth of the range.</summary>
    Page,
}

public static class TuningSliderMapping
{
    /// <summary>A step when the entry states none: a hundredth of the range, in slider position.</summary>
    public const double DefaultStepFraction = 0.01;

    /// <summary>The slider position of <paramref name="value"/>, clamped to 0..1.</summary>
    public static double ToPosition(double value, double min, double max, TuneScale scale)
    {
        if (!(max > min)) return 0;
        double p = TunableValue.Effective(scale, min, max) == TuneScale.Log && min > 0 && value > 0
            ? Math.Log(value / min) / Math.Log(max / min)
            : (value - min) / (max - min);
        return Math.Clamp(double.IsFinite(p) ? p : 0, 0, 1);
    }

    /// <summary>The value at slider <paramref name="position"/> (clamped to 0..1).</summary>
    public static double FromPosition(double position, double min, double max, TuneScale scale)
    {
        double p = Math.Clamp(position, 0, 1);
        if (!(max > min)) return min;
        return TunableValue.Effective(scale, min, max) == TuneScale.Log && min > 0
            ? min * Math.Pow(max / min, p)
            : min + p * (max - min);
    }

    /// <summary>
    /// <paramref name="value"/> as the row may hold it: an integer row rounds to a whole number, a
    /// row with a <paramref name="step"/> to the nearest multiple of it counted from
    /// <paramref name="min"/>, and either stays inside [min, max].
    /// </summary>
    public static double Snap(double value, double min, double max, bool integer, double? step)
    {
        double v = value;
        if (step is > 0 and var s) v = min + Math.Round((v - min) / s) * s;
        if (integer) v = Math.Round(v);
        if (max > min)
        {
            if (v < min) v = integer ? Math.Ceiling(min) : min;
            if (v > max) v = integer ? Math.Floor(max) : max;
        }
        return v;
    }

    /// <summary>
    /// The value one key press away from <paramref name="value"/> in <paramref name="direction"/>
    /// (+1 or −1). With a stated <paramref name="step"/> an arrow moves by that step in VALUE; without
    /// one, and for Page always, it moves by a fraction of the slider — which on a log row is a ratio,
    /// so a nudge at the bottom of a decade is as visible as one at the top. An integer row never moves
    /// by less than 1.
    /// </summary>
    public static double Nudge(double value, int direction, TuningNudge size,
                               double min, double max, TuneScale scale, bool integer, double? step)
    {
        int sign = Math.Sign(direction);
        if (sign == 0) return value;
        int count = size == TuningNudge.TenSteps ? 10 : 1;

        double next;
        if (size != TuningNudge.Page && (step is > 0 || integer))
        {
            double s = step is > 0 and var st ? st : 1.0;
            if (integer) s = Math.Max(1.0, Math.Round(s));
            next = value + sign * count * s;
        }
        else
        {
            double fraction = size == TuningNudge.Page ? 0.1 : count * DefaultStepFraction;
            next = FromPosition(ToPosition(value, min, max, scale) + sign * fraction, min, max, scale);
        }
        return Snap(next, min, max, integer, step);
    }
}
