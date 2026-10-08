using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CircuitRF.Design.Matching;

/// <summary>
/// The ladders of buyable component values, and the snap onto them — shared by the Smith Chart's
/// discrete toggle (<c>docs/design/smith-chart.md</c> §5.6a) and the optimizer's
/// <c>discrete=preferred</c> (<c>docs/design/tuning-optimization.md</c> §12).
/// </summary>
/// <remarks>
/// <b>Every series here is IEC 60063's</b> — E12, E24 and E96 are published international
/// standards, so the whole tables can be stated without naming anything proprietary
/// (<c>CLAUDE.md</c>'s standing rule, which covers a shipped data table exactly as it covers prose).
///
/// <para><b>What ships</b>: capacitors and inductors on E12 (the Smith Chart's owner decision,
/// 2026-09-21 — the series a narrow RF part range is stocked on), resistors on E24, with E96 available
/// for anyone stocked on 1 % parts. The ladders in force are per-USER and live in the preferences
/// store; a document never carries one, because it would snap to somebody else's parts drawer on
/// somebody else's machine.</para>
/// </remarks>
public static class PreferredValues
{
    /// <summary>The E12 mantissas — IEC 60063's twelve-per-decade series.</summary>
    public static readonly IReadOnlyList<double> E12 =
        [1.0, 1.2, 1.5, 1.8, 2.2, 2.7, 3.3, 3.9, 4.7, 5.6, 6.8, 8.2];

    /// <summary>The E6 mantissas — IEC 60063's six-per-decade series, every other E12 rung.</summary>
    public static readonly IReadOnlyList<double> E6 = [1.0, 1.5, 2.2, 3.3, 4.7, 6.8];

    /// <summary>The E24 mantissas — IEC 60063's twenty-four-per-decade series.</summary>
    public static readonly IReadOnlyList<double> E24 =
        [1.0, 1.1, 1.2, 1.3, 1.5, 1.6, 1.8, 2.0, 2.2, 2.4, 2.7, 3.0,
         3.3, 3.6, 3.9, 4.3, 4.7, 5.1, 5.6, 6.2, 6.8, 7.5, 8.2, 9.1];

    /// <summary>The E96 mantissas — IEC 60063's ninety-six-per-decade (1 %) series.</summary>
    public static readonly IReadOnlyList<double> E96 =
        [1.00, 1.02, 1.05, 1.07, 1.10, 1.13, 1.15, 1.18, 1.21, 1.24, 1.27, 1.30,
         1.33, 1.37, 1.40, 1.43, 1.47, 1.50, 1.54, 1.58, 1.62, 1.65, 1.69, 1.74,
         1.78, 1.82, 1.87, 1.91, 1.96, 2.00, 2.05, 2.10, 2.15, 2.21, 2.26, 2.32,
         2.37, 2.43, 2.49, 2.55, 2.61, 2.67, 2.74, 2.80, 2.87, 2.94, 3.01, 3.09,
         3.16, 3.24, 3.32, 3.40, 3.48, 3.57, 3.65, 3.74, 3.83, 3.92, 4.02, 4.12,
         4.22, 4.32, 4.42, 4.53, 4.64, 4.75, 4.87, 4.99, 5.11, 5.23, 5.36, 5.49,
         5.62, 5.76, 5.90, 6.04, 6.19, 6.34, 6.49, 6.65, 6.81, 6.98, 7.15, 7.32,
         7.50, 7.68, 7.87, 8.06, 8.25, 8.45, 8.66, 8.87, 9.09, 9.31, 9.53, 9.76];

    /// <summary>
    /// Capacitance, <b>0.1 pF … 100 nF</b>, E12.
    /// </summary>
    /// <remarks>
    /// The bottom is the owner's (2026-09-21): 0.1 pF is where an RF chip capacitor range starts, and
    /// below it a matching element stops being a part and becomes a layout feature. The top is a DC
    /// block — 100 nF is a decade past the largest value a narrowband match ever calls for.
    /// </remarks>
    public static IReadOnlyList<double> ShippedCapacitorsFarad { get; } = Series(E12, -13, -8);

    /// <summary>
    /// Inductance, <b>0.1 nH … 100 µH</b>, E12.
    /// </summary>
    /// <remarks>
    /// 0.1 nH is bond-wire scale and the smallest value a chip inductor range carries; 100 µH is an
    /// RF choke.
    /// </remarks>
    public static IReadOnlyList<double> ShippedInductorsHenry { get; } = Series(E12, -10, -5);

