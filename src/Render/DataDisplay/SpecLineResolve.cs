// ================================================================
//  SpecLineResolve.cs  —  which goals a trace draws, and their limits
//  as lines (brief-yield-8 R-ya8-3)
//
//  A Monte Carlo result records each scored goal's own `goal …` line
//  (`yield.goal:<g>:spec`, docs/design/results-dataset-layout.md), so
//  the limits come from the FILE — the CLI's `plot`, which has nothing
//  else, draws them exactly as the window does.
//
//  The goal a trace draws is found two ways, and only two:
//   • a histogram, CDF or normal plot of `goal:<g>:worst` — or of an
//     operand that reads the same quantity as a goal with no range —
//     draws the limit VERTICALLY, at the value;
//   • any other curve reads the same quantity as a goal when
//     TraceGoalReader's own translation ("Add as goal…") of it matches
//     the goal's expression (TraceToGoal.SameQuantity), and draws the
//     limit ACROSS the goal's range, sloped where it slopes.
//  So the one place that knows how a trace and a goal correspond is
//  that translation, read in reverse.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class SpecLineResolve
{
    /// <summary>The suffix of the cube a result records a goal's line as: <c>goal:&lt;g&gt;:spec</c>.</summary>
    private static readonly Regex SpecCube = new(@"^goal:(?<g>.+):spec$", RegexOptions.CultureInvariant);

    private static readonly Regex WorstRef = new(@"goal:(?<g>[^:\s,()\[\]]+):worst", RegexOptions.CultureInvariant);

    /// <summary>The goals <paramref name="ds"/> records, in the order it holds them. A line that no longer
    /// parses is skipped — the picture loses that goal's lines, never the trace. A run at each corner stacks the
    /// line under a <c>corner</c> axis; the line is the same at every corner, so the first is read
    /// (brief-yield-16 R-ya16-3).</summary>
    public static IReadOnlyList<OptimizationGoal> GoalsOf(DataSet ds)
    {
        var goals = new List<OptimizationGoal>();
        foreach (var g in ds.Groups)
            foreach (var (name, cube) in ds.CubesIn(g))
            {
                if (!SpecCube.IsMatch(name) || cube.Rank < 1 || cube.Axes[^1].Labels is not { Length: > 0 } labels) continue;
                try { goals.Add(TuningDirectiveText.ReadGoalLine(labels[0])); }
                catch (TuningDirectiveException) { }
            }
        return goals;
    }

    /// <summary>The spec lines <paramref name="trace"/> draws against <paramref name="ds"/>; empty when it draws no
    /// recorded goal's quantity.</summary>
    public static IReadOnlyList<SpecLine> For(Trace trace, DataSet ds)
    {
        if (!trace.IsCubeBound || trace.IsContourTrace) return [];
        var goals = GoalsOf(ds);
        if (goals.Count == 0) return [];

        // A statistic of a value: the limit is a VALUE on the X axis.
        if (trace.Expression is { } expr && TraceStatistics.ValueOperand(expr) is { } operand)
        {
            var g = GoalOfWorst(operand, goals)
                 ?? goals.FirstOrDefault(x => x.Range is null && TraceToGoal.SameQuantity(operand, x.Expression));
            return g is null ? [] : Vertical(g);
        }

        // A curve: the limit across the goal's range.
        string? read = TraceGoalReader.GoalExpressionOf(trace, ds);
        if (read is null) return [];
        var lines = new List<SpecLine>();
        if (GoalOfWorst(read, goals) is { } worst)
            lines.AddRange(Across(worst, ignoreRange: true));
        foreach (var g in goals)
            if (TraceToGoal.SameQuantity(read, g.Expression)
                && (g.Range is null || string.Equals(g.Range.Axis, trace.CubeXAxisName, StringComparison.Ordinal)))
                lines.AddRange(Across(g, ignoreRange: false));
        return lines;
    }

    private static OptimizationGoal? GoalOfWorst(string text, IReadOnlyList<OptimizationGoal> goals)
        => WorstRef.Match(text) is { Success: true } m
            ? goals.FirstOrDefault(g => g.Name == m.Groups["g"].Value)
            : null;

    /// <summary>The goal's limits as values: one line for <c>ge</c>/<c>le</c>/<c>eq</c> — a sloped limit at its
    /// TIGHTER end, which is the value every trial has to clear — and the two band edges for <c>in</c>/<c>out</c>.</summary>
    private static IReadOnlyList<SpecLine> Vertical(OptimizationGoal g)
    {
        if (!Limits(g, out double lo, out double atHi, out double upper)) return [];
        SpecLine V(string op, double x, string text) => new(g.Name, $"{g.Name} {op} {text}", true, x, x, double.NaN, double.NaN);
        switch (g.Type)
        {
            case GoalType.Ge: return [atHi > lo ? V("≥", atHi, g.LimitAtHi!) : V("≥", lo, g.Limit)];
            case GoalType.Le: return [atHi < lo ? V("≤", atHi, g.LimitAtHi!) : V("≤", lo, g.Limit)];
            case GoalType.Eq: return [V("=", lo, g.Limit)];
            case GoalType.In: return [V("≥", lo, g.Limit), V("≤", upper, g.UpperLimit!)];
            default:          return [V("≤", lo, g.Limit), V("≥", upper, g.UpperLimit!)];
        }
    }

    /// <summary>The goal's limits across its range, in the X axis's base unit; NaN ends run to the window's edge.</summary>
    private static IReadOnlyList<SpecLine> Across(OptimizationGoal g, bool ignoreRange)
    {
        if (!Limits(g, out double lo, out double atHi, out double upper)) return [];
        double x0 = double.NaN, x1 = double.NaN;
        if (!ignoreRange && g.Range is { } r)
        {
            if (!TunableValue.TryParse(r.Lo, out _, out _, out x0) || !TunableValue.TryParse(r.Hi, out _, out _, out x1)) return [];
        }
        // A slope needs a range to run across; without one the limit is flat.
        if (double.IsNaN(x0)) atHi = lo;
        SpecLine L(string op, double y0, double y1, string text) => new(g.Name, $"{g.Name} {op} {text}", false, x0, x1, y0, y1);
        string Sloped(string t) => g.LimitAtHi is { } h && !double.IsNaN(x0) ? $"{t} to {h}" : t;
        return g.Type switch
        {
            GoalType.Ge => [L("≥", lo, atHi, Sloped(g.Limit))],
            GoalType.Le => [L("≤", lo, atHi, Sloped(g.Limit))],
            GoalType.Eq => [L("=", lo, atHi, Sloped(g.Limit))],
            GoalType.In => [L("≥", lo, lo, g.Limit), L("≤", upper, upper, g.UpperLimit!)],
            _           => [L("≤", lo, lo, g.Limit), L("≥", upper, upper, g.UpperLimit!)],
        };
    }

    /// <summary>The goal's limit, its value at the range's high end (the limit itself when not sloped) and the
    /// band's upper edge, in base SI. False when one is not a number.</summary>
    private static bool Limits(OptimizationGoal g, out double lo, out double atHi, out double upper)
    {
        atHi = upper = double.NaN;
        if (!TunableValue.TryParse(g.Limit, out _, out _, out lo)) return false;
        atHi = lo;
        if (g.LimitAtHi is { } h && !TunableValue.TryParse(h, out _, out _, out atHi)) return false;
        if (g.Type is GoalType.In or GoalType.Out)
        {
            if (g.UpperLimit is null || !TunableValue.TryParse(g.UpperLimit, out _, out _, out upper)) return false;
            if (lo > upper) (lo, upper) = (upper, lo);
        }
        return true;
    }
}
