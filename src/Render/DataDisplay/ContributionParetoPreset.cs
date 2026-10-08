// ================================================================
//  ContributionParetoPreset.cs  —  which variables drive a goal, as a
//  Pareto chart (brief-yield-9 R-ya9-5)
//
//  Contributions are never computed unasked (YA-4): the chart is made
//  by an explicit action, which ranks the goal ONCE through
//  ResultContributions.Store — the same ranking the run and the CLI give
//  — and keeps it in the source as `yield.contrib:<goal>` over a
//  labelled `contributor` axis. The plot is then two ordinary traces
//  over that cube: the shares as bars (in percent, largest first, the
//  contributors' names on the X axis) and their running total as a line
//  on the right axis. Nothing here computes a number.
// ================================================================

using System.Collections.Generic;
using CircuitRF.Design.Statistics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class ContributionParetoPreset
{
    /// <summary>
    /// Ranks <paramref name="name"/> into <paramref name="ds"/> unless it is already there, and returns the Pareto plot
    /// over it bound to <paramref name="sourceRef"/> — or the refusal's sentence when nothing could be ranked.
    /// <paramref name="stored"/> is true when this call added the cubes (the caller then writes the source back).
    /// </summary>
    public static (PlotContainerConfig? Plot, string? Refusal) Build(DataSet ds, string name, string sourceRef, out bool stored)
    {
        stored = false;
        string share = $"{ResultContributions.Group}.{ResultContributions.CubeName(name)}";
        string cumulative = $"{ResultContributions.Group}.{ResultContributions.CumulativeName(name)}";
        if (!ds.Contains(share) || !ds.Contains(cumulative))
        {
            var report = ResultContributions.Store(ds, name);
            if (report.Refusal is { } why) return (null, why.Render());
            if (report.Contributors.Count == 0) return (null, $"Nothing drives {name}: no statistical variable varied.");
            stored = true;
        }

        int count = ds[share].Axes[0].Length;
        var bars = new TraceConfig
        {
            SourcePath = sourceRef,
            Expression = $"100*{share}",
            Properties = new TracePropertiesConfig
            {
                LineColorIndex = TraceProperties.LineColorOrder[0], MarkerColorIndex = TraceProperties.LineColorOrder[0],
                DrawStyle = TraceDrawStyle.Bars,
            },
        };
        var line = new TraceConfig
        {
            SourcePath       = sourceRef,
            Expression       = $"100*{cumulative}",
            UseSecondaryAxis = true,
            Properties       = new TracePropertiesConfig
            {
                LineColorIndex = TraceProperties.LineColorOrder[1], MarkerColorIndex = TraceProperties.LineColorOrder[1],
                MarkerEnabled  = true,
            },
        };
        return (new PlotContainerConfig
        {
            PlotType      = PlotType.Rect,
            Width         = System.Math.Max(420, 90 + 70 * count),
            Height        = 300,
            CustomTitle   = $"Contributions to {name}",
            CustomTitleOn = true,
            CustomYLabel  = "share of variance (%)",
            CustomYLabelOn = true,
            Traces        = new List<TraceConfig> { bars, line },
        }, null);
    }
}
