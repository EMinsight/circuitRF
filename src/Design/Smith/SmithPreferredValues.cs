using System;
using System.Collections.Generic;
using CircuitRF.Design.Matching;

namespace CircuitRF.Design.Smith;

/// <summary>
/// The discrete component values a Smith Chart design may be restricted to, and the snap onto them
/// (<c>docs/design/smith-chart.md</c> §5.6a). The ladder arithmetic itself — the series, the snap,
/// the list text — is <see cref="PreferredValues"/>'s, shared with the optimizer; what is here is the
/// Smith Chart's own rule about WHICH of its values are on a ladder.
/// </summary>
/// <remarks>
/// <b>A real matching network is built out of parts somebody can buy.</b> The sliders and the
/// grippers are continuous, so a two-element match lands on 2.37 nH and 1.64 pF — values that cannot
/// be ordered, and whose chart is therefore a picture of a circuit nobody will build. With
/// <see cref="SmithDesign.SnapToPreferredValues"/> on, every value an edit produces lands on one of
/// these ladders instead, so what the chart draws is what the bench will do.
///
/// <para><b>The shipped ladders are E12, the IEC 60063 preferred numbers</b> (owner decision,
/// 2026-09-21) — twelve values per decade, <c>1.0 1.2 1.5 1.8 2.2 2.7 3.3 3.9 4.7 5.6 6.8 8.2</c>,
/// which is the series a narrow RF part range is stocked on: close enough that snapping to it costs
/// little, coarse enough that what you land on is a value somebody carries. It is a published
/// international standard, so the whole table can be stated here without naming anything
/// proprietary — <c>CLAUDE.md</c>'s standing rule, which covers a shipped data table exactly as it
/// covers prose. <b>Anyone stocked on something else pastes their own list in</b>, which is what
/// makes the shipped choice cheap rather than a compromise — see the editor in
/// <c>src/Ui/Smith</c>.</para>
///
/// <para><b>Inductance and capacitance only</b> (owner decision, 2026-09-21). A resistance in this
/// vocabulary is as often a parasitic as a part: the <c>R</c> of an <c>SRLC</c>, an <c>SRL</c> or a
/// <c>PRC</c> is an ESR or a leakage term, something measured rather than ordered, and snapping it
/// to a preferred value states something untrue about the design. A line's Z₀, an electrical length
/// and a <c>Z1P</c>'s two parts are continuous quantities by construction and are not on any
/// ladder at all.</para>
/// </remarks>
public static class SmithPreferredValues
{
    /// <summary>The E12 mantissas — IEC 60063's twelve-per-decade series.</summary>
    public static IReadOnlyList<double> E12 => PreferredValues.E12;

    /// <summary>Capacitance, <b>0.1 pF … 100 nF</b> (<see cref="PreferredValues.ShippedCapacitorsFarad"/>).</summary>
    public static IReadOnlyList<double> ShippedCapacitorsFarad => PreferredValues.ShippedCapacitorsFarad;

    /// <summary>Inductance, <b>0.1 nH … 100 µH</b> (<see cref="PreferredValues.ShippedInductorsHenry"/>).</summary>
    public static IReadOnlyList<double> ShippedInductorsHenry => PreferredValues.ShippedInductorsHenry;

    /// <summary>
    /// The ladder for one parameter, or null for a parameter that is not on one.
    /// </summary>
    /// <remarks>
    /// <b>This is the one place the L-and-C rule is written down.</b> Every caller asks here rather
    /// than testing the parameter itself, so a vocabulary that grows a member cannot grow a snap
    /// nobody decided on. The resistor ladder <see cref="PreferredValues"/> also holds is for the
    /// optimizer; it is not consulted here.
    /// </remarks>
    public static IReadOnlyList<double>? LadderFor(
        SmithParameter p,
        IReadOnlyList<double> capacitorsFarad,
        IReadOnlyList<double> inductorsHenry) => p switch
    {
        SmithParameter.C => capacitorsFarad,
        SmithParameter.L => inductorsHenry,
        _                => null,
    };

    /// <summary>True when <paramref name="p"/> is a parameter this feature restricts at all.</summary>
    public static bool AppliesTo(SmithParameter p) => p is SmithParameter.L or SmithParameter.C;

    /// <summary>The quantity a ladder's numbers are — what formats and parses them.</summary>
    public static MatchQuantity QuantityOf(SmithParameter p)
        => p == SmithParameter.C ? MatchQuantity.Capacitance : MatchQuantity.Inductance;

    // ── the snap ─────────────────────────────────────────────────────────────

    /// <summary>The rung nearest <paramref name="value"/> by RATIO — <see cref="PreferredValues.Snap"/>,
    /// which says why, and what a non-positive value or an empty ladder does.</summary>
    public static double Snap(double value, IReadOnlyList<double>? ladder) => PreferredValues.Snap(value, ladder);

    /// <summary>
    /// Snaps every snappable value in <paramref name="design"/> and returns how many MOVED — what
    /// turning the toggle on does.
    /// </summary>
    /// <remarks>
    /// <b>It walks the parameter table rather than the kinds</b>
    /// (<see cref="SmithComponentMap.Parameters"/>), so the eight RLC-family members are covered by
    /// the same three lines as the bare <c>L</c> and <c>C</c> and a kind added later needs nothing
    /// here. A disabled element is snapped too: it is still part of the design, it is drawn the
    /// moment it is re-enabled, and a value that changed the next time it was switched on would be
    /// the worst of both.
    /// </remarks>
    public static int SnapDesign(
        SmithDesign design,
        IReadOnlyList<double> capacitorsFarad,
        IReadOnlyList<double> inductorsHenry)
    {
        int moved = 0;

        foreach (var element in design.Elements)
        {
            foreach (var p in SmithComponentMap.Parameters(element.Kind))
            {
                if (LadderFor(p, capacitorsFarad, inductorsHenry) is not { } ladder) continue;

                double before = p == SmithParameter.C ? element.Values.CFarad : element.Values.LHenry;
                double after  = Snap(before, ladder);
                if (after == before) continue;

                if (p == SmithParameter.C) element.Values.CFarad = after;
                else                       element.Values.LHenry = after;
                moved++;
            }
        }

        return moved;
    }

    // ── the list as text — PreferredValues' own, for the editor ──────────────

    /// <inheritdoc cref="PreferredValues.BareUnit"/>
    public static string BareUnit(MatchQuantity quantity) => PreferredValues.BareUnit(quantity);

    /// <inheritdoc cref="PreferredValues.Format"/>
    public static string Format(IEnumerable<double> values, MatchQuantity quantity) => PreferredValues.Format(values, quantity);

    /// <inheritdoc cref="PreferredValues.FormatOne"/>
    public static string FormatOne(double value, MatchQuantity quantity) => PreferredValues.FormatOne(value, quantity);

    /// <inheritdoc cref="PreferredValues.TryParse"/>
    public static bool TryParse(
        string? text, MatchQuantity quantity, string fallbackUnit,
        out IReadOnlyList<double> values, out string? error)
        => PreferredValues.TryParse(text, quantity, fallbackUnit, out values, out error);
}