    /// <summary>
    /// Resistance, <b>1 Ω … 10 MΩ</b>, E24 — the series a general resistor range is stocked on. The
    /// E96 ladder over the same span is <see cref="ResistorsE96Ohm"/>.
    /// </summary>
    public static IReadOnlyList<double> ShippedResistorsOhm { get; } = Series(E24, 0, 6);

    /// <summary>E96 over the shipped resistor span, 1 Ω … 10 MΩ.</summary>
    public static IReadOnlyList<double> ResistorsE96Ohm { get; } = Series(E96, 0, 6);

    /// <summary>The shipped ladder of a quantity, or null for one with no ladder.</summary>
    public static IReadOnlyList<double>? Shipped(MatchQuantity quantity) => quantity switch
    {
        MatchQuantity.Capacitance => ShippedCapacitorsFarad,
        MatchQuantity.Inductance  => ShippedInductorsHenry,
        MatchQuantity.Resistance  => ShippedResistorsOhm,
        _                         => null,
    };

    /// <summary>True when <paramref name="quantity"/> has a ladder at all.</summary>
    public static bool HasLadder(MatchQuantity quantity) => Shipped(quantity) is not null;

    /// <summary>
    /// The quantity a parameter's UNIT names — what a tunable's <c>discrete=preferred</c> snaps by
    /// (brief-tuneopt-8 R-to8-2): farads, henries, ohms at any SI prefix; null for anything else.
    /// </summary>
    public static MatchQuantity? QuantityOfUnit(string? unit) => CircuitRF.Core.Expressions.Units.BaseUnit(unit ?? "") switch
    {
        "F"                                => MatchQuantity.Capacitance,
        "H"                                => MatchQuantity.Inductance,
        "Ohm" or "ohm" or "Ohms" or "ohms" => MatchQuantity.Resistance,
        _                                  => null,
    };

    // ── the snap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The entry of <paramref name="ladder"/> nearest <paramref name="value"/>, <b>measured as a
    /// RATIO and not as a difference</b>.
    /// </summary>
    /// <remarks>
    /// <b>Nearest in log space is the only nearest that means anything here.</b> A ladder is
    /// geometric — the gap from 1.0 to 1.2 pF is 0.2 pF and the gap from 68 to 82 pF is 14 pF — so a
    /// linear nearest would be dominated by whichever decade the value happens to be in and would
    /// pull 1.09 pF up to 1.2 rather than down to 1.0. The midpoint between two rungs is their
    /// geometric mean, which is what a component tolerance is symmetric about.
    ///
    /// <para><b>A value that is not strictly positive is returned untouched.</b> Zero henries is a
    /// wire and zero farads is an open circuit — both are meaningful entries and neither is a small
    /// part, so neither is snapped to the bottom rung. An empty ladder likewise returns the value
    /// unchanged rather than having nothing to return.</para>
    ///
    /// <para><b>A value past either end lands ON that end.</b> Nearest is nearest.</para>
    /// </remarks>
    public static double Snap(double value, IReadOnlyList<double>? ladder)
    {
        if (ladder is null || ladder.Count == 0) return value;
        if (!double.IsFinite(value) || value <= 0.0) return value;

        double best = ladder[0];
        double bestRatio = double.PositiveInfinity;

        foreach (double rung in ladder)
        {
            if (!(rung > 0) || !double.IsFinite(rung)) continue;
            double ratio = Math.Abs(Math.Log(value / rung));
            if (ratio < bestRatio) { bestRatio = ratio; best = rung; }
        }

        return double.IsFinite(bestRatio) ? best : value;
    }

    /// <summary>
    /// The rungs of <paramref name="ladder"/> either side of <paramref name="value"/> — the largest at
    /// or below it and the smallest at or above it; one rung twice when the value sits on one or past
    /// an end. What snap-and-polish's neighbour set is built from (brief-tuneopt-8 R-to8-4).
    /// </summary>
    public static (double Below, double Above) Bracket(double value, IReadOnlyList<double> ladder)
    {
        double below = double.NaN, above = double.NaN;
        foreach (double rung in ladder)
        {
            if (rung <= value && (double.IsNaN(below) || rung > below)) below = rung;
            if (rung >= value && (double.IsNaN(above) || rung < above)) above = rung;
        }
        if (double.IsNaN(below)) below = above;
        if (double.IsNaN(above)) above = below;
        return (below, above);
    }

    // ── the list as text (the editor's two modes read and write this) ────────

