using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// What counts as a plain number for tuning (overview D1) — <c>47</c>, <c>47 pF</c>, <c>47pF</c>,
/// <c>1.2e-9</c> — and the arithmetic on one: its value in its own unit, its default range, and how
/// a value is spelled back as the text a schematic holds.
/// </summary>
public static class TunableValue
{
    /// <summary>
    /// The <c>discrete=</c> choices an entry for <paramref name="t"/> offers (brief-tuneopt-8 R-to8-2/7):
    /// a part of a complex value only <c>none</c> — a part is continuous; otherwise <c>none</c> and
    /// <c>integer</c>, and <c>preferred</c> only when the parameter's unit names a quantity with a
    /// ladder (a capacitance, an inductance, a resistance). A choice not listed is ABSENT from the
    /// row, not greyed, and a hand-written line naming it is a <c>check</c> error.
    /// </summary>
    public static IReadOnlyList<TuneDiscrete> DiscreteChoices(Tunable t)
        => t.Part is not null ? [TuneDiscrete.None]
         : CircuitRF.Design.Matching.PreferredValues.QuantityOfUnit(t.Unit) is null ? [TuneDiscrete.None, TuneDiscrete.Integer]
         : [TuneDiscrete.None, TuneDiscrete.Integer, TuneDiscrete.Preferred];

    private static readonly Regex _plain = new(
        @"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*([A-Za-zΩµμ%°]+)?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads value text as a plain number with an optional recognized unit. <paramref name="number"/>
    /// is in that unit (47 for <c>47 pF</c>), <paramref name="unit"/> its engine spelling (<c>pF</c>, or
    /// "" for none), and <paramref name="si"/> the number in base SI. False for anything else — an
    /// expression, a variable reference, a string.
    /// </summary>
    public static bool TryParse(string text, out double number, out string unit, out double si)
    {
        number = 0; unit = ""; si = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = _plain.Match(text);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return false;
        if (m.Groups[2].Success)
        {
            unit = UnitNormalizer.ToEngineUnit(m.Groups[2].Value);
            if (!Units.IsRecognizedUnit(unit)) return false;
        }
        si = number * (unit.Length == 0 ? 1.0 : Units.Scale(unit) ?? 1.0);
        return true;
    }

    /// <summary>Value text as a number in <paramref name="unit"/>: a bare number is already in it, a
    /// number with another unit is converted (<c>2000 fF</c> in pF is 2). Null for anything else.</summary>
    public static double? InUnit(string? text, string unit)
    {
        if (text is null || !TryParse(text, out double n, out string u, out double si)) return null;
        if (u.Length == 0 || u == unit) return n;
        if (unit.Length == 0) return si;
        return Units.Scale(unit) is { } scale and not 0 ? si / scale : null;
    }

    /// <summary>The value text an expression and its unit column make together — <c>47 pF</c>.</summary>
    public static string Text(string expression, string? unit)
        => string.IsNullOrEmpty(unit) ? expression.Trim() : $"{expression.Trim()} {unit}";

    /// <summary>
    /// Splits value text into the expression and unit a schematic row (or a netlist assignment) would
    /// hold — <c>47 pF</c> → (<c>47</c>, <c>pF</c>). A value written with no unit keeps
    /// <paramref name="existingUnit"/>, because typing <c>47</c> into a row whose unit column says pF
    /// means 47 pF.
    /// </summary>
    public static (string Expression, string? Unit) Split(string text, string? existingUnit)
    {
        var (expr, unit) = Units.LiftInlineUnit(text.Trim());
        return (expr, unit ?? (string.IsNullOrEmpty(existingUnit) ? null : existingUnit));
    }

    /// <summary>
    /// The range a tunable gets when it is first activated (overview D4), in its own unit: a positive
    /// value v → [v/2, 2v]; a negative one → [v − |v|/2, v + |v|/2]; zero → [0, 1], which is a guess
    /// and says so.
    /// </summary>
    public static (string Min, string Max, bool Guessed) DefaultRange(double number, string unit, string numberFormat = "G15")
    {
        if (number == 0) return (Format(0, unit), Format(1, unit), true);
        return number > 0
            ? (Format(number / 2, unit, numberFormat), Format(number * 2, unit, numberFormat), false)
            : (Format(number * 1.5, unit, numberFormat), Format(number * 0.5, unit, numberFormat), false);
    }

    /// <summary>
    /// The default range of one part of a complex value (overview D18): a phase gets ±90° around its
    /// value; the real, imaginary and magnitude parts follow <see cref="DefaultRange"/>. Six significant
    /// figures: a part is arithmetic on the value (|50+10j| is 50.990195…), and nobody typed those digits.
    /// </summary>
    public static (string Min, string Max, bool Guessed) DefaultPartRange(ComplexPart part, double number, string unit)
        => part == ComplexPart.Phase
            ? (Format(number - 90, unit, "G6"), Format(number + 90, unit, "G6"), false)
            : DefaultRange(number, unit, "G6");

    /// <summary>The effective scale of a range: <see cref="TuneScale.Auto"/> is log when min &gt; 0
    /// and max/min ≥ 10.</summary>
    public static TuneScale Effective(TuneScale scale, double min, double max)
        => scale != TuneScale.Auto ? scale : min > 0 && max / min >= 10 ? TuneScale.Log : TuneScale.Lin;

    /// <summary>A number in a unit, spelled the way a schematic row holds it.</summary>
    public static string Format(double number, string unit, string numberFormat = "G15")
    {
        // G15, not R: a default bound is arithmetic on the value (×1.5 of -0.1 is -0.15000000000000002
        // in binary), and fifteen digits is more than any row is typed with.
        string n = (number == 0 ? 0.0 : number).ToString(numberFormat, CultureInfo.InvariantCulture);
        return unit.Length == 0 ? n : $"{n} {unit}";
    }
}
