using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Pdk;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Statistics;

/// <summary>One kit corner axis as a design is set on it: its label, its corner file, and the section chosen
/// (null = none chosen, which binds the kit's first, nominal section).</summary>
public sealed record KitCornerAxisState(string Axis, string File, string? Selected);

/// <summary>
/// What a design's distribution calls amount to (docs/design/yield.md §7): how many process and mismatch streams,
/// which selected corner sections brought statistics, and an Info note per kit axis whose statistical section is
/// not selected — the user is one click away from it.
/// </summary>
/// <param name="Calls">Every distribution call the elaboration reached, once per stream.</param>
/// <param name="Sections">The selected corner sections that carry distribution calls, as <c>section (file)</c>.</param>
/// <param name="Notes">Info notes: statistical sections the kit offers and the design has not selected.</param>
public sealed record KitStatisticsReport(
    IReadOnlyList<StatisticalCall> Calls,
    IReadOnlyList<string>          Sections,
    IReadOnlyList<Diagnostic>      Notes)
{
    public int Process  => Calls.Count(c => c.Kind == StatisticalKind.Process);
    public int Mismatch => Calls.Count(c => c.Kind == StatisticalKind.Mismatch);
    public bool Any     => Calls.Count > 0;
}

/// <summary>Discovery and the refusal for a design's own and its kit's statistics (R-ya3-5).</summary>
public static class KitStatistics
{
    /// <param name="calls">The elaboration's calls (<c>ElaboratedNetlist.StatisticalCalls</c>).</param>
    /// <param name="selected">The corner sections the design's selections read (<c>WorkspaceCorners.Bind</c>).</param>
    /// <param name="axes">The kit axes the workspace offers and what the design chose on each; null where no
    /// workspace corners are at hand (the CLI today), which says nothing about unselected sections.</param>
    public static KitStatisticsReport Report(
        IReadOnlyList<StatisticalCall> calls,
        IEnumerable<PdkCornerSection>? selected = null,
        IEnumerable<KitCornerAxisState>? axes = null)
    {
        var sections = (selected ?? [])
            .Where(s => s.Statistics.Count > 0)
            .Select(s => $"{s.Section} ({Path.GetFileName(s.AxisFile)})")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var notes = new List<Diagnostic>();
        foreach (var axis in axes ?? [])
        {
            var statistical = PdkCorners.StatisticalSections(axis.File);
            if (statistical.Count == 0) continue;
            if (axis.Selected is { Length: > 0 } chosen &&
                statistical.Any(s => s.Equals(chosen, StringComparison.OrdinalIgnoreCase))) continue;

            string named = statistical.Count == 1
                ? $"section '{statistical[0]}' is"
                : $"sections {string.Join(", ", statistical.Select(s => $"'{s}'"))} are";
            notes.Add(StatisticsDiagnostics.StatisticalSectionNotSelected(axis.Axis, named));
        }

        return new KitStatisticsReport([.. calls], sections, notes);
    }

    /// <summary>
    /// The refusal a Monte Carlo run with a yield goal states when nothing would vary: no tune entry carries a
    /// tolerance and the design holds no distribution call. Null when something varies, or when no enabled goal
    /// is a yield spec (a Monte Carlo of the nominal alone is not asked for by anything).
    /// </summary>
    public static Diagnostic? NothingVaries(TuningSetup setup, IReadOnlyList<StatisticalCall> calls)
    {
        if (!setup.Goals.Any(g => g.Enabled && g.ForYield)) return null;
        if (setup.Variables.Any(e => e.IsStatistical) || calls.Count > 0) return null;
        return StatisticsDiagnostics.RunNothingVaries();
    }
}
