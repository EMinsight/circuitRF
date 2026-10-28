// How a review sentence spells a length: the layout's display unit, rounded as a reviewer reads a
// board — 0.12 mm, 4.5 mil, 120 µm — never twelve digits.

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
}