    /// <summary>
    /// The unit a BARE number in one of these lists is read as, and the smallest unit a formatted
    /// list is allowed to reach for.
    /// </summary>
    /// <remarks>
    /// <b>The floor is what makes the list read like a parts list.</b> The Auto ladder picks the
    /// largest prefix that leaves the value at or above 1, so the bottom of the capacitor ladder
    /// comes out as <c>100 fF … 820 fF</c> — arithmetically right, and not how anybody writes an RF
    /// capacitor. The owner's own floor is 0.1 pF, spelled that way. Above the floor Auto takes
    /// over, so the top is <c>100 nF</c> rather than <c>100000 pF</c>. A resistor's floor is the
    /// ohm.
    /// </remarks>
    public static string BareUnit(MatchQuantity quantity) => quantity switch
    {
        MatchQuantity.Capacitance => "pF",
        MatchQuantity.Resistance  => "Ω",
        _                         => "nH",
    };

    /// <summary>A ladder as the editor shows it — <b>one value per line, each with its unit</b>.</summary>
    public static string Format(IEnumerable<double> values, MatchQuantity quantity)
    {
        var sb = new StringBuilder();
        foreach (double v in values)
            sb.AppendLine(FormatOne(v, quantity));
        return sb.ToString();
    }

    /// <summary>One value, with its unit — the spelling a row shows and the text mode writes. See
    /// <see cref="BareUnit"/> for why it does not simply hand the value to Auto.</summary>
    public static string FormatOne(double value, MatchQuantity quantity)
    {
        string floor = BareUnit(quantity);
        string unit  = Math.Abs(value) < MatchValueFormat.Scale(floor)
            ? floor
            : MatchValueFormat.AutoUnit;
        return MatchValueFormat.FormatWithUnit(value, quantity, unit, 5);
    }

    /// <summary>
    /// Reads a pasted or typed ladder.
    /// </summary>
    /// <remarks>
    /// <b>The comma is a SEPARATOR here and never a decimal point</b>, which is the one place this
    /// field departs from circuitRF's own decimal-comma rule — and the departure is that rule's own
    /// (<c>NumericText.NormalizeDecimalSeparator</c>: "the same holds for any field whose own grammar
    /// separates values with commas … which is why that text must never be passed through here").
    /// <c>1,2,3</c> in a list is either three values or two, and nothing in the text says which; a
    /// `.cnl`'s <c>Values=</c> list settled the same question the same way.
    ///
    /// <para><b>Tolerant about what it splits on, strict about what a field contains.</b> Newlines,
    /// commas, semicolons, tabs and blank entries all separate; a <c>#</c> comment runs to the end of
    /// its line. Each field is then the ordinary <c>"value unit"</c> an <c>InlineEditText</c> takes,
    /// with a bare number read as <paramref name="fallbackUnit"/> — and a unit from the WRONG ladder
    /// is a refusal rather than a token to discard, so <c>2.2 nH</c> in the capacitor list says so.
    /// </para>
    ///
    /// <para><b>The result is sorted and de-duplicated, and an EMPTY list is refused.</b> A ladder
    /// with nothing on it would leave a snap switched on and doing nothing, with no symptom at all.
    /// </para>
    /// </remarks>
    public static bool TryParse(
        string? text, MatchQuantity quantity, string fallbackUnit,
        out IReadOnlyList<double> values, out string? error)
    {
        values = [];
        error  = null;

        var parsed = new List<double>();

        foreach (string rawLine in (text ?? "").Split('\n'))
        {
            string line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];

            foreach (string field in line.Split([',', ';', '\t', '\r'], StringSplitOptions.TrimEntries))
            {
                if (field.Length == 0) continue;

                if (!MatchValueFormat.TryParseWithUnit(field, quantity, fallbackUnit, out double v, out _))
                {
                    error = $"\"{field}\" is not {Noun(quantity)}. Write a number with its unit " +
                            $"(\"{Example(quantity)}\"), or a bare number, which is read as {fallbackUnit}.";
                    return false;
                }

                if (!(v > 0) || !double.IsFinite(v))
                {
                    error = $"\"{field}\" is not a value a part can have. Every entry has to be " +
                            "greater than zero.";
                    return false;
                }

                parsed.Add(v);
            }
        }

        if (parsed.Count == 0)
        {
            error = "A list with nothing in it would leave the toggle switched on and doing " +
                    "nothing, silently. Add a value, or press Revert to shipped values.";
            return false;
        }

