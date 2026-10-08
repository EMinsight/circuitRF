// ================================================================
//  TuningDigits.cs  —  how many significant digits the Tuning and
//  Optimizer panels spell a value with
//
//  One setting per schematic (TuningSetup.Digits), shared by both
//  panels and chosen from the digits button in either header. It is
//  the spelling of a tuned or optimized VALUE — what a row shows, what
//  a live tuning move simulates, and what Push, Lock in and Send to
//  Tuning write — so the number on screen is the number that ran and
//  the number that lands in the schematic. Range bounds and steps keep
//  their own spelling: they are typed, not computed.
// ================================================================

using System.Collections.Generic;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tuning;

public static class TuningDigits
{
    /// <summary>What a schematic that never chose gets — the six figures the rows always used.</summary>
    public const int Default = 6;

    /// <summary>"All digits": every figure a double carries that survives a round trip as text.</summary>
    public const int All = 15;

    /// <summary>The menu, in order.</summary>
    public static IReadOnlyList<int> Choices { get; } = [3, 4, 5, 6, All];

    /// <summary>The digits <paramref name="setup"/> asks for, or <see cref="Default"/>.</summary>
    public static int Of(TuningSetup? setup) => setup?.Digits is int d && d is >= 1 and <= All ? d : Default;

    public static string Label(int digits) => digits == All ? "All digits" : $"{digits} digits";

    /// <summary>The .NET format string for <paramref name="digits"/> significant figures.</summary>
    public static string NumberFormat(int digits) => "G" + digits;

    /// <summary><paramref name="v"/> to <paramref name="digits"/> significant figures, then the unit.</summary>
    public static string Format(double v, string unit, int digits)
    {
        string n = (v == 0 ? 0.0 : v).ToString(NumberFormat(digits), CultureInfo.InvariantCulture);
        return unit.Length == 0 ? n : $"{n} {unit}";
    }

    /// <summary>
    /// Value text (<c>1.83456789 pF</c>, <c>30.1234+52.5678j Ohm</c>) re-spelled to
    /// <paramref name="digits"/> figures, in its own unit and complex form. Text that is not a value
    /// comes back unchanged.
    /// </summary>
    public static string Round(string text, int digits)
    {
        if (TunableValue.TryParse(text, out double number, out string unit, out _))
            return Format(number, unit, digits);
        if (ComplexValue.TryParse(text, out var z, out string zUnit, out var form))
            return ComplexValue.Format(z, zUnit, form, NumberFormat(digits));
        return text;
    }

    /// <summary><see cref="Round(string, int)"/> over a key → value map.</summary>
    public static IReadOnlyDictionary<string, string> Round(IReadOnlyDictionary<string, string> values, int digits)
    {
        var rounded = new Dictionary<string, string>(values.Count, System.StringComparer.Ordinal);
        foreach (var (k, v) in values) rounded[k] = Round(v, digits);
        return rounded;
    }
}

/// <summary>One row of a panel header's digits menu.</summary>
public sealed record TuningDigitsChoice(int Digits, bool IsChecked, System.Windows.Input.ICommand Command)
{
    public string Label => TuningDigits.Label(Digits);
}
