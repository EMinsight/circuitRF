// ================================================================
//  TraceStatistics.cs  —  the trace card's Statistics menu as a
//  function: a trace in, the expression and draw style it becomes out
//  (brief-yield-8 R-ya8-2)
//
//  THE MENU WRITES AN ORDINARY EXPRESSION. A histogram is the trace
//  `histogram(trials.goal:S21:worst, 23)` drawn as bars; a CDF is
//  `cdf(…)` as a step; a normal plot is `normq(…)`; a yield
//  sensitivity is `100*yield_sens(trials.pass, trials.stat:R1.R, 12)`
//  with the trials per bin beside it. The card shows the expression, so
//  what the menu did is visible and editable, and the numbers are the
//  expression engine's (YA-4's functions) — the Data Display adds the
//  drawing, never a statistic.
//
//  `circuitrf plot --trace …,stat=histogram` calls the same Build, so
//  the CLI's rewrite IS the menu's.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Expressions;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

/// <summary>The Statistics menu's entries.</summary>
public enum TraceStatistic { Histogram, Cdf, Quantile, YieldSensitivity }

/// <summary>
/// What a trace becomes: its <see cref="Expression"/> and <see cref="Style"/>, and — for a yield sensitivity — the
/// expression of the light second series beside it (the trials per bin), drawn as a step on the right axis.
/// </summary>
public sealed record StatisticsRewrite(string Expression, TraceDrawStyle Style, string? Companion = null);

public static class TraceStatistics
{
    /// <summary>A live histogram holds its bin range from the first frame with at least this many trials (R-ya8-7).</summary>
    public const int HoldAfterTrials = 30;

    /// <summary>The group a Monte Carlo result keeps its per-trial values in (docs/design/results-dataset-layout.md).</summary>
    private const string TrialsGroup = "trials";

    /// <summary>
    /// The axes the menu can take a statistic over: every axis of the trace's slice except the port pair (<c>i</c>,
    /// <c>j</c>), the <c>trial</c> axis first and the axes the trace keeps next — or, for a typed expression, its X
    /// axis alone. Empty for a trace the menu cannot rewrite.
    /// </summary>
    public static IReadOnlyList<string> Axes(Trace trace)
    {
        var (_, cube, slice, _) = Reads(trace);
        if (cube is null || slice is null)
            return trace.IsCubeBound && trace.CubeXAxisName is { Length: > 0 } x ? [x] : [];
        return slice.Where(s => s.AxisName is not ("i" or "j"))
                    .OrderBy(s => s.AxisName == Evaluator.TrialAxis ? 0 : s.Role != AxisRole.PinToIndex ? 1 : 2)
                    .Select(s => s.AxisName).ToList();
    }

    /// <summary>
    /// What the trace reads, as a value over <paramref name="axis"/> alone — the operand every statistic is taken of.
    /// The chosen axis is kept whole; every other axis keeps its pin, and one the trace kept (its X, a family) is
    /// pinned at its first sample, which the expression then shows. A typed expression is its own operand.
    /// </summary>
    public static string? Operand(Trace trace, string axis)
    {
        var (expression, cube, slice, transform) = Reads(trace);
        // A typed expression is its own operand — unless it is already a statistic, which is OF its operand.
        if (cube is null || slice is null) return expression is null ? null : ValueOperand(expression) ?? expression;

        var pinned = slice.Select(s =>
            s.AxisName == axis                ? new AxisSlice(s.AxisName, AxisRole.KeepAsX, 0)
            : s.Role == AxisRole.PinToIndex   ? s
            : new AxisSlice(s.AxisName, AxisRole.PinToIndex, Math.Max(0, s.RangeStart))).ToArray();
        if (!pinned.Any(s => s.AxisName == axis)) return null;

        var scratch = new Trace(trace, includeMarkers: false) { CubeName = cube, Slice = pinned };
        string? body = scratch.PickerBody(forExpression: true);
        if (body is null) return null;
        return transform == CubeTransform.None ? body : $"{Trace.TransformFunctionName(transform)}({body})";
    }

