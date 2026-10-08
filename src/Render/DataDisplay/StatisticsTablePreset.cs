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

    /// <summary>True when <paramref name="ds"/> carries a statistics table.</summary>
    public static bool Available(DataSet? ds) => ds is not null && ds.ContainsGroup(Group) && ds.CubesIn(Group).Count > 0;

    /// <summary>
    /// The Table plot: one column per statistic in the order the result holds them, each a cube trace over the
    /// <c>quantity</c> axis of <paramref name="sourceRef"/>. Null when the source carries no statistics table.
    /// </summary>
    public static PlotContainerConfig? Build(DataSet ds, string sourceRef)
    {
        if (!Available(ds)) return null;
        var traces = new List<TraceConfig>();
        foreach (var (name, cube) in ds.CubesIn(Group))
        {
            if (cube.Rank != 1 || cube.Axes[0].Name != RowAxis) continue;
            int color = TraceProperties.LineColorOrder[traces.Count % TraceProperties.LineColorOrder.Length];
            traces.Add(new TraceConfig
            {
                SourcePath    = sourceRef,
                CubeName      = $"{Group}.{name}",
                CubeSlice     = [new AxisSliceConfig { AxisName = RowAxis, Role = AxisRole.KeepAsX }],
                FormatString  = PrecisionFormat.G,
                MaximumFractionDigits = 4,
                ColumnWidth   = 90,
                Properties    = new TracePropertiesConfig { LineColorIndex = color, MarkerColorIndex = color },
            });
        }
        if (traces.Count == 0) return null;
        return new PlotContainerConfig
        {
            PlotType        = PlotType.Table,
            Width           = 160 + 90 * traces.Count,
            Height          = 360,
            CustomTitle     = "Statistics",
            CustomTitleOn   = true,
            FreqColumnWidth = 160,
            Traces          = traces,
        };
    }
}
