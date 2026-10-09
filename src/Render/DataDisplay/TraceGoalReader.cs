using System;
using System.Linq;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

/// <summary>
/// Reads what "Add as goal…" needs off a <see cref="Trace"/> and its <see cref="Plot"/> into a
/// <see cref="TraceGoalSource"/> (brief-tuneopt-9 R-to9-4). It translates nothing — that is
/// <see cref="TraceToGoal"/>, in <c>src/Design</c> — it only knows where on a trace each fact lives,
/// so it stays below the firewall beside the model it reads and the CLI could ask the same question.
/// </summary>
public static class TraceGoalReader
{
    /// <summary>The goal <paramref name="trace"/> becomes, or why it cannot become one.</summary>
    /// <param name="source">The DataSet the trace is drawn from — a schematic's run results.</param>
    public static TraceGoalResult Translate(Trace trace, Plot plot, DataSet? source,
                                            CircuitRF.Core.Design.TestBench? tb = null)
        => TraceToGoal.Translate(Read(trace, plot, source), tb);

    public static TraceGoalSource Read(Trace trace, Plot plot, DataSet? source)
    {
        var (lo, hi, axis, unit) = VisibleRange(trace, plot);
        var common = new TraceGoalSource
        {
            Axis = axis, AxisUnit = unit, Lo = lo, Hi = hi,
            MarkerValue = plot.PlotType == PlotType.Rect ? VisibleMarkerValue(trace, plot) : null,
            Unsupported = Unsupported(trace, source),
        };
        if (common.Unsupported is not null) return common;

        if (trace.IsCubeBound) return CubeSource(trace, source, common);

        string? sCube = source is null ? null : NetworkMetrics.FindSCubeSpec(source);
        var network = common with
        {
            SCube = sCube,
            PortCount = source is null ? 2 : NetworkMetrics.PortCount(source),
            Analysis = sCube is { } spec && spec.LastIndexOf('.') is int d && d > 0 ? spec[..d] : null,
        };
        if (!trace.IsDerived)
            return network with
            {
                Kind = TraceGoalKind.Network,
                Row = trace.Row + 1, Col = trace.Col + 1,
                Format = trace.YAxis switch
                {
                    DependentVarFormat.Db        => TraceNetworkFormat.Db,
                    DependentVarFormat.Mag       => TraceNetworkFormat.Mag,
                    DependentVarFormat.Phase     => TraceNetworkFormat.Phase,
                    DependentVarFormat.Real      => TraceNetworkFormat.Real,
                    DependentVarFormat.Imaginary => TraceNetworkFormat.Imaginary,
                    _                            => TraceNetworkFormat.Complex,
                },
            };

        return network with
        {
            Kind = TraceGoalKind.Derived,
            Metric = trace.Derived switch
            {
                DerivedParameters.Mu         => TraceDerivedMetric.Mu,
                DerivedParameters.MuPrime    => TraceDerivedMetric.MuPrime,
                DerivedParameters.K          => TraceDerivedMetric.K,
                DerivedParameters.DeltaMag   => TraceDerivedMetric.DeltaMag,
                DerivedParameters.MaxGain    => TraceDerivedMetric.MaxGain,
                DerivedParameters.Passivity  => TraceDerivedMetric.Passivity,
                DerivedParameters.GroupDelay => TraceDerivedMetric.GroupDelay,
                _                            => TraceDerivedMetric.Other,
            },
            MetricLabel = trace.Derived.Description(),
            InputPort = trace.InputPort, OutputPort = trace.OutputPort,
            MaxGainIsLog = trace.MaxGainIsLog,
            PassivityWholeNetwork = trace.PassivityWholeNetwork,
            // The card plots group delay in ns; group_delay() is in seconds.
            MarkerUnit = trace.Derived == DerivedParameters.GroupDelay ? "ns" : "",
        };
    }

    /// <summary>
    /// The expression a cube trace's goal would read — <see cref="TraceToGoal"/>'s translation, without a plot's
    /// window or marker — or null when the trace cannot be a goal. What the spec lines (brief-yield-8 R-ya8-3) match a
    /// curve to a goal with, so the one place that knows how a trace and a goal correspond is this translation.
    /// </summary>
    public static string? GoalExpressionOf(Trace trace, DataSet? source)
    {
        if (!trace.IsCubeBound || Unsupported(trace, source) is not null) return null;
        return TraceToGoal.Translate(CubeSource(trace, source, new TraceGoalSource())).Goal?.Expression;
    }

    private static TraceGoalSource CubeSource(Trace trace, DataSet? source, TraceGoalSource common)
    {
        // A pinned corner picks which corner of a run at each corner is drawn; a goal holds at every corner, so the
        // corner is not part of what it reads.
        if (trace.Slice?.Any(IsPinnedCorner) == true)
            trace = new Trace(trace, includeMarkers: false) { Slice = [.. trace.Slice.Where(s => !IsPinnedCorner(s))] };
        string? body = trace.PickerBody(forExpression: true);
        var transform = trace.Transform;
        if (body is null)
        {
            // A typed expression already carries its own functions; the card's transform does not apply.
            body = trace.Expression;
            transform = CubeTransform.None;
        }
        int row = 0, col = 0;
        if (trace.Slice is { } slice)
            foreach (var s in slice)
            {
                if (s.Role != AxisRole.PinToIndex) continue;
                if (s.AxisName == "i") row = s.Index + 1;
                if (s.AxisName == "j") col = s.Index + 1;
            }
        return common with
        {
            Kind = TraceGoalKind.Cube,
            Body = body,
            Transform = Map(transform),
            ValueIsComplex = trace.CubeDataIsComplex,
            Analysis = AnalysisOf(trace.CubeName ?? body, source),
            Row = row, Col = col,
        };
    }

