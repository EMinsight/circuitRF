// ================================================================
//  TrialViews.cs  —  the trace card's trial entries as functions
//  (brief-yield-9 R-ya9-1, R-ya9-3, R-ya9-4)
//
//  The menu in the window and `plot --trace` both set a trace's trial
//  views through here, so the two cannot set them differently: what the
//  colour-by choices are, whether an envelope is offered (and why not on
//  a Smith or Polar plot), and what "Scatter vs" writes — an ordinary
//  Plot Versus spec and a points style, both visible on the card.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Statistics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class TrialViews
{
    /// <summary>
    /// What <paramref name="trace"/> can be coloured by in <paramref name="ds"/>: <c>pass</c> on a yield source and
    /// each goal's own <c>trials.goal:&lt;g&gt;:pass</c>, on a family over <c>trial</c> or a scatter over trials; or
    /// <c>corner</c> on a family over <c>corner</c>. Each is (the value <see cref="Trace.ColorBy"/> takes, its label).
    /// </summary>
    public static IReadOnlyList<(string Value, string Label)> ColourByChoices(Trace trace, DataSet ds)
    {
        string? axis = trace.IsFamily ? trace.FamilyAxisName : trace.ElementKind == TrialElements.Points ? Evaluator.TrialAxis : null;
        if (axis == "corner") return [(Trace.ColorByCorner, "Corner")];
        if (axis != Evaluator.TrialAxis || TraceStatistics.PassSpec(ds) is null) return [];
        var choices = new List<(string, string)> { (Trace.ColorByPass, "Pass / Fail") };
        foreach (var g in ResultContributions.GoalsOf(ds))
            if (ds.Contains($"trials.goal:{g}:pass")) choices.Add(($"trials.goal:{g}:pass", $"{g} Pass / Fail"));
        return choices;
    }

    /// <summary>Whether an envelope can be drawn on <paramref name="trace"/> on a <paramref name="plotType"/> plot, and
    /// the reason it cannot — the menu item's tooltip (R-ya9-3).</summary>
    public static string? EnvelopeRefusal(Trace trace, PlotType plotType)
        => !trace.IsFamily ? "An envelope is a family's: a band across its members at each X."
         : plotType != PlotType.Rect ? TrialResolve.ComplexPlaneRefusal
         : null;

    /// <summary>The envelopes the menu offers.</summary>
    public static IReadOnlyList<TrialEnvelope> EnvelopeChoices { get; } =
    [
        new(EnvelopeKind.MinMax, 0), new(EnvelopeKind.Percentile, 1), new(EnvelopeKind.Percentile, 5),
        new(EnvelopeKind.Sigma, 1), new(EnvelopeKind.Sigma, 3),
    ];

    /// <summary>
    /// What a trace over trials can be scattered against (R-ya9-4): every statistical entry's drawn value, every
    /// goal's worst value and every scalar measure kept per trial — each a per-trial cube's address — less the
    /// trace's own. Empty for a trace whose X is not the trial axis.
    /// </summary>
    public static IReadOnlyList<string> ScatterCandidates(Trace trace, DataSet ds)
    {
        if (trace.IsFamily || trace.CubeName is null || trace.Slice is not { } slice
            || !slice.Any(s => s.Role == AxisRole.KeepAsX && s.AxisName == Evaluator.TrialAxis)) return [];
        var all = TraceStatistics.StatSpecs(ds)
            .Concat(ResultContributions.GoalsOf(ds).Select(g => $"trials.goal:{g}:worst"))
            .Concat(ResultContributions.MeasuresOf(ds).Select(m => $"{DataSet.MeasurementsGroup}.{m}"));
        return [.. all.Where(c => !string.Equals(c, trace.CubeName, StringComparison.Ordinal) && !c.EndsWith("." + trace.CubeName, StringComparison.Ordinal))];
    }

    /// <summary>
    /// "Scatter vs" (R-ya9-4): the trace drawn against <paramref name="xSpec"/> — an ordinary Plot Versus spec — as
    /// points with no line, coloured by pass where the source scores goals. The caller re-resolves.
    /// </summary>
    public static void ApplyScatter(Trace trace, DataSet ds, string xSpec)
    {
        trace.XSpec       = xSpec;
        trace.XSourcePath = null;
        trace.Properties.LineEnabled   = false;
        trace.Properties.MarkerEnabled = true;
        if (trace.ColorBy is null && TraceStatistics.PassSpec(ds) is not null) trace.ColorBy = Trace.ColorByPass;
        if (trace.CubeName is not null) trace.Expression = trace.BuildPickerExpression();
    }
}