    /// <summary>The source's per-trial pass cube (<c>trials.pass</c>), or null when it scores no goal.</summary>
    public static string? PassSpec(DataSet ds) => ds.Contains($"{TrialsGroup}.pass") ? $"{TrialsGroup}.pass" : null;

    /// <summary>Every statistical entry's drawn value in the source (<c>trials.stat:&lt;key&gt;</c>) — what a yield
    /// sensitivity is binned over.</summary>
    public static IReadOnlyList<string> StatSpecs(DataSet ds)
        => ds.ContainsGroup(TrialsGroup)
            ? [.. ds.CubesIn(TrialsGroup).Keys.Where(k => k.StartsWith("stat:", StringComparison.Ordinal)).Select(k => $"{TrialsGroup}.{k}")]
            : [];

    /// <summary>The Freedman–Diaconis bin count of the operand's values (<see cref="SampleStatistics.FreedmanDiaconisBins"/>),
    /// or null when the operand does not evaluate to a value over one axis.</summary>
    public static int? AutoBins(string operand, DataSet ds)
    {
        if (!TraceExpression.TryEvaluateValue(operand, ds, out var v, out _) || v.Kind != ValueKind.Cube) return null;
        var cube = v.AsCube();
        if (cube.Rank != 1 || cube.DataKind != DataKind.Real) return null;
        return SampleStatistics.FreedmanDiaconisBins(cube.RealValues);
    }

    /// <summary>
    /// The rewrite one menu entry makes of <paramref name="trace"/>, or the reason it cannot. <paramref name="axis"/>
    /// defaults to <c>trial</c> when the trace has one; <paramref name="bins"/> to the Freedman–Diaconis count;
    /// <paramref name="statSpec"/> names the <c>trials.stat:&lt;key&gt;</c> a yield sensitivity bins over.
    /// </summary>
    public static (StatisticsRewrite? Rewrite, string? Refusal) Build(
        Trace trace, DataSet ds, TraceStatistic kind,
        string? axis = null, int? bins = null, bool percent = false, string? statSpec = null)
    {
        if (kind == TraceStatistic.YieldSensitivity)
        {
            if (PassSpec(ds) is not { } pass)
                return (null, "This source scores no goal, so there is no pass/fail to take a yield sensitivity of.");
            if (statSpec is null || !StatSpecs(ds).Contains(statSpec, StringComparer.Ordinal))
                return (null, $"Name a statistical variable to bin over: {string.Join(", ", StatSpecs(ds))}.");
            int n = bins ?? AutoBins($"{statSpec} + 0*{pass}", ds) ?? 10;
            string b = n.ToString(CultureInfo.InvariantCulture);
            // The companion counts the SAME trials over the SAME bins: `+ 0*pass` makes a trial with no pass/fail a
            // NaN, which the histogram skips exactly as yield_sens does, so the two share their extent.
            return (new StatisticsRewrite($"100*yield_sens({pass}, {statSpec}, {b})", TraceDrawStyle.Bars,
                                          $"histogram({statSpec} + 0*{pass}, {b})"), null);
        }

        var axes = Axes(trace);
        axis ??= axes.Contains(Evaluator.TrialAxis) ? Evaluator.TrialAxis : axes.Count == 1 ? axes[0] : null;
        if (axis is null)
            return (null, $"Choose the axis to take the statistic over: {string.Join(", ", axes)}.");
        if (!axes.Contains(axis))
            return (null, $"This trace has no axis '{axis}'. Its axes are: {string.Join(", ", axes)}.");
        if (Operand(trace, axis) is not { } operand)
            return (null, "This trace reads nothing a statistic can be taken of.");

        switch (kind)
        {
            case TraceStatistic.Histogram:
            {
                int n = bins ?? AutoBins(operand, ds) ?? 10;
                string b = n.ToString(CultureInfo.InvariantCulture);
                return (new StatisticsRewrite(percent ? $"histogram({operand}, {b}, \"percent\")" : $"histogram({operand}, {b})",
                                              TraceDrawStyle.Bars), null);
            }
            case TraceStatistic.Cdf:      return (new StatisticsRewrite($"cdf({operand})", TraceDrawStyle.Step), null);
            default:                      return (new StatisticsRewrite($"normq({operand})", TraceDrawStyle.Line), null);
        }
    }

