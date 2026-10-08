// ================================================================
//  DoeDisplayPreset.cs  —  the one-click design-of-experiments display
//  (brief-yield-14 R-ya14-6)
//
//  A Data Display document over a `<design>.doe.npy`: per response,
//  the effects Pareto (|effect| as bars, largest first, the terms'
//  names on the X axis, Lenth's margin as a line), the main-effects
//  plot (the response's mean at each level, one line per factor) and
//  the interaction plot of its strongest active pair (the response
//  across factor A, one line per level of factor B).
//
//  THIS COMPOSES, IT DOES NOT DRAW, AND IT COMPUTES NOTHING. Every
//  number is a cube DoeRun wrote (docs/design/results-dataset-layout.md
//  §"Design of experiments"); every plot is a cube trace a user could
//  build on the trace card — bars are YA-8's draw style, the family is
//  the ordinary FamilyIterate slice.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class DoeDisplayPreset
{
    /// <summary>The tab the display opens on.</summary>
    public const string TabName = "DOE";

    private const string Effects = "effects", Main = "main", Interaction = "interaction", Runs = "runs";
    private const double PlotWidth = 440, PlotHeight = 320, Gap = 24;

    /// <summary>The kinds of plot, in the order a response's row places them.</summary>
    public enum PlotKind { EffectsPareto, MainEffects, Interaction }

    /// <summary>One placed plot and what it is.</summary>
    public sealed record ComposedPlot(PlotKind Kind, string Response, PlotContainerConfig Config);

    /// <summary>
    /// The responses a display draws: each goal once — its worst value, or its margin when its value was not
    /// analysed — then every measure. A goal's margin beside its value repeats the same effects with the sign turned.
    /// </summary>
    public static IReadOnlyList<string> Responses(DataSet ds)
    {
        if (!ds.ContainsGroup(Runs) || !ds.ContainsGroup(Effects)) return [];
        var analysed = ds.CubesIn(Effects).Keys.Where(k => k.EndsWith(":effect", StringComparison.Ordinal))
                         .Select(k => k[..^":effect".Length]).ToList();
        var shown = new List<string>();
        foreach (var r in analysed)
        {
            if (r.StartsWith("goal:", StringComparison.Ordinal) && r.EndsWith(":margin", StringComparison.Ordinal)
                && analysed.Contains(r[..^":margin".Length] + ":worst")) continue;
            shown.Add(r);
        }
        return shown;
    }

    /// <summary>The plots over <paramref name="ds"/>, every trace bound to <paramref name="sourceRef"/>, one row per
    /// response. Empty when the source is not a design-of-experiments result.</summary>
    public static IReadOnlyList<ComposedPlot> Compose(DataSet ds, string sourceRef)
    {
        var plots = new List<ComposedPlot>();
        foreach (var r in Responses(ds))
        {
            var row = new List<ComposedPlot>();
            if (Pareto(ds, r, sourceRef) is { } pareto) row.Add(new ComposedPlot(PlotKind.EffectsPareto, r, pareto));
            if (MainEffects(ds, r, sourceRef) is { } main) row.Add(new ComposedPlot(PlotKind.MainEffects, r, main));
            if (StrongestPair(ds, r) is { } pair && InteractionPlot(ds, r, pair, sourceRef) is { } inter)
                row.Add(new ComposedPlot(PlotKind.Interaction, r, inter));
            plots.AddRange(row);
        }

        double y = Gap;
        foreach (var row in plots.GroupBy(p => p.Response))
        {
            double x = Gap, h = 0;
            foreach (var p in row)
            {
                p.Config.Left = x;
                p.Config.Top = y;
                x += p.Config.Width + Gap;
                h = Math.Max(h, p.Config.Height);
            }
            y += h + Gap;
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

    // ── The effects Pareto ─────────────────────────────────────────────────────────────

    private static PlotContainerConfig? Pareto(DataSet ds, string r, string sourceRef)
    {
        string bars = $"{Effects}.{r}:abs", line = $"{Effects}.{r}:margin_line";
        if (!ds.Contains(bars) || !ds.Contains(line)) return null;
        int count = ds[bars].Axes[0].Length;
        var b = CubeTrace(bars, sourceRef, [new AxisSlice("rank", AxisRole.KeepAsX, 0)], 0);
        b.Properties!.DrawStyle = TraceDrawStyle.Bars;
        var m = CubeTrace(line, sourceRef, [new AxisSlice("rank", AxisRole.KeepAsX, 0)], 1);
        var plot = Rect($"{r}: effects (Pareto)", b, m);
        plot.Width = Math.Max(PlotWidth, 90 + 44 * count);
        plot.CustomYLabel = "|effect| (bars), Lenth margin (line)";
        plot.CustomYLabelOn = true;
        return plot;
    }

    // ── Main effects ───────────────────────────────────────────────────────────────────

    private static PlotContainerConfig? MainEffects(DataSet ds, string r, string sourceRef)
    {
        if (!ds.ContainsGroup(Main)) return null;
        string prefix = r + ":";
        var cubes = ds.CubesIn(Main).Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (cubes.Count == 0) return null;
        var traces = cubes.Select((k, i) =>
        {
            var t = CubeTrace($"{Main}.{k}", sourceRef, [new AxisSlice("level", AxisRole.KeepAsX, 0)], i);
            t.Properties!.MarkerEnabled = true;
            return t;
        }).ToArray();
        var plot = Rect($"{r}: main effects", traces);
        plot.CustomXLabel = "coded level";
        plot.CustomXLabelOn = true;
        return plot;
    }

    // ── Interaction ────────────────────────────────────────────────────────────────────

    /// <summary>The pair whose interaction effect is largest — among the active ones when any is — as its cube's name
    /// in the interaction group; null when the design estimates no interaction.</summary>
    public static string? StrongestPair(DataSet ds, string r)
    {
        if (!ds.ContainsGroup(Interaction) || !ds.Contains($"{Effects}.{r}:effect")) return null;
        var effect = ds[$"{Effects}.{r}:effect"];
        var active = ds.Contains($"{Effects}.{r}:active") ? ds[$"{Effects}.{r}:active"].RealValues : null;
        var terms = effect.Axes[0].Labels ?? [];
        var cubes = ds.CubesIn(Interaction).Keys.Where(k => k.StartsWith(r + ":", StringComparison.Ordinal)).ToList();
        if (cubes.Count == 0) return null;

        // Interaction cubes are written in the model's order of two-factor terms, so the k-th two-letter term is the
        // k-th cube.
        var pairTerms = terms.Select((t, i) => (t, i)).Where(x => x.t.Length == 2 && !x.t.Contains('^')).ToList();
        if (pairTerms.Count != cubes.Count) return cubes[0];
        int best = -1;
        bool bestActive = false;
        for (int p = 0; p < pairTerms.Count; p++)
        {
            int i = pairTerms[p].i;
            bool isActive = active is not null && active[i] > 0;
            double v = Math.Abs(effect.RealValues[i]);
            if (best < 0 || (isActive && !bestActive) || (isActive == bestActive && v > Math.Abs(effect.RealValues[pairTerms[best].i])))
            {
                best = p;
                bestActive = isActive;
            }
        }
        return cubes[best];
    }

    private static PlotContainerConfig? InteractionPlot(DataSet ds, string r, string cube, string sourceRef)
    {
        string spec = $"{Interaction}.{cube}";
        if (!ds.Contains(spec)) return null;
        var t = CubeTrace(spec, sourceRef,
            [new AxisSlice("by_level", AxisRole.FamilyIterate, 0), new AxisSlice("level", AxisRole.KeepAsX, 0)], 0);
        t.Properties!.MarkerEnabled = true;
        string pair = cube[(r.Length + 1)..];
        int star = pair.IndexOf('*');
        var plot = Rect($"{r}: interaction {pair.Replace("*", " × ")}", t);
        plot.CustomXLabel = star > 0 ? $"{pair[..star]} (coded)" : "coded level";
        plot.CustomXLabelOn = true;
        return plot;
    }

    // ── Shared ─────────────────────────────────────────────────────────────────────────

    private static TraceConfig CubeTrace(string spec, string sourceRef, AxisSlice[] slice, int colour)
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db, false)
        {
            CubeName = spec,
            Slice    = slice,
        };
        t.Expression = t.BuildPickerExpression();
        int c = TraceProperties.LineColorOrder[colour % TraceProperties.LineColorOrder.Length];
        return new TraceConfig
        {
            SourcePath = sourceRef,
            CubeName   = spec,
            CubeSlice  = [.. slice.Select(AxisSliceConfig.From)],
            Expression = t.Expression,
            Properties = new TracePropertiesConfig { LineColorIndex = c, MarkerColorIndex = c },
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
