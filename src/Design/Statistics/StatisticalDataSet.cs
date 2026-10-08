using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Statistics;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A Monte Carlo or yield run as one <see cref="DataSet"/> (yield overview D9; the names are recorded in
/// docs/design/results-dataset-layout.md §"Monte Carlo and yield"):
/// <list type="bullet">
/// <item>every analysis group the nominal produced (<c>SP1</c>, <c>measurements</c>, …), each cube with an outer
/// <c>trial</c> axis over the trials the save policy kept — a trial that did not evaluate is NaN there; a real
/// scalar measurement is kept for EVERY trial, being a scalar;</item>
/// <item><c>trials</c> — per trial: <c>stat:&lt;key&gt;</c> (base SI), <c>z:&lt;key&gt;</c>,
/// <c>z:process:&lt;stream&gt;</c>, <c>z:mismatch:&lt;stream&gt;</c>, per goal <c>goal:&lt;g&gt;:pass</c>,
/// <c>goal:&lt;g&gt;:margin</c>, <c>goal:&lt;g&gt;:worst</c>, then <c>pass</c>, <c>status</c> and the <c>reasons</c>
/// table;</item>
/// <item><c>nominal</c> — the nominal's cubes with no trial axis, each named by its own address
/// (<c>SP1.S</c>, <c>trials.pass</c>), so a cube's nominal is <c>nominal.</c> + its address;</item>
/// <item><c>yield</c> — the summary: counts, the yield and its interval overall and per goal, the settings.</item>
/// </list>
/// </summary>
internal static class StatisticalDataSet
{
    public const string TrialsGroup  = "trials";
    public const string NominalGroup = "nominal";
    public const string YieldGroup   = "yield";

    public sealed record Inputs(
        StatisticalMode                       Mode,
        StatisticsSettings                    Settings,
        IReadOnlyList<OptimizationGoal>       Goals,
        IReadOnlyList<TunableEntry>           Entries,
        IReadOnlyDictionary<string, Tunable>  Tunables,
        IReadOnlyList<TrialRecord>            Records,
        PointEvaluation                       Nominal,
        SaveReport                            Save,
        YieldEstimate                         Overall,
        IReadOnlyList<GoalYield>              PerGoal,
        AutoStopVerdict?                      Verdict,
        string                                Finish);

    public static DataSet Build(Inputs x)
    {
        var ds = new DataSet();
        var records = x.Records;
        int n = records.Count;
        var trialAxis = TrialAxis(n);
        bool countFails = x.Settings.NonConverged == NonConvergedPolicy.Fail;

        // ── The analysis groups, trial-stacked ─────────────────────────────────────
        var nominalData = x.Nominal.Data;
        int kept = Math.Min(x.Save.KeptTrials, n);
        if (nominalData is not null)
            foreach (var g in nominalData.Groups)
                foreach (var (name, cube) in nominalData.CubesIn(g))
                {
                    if (name.StartsWith("__", StringComparison.Ordinal)) { ds.AddToGroup(g, name, cube); continue; }
                    if (g == DataSet.MeasurementsGroup && StatisticalRun.IsScalar(cube))
                    {
                        ds.AddToGroup(g, name, Column(trialAxis,
                            records.Select(r => r.Scalars.TryGetValue(name, out double v) ? v : double.NaN), cube.Unit));
                        continue;
                    }
                    if (kept == 0) continue;
                    var slices = new DataCube[kept];
                    for (int i = 0; i < kept; i++)
                        slices[i] = records[i].Data is { } d && d.ContainsGroup(g) && d.CubesIn(g).TryGetValue(name, out var c) && SameShape(c, cube)
                            ? c : NaNLike(cube);
                    var stacked = DataCube.PrependAxis(TrialAxis(kept), slices);
                    stacked.Unit = cube.Unit;
                    ds.AddToGroup(g, name, stacked);
                }

        // ── trials ─────────────────────────────────────────────────────────────────
        foreach (var e in x.Entries)
        {
            string unit = x.Tunables.TryGetValue(e.Key, out var t) && t.Unit.Length > 0 ? Units.BaseUnit(t.Unit) : "";
            ds.AddToGroup(TrialsGroup, "stat:" + e.Key,
                Column(trialAxis, records.Select(r => r.Draws.TryGetValue(e.Key, out double v) ? v : double.NaN), unit));
        }
        foreach (var e in x.Entries)
            ds.AddToGroup(TrialsGroup, "z:" + e.Key,
                Column(trialAxis, records.Select(r => r.Z.TryGetValue(e.Key, out double v) ? v : double.NaN), ""));
        foreach (var (stream, kind) in KitStreams(records))
            ds.AddToGroup(TrialsGroup, $"z:{KindName(kind)}:{stream}",
                Column(trialAxis, records.Select(r => r.Kit.TryGetValue(stream, out var d) ? d.Z : double.NaN), ""));

        foreach (var g in x.Goals)
        {
            GoalScore? Of(TrialRecord r) => r.Goals.FirstOrDefault(s => s.Name == g.Name);
            ds.AddToGroup(TrialsGroup, $"goal:{g.Name}:pass", Column(trialAxis, records.Select(r =>
                r.Evaluated ? (Of(r)?.Met == true ? 1.0 : 0.0) : countFails ? 0.0 : double.NaN), ""));
            ds.AddToGroup(TrialsGroup, $"goal:{g.Name}:margin", Column(trialAxis, records.Select(r => r.Evaluated ? Of(r)?.Margin ?? double.NaN : double.NaN), ""));
            ds.AddToGroup(TrialsGroup, $"goal:{g.Name}:worst", Column(trialAxis, records.Select(r => r.Evaluated ? Of(r)?.WorstValue ?? double.NaN : double.NaN), ""));
        }
        if (x.Goals.Count > 0)
            ds.AddToGroup(TrialsGroup, "pass", Column(trialAxis, records.Select(r =>
                r.Evaluated ? (r.Pass ? 1.0 : 0.0) : countFails ? 0.0 : double.NaN), ""));

        // status: 0 evaluated, k ≥ 1 the k-th distinct reason in the reasons table.
        var reasons = new List<string>();
        var status = records.Select(r =>
        {
            if (r.Evaluated) return 0.0;
            string why = r.Reason?.Render() ?? "did not evaluate";
            int k = reasons.IndexOf(why);
            if (k < 0) { reasons.Add(why); k = reasons.Count - 1; }
            return k + 1.0;
        }).ToArray();
        ds.AddToGroup(TrialsGroup, "status", Column(trialAxis, status, ""));
        if (reasons.Count > 0)
            ds.AddToGroup(TrialsGroup, "reasons", new DataCube(
                [new Axis("reason", [.. Enumerable.Range(1, reasons.Count).Select(i => (double)i)], "", [.. reasons])],
                [.. Enumerable.Range(1, reasons.Count).Select(i => (double)i)]));

        // ── nominal ────────────────────────────────────────────────────────────────
        if (nominalData is not null)
            foreach (var g in nominalData.Groups)
                foreach (var (name, cube) in nominalData.CubesIn(g))
                    if (!name.StartsWith("__", StringComparison.Ordinal))
                        ds.AddToGroup(NominalGroup, $"{g}.{name}", cube);
        foreach (var e in x.Entries)
            if (x.Tunables.TryGetValue(e.Key, out var t))
                ds.AddToGroup(NominalGroup, $"{TrialsGroup}.stat:{e.Key}",
                    new DataCube([], [t.Value * StatisticalRun.UnitScale(t.Unit)]) { Unit = t.Unit.Length > 0 ? Units.BaseUnit(t.Unit) : "" });
        foreach (var g in x.Goals)
            if (x.Nominal.Goals.FirstOrDefault(s => s.Name == g.Name) is { } s)
            {
                ds.AddToGroup(NominalGroup, $"{TrialsGroup}.goal:{g.Name}:pass", DataCube.Scalar(s.Met ? 1.0 : 0.0));
                ds.AddToGroup(NominalGroup, $"{TrialsGroup}.goal:{g.Name}:margin", DataCube.Scalar(s.Margin));
                ds.AddToGroup(NominalGroup, $"{TrialsGroup}.goal:{g.Name}:worst", DataCube.Scalar(s.WorstValue));
            }
        if (x.Goals.Count > 0)
            ds.AddToGroup(NominalGroup, $"{TrialsGroup}.pass", DataCube.Scalar(x.Nominal.Pass ? 1.0 : 0.0));

        // ── yield (the summary) ──────────────────────────────────────────────────
        var s0 = x.Settings;
        Scalar(ds, "trials", n);
        Scalar(ds, "did_not_evaluate", records.Count(r => !r.Evaluated));
        Estimate(ds, "", x.Overall);
        foreach (var gy in x.PerGoal) Estimate(ds, $"goal:{gy.Goal}:", gy.Estimate);
        Scalar(ds, "confidence", s0.EffectiveConfidence / 100);
        Scalar(ds, "target", x.Mode == StatisticalMode.Yield && s0.Target is { } tp ? tp / 100 : double.NaN);
        Scalar(ds, "seed", s0.EffectiveSeed);
        Scalar(ds, "saved_trials", kept);
        Label(ds, "mode", x.Mode == StatisticalMode.Yield ? "yield" : "montecarlo", (int)x.Mode);
        Label(ds, "sampling", s0.Sampling.ToString().ToLowerInvariant(), (int)s0.Sampling);
        Label(ds, "nonconverged", s0.NonConverged.ToString().ToLowerInvariant(), (int)s0.NonConverged);
        Label(ds, "save", x.Save.Sentence, x.Save.KeptTrials);
        Label(ds, "stopped", x.Finish, x.Verdict is null ? 0 : (int)x.Verdict.Value);
        return ds;
    }

    private static Axis TrialAxis(int n) => new(Evaluator.TrialAxis, [.. Enumerable.Range(1, n).Select(i => (double)i)]);

    private static DataCube Column(Axis axis, IEnumerable<double> values, string unit)
        => new([axis], [.. values]) { Unit = unit };

    private static void Scalar(DataSet ds, string name, double v) => ds.AddToGroup(YieldGroup, name, DataCube.Scalar(v));

    private static void Estimate(DataSet ds, string prefix, YieldEstimate e)
    {
        Scalar(ds, prefix + "passes", e.Passes);
        Scalar(ds, prefix + "counted", e.Counted);
        Scalar(ds, prefix + "yield", e.Yield);
        Scalar(ds, prefix + "lower", e.Lower);
        Scalar(ds, prefix + "upper", e.Upper);
    }

    /// <summary>A text setting as a one-point cube labelled with it — a cube holds numbers, an axis label text.</summary>
    private static void Label(DataSet ds, string name, string text, double value)
        => ds.AddToGroup(YieldGroup, name, new DataCube([new Axis(name, [value], "", [text])], [value]));

    private static IEnumerable<(string Stream, StatisticalKind Kind)> KitStreams(IReadOnlyList<TrialRecord> records)
    {
        var seen = new Dictionary<string, StatisticalKind>(StringComparer.Ordinal);
        foreach (var r in records)
            foreach (var (stream, d) in r.Kit) seen.TryAdd(stream, d.Kind);
        return seen.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value));
    }

    internal static string KindName(StatisticalKind k) => k == StatisticalKind.Process ? "process" : "mismatch";

    private static bool SameShape(DataCube a, DataCube b)
    {
        if (a.DataKind != b.DataKind || a.Rank != b.Rank) return false;
        for (int d = 0; d < a.Rank; d++) if (a.Axes[d].Length != b.Axes[d].Length) return false;
        return true;
    }

    private static DataCube NaNLike(DataCube c)
    {
        int len = c.BufferLength;
        return c.DataKind == DataKind.Complex
            ? new DataCube([.. c.Axes], Enumerable.Repeat(new Complex(double.NaN, double.NaN), len).ToArray()) { Unit = c.Unit }
            : new DataCube([.. c.Axes], Enumerable.Repeat(double.NaN, len).ToArray()) { Unit = c.Unit };
    }
}