    /// <summary>
    /// Rewrites <paramref name="trace"/> as <paramref name="rewrite"/> says, remembering what it was the FIRST time,
    /// so a second entry (Histogram, then CDF) still takes the original trace's data and Back to curves still goes
    /// all the way back. The caller re-resolves.
    /// </summary>
    public static void Apply(Trace trace, StatisticsRewrite rewrite)
    {
        trace.StatisticsOrigin ??= new TraceStatisticsOrigin(
            trace.Expression, trace.CubeName, trace.Slice, trace.Transform, trace.Properties.DrawStyle);
        trace.CubeName  = null;
        trace.Slice     = null;
        trace.Transform = CubeTransform.None;
        trace.Expression = rewrite.Expression;
        trace.Properties.DrawStyle = rewrite.Style;
        trace.HeldBinRange = null;
    }

    /// <summary>
    /// The light second series a yield sensitivity adds (R-ya8-2): a step on the right axis, drawn at half opacity
    /// because it is an added series (<see cref="StatisticsRenderer"/>), which a saved display remembers. Its
    /// origin is EMPTY — the mark of a series the menu added — so Back to curves on it, or on the trace it
    /// accompanies, removes it rather than restoring anything.
    /// </summary>
    public static Trace CompanionOf(Trace trace, string expression)
    {
        var c = new Trace(trace, includeMarkers: false)
        {
            CubeName = null, Slice = null, Transform = CubeTransform.None, Expression = expression,
            UseSecondaryAxis = true, StatisticsOrigin = AddedSeries, ShowNormalFit = false,
        };
        c.Properties.DrawStyle = TraceDrawStyle.Step;
        return c;
    }

    /// <summary>The origin of a series the menu added rather than rewrote.</summary>
    public static TraceStatisticsOrigin AddedSeries { get; } = new(null, null, null, CubeTransform.None, TraceDrawStyle.Line);

    /// <summary>True for a series the menu ADDED (a yield sensitivity's trials per bin).</summary>
    public static bool IsAddedSeries(Trace trace) => trace.StatisticsOrigin is { Expression: null, CubeName: null };

    /// <summary>
    /// "Back to curves": restores the trace exactly as it was before the first menu entry, and returns the series the
    /// menu ADDED on the same plot from the same source (a yield sensitivity's companion), which the caller removes.
    /// A trace that is itself an added series is returned for removal. Empty, and nothing changed, for a trace that
    /// is not a statistics view.
    /// </summary>
    public static IReadOnlyList<Trace> BackToCurves(Plot plot, Trace trace)
    {
        if (trace.StatisticsOrigin is not { } o) return [];
        if (IsAddedSeries(trace)) return [trace];

        trace.Expression = o.Expression;
        trace.CubeName   = o.CubeName;
        trace.Slice      = o.Slice;
        trace.Transform  = o.Transform;
        trace.Properties.DrawStyle = o.DrawStyle;
        trace.StatisticsOrigin = null;
        trace.ShowNormalFit    = false;
        trace.NormalFit        = null;
        trace.BarWidth         = null;
        trace.HeldBinRange     = null;
        return [.. plot.Traces.Where(t => !ReferenceEquals(t, trace) && IsAddedSeries(t)
                                          && string.Equals(t.SourcePath, trace.SourcePath, StringComparison.OrdinalIgnoreCase))];
    }

    // ── reading an expression back ───────────────────────────────────────────

    /// <summary>
    /// The value a histogram, CDF or normal plot is OF — the first argument of the one call the expression is —
    /// or null when the expression is not one of those calls. What the spec lines and the normal fit read.
    /// </summary>
    public static string? ValueOperand(string expression)
        => OuterCall(expression, out var name, out var args) && name is "histogram" or "cdf" or "normq" && args.Count > 0
            ? args[0] : null;

    /// <summary>True when the expression is one <c>histogram(…)</c> call with no range of its own.</summary>
    public static bool IsUnrangedHistogram(string expression)
        => OuterCall(expression, out var name, out var args) && name == "histogram" && args.Count is 2 or 3;

