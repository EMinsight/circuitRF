// ================================================================
//  YieldDisplayPreset.cs  —  the Yield panel's one-click yield display
//  (brief-yield-10 R-ya10-8)
//
//  A Data Display document over a Monte Carlo result: per goal, the
//  trials as a pass/fail family with the nominal and the goal's spec
//  lines; per goal, a histogram of its worst value with the limits; a
//  yield sensitivity over the variable that drives the first goal most;
//  and the statistics table.
//
//  THIS COMPOSES, IT DOES NOT DRAW. Every plot is what a user would get
//  from the trace card: a family is a cube trace coloured by `pass`
//  (YA-9), the histogram and the sensitivity are TraceStatistics.Build's
//  own rewrites (YA-8), the table is StatisticsTablePreset, and the spec
//  lines are SpecLineResolve's, found at draw time from the goals the
//  result records. Which cube a goal's family reads is found the way the
//  spec lines find a goal for a curve — TraceGoalReader's translation,
//  compared with TraceToGoal.SameQuantity — so the family a goal gets is
//  exactly a curve its spec lines land on.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class YieldDisplayPreset
{
    /// <summary>The group a Monte Carlo result keeps its per-trial values in (docs/design/results-dataset-layout.md).</summary>
    private const string TrialsGroup = "trials";

    /// <summary>The tab the display opens on.</summary>
    public const string TabName = "Yield";

    private const double PlotWidth = 520, PlotHeight = 360, Gap = 24;

    /// <summary>At most this many slices of one cube are tried when looking for a goal's family.</summary>
    private const int MaxCandidates = 2000;

    /// <summary>The kinds of plot the display is built from, in the order they are placed.</summary>
    public enum PlotKind { Family, Histogram, YieldSensitivity, StatisticsTable }

    /// <summary>One placed plot and what it is.</summary>
    public sealed record ComposedPlot(PlotKind Kind, string? Goal, PlotContainerConfig Config);

    /// <summary>
    /// The plots of a yield display over <paramref name="ds"/>, every trace bound to <paramref name="sourceRef"/>,
    /// laid out two to a row. Empty when the source is not a Monte Carlo result.
    /// </summary>
    public static IReadOnlyList<ComposedPlot> Compose(DataSet ds, string sourceRef)
    {
        var plots = new List<ComposedPlot>();
        if (!ds.ContainsGroup(TrialsGroup)) return plots;
        var goals = SpecLineResolve.GoalsOf(ds);

        foreach (var g in goals)
            if (Family(ds, g, sourceRef) is { } family)
                plots.Add(new ComposedPlot(PlotKind.Family, g.Name, family));

        foreach (var g in goals)
            if (Histogram(ds, g, sourceRef) is { } histogram)
                plots.Add(new ComposedPlot(PlotKind.Histogram, g.Name, histogram));

        if (YieldSensitivity(ds, goals, sourceRef) is { } sensitivity)
            plots.Add(new ComposedPlot(PlotKind.YieldSensitivity, null, sensitivity));

        if (StatisticsTablePreset.Build(ds, sourceRef) is { } table)
            plots.Add(new ComposedPlot(PlotKind.StatisticsTable, null, table));

        double y = Gap;
        for (int i = 0; i < plots.Count; i += 2)
        {
            double x = Gap, rowHeight = 0;
            foreach (var p in plots.Skip(i).Take(2))
            {
                p.Config.Left = x;
                p.Config.Top  = y;
                x += p.Config.Width + Gap;
                rowHeight = Math.Max(rowHeight, p.Config.Height);
            }
            y += rowHeight + Gap;
        }
        return plots;
    }

    /// <summary>The display as a document: one tab of <see cref="Compose"/>'s plots.</summary>
    public static DataDisplayConfig Build(DataSet ds, string sourceRef) => new()
    {
        FormatVersion      = DataDisplayConfig.CurrentFormatVersion,
        SelectedDataSource = sourceRef,
        Tabs               = [new TabConfig { Name = TabName, Plots = [.. Compose(ds, sourceRef).Select(p => p.Config)] }],
    };

    // ── A goal's trials as a pass/fail family ──────────────────────────────────────────

    /// <summary>
    /// The goal's quantity over every trial, coloured by pass with the nominal drawn over it — a family over the
    /// <c>trial</c> axis of the cube the goal reads. A goal whose value is one number per trial (no swept axis to draw
    /// a curve over) is drawn as its worst value against the trial number instead, which its limits still cross.
    /// </summary>
    private static PlotContainerConfig? Family(DataSet ds, OptimizationGoal goal, string sourceRef)
    {
        if (FindFamily(ds, goal) is { } t)
            return Rect($"{goal.Name}: trials", Cube(t, sourceRef, Trace.ColorByPass));

        string worst = $"{TrialsGroup}.goal:{goal.Name}:worst";
        if (!ds.Contains(worst)) return null;
        var points = TraceOver(worst);
        points.Properties.LineEnabled = false;
        points.Properties.MarkerEnabled = true;
        return Rect($"{goal.Name}: worst value per trial", Cube(points, sourceRef, Trace.ColorByPass));
    }

    /// <summary>The cube trace whose goal translation is <paramref name="goal"/>'s quantity, iterating the trials; null
    /// when no cube the result holds reads it over a swept axis.</summary>
    internal static Trace? FindFamily(DataSet ds, OptimizationGoal goal)
    {
        foreach (var group in ds.Groups)
        {
            if (group is TrialsGroup or "nominal" or ResultContributions.Group or StatisticsTablePreset.Group) continue;
            if (goal.Analysis is { Length: > 0 } a && !string.Equals(group, a, StringComparison.Ordinal)) continue;
            foreach (var (name, cube) in ds.CubesIn(group))
            {
                if (name.StartsWith("__", StringComparison.Ordinal) || cube.Rank < 2) continue;
                if (cube.Axes[0].Name != Evaluator.TrialAxis) continue;
                string spec = group == DataSet.DefaultGroup || group == DataSet.MeasurementsGroup ? name : $"{group}.{name}";
                foreach (var t in Candidates(spec, cube, goal))
                    if (TraceGoalReader.GoalExpressionOf(t, ds) is { } read && TraceToGoal.SameQuantity(read, goal.Expression))
                        return t;
            }
        }
        return null;
    }

    /// <summary>Every slice of <paramref name="cube"/> a goal could read as a curve: the trials iterated, one swept axis
    /// kept as X (the goal's range axis when it has one), every other axis pinned, under each transform a goal can
    /// name.</summary>
    private static IEnumerable<Trace> Candidates(string spec, DataCube cube, OptimizationGoal goal)
    {
        var axes = cube.Axes.Skip(1).ToList();
        static bool Swept(Axis a) => a.Name is not ("i" or "j") && a.Labels is not { Length: > 0 };
        var x = axes.FirstOrDefault(a => a.Name == goal.Range?.Axis && Swept(a)) ?? axes.FirstOrDefault(Swept);
        if (x is null) yield break;

        // The pinned axes' choices: every port of i and j, every label of a labelled axis, the first sample otherwise.
        var pinned = axes.Where(a => a != x).ToList();
        var choices = pinned.Select(a => a.Name is "i" or "j" || a.Labels is { Length: > 0 }
            ? Enumerable.Range(0, a.Length).ToArray() : [0]).ToList();
        var transforms = cube.DataKind == DataKind.Complex
            ? new[] { CubeTransform.dB20, CubeTransform.Mag, CubeTransform.Phase, CubeTransform.Real, CubeTransform.Imag }
            : [CubeTransform.None, CubeTransform.dB20, CubeTransform.dB10, CubeTransform.Mag];

        int emitted = 0;
        var index = new int[pinned.Count];
        while (true)
        {
            var slice = new List<AxisSlice> { new(Evaluator.TrialAxis, AxisRole.FamilyIterate, 0) };
            foreach (var a in axes)
            {
                if (a == x) { slice.Add(new AxisSlice(a.Name, AxisRole.KeepAsX, 0)); continue; }
                int k = pinned.IndexOf(a);
                int at = choices[k][index[k]];
                slice.Add(new AxisSlice(a.Name, AxisRole.PinToIndex, at, Label: a.Labels is { Length: > 0 } l ? l[at] : ""));
            }
            foreach (var tr in transforms)
            {
                var t = Bare();
                t.CubeName  = spec;
                t.Slice     = [.. slice];
                t.Transform = tr;
                t.Expression = t.BuildPickerExpression();
                yield return t;
                if (++emitted >= MaxCandidates) yield break;
            }

            int i = pinned.Count - 1;
            while (i >= 0 && ++index[i] == choices[i].Length) index[i--] = 0;
            if (i < 0) yield break;
        }
    }

    // ── A goal's worst value as a histogram ────────────────────────────────────────────

    private static PlotContainerConfig? Histogram(DataSet ds, OptimizationGoal goal, string sourceRef)
    {
        string worst = $"{TrialsGroup}.goal:{goal.Name}:worst";
        if (!ds.Contains(worst)) return null;
        var t = TraceOver(worst);
        var (rewrite, _) = TraceStatistics.Build(t, ds, TraceStatistic.Histogram);
        if (rewrite is null) return null;
        TraceStatistics.Apply(t, rewrite);
        return Rect($"{goal.Name}: worst value", Expression(t, sourceRef));
    }

    // ── Yield against the variable that drives it most ─────────────────────────────────

    private static PlotContainerConfig? YieldSensitivity(DataSet ds, IReadOnlyList<OptimizationGoal> goals, string sourceRef)
    {
        var stats = TraceStatistics.StatSpecs(ds);
        if (TraceStatistics.PassSpec(ds) is null || stats.Count == 0) return null;

        string spec = stats[0];
        if (goals.Count > 0)
        {
            var report = ResultContributions.Of(ds, goals[0].Name);
            if (report.Refusal is null)
                foreach (var c in report.Contributors.Where(c => c.Kind == "entry"))
                {
                    string candidate = $"{TrialsGroup}.stat:{c.Name}";
                    if (stats.Contains(candidate, StringComparer.Ordinal)) { spec = candidate; break; }
                }
        }

        var t = TraceOver(spec);
        var (rewrite, _) = TraceStatistics.Build(t, ds, TraceStatistic.YieldSensitivity, statSpec: spec);
        if (rewrite is null) return null;
        TraceStatistics.Apply(t, rewrite);
        var traces = new List<TraceConfig> { Expression(t, sourceRef) };
        if (rewrite.Companion is { } companion)
            traces.Add(Expression(TraceStatistics.CompanionOf(t, companion), sourceRef));
        string key = spec[(spec.IndexOf(':') + 1)..];
        return Rect($"Yield vs {key}", [.. traces]);
    }

    // ── Shared ─────────────────────────────────────────────────────────────────────────

    private static Trace Bare() => new(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db, false);

    /// <summary>A trace reading a per-trial cube over its <c>trial</c> axis.</summary>
    private static Trace TraceOver(string spec)
    {
        var t = Bare();
        t.CubeName = spec;
        t.Slice    = [new AxisSlice(Evaluator.TrialAxis, AxisRole.KeepAsX, 0)];
        t.Expression = t.BuildPickerExpression();
        return t;
    }

    private static TraceConfig Cube(Trace t, string sourceRef, string? colorBy)
    {
        t.ColorBy = colorBy;
        var c = new TraceConfig
        {
            SourcePath    = sourceRef,
            CubeName      = t.CubeName,
            CubeSlice     = [.. t.Slice!.Select(AxisSliceConfig.From)],
            CubeTransform = t.Transform,
            Expression    = t.Expression,
            Properties    = Properties(t),
        };
        c.SetTrialViews(t);
        return c;
    }

    private static TraceConfig Expression(Trace t, string sourceRef) => new()
    {
        SourcePath       = sourceRef,
        Expression       = t.Expression,
        UseSecondaryAxis = t.UseSecondaryAxis,
        Properties       = Properties(t),
        StatisticsOrigin = StatisticsOriginConfig.From(t.StatisticsOrigin),
    };

    private static TracePropertiesConfig Properties(Trace t)
    {
        int color = TraceProperties.LineColorOrder[t.UseSecondaryAxis ? 1 : 0];
        return new TracePropertiesConfig
        {
            LineColorIndex   = color,
            MarkerColorIndex = color,
            DrawStyle        = t.Properties.DrawStyle,
            LineEnabled      = t.Properties.LineEnabled,
            MarkerEnabled    = t.Properties.MarkerEnabled,
        };
    }

    private static PlotContainerConfig Rect(string title, params TraceConfig[] traces) => new()
    {
        PlotType      = PlotType.Rect,
        Width         = PlotWidth,
        Height        = PlotHeight,
        CustomTitle   = title,
        CustomTitleOn = true,
        Traces        = [.. traces],
    };
}
