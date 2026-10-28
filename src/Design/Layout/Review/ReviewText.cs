// How a review sentence spells a length: the layout's display unit, rounded as a reviewer reads a
// board — 0.12 mm, 4.5 mil, 120 µm — never twelve digits.

using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Layout.Review;

public static class ReviewText
{
    /// <summary>Decimal places per unit, trailing zeros trimmed.</summary>
    private static int Decimals(LayoutUnit unit) => unit switch
    {
        LayoutUnit.Nm   => 0,
        LayoutUnit.Um   => 1,
        LayoutUnit.Mm   => 2,
        LayoutUnit.Mil  => 1,
        LayoutUnit.Inch => 3,
        _               => 2,
    };

    /// <summary><paramref name="dbu"/> in <paramref name="unit"/> with its suffix: "0.25 mm".</summary>
    public static string Length(long dbu, LayoutUnit unit, int dbuPerMicron) =>
        $"{LayoutUnits.Format(dbu, unit, dbuPerMicron, Decimals(unit))} {LayoutUnits.Suffix(unit)}";

    /// <summary>Millimetres in one <paramref name="unit"/>.</summary>
    public static double MmPer(LayoutUnit unit) => unit switch
    {
        LayoutUnit.Nm   => 1e-6,
        LayoutUnit.Um   => 1e-3,
        LayoutUnit.Mil  => 0.0254,
        LayoutUnit.Inch => 25.4,
        _               => 1.0,
    };

    /// <summary>An area stored in mm², said in <paramref name="unit"/> squared — the layout's own unit, as every
    /// length is: "2 mm²", "2,000,000 µm²", "3,100 mil²".</summary>
    public static string Area(double mm2, LayoutUnit unit)
    {
        double v = mm2 / (MmPer(unit) * MmPer(unit));
        string n = Math.Abs(v) >= 1000 ? v.ToString("#,0", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
        return $"{n} {LayoutUnits.Suffix(unit)}²";
    }

    /// <summary>The number <see cref="Area"/> writes, without its unit — what an edit box shows.</summary>
    public static string AreaNumber(double mm2, LayoutUnit unit) =>
        (mm2 / (MmPer(unit) * MmPer(unit))).ToString("0.######", CultureInfo.InvariantCulture);

    private static readonly Regex AreaPattern =
        new(@"^\s*([-+]?[0-9]*[.,]?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*([a-zA-Zµμ]*)\s*(?:²|\^2|2)?\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// An area typed as a number and a unit squared — <c>2 mm²</c>, <c>3100mil2</c>, <c>2e6 um^2</c> — in mm².
    /// A bare number is in <paramref name="fallback"/> squared, or refused when there is none: like a length, a
    /// bare area could be any unit.
    /// </summary>
    public static bool TryParseArea(string? text, LayoutUnit? fallback, out double mm2)
    {
        mm2 = 0;
        var m = AreaPattern.Match(text ?? "");
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return false;
        LayoutUnit? unit = m.Groups[2].Value.ToLowerInvariant() switch
        {
            ""                          => fallback,
            "nm"                        => LayoutUnit.Nm,
            "u" or "um" or "µm" or "μm" => LayoutUnit.Um,
            "mm"                        => LayoutUnit.Mm,
            "mil"                       => LayoutUnit.Mil,
            "in" or "inch"              => LayoutUnit.Inch,
            _                           => null,
        };
        if (unit is not { } u || !double.IsFinite(v)) return false;
        // Twelve significant digits: 2,000,000 µm² is 2 mm² exactly, not 2 plus rounding noise that would read as a
        // threshold changed from its default.
        mm2 = double.Parse((v * MmPer(u) * MmPer(u)).ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>A width and a height sharing one suffix: "0.6 × 0.5 mm".</summary>
    public static string Size(long w, long h, LayoutUnit unit, int dbuPerMicron) =>
        $"{LayoutUnits.Format(w, unit, dbuPerMicron, Decimals(unit))} × " +
        $"{LayoutUnits.Format(h, unit, dbuPerMicron, Decimals(unit))} {LayoutUnits.Suffix(unit)}";
}
