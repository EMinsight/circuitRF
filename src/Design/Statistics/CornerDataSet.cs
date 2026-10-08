using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A corner run as one <see cref="DataSet"/> with an outer <c>corner</c> axis whose labels are the corner names, the
/// nominal first (yield overview D9, brief-yield-6 R-ya6-2; names in docs/design/results-dataset-layout.md
/// §"Corners"):
/// <list type="bullet">
/// <item>every analysis group the corners produced, each cube stacked <c>[corner, …]</c> — NaN where a corner did not
/// evaluate;</item>
/// <item><c>corners</c> — per corner: <c>goal:&lt;g&gt;:pass</c>, <c>goal:&lt;g&gt;:margin</c>,
/// <c>goal:&lt;g&gt;:worst</c>, <c>pass</c>, <c>temp</c> (°C; NaN where the corner sets none), <c>status</c> and the
/// <c>reasons</c> table — the shapes the <c>trials</c> group has, over corners instead of trials.</item>
/// </list>
/// A Monte Carlo at each corner (R-ya6-3) is <see cref="Stack"/>: each corner's run stacked under the same axis, the
/// <c>trial</c> axis inside it.
/// </summary>
internal static class CornerDataSet
{
    public const string CornersGroup = "corners";
    public const string CornerAxis   = "corner";

    public static DataSet Build(IReadOnlyList<CornerEvaluation> rows, IReadOnlyList<OptimizationGoal> goals, bool countFails)
    {
        var axis = Axis([.. rows.Select(r => r.Name)]);
        var ds = Stack([.. rows.Select(r => r.Name)], [.. rows.Select(r => r.Data)]) ?? new DataSet();

        foreach (var g in goals)
        {
            var goal = g;
            GoalScore? Of(CornerEvaluation r) => r.Goals.FirstOrDefault(s => s.Name == goal.Name);
            ds.AddToGroup(CornersGroup, $"goal:{g.Name}:pass", Column(axis, rows.Select(r =>
                r.Evaluated ? (Of(r)?.Met == true ? 1.0 : 0.0) : countFails ? 0.0 : double.NaN), ""));
            ds.AddToGroup(CornersGroup, $"goal:{g.Name}:margin", Column(axis, rows.Select(r => r.Evaluated ? Of(r)?.Margin ?? double.NaN : double.NaN), ""));
            ds.AddToGroup(CornersGroup, $"goal:{g.Name}:worst", Column(axis, rows.Select(r => r.Evaluated ? Of(r)?.WorstValue ?? double.NaN : double.NaN), ""));
        }
        if (goals.Count > 0)
            ds.AddToGroup(CornersGroup, "pass", Column(axis, rows.Select(r =>
                r.Evaluated ? (r.Pass ? 1.0 : 0.0) : countFails ? 0.0 : double.NaN), ""));
        ds.AddToGroup(CornersGroup, "temp", Column(axis, rows.Select(r => CornerRun.TempOf(r.Definition) ?? double.NaN), "degC"));

        var reasons = new List<string>();
        ds.AddToGroup(CornersGroup, "status", Column(axis, rows.Select(r =>
        {
            if (r.Evaluated) return 0.0;
            string why = r.Reason?.Render() ?? "did not evaluate";
            int k = reasons.IndexOf(why);
            if (k < 0) { reasons.Add(why); k = reasons.Count - 1; }
            return k + 1.0;
        }), ""));
        if (reasons.Count > 0)
            ds.AddToGroup(CornersGroup, "reasons", new DataCube(
                [new Axis("reason", [.. Enumerable.Range(1, reasons.Count).Select(i => (double)i)], "", [.. reasons])],
                [.. Enumerable.Range(1, reasons.Count).Select(i => (double)i)]));
        return ds;
    }

    /// <summary>
    /// <paramref name="sets"/> stacked under a <c>corner</c> axis labelled <paramref name="names"/>: every cube any of
    /// them holds, NaN for a corner that lacks it. A cube whose outer axis is <c>trial</c> is padded with NaN to the
    /// longest corner's trial count (auto-stop ends corners at different counts). A cube whose shape differs between
    /// corners in any other way cannot share an axis and is left out; <c>__</c> metadata passes through once,
    /// unstacked. Null when no corner produced anything.
    /// </summary>
    public static DataSet? Stack(IReadOnlyList<string> names, IReadOnlyList<DataSet?> sets)
    {
        if (sets.All(s => s is null)) return null;
        var axis = Axis(names);
        var ds = new DataSet();
        var order = new List<(string Group, string Name)>();
        var seen = new HashSet<(string, string)>();
        foreach (var set in sets)
            if (set is not null)
                foreach (var g in set.Groups)
                    foreach (var name in set.CubesIn(g).Keys)
                        if (seen.Add((g, name))) order.Add((g, name));

        foreach (var (g, name) in order)
        {
            var cubes = sets.Select(s => s is not null && s.ContainsGroup(g) && s.CubesIn(g).TryGetValue(name, out var c) ? c : null).ToList();
            var template = cubes.First(c => c is not null)!;
            if (name.StartsWith("__", StringComparison.Ordinal)) { ds.AddToGroup(g, name, template); continue; }

            bool trialOuter = template.Rank > 0 && template.Axes[0].Name == Evaluator.TrialAxis;
            if (trialOuter)
            {
                int longest = cubes.Where(c => c is not null).Max(c => c!.Axes[0].Length);
                if (cubes.First(c => c is not null && c.Axes[0].Length == longest) is { } t) template = t;
                cubes = [.. cubes.Select(c => c is null ? null : Pad(c, template.Axes[0]))];
            }
            if (cubes.Any(c => c is not null && !SameShape(c, template))) continue;
            var stacked = DataCube.PrependAxis(axis, [.. cubes.Select(c => c ?? NaNLike(template))]);
            stacked.Unit = template.Unit;
            ds.AddToGroup(g, name, stacked);
        }
        return ds;
    }

    private static Axis Axis(IReadOnlyList<string> names)
        => new(CornerAxis, [.. Enumerable.Range(1, names.Count).Select(i => (double)i)], "", [.. names]);

    private static DataCube Column(Axis axis, IEnumerable<double> values, string unit)
        => new([axis], [.. values]) { Unit = unit };

    /// <summary><paramref name="c"/> with its outer trial axis lengthened to <paramref name="trials"/>, the new trials NaN.
    /// The trial axis is outermost, so a trial's values are one contiguous run of the row-major buffer.</summary>
    private static DataCube Pad(DataCube c, Axis trials)
    {
        if (c.Axes[0].Length == trials.Length) return c;
        if (c.Axes[0].Length > trials.Length) return c;
        var axes = c.Axes.ToArray();
        axes[0] = trials;
        int per = c.BufferLength / Math.Max(1, c.Axes[0].Length);
        int total = per * trials.Length;
        if (c.DataKind == DataKind.Complex)
        {
            var v = new Complex[total];
            Array.Fill(v, new Complex(double.NaN, double.NaN));
            c.ComplexValues.CopyTo(v, 0);
            return new DataCube(axes, v) { Unit = c.Unit };
        }
        var r = new double[total];
        Array.Fill(r, double.NaN);
        c.RealValues.CopyTo(r, 0);
        return new DataCube(axes, r) { Unit = c.Unit };
    }

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
