using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A setup's statistical part said in words and numbers — what <c>explain</c> prints and the Yield
/// panel shows (docs/design/yield.md §5). It decides nothing; the numbers come from
/// <see cref="ResolvedSpread"/> and <see cref="ClopperPearson"/>.
/// </summary>
public static class StatisticsSummary
{
    /// <summary>The yield the expected-interval figure is quoted at, in percent.</summary>
    public const double QuotedYield = 90;

    /// <summary>The half-width the trial-count figure aims under, in percent.</summary>
    public const double QuotedHalfWidth = 2;

    /// <summary>
    /// One entry's spread in words: <c>gauss σ = 1 Ohm (2 %)</c>, <c>unif 45 Ohm .. 55 Ohm (±10 %)</c>,
    /// <c>discrete 4 .. 8 by 2</c>; with <c>, truncated at ±3σ</c> and <c> — off (stat=0)</c> where they
    /// apply. Null for an entry with no distribution.
    /// </summary>
    public static string? Describe(TunableEntry e, Tunable t)
    {
        if (e.Distribution == StatDistribution.None) return null;
        string dist = StatisticsValidator.Word(e.Distribution);
        string off = e.Stat ? "" : " — off (stat=0)";
        if (ResolvedSpread.Of(e, t) is not { } r) return $"{dist} (spread incomplete){off}";

        string text = r.Distribution switch
        {
            StatDistribution.Gauss or StatDistribution.LogNorm
                => $"{dist} σ = {InUnit(r.Sigma!.Value, t.Unit)}{Relative(r.Sigma!.Value, r.NominalSi)}"
                 + (r.Trunc is { } k ? $", truncated at ±{k.ToString("G6", CultureInfo.InvariantCulture)}σ" : ""),
            StatDistribution.Unif
                => $"{dist} {InUnit(r.Lo!.Value, t.Unit)} .. {InUnit(r.Hi!.Value, t.Unit)}"
                 + (e.Spread?.Tol is not null ? Relative((r.Hi!.Value - r.Lo!.Value) / 2, r.NominalSi, plusMinus: true) : ""),
            _   => $"{dist} {InUnit(r.Lo!.Value, t.Unit)} .. {InUnit(r.Hi!.Value, t.Unit)} by {InUnit(r.Step!.Value, t.Unit)}",
        };
        return text + off;
    }

    /// <summary>
    /// What <paramref name="trials"/> trials can resolve: the expected half-width of the Clopper–Pearson
    /// yield interval at a yield of <see cref="QuotedYield"/> %, and the fewest trials that bring it under
    /// ±<see cref="QuotedHalfWidth"/> %. Both in percent points. NaN and null for settings the validator refuses — a
    /// confidence outside (0, 100) % or no trials — which have no interval to quote (brief-yield-15 R-ya15-1).
    /// </summary>
    public static (double HalfWidth, int? TrialsForQuoted) ExpectedInterval(int trials, double confidencePercent)
    {
        double c = confidencePercent / 100;
        if (!(c > 0 && c < 1) || trials < 1) return (double.NaN, null);
        double hw = ClopperPearson.ExpectedHalfWidth(QuotedYield / 100, trials, c) * 100;
        return (hw, ClopperPearson.TrialsFor(QuotedYield / 100, QuotedHalfWidth / 100, c));
    }

    private static string InUnit(double si, string unit)
    {
        double scale = unit.Length == 0 ? 1 : Core.Expressions.Units.Scale(unit) ?? 1;
        return TunableValue.Format(si / scale, unit, "G6");
    }

    private static string Relative(double width, double nominal, bool plusMinus = false)
        => nominal == 0 ? ""
         : $" ({(plusMinus ? "±" : "")}{(width / Math.Abs(nominal) * 100).ToString("G4", CultureInfo.InvariantCulture)} %)";
}