        parsed.Sort();

        // De-duplicated by RATIO, for Snap's own reason: two entries a part in a million apart are
        // one rung typed twice, and formatting them would print the same string on two rows.
        var unique = new List<double>(parsed.Count);
        foreach (double v in parsed)
            if (unique.Count == 0 || v / unique[^1] > 1.0 + 1e-9)
                unique.Add(v);

        values = unique;
        return true;
    }

    private static string Noun(MatchQuantity q) => q switch
    {
        MatchQuantity.Capacitance => "a capacitance",
        MatchQuantity.Resistance  => "a resistance",
        _                         => "an inductance",
    };

    private static string Example(MatchQuantity q) => q switch
    {
        MatchQuantity.Capacitance => "2.2 pF",
        MatchQuantity.Resistance  => "2.2 kΩ",
        _                         => "2.2 nH",
    };

    // ── building a ladder ────────────────────────────────────────────────────

    /// <summary>The E48 mantissas — IEC 60063's forty-eight-per-decade (2 %) series, every other E96 rung.</summary>
    public static readonly IReadOnlyList<double> E48 = [.. E96.Where((_, i) => i % 2 == 0)];

    /// <summary>
    /// Every rung of the series <paramref name="mantissas"/> inside [<paramref name="lo"/>,
    /// <paramref name="hi"/>], ascending — in whatever unit the bounds are in, since a series is the same
    /// in every decade. Empty when the range holds no rung or does not reach above zero; a range that
    /// starts at or below zero is taken from three decades under <paramref name="hi"/>.
    /// </summary>
    public static IReadOnlyList<double> Between(IReadOnlyList<double> mantissas, double lo, double hi)
    {
        var list = new List<double>();
        if (!(hi > 0) || !double.IsFinite(lo) || !double.IsFinite(hi) || hi < lo) return list;
        double bottom = lo > 0 ? lo : hi / 1e3;
        int first = (int)Math.Floor(Math.Log10(bottom)) - 1, last = (int)Math.Ceiling(Math.Log10(hi)) + 1;
        for (int decade = first; decade <= last; decade++)
        {
            double mag = Math.Pow(10.0, decade);
            foreach (double m in mantissas)
            {
                // Rounded to the mantissa's own figures, so 4.7e-9 is not 4.700000000000001e-9.
                double v = double.Parse((m * mag).ToString("G12", System.Globalization.CultureInfo.InvariantCulture),
                                        System.Globalization.CultureInfo.InvariantCulture);
                if (v >= lo * (1 - 1e-12) && v <= hi * (1 + 1e-12)) list.Add(v);
            }
        }
        return list;
    }

    /// <summary>
    /// A series' mantissas across the decades <paramref name="first"/> … <paramref name="last"/>
    /// (powers of ten), <b>plus the next decade's first rung</b> so the range closes on a round number
    /// rather than on 8.2 of it.
    /// </summary>
    public static IReadOnlyList<double> Series(IReadOnlyList<double> mantissas, int first, int last)
    {
        var list = new List<double>((last - first + 1) * mantissas.Count + 1);

        for (int decade = first; decade <= last; decade++)
        {
            double mag = Math.Pow(10.0, decade);
            foreach (double m in mantissas) list.Add(m * mag);
        }

        list.Add(Math.Pow(10.0, last + 1));
        return list;
    }
}

/// <summary>
/// The three ladders in force for one run (brief-tuneopt-8 R-to8-2) — a preference passed as an
/// ARGUMENT (<c>src/Design/CLAUDE.md</c>): the GUI hands in the user's, a headless run the shipped
/// ones.
/// </summary>
public sealed record PreferredLadders(
    IReadOnlyList<double> CapacitorsFarad,
    IReadOnlyList<double> InductorsHenry,
    IReadOnlyList<double> ResistorsOhm)
{
    /// <summary>What circuitRF ships.</summary>
    public static PreferredLadders Shipped { get; } = new(
        PreferredValues.ShippedCapacitorsFarad, PreferredValues.ShippedInductorsHenry, PreferredValues.ShippedResistorsOhm);

    /// <summary>The ladder of a quantity, in base SI, or null for one with no ladder.</summary>
    public IReadOnlyList<double>? For(MatchQuantity quantity) => quantity switch
    {
        MatchQuantity.Capacitance => CapacitorsFarad,
        MatchQuantity.Inductance  => InductorsHenry,
        MatchQuantity.Resistance  => ResistorsOhm,
        _                         => null,
    };
}
