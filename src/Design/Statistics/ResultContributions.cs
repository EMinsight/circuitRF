using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using Optimization = CircuitRF.Design.Optimization;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// Contributions (R-ya4-9) from a saved Monte Carlo result rather than a run in memory (brief-yield-9 R-ya9-5), and
/// the cube a Data Display draws them from. A <c>.yield.npy</c> keeps everything the ranking reads — each entry's and
/// kit stream's z per trial, each goal's worst value, each scalar measure, each trial's status — so the trials are
/// rebuilt from it and handed to <see cref="StatisticalContributions.Of"/>: there is one ranking, and a ranking taken
/// from the file is the one the run would have given.
/// </summary>
public static class ResultContributions
{
    /// <summary>The group the contribution cubes are stored in, beside the run's summary.</summary>
    public const string Group = StatisticalDataSet.YieldGroup;

    /// <summary>The labelled axis a contribution cube is over.</summary>
    public const string ContributorAxis = "contributor";

    /// <summary>The cube a ranking of <paramref name="name"/> is stored as: each contributor's share of the explained
    /// variance (a fraction), largest first, over <see cref="ContributorAxis"/>.</summary>
    public static string CubeName(string name) => $"contrib:{name}";

    /// <summary>Its running total, the Pareto's cumulative line.</summary>
    public static string CumulativeName(string name) => $"contrib:{name}:cumulative";

    /// <summary>The goals a result scored — the names of its <c>trials.goal:&lt;g&gt;:worst</c> cubes.</summary>
    public static IReadOnlyList<string> GoalsOf(DataSet ds)
        => ds.ContainsGroup(StatisticalDataSet.TrialsGroup)
            ? [.. ds.CubesIn(StatisticalDataSet.TrialsGroup).Keys
                   .Where(k => k.StartsWith("goal:", StringComparison.Ordinal) && k.EndsWith(":worst", StringComparison.Ordinal))
                   .Select(k => k[5..^6])]
            : [];

    /// <summary>The scalar measures a result kept per trial — what else a ranking can be taken of.</summary>
    public static IReadOnlyList<string> MeasuresOf(DataSet ds)
        => ds.ContainsGroup(DataSet.MeasurementsGroup)
            ? [.. ds.CubesIn(DataSet.MeasurementsGroup)
                   .Where(kv => kv.Value is { Rank: 1, DataKind: DataKind.Real } c && c.Axes[0].Name == Evaluator.TrialAxis)
                   .Select(kv => kv.Key)]
            : [];

    /// <summary>The ranking of goal or measure <paramref name="name"/> over the trials <paramref name="ds"/> holds.</summary>
    public static ContributionReport Of(DataSet ds, string name)
    {
        var trials = StatisticalDataSet.TrialsGroup;
        if (!ds.Contains($"{trials}.status"))
            return new ContributionReport(name, [], double.NaN, false, 0,
                StatisticsDiagnostics.ContributionUnknown(name, "nothing — this is not a Monte Carlo result"));
        var status = ds[$"{trials}.status"].RealValues;
        int n = status.Length;

        var entries = new List<(string Key, double[] Z)>();
        var kits    = new List<(string Stream, StatisticalKind Kind, double[] Z)>();
        foreach (var (cubeName, cube) in ds.CubesIn(trials))
        {
            if (!cubeName.StartsWith("z:", StringComparison.Ordinal) || cube.Rank != 1 || cube.Axes[0].Length != n) continue;
            string key = cubeName[2..];
            if (key.StartsWith("process:", StringComparison.Ordinal)) kits.Add((key[8..], StatisticalKind.Process, cube.RealValues));
            else if (key.StartsWith("mismatch:", StringComparison.Ordinal)) kits.Add((key[9..], StatisticalKind.Mismatch, cube.RealValues));
            else entries.Add((key, cube.RealValues));
        }

        var goals = GoalsOf(ds);
        double[]? worst = goals.Contains(name) ? ds[$"{trials}.goal:{name}:worst"].RealValues : null;
        double[]? measure = worst is null && MeasuresOf(ds).Contains(name) ? ds[$"{DataSet.MeasurementsGroup}.{name}"].RealValues : null;

        var records = new List<TrialRecord>(n);
        for (int i = 0; i < n; i++)
        {
            bool evaluated = status[i] == 0;
            var z = entries.Where(e => double.IsFinite(e.Z[i])).ToDictionary(e => e.Key, e => e.Z[i], StringComparer.Ordinal);
            var kit = kits.Where(k => double.IsFinite(k.Z[i])).ToDictionary(k => k.Stream, k => (k.Kind, k.Z[i]), StringComparer.Ordinal);
            IReadOnlyList<Optimization.GoalScore> scores = worst is null ? []
                : [new Optimization.GoalScore(name, [], 0, null, null, worst[i])];
            IReadOnlyDictionary<string, double> scalars = measure is null
                ? new Dictionary<string, double>()
                : new Dictionary<string, double> { [name] = measure[i] };
            records.Add(new TrialRecord(i + 1, evaluated ? Optimization.PointStatus.Evaluated : Optimization.PointStatus.DidNotEvaluate,
                                        null, new Dictionary<string, string>(), new Dictionary<string, double>(), z, kit,
                                        scores, scalars, null));
        }
        return StatisticalContributions.Of(name, records, [.. goals.Select(g => new OptimizationGoal { Name = g })]);
    }

    /// <summary>
    /// Ranks <paramref name="name"/> and stores the ranking in <paramref name="ds"/> as <see cref="CubeName"/> and
    /// <see cref="CumulativeName"/> — computed once, on request, so a display drawing it redraws without recomputing.
    /// The report, with its refusal when nothing could be ranked (and then nothing is stored).
    /// </summary>
    public static ContributionReport Store(DataSet ds, string name)
    {
        var report = Of(ds, name);
        if (report.Refusal is not null || report.Contributors.Count == 0) return report;
        var axis = new Axis(ContributorAxis, [.. Enumerable.Range(1, report.Contributors.Count).Select(i => (double)i)], "",
                            [.. report.Contributors.Select(c => c.Name)]);
        double sum = 0;
        var cumulative = report.Contributors.Select(c => sum += c.Share).ToArray();
        ds.AddToGroup(Group, CubeName(name), new DataCube([axis], [.. report.Contributors.Select(c => c.Share)]));
        ds.AddToGroup(Group, CumulativeName(name), new DataCube([axis], cumulative));
        return report;
    }
}
