using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The statistical half of <c>explain</c> (docs/design/yield.md): each tunable's tolerance for
/// <c>--tunables</c>, and for <c>--analysis</c> the effective statistics settings, the goals by what
/// they serve, the corners, the correlation matrix a run would use and what the trial count can
/// resolve. It owns no rule — the numbers are <see cref="StatisticsSummary"/>'s and
/// <see cref="StatisticsValidator"/>'s.
/// </summary>
internal static class ExplainStatistics
{
    /// <summary>The <c>stat</c> object of one tunable row; null when its entry has no distribution.</summary>
    public static ExplainStatJson? Stat(TunableEntry? e, Tunable t)
    {
        if (e is null || e.Distribution == StatDistribution.None || StatisticsSummary.Describe(e, t) is not { } text)
            return null;
        var r = ResolvedSpread.Of(e, t);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.Spread is { } sp)
            foreach (var (k, v) in new[] { ("sd", sp.Sd), ("tol", sp.Tol), ("sigmas", sp.Sigmas), ("lo", sp.Lo),
                                           ("hi", sp.Hi), ("by", sp.By), ("trunc", sp.Trunc) })
                if (v is not null) written[k] = v;
        return new ExplainStatJson(
            AnalysisDirectiveSchema.DistTokens[(int)e.Distribution], e.Stat, text,
            Core.Expressions.Units.BaseUnit(t.Unit), r?.Sigma, r?.Lo, r?.Hi, r?.Step, r?.Trunc, written);
    }

    /// <summary>The statistical report of a testbench; null when its setup has no statistical content.</summary>
    public static ExplainStatisticsJson? Collect(TestBench tb)
    {
        if (tb.Tuning is not { } setup || !HasStatistics(setup)) return null;
        var s = setup.Statistics ?? new StatisticsSettings();
        var (halfWidth, trialsFor) = StatisticsSummary.ExpectedInterval(s.EffectiveTrials, s.EffectiveConfidence);

        ExplainCorrelationJson? correlation = null;
        if (StatisticsValidator.CorrelationOf(setup) is { } m)
        {
            int n = m.Keys.Count;
            var rows = Enumerable.Range(0, n)
                .Select(i => (IReadOnlyList<double>)[.. Enumerable.Range(0, n).Select(j => m.Matrix[i, j])]).ToList();
            correlation = new ExplainCorrelationJson(m.Keys, rows, m.Repaired, m.LargestChange);
        }

        return new ExplainStatisticsJson(
            s.EffectiveTrials, s.EffectiveSeed,
            AnalysisDirectiveSchema.SamplingTokens[(int)s.Sampling], s.Target, s.EffectiveConfidence, s.AutoStop,
            AnalysisDirectiveSchema.NonConvergedTokens[(int)s.NonConverged], s.Save ?? "auto",
            s.Process ?? true, s.Mismatch ?? true, s.SigmaScale ?? 1, s.Parallelism,
            AnalysisDirectiveSchema.ScopeTokens[(int)s.Scope], s.Corners ?? "none",
            [.. setup.Variables.Where(e => e.IsStatistical).Select(e => e.Key)],
            [.. setup.Goals.Select(g => new ExplainGoalUseJson(g.Name, AnalysisDirectiveSchema.UseTokens[(int)g.Use], g.Enabled))],
            [.. setup.Corners.Select(c => new ExplainCornerJson(
                c.Name, c.Enabled, c.IsStatistical ? "statistical" : "value", c.Temp,
                new Dictionary<string, string>(c.Values, StringComparer.Ordinal), c.Trial))],
            correlation, StatisticsSummary.QuotedYield, halfWidth, trialsFor);
    }

    private static bool HasStatistics(TuningSetup setup)
        => setup.Statistics is not null || setup.Correlations.Count > 0 || setup.Corners.Count > 0
        || setup.Variables.Any(e => e.Distribution != StatDistribution.None)
        || setup.Goals.Any(g => g.Use != GoalUse.Both);

    public static void Print(ExplainStatisticsJson r)
    {
        static string N(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        Console.WriteLine();
        Console.WriteLine("Statistics:");
        Console.WriteLine($"  trials {r.Trials} · seed {r.Seed} · sampling {r.Sampling} · confidence {N(r.Confidence)} %" +
                          (r.Target is { } t ? $" · target {N(t)} %" : "") + (r.AutoStop ? " · autostop" : "") +
                          $" · non-converged {r.NonConverged} · save {r.Save}");
        Console.WriteLine($"  kit process {(r.Process ? "on" : "off")} · mismatch {(r.Mismatch ? "on" : "off")} · sigma scale {N(r.SigmaScale)}" +
                          $" · analyses {r.Analyses} · corners {r.Corners}");
        Console.WriteLine($"  statistical entries: {(r.StatisticalEntries.Count == 0 ? "none" : string.Join(", ", r.StatisticalEntries))}");
        foreach (var use in new[] { "yield", "both", "opt" })
        {
            var names = r.Goals.Where(g => g.Use == use).Select(g => g.Enabled ? g.Name : g.Name + " (disabled)").ToList();
            if (names.Count > 0) Console.WriteLine($"  goals use={use}: {string.Join(", ", names)}");
        }
        foreach (var c in r.CornerList)
        {
            string what = c.Kind == "statistical" ? $"trial {c.Trial}"
                        : string.Join(" ", (c.Temp is { } temp ? new[] { $"temp={temp}" } : []).Concat(c.Values.Select(kv => $"{kv.Key}={kv.Value}")));
            Console.WriteLine($"  corner {c.Name}{(c.Enabled ? "" : " (disabled)")}: {what}");
        }
        if (r.Correlation is { } m)
        {
            Console.WriteLine($"  correlation over {string.Join(", ", m.Keys)}" +
                              (m.Repaired ? $" — repaired to the nearest valid matrix, largest change {N(m.LargestChange)}" : ""));
            foreach (var row in m.Matrix) Console.WriteLine("    " + string.Join("  ", row.Select(v => v.ToString("F4", CultureInfo.InvariantCulture).PadLeft(7))));
        }
        Console.WriteLine($"  at a yield of {N(r.AtYield)} %, {r.Trials} trials resolve it to ±{N(r.ExpectedHalfWidth)} %" +
                          (r.TrialsForTwoPercent is { } n ? $"; {n} trials resolve it to under ±{N(StatisticsSummary.QuotedHalfWidth)} %" : ""));
    }
}
