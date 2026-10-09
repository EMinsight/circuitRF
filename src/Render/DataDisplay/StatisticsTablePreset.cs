// ================================================================
//  StatisticsTablePreset.cs  —  the statistics table as a Table plot
//  (brief-yield-8 R-ya8-5)
//
//  A Monte Carlo result carries its statistics table as the
//  `statistics` group (docs/design/results-dataset-layout.md): one cube
//  per column over a `quantity` axis labelled with each row's name, all
//  computed by the run with SampleStatistics — the functions the CLI's
//  statistics table uses. This preset is ordinary cube traces over that
//  axis, one per column, so the rows are the quantities, the columns
//  the statistics, and nothing here computes a number. A live run
//  publishes the group with every frame, so the table follows it.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class StatisticsTablePreset
{
    /// <summary>The result group the table reads.</summary>
    public const string Group = "statistics";

    /// <summary>The axis its rows are on.</summary>
    public const string RowAxis = "quantity";

    /// <summary>True when <paramref name="ds"/> carries a statistics table of one run.</summary>
    public static bool Available(DataSet? ds)
        => ds is not null && ds.ContainsGroup(Group) && ds.CubesIn(Group).Count > 0 && Refusal(ds) is null;

    /// <summary>Why <paramref name="ds"/>'s table cannot be added: a run at each corner holds one table per corner,
    /// stacked under a <c>corner</c> axis (brief-yield-16 R-ya16-3). Null otherwise.</summary>
    public static string? Refusal(DataSet? ds)
        => ds is null ? null : YieldDisplayPreset.CornerStackedRefusal(ds, "The statistics table");

    /// <summary>
    /// The Table plot: one column per statistic in the order the result holds them, each a cube trace over the
    /// <c>quantity</c> axis of <paramref name="sourceRef"/>. Null when the source carries no statistics table.
    /// </summary>
    public static PlotContainerConfig? Build(DataSet ds, string sourceRef)
    {
        if (!Available(ds)) return null;
        var plot = new PlotContainerConfig();
        var traces = new List<TraceConfig>();
        Axis? rows = null;
        foreach (var (name, cube) in ds.CubesIn(Group))
        {
            if (cube.Rank != 1 || cube.Axes[0].Name != RowAxis) continue;
            rows ??= cube.Axes[0];
            int color = TraceProperties.LineColorOrder[traces.Count % TraceProperties.LineColorOrder.Length];
            string spec = $"{Group}.{name}";
            traces.Add(new TraceConfig
            {
                SourcePath    = sourceRef,
                CubeName      = spec,
                CubeSlice     = [new AxisSliceConfig { AxisName = RowAxis, Role = AxisRole.KeepAsX }],
                FormatString  = PrecisionFormat.G,
                MaximumFractionDigits = Digits,
                // Wide enough for its header and every value it holds: a clipped column name is a column
                // nobody can read (brief-yield-16 follow-up).
                ColumnWidth   = Math.Ceiling(Math.Max(MinColumn, TableRenderer.FitWidth(
                    spec, cube.RealValues.Select(v => PrecisionFormat.G.Format(v, Digits)), plot.FontSize, xAxis: false))),
                Properties    = new TracePropertiesConfig { LineColorIndex = color, MarkerColorIndex = color },
            });
        }
        if (traces.Count == 0) return null;
        // The row names — each goal's and measure's quantity — under the axis's own header.
        double rowColumn = Math.Ceiling(TableRenderer.FitWidth(RowAxis, rows!.Labels ?? [], plot.FontSize, xAxis: true));
        plot.PlotType        = PlotType.Table;
        plot.Width           = rowColumn + traces.Sum(t => t.ColumnWidth);
        plot.Height          = 360;
        plot.CustomTitle     = "Statistics";
        plot.CustomTitleOn   = true;
        plot.FreqColumnWidth = rowColumn;
        plot.Traces          = traces;
        return plot;
    }

    /// <summary>The significant fraction digits a statistic is shown to.</summary>
    private const int Digits = 4;

    /// <summary>The narrowest a column is made, so a short header does not make a cramped one.</summary>
    private const double MinColumn = 70;
}
