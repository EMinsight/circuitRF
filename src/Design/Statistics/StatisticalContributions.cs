using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// Which statistical variables drive a scalar (R-ya4-9, docs/design/yield.md §8.6): the scalar over the evaluated
/// trials regressed (<see cref="Regression"/>) on every stream's z-value, then gathered into contributors — a
/// statistical entry, a kit process draw, or ALL of one instance's mismatch draws, because a designer acts on an
/// instance, not on a stream.
/// </summary>
internal static class StatisticalContributions
{
    public static ContributionReport Of(string name, IReadOnlyList<TrialRecord> records, IReadOnlyList<OptimizationGoal> goals)
    {
        bool isGoal = goals.Any(g => g.Name == name);
        double Value(TrialRecord r)
        {
            if (isGoal) return r.Goals.FirstOrDefault(s => s.Name == name)?.WorstValue ?? double.NaN;
            return r.Scalars.TryGetValue(name, out double v) ? v : double.NaN;
        }
        if (!isGoal && !records.Any(r => r.Scalars.ContainsKey(name)))
        {
            var known = goals.Select(g => g.Name).Concat(records.SelectMany(r => r.Scalars.Keys)).Distinct().ToList();
            return Refused(name, StatisticsDiagnostics.ContributionUnknown(name, known.Count == 0 ? "nothing" : string.Join(", ", known)));
        }

        var used = records.Where(r => r.Evaluated && double.IsFinite(Value(r))).ToList();
        if (used.Count < 3) return Refused(name, StatisticsDiagnostics.ContributionTooFew(name, used.Count));

        // The streams, in a fixed order: entries as the setup lists them, then kit streams by kind and name.
        var columns = new List<(string Stream, string Contributor, string Kind)>();
        foreach (var key in used.SelectMany(r => r.Z.Keys).Distinct(StringComparer.Ordinal))
            columns.Add((key, key, "entry"));
        foreach (var (stream, kind) in used.SelectMany(r => r.Kit.Select(kv => (kv.Key, kv.Value.Kind)))
                                           .Distinct().OrderBy(s => s.Kind).ThenBy(s => s.Key, StringComparer.Ordinal))
            columns.Add(kind == StatisticalKind.Process
                ? ("kit:" + stream, stream, "process")
                : ("kit:" + stream, InstanceOf(stream), "mismatch"));
        if (columns.Count == 0) return Refused(name, StatisticsDiagnostics.ContributionNothingVaries());

        // A stream a trial did not draw sat at its nominal: z = 0.
        var x = used.Select(r => columns.Select(c => c.Stream.StartsWith("kit:", StringComparison.Ordinal)
                    ? r.Kit.TryGetValue(c.Stream[4..], out var d) ? d.Z : 0
                    : r.Z.TryGetValue(c.Stream, out double z) ? z : 0).ToArray()).ToList();
        var y = used.Select(Value).ToList();
        var fit = Regression.Fit(x, y);

        var contributors = new List<Contributor>();
        foreach (var group in columns.Select((c, j) => (c, j)).GroupBy(t => (t.c.Contributor, t.c.Kind)))
        {
            int[] js = [.. group.Select(t => t.j)];
            double coefficient = js.Length == 1 ? fit.Coefficients[js[0]] : Math.Sqrt(js.Sum(j => fit.Coefficients[j] * fit.Coefficients[j]));
            double share = js.Sum(j => fit.Shares[j]);
            var linear = x.Select(row => js.Sum(j => fit.Coefficients[j] * row[j])).ToList();
            double rho = js.Length == 1 ? Regression.Spearman(x.Select(row => row[js[0]]).ToList(), y) : Regression.Spearman(linear, y);
            contributors.Add(new Contributor(group.Key.Contributor, group.Key.Kind, coefficient, share, rho, js.Length));
        }
        contributors.Sort((a, b) => b.Share.CompareTo(a.Share));
        return new ContributionReport(name, contributors, fit.RSquared, fit.Underdetermined, used.Count);
    }

    /// <summary>The instance a mismatch stream belongs to: its site <c>&lt;instance&gt;.&lt;param&gt;</c> less the
    /// parameter, and less the <c>#n</c> a second call in one site carries.</summary>
    internal static string InstanceOf(string stream)
    {
        int hash = stream.IndexOf('#');
        string site = hash < 0 ? stream : stream[..hash];
        int dot = site.LastIndexOf('.');
        return dot < 0 ? site : site[..dot];
    }

    private static ContributionReport Refused(string name, CircuitRF.Diagnostics.Diagnostic why)
        => new(name, [], double.NaN, false, 0, why);
}