    /// <summary>The histogram with <paramref name="lo"/>…<paramref name="hi"/> written in as its range — how a live
    /// histogram's held bins are evaluated (R-ya8-7). Any other expression is returned unchanged.</summary>
    public static string WithBinRange(string expression, double lo, double hi)
    {
        if (!OuterCall(expression, out var name, out var args) || name != "histogram" || args.Count is not (2 or 3))
            return expression;
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var parts = new List<string> { args[0], args[1], R(lo), R(hi) };
        if (args.Count == 3) parts.Add(args[2]);
        return $"histogram({string.Join(", ", parts)})";
    }

    /// <summary>The range a live histogram's bins are held at: its operand's extent, once it has at least
    /// <see cref="HoldAfterTrials"/> values — read through <c>pctl_over</c>, which skips a trial with no value.</summary>
    public static (double Lo, double Hi)? HoldRange(string expression, DataSet ds)
    {
        if (!IsUnrangedHistogram(expression) || ValueOperand(expression) is not { } op) return null;
        if (!TraceExpression.TryEvaluateValue(op, ds, out var v, out _) || v.Kind != ValueKind.Cube) return null;
        if (SampleStatistics.Present(v.AsCube().RealValues).Length < HoldAfterTrials) return null;
        if (!TraceExpression.TryEvaluateValue($"pctl_over({op}, 0)", ds, out var lo, out _)
            || !TraceExpression.TryEvaluateValue($"pctl_over({op}, 100)", ds, out var hi, out _)
            || lo.Kind != ValueKind.Real || hi.Kind != ValueKind.Real) return null;
        return (lo.AsReal(), hi.AsReal());
    }

    /// <summary>
    /// The fitted normal curve of a histogram trace (R-ya8-4): its operand's <c>mean_over</c> and <c>std_over</c>, and
    /// the area under the bars it is scaled to. Null when the trace is not a histogram or its operand has no spread.
    /// </summary>
    public static NormalFitCurve? NormalFitOf(Trace trace, DataSet ds)
    {
        if (trace.Expression is not { } e || !OuterCall(e, out var name, out _) || name != "histogram") return null;
        if (ValueOperand(e) is not { } op) return null;
        if (!TraceExpression.TryEvaluateValue($"mean_over({op})", ds, out var mean, out _)
            || !TraceExpression.TryEvaluateValue($"std_over({op})", ds, out var sd, out _)
            || mean.Kind != ValueKind.Real || sd.Kind != ValueKind.Real) return null;
        double mu = mean.AsReal(), sigma = sd.AsReal();
        if (!double.IsFinite(mu) || !(sigma > 0)) return null;
        double width = trace.BarWidth ?? double.NaN;
        if (!(width > 0)) return null;
        double sum = trace.Points.Sum(p => (double)p.Y);
        return new NormalFitCurve(mu, sigma, sum * width);
    }

    /// <summary>The name and top-level arguments of an expression that is ONE function call, end to end.</summary>
    private static bool OuterCall(string expression, out string name, out List<string> args)
    {
        name = ""; args = [];
        string e = expression.Trim();
        int open = e.IndexOf('(');
        if (open <= 0 || !e.EndsWith(')')) return false;
        name = e[..open].Trim();
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;

        int depth = 0, start = open + 1;
        bool quoted = false;
        for (int i = open; i < e.Length; i++)
        {
            char c = e[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']')
            {
                depth--;
                if (depth == 0 && i != e.Length - 1) return false;   // the call closes before the end
            }
            else if (c == ',' && depth == 1)
            {
                args.Add(e[start..i].Trim());
                start = i + 1;
            }
        }
        args.Add(e[start..^1].Trim());
        return depth == 0;
    }

    /// <summary>What a trace reads now — or, for a statistics view, what it read before the menu rewrote it.</summary>
    private static (string? Expression, string? Cube, AxisSlice[]? Slice, CubeTransform Transform) Reads(Trace trace)
        => trace.StatisticsOrigin is { } o && !IsAddedSeries(trace)
            ? (o.Expression, o.CubeName, o.Slice, o.Transform)
            : (trace.Expression, trace.CubeName, trace.Slice, trace.Transform);
}
