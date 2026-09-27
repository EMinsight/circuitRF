// The snap-distance control's ladder and its typed-entry rule, shared by every editor that shows one: the layout
// editor's toolbar (brief-snap-distance-and-geometry-snap.md §1) and the 3D editor's (3D editor bugs round 2). One
// implementation, so "1 mil" means the same rungs, the same spelling and the same parse in both.

using System.Collections.Generic;
using CircuitRF.Design.Layout;

namespace CircuitRF.Ui.Layout;

public static class SnapLadder
{
    /// <summary>
    /// The ladder's rungs, as multiples of the technology's own default snap.
    ///
    /// <para><b>Two SUB-unit rungs, added at the owner's request (2026-08-16)</b> — on a PCB
    /// technology whose default snap is 1 mil these are 0.5 mil and 0.1 mil, and on any other
    /// technology they are the same fractions of that process's own step, which is what R-snp-2's
    /// relative ladder means by "the equivalent in the other units". A fixed
    /// <c>0.1 · 0.5 · 1 · 5 · 10</c> list of ABSOLUTE lengths would be the "one WHAT" defect R-snp-2
    /// exists to avoid.</para>
    ///
    /// <para><c>decimal</c>, not <c>double</c>, for the reason <see cref="LayoutUnits"/> gives at
    /// length: a double cannot represent 1 mil = 25,400 nm exactly, and these products are rounded to
    /// integer DBU. In decimal, 0.1 × 25,400 is 2,540 on the nose.</para>
    /// </summary>
    public static readonly decimal[] Multipliers = [0.1m, 0.5m, 1m, 5m, 10m, 25m, 50m];

    /// <summary>The ladder for a base step of <paramref name="baseDbu"/> (≤ 0 falls back to 1 µm), spelled in
    /// <paramref name="unit"/> with its suffix. A pure function of its arguments — never of the current snap
    /// (brief-snap-ladder-crash.md R-crash-1: an items collection must not be a function of the selection made
    /// from it).</summary>
    public static IReadOnlyList<string> Build(long baseDbu, LayoutUnit unit, int dbuPerMicron)
    {
        if (baseDbu <= 0) baseDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Um, dbuPerMicron);
        baseDbu = NiceBase(baseDbu, unit, dbuPerMicron);
        var rungs = new List<string>(Multipliers.Length);
        long previous = 0;
        foreach (var mult in Multipliers)
        {
            long dbu = (long)decimal.Round(baseDbu * mult, MidpointRounding.AwayFromZero);

            // A sub-unit rung on an already-tiny base quantises to zero DBU — which is not a fine snap
            // but the OFF state (LayoutSnapping's "SnapDbu <= 0 means none"), so offering it as a
            // distance would be a trap. It can also collapse onto the rung below it. Multipliers
            // ascend, so comparing against the last one kept is enough to drop both.
            if (dbu <= 0 || dbu == previous) continue;
            previous = dbu;
            rungs.Add(Spell(dbu, unit, dbuPerMicron));
        }
        return rungs;
    }

    /// <summary>
    /// The base the rungs multiply, as a round number of <paramref name="unit"/> (3D editor bugs round 3). A base that is
    /// already 1, 2, 2.5 or 5 × 10ⁿ of the display unit is kept. One that is not — a 1 µm process shown in mil is
    /// 0.03937 mil, a 1 mil process shown in µm is 25.4 µm — is replaced by the power of ten of the display unit nearest
    /// it (0.1 mil, 10 µm): the old ladder spelled every rung of the base's own unit converted, which offered choices
    /// like 0.0039 mil and 1.9685 mil. A power of ten too small for one DBU keeps the original base.
    /// </summary>
    internal static long NiceBase(long baseDbu, LayoutUnit unit, int dbuPerMicron)
    {
        decimal v = LayoutUnits.FromDbu(baseDbu, unit, dbuPerMicron);
        if (v <= 0) return baseDbu;
        decimal p10 = 1m;
        while (p10 > v) p10 /= 10m;
        while (p10 * 10m <= v) p10 *= 10m;
        decimal mantissa = v / p10;
        if (mantissa is 1m or 2m or 2.5m or 5m) return baseDbu;
        // Nearest power of ten on a log scale: above √10 · 10ⁿ, the next one up.
        decimal nice = (double)mantissa >= Math.Sqrt(10) ? p10 * 10m : p10;
        long dbu = LayoutUnits.ToDbu(nice, unit, dbuPerMicron);
        return dbu > 0 ? dbu : baseDbu;
    }

    /// <summary>A snap distance as the control shows it: <c>1 mil</c>, <c>25.4 µm</c>.</summary>
    public static string Spell(long dbu, LayoutUnit unit, int dbuPerMicron)
        => $"{LayoutUnits.Format(dbu, unit, dbuPerMicron)} {LayoutUnits.Suffix(unit)}";

    /// <summary>Typed entry: a bare number in <paramref name="unit"/> or a unit-suffixed one ("2.5mil", "0.1u"). Zero is
    /// accepted (the off state); anything else unparseable or negative is false.</summary>
    public static bool TryParse(string text, LayoutUnit unit, int dbuPerMicron, out long dbu)
        => LayoutUnits.TryParse(text, unit, dbuPerMicron, out dbu) && dbu >= 0;
}