    private static bool IsPinnedCorner(AxisSlice s)
        => s.AxisName == ResultContributions.CornerAxis && s.Role == AxisRole.PinToIndex;

    /// <summary>The reasons that hold whatever the trace's transform is.</summary>
    private static string? Unsupported(Trace trace, DataSet? source)
    {
        if (source is null || source.Groups.All(g => g is DataSet.DefaultGroup or DataSet.MeasurementsGroup))
            return "This trace is not drawn from a schematic's simulation results, so there is no analysis for a goal to run.";
        if (trace.IsContourTrace || trace.IsSummaryColumn || trace.IsAnnotation)
            return "This item is not a curve over a swept axis, so it cannot be a goal.";
        if (trace.IsVersus)
            return "This trace is plotted against another quantity; a goal holds over one swept axis. " +
                   "Plot it against its own axis to add it as a goal.";
        if (trace.Expression is { } e && e.Contains("::", StringComparison.Ordinal))
            return "This trace reads another data source, which a goal's analysis cannot.";
        if (trace.Z0OverrideEnabled)
            return "This trace is renormalized on the plot, and a goal reads the analysis's own reference impedance. " +
                   "Turn the plot's Z0 override off to add it as a goal.";
        if (!trace.IsCubeBound && !trace.IsDerived && trace.MatrixType != MatrixType.S)
            return $"{trace.MatrixType} is converted on the plot and has no accessor in an expression; plot S to add it as a goal.";
        if (trace.IsCubeBound && trace.CubeName is { } cube && cube.Split('.')[^1] is "Z" or "Y"
            && NetworkMetrics.IsNetworkParamCubeSpec(source, cube))
            return "Z and Y are converted from S on the plot and have no accessor in an expression; plot S to add it as a goal.";
        return null;
    }

    /// <summary>The analysis a cube spec's group names — <c>SP1</c> of <c>SP1.S</c> — or null for a
    /// measurement or anything not in an analysis group.</summary>
    private static string? AnalysisOf(string? spec, DataSet? source)
    {
        if (spec is null || source is null) return null;
        foreach (var g in source.Groups)
            if (g is not (DataSet.DefaultGroup or DataSet.MeasurementsGroup)
                && spec.StartsWith(g + ".", StringComparison.Ordinal))
                return g;
        return null;
    }

    /// <summary>The visible X window clipped to the trace's own data, in display units, with the axis
    /// it is on and the unit those numbers are in.</summary>
    private static (double? Lo, double? Hi, string Axis, string Unit) VisibleRange(Trace trace, Plot plot)
    {
        bool cube = trace.IsCubeBound;
        string axis = cube ? (string.IsNullOrEmpty(trace.CubeXAxisName) ? "freq" : trace.CubeXAxisName) : "freq";
        bool freqLike = !cube || trace.CubeXUnit is "Hz" or "kHz" or "MHz" or "GHz";
        string unit = freqLike ? plot.FreqUnits.Description() : trace.CubeXUnit ?? "";

        var win = plot.Axes.Window;
        double lo = Math.Min(win.Left, win.Right), hi = Math.Max(win.Left, win.Right);
        var xs = trace.IsFamily && trace.FamilyCurves.Count > 0
            ? trace.FamilyCurves.SelectMany(c => c.Points).Select(p => (double)p.X)
            : trace.Points.Select(p => (double)p.X);
        var data = xs.Where(double.IsFinite).ToList();
        if (data.Count > 0)
        {
            lo = Math.Max(lo, data.Min());
            hi = Math.Min(hi, data.Max());
        }
        return hi >= lo && double.IsFinite(lo) && double.IsFinite(hi) ? (lo, hi, axis, unit) : (null, null, axis, unit);
    }

    /// <summary>The value at the first of the trace's own markers that lies in the visible X window.</summary>
    private static double? VisibleMarkerValue(Trace trace, Plot plot)
    {
        var win = plot.Axes.Window;
        double lo = Math.Min(win.Left, win.Right), hi = Math.Max(win.Left, win.Right);
        foreach (var m in trace.Markers)
        {
            if (m.FreePosition) continue;
            var p = trace.GetMarkerDataLocation(m);
            if (p.X >= lo && p.X <= hi && double.IsFinite(p.Y)) return p.Y;
        }
        return null;
    }

    private static TraceValueTransform Map(CubeTransform t) => t switch
    {
        CubeTransform.dB20  => TraceValueTransform.dB20,
        CubeTransform.dB10  => TraceValueTransform.dB10,
        CubeTransform.dB    => TraceValueTransform.PowerDb,
        CubeTransform.Mag   => TraceValueTransform.Mag,
        CubeTransform.Phase => TraceValueTransform.Phase,
        CubeTransform.Real  => TraceValueTransform.Real,
        CubeTransform.Imag  => TraceValueTransform.Imag,
        CubeTransform.Conj  => TraceValueTransform.Conj,
        _                   => TraceValueTransform.None,
    };
}
