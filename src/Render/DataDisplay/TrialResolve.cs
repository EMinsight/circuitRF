// ================================================================
//  TrialResolve.cs  —  the trial views' numbers, filled in at resolve
//  time (brief-yield-9)
//
//  Called by TraceResolve.SetCubeDataFrom — the one resolve the window
//  and the CLI share — after the curves themselves. Nothing here is a
//  new statistic: a member's category is its colour-by cube's value, the
//  nominal is the trace re-read from the result's `nominal` group, an
//  envelope is YA-4's `pctl_over`/`median_over`/`mean_over`/`std_over`
//  over the family axis evaluated by the expression engine, and a
//  scatter's fit is Engine.Statistics.Regression — one implementation
//  each.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Statistics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class TrialResolve
{
    /// <summary>The group a Monte Carlo result keeps the nominal's cubes in, each named by its own address.</summary>
    public const string NominalGroup = "nominal";

    /// <summary>Why an envelope is not offered on a Smith or Polar plot (R-ya9-3) — the menu item's tooltip.</summary>
    public const string ComplexPlaneRefusal =
        "An envelope is a band between two values at each X. On a Smith or Polar plot each point is a complex value, " +
        "and a pointwise band of complex values is not a region.";

    /// <summary>Fills every resolved trial field of <paramref name="t"/> from <paramref name="ds"/>. Never throws: a
    /// trial view is an addition to the curves, and failing to draw one must not cost them.</summary>
    public static void Apply(Trace t, DataSet? ds, PlotType plotType, FreqUnit freqUnit)
    {
        t.MemberCategories = null;
        t.MemberSlots      = null;
        t.Counts           = null;
        t.NominalPoints    = Array.Empty<Vector2>();
        t.Band             = null;
        t.EnvelopeRefusal  = null;
        t.Fit              = null;
        t.ElementTrials    = null;
        t.ElementKind      = TrialElements.None;
        if (ds is null || t.ExpressionError is not null) return;
        try
        {
            Elements(t, ds, freqUnit);
            ColourBy(t, ds);
            Nominal(t, ds, plotType, freqUnit);
            Envelope(t, ds, plotType, freqUnit);
            FitLine(t, plotType);
        }
        catch (Exception ex)
        {
            DataDisplayDiagnostics.Note($"trial view skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── which trial each element is ──────────────────────────────────────────────

    private static void Elements(Trace t, DataSet ds, FreqUnit freqUnit)
    {
        if (t.IsFamily)
        {
            if (t.FamilyAxisName != Evaluator.TrialAxis || t.FamilyCurves.Count == 0) return;
            t.ElementTrials = [.. t.FamilyCurves.Select(c => new[] { (int)Math.Round(c.AxisValue) })];
            t.ElementKind   = TrialElements.Curves;
            return;
        }

        // A histogram's bar holds every trial whose value lies in its bin.
        if (t.DrawsBars && t.Expression is { } e && TraceStatistics.ValueOperand(e) is { } op
            && e.TrimStart().StartsWith("histogram", StringComparison.Ordinal) && t.BarWidth is double w && w > 0)
        {
            if (!TraceExpression.TryEvaluateValue(op, ds, out var v, out _) || v.Kind != ValueKind.Cube) return;
            var cube = v.AsCube();
            if (cube.Rank != 1 || cube.DataKind != DataKind.Real || cube.Axes[0].Name != Evaluator.TrialAxis) return;
            double scale = t.CubeXDisplayScale(freqUnit);
            var values = cube.RealValues;
            var trials = cube.Axes[0].Values;
            var bars = new int[t.Points.Count][];
            for (int p = 0; p < t.Points.Count; p++)
            {
                double c = t.Points[p].X / scale, half = w / 2, slack = 1e-9 * Math.Max(Math.Abs(c), w);
                bars[p] = [.. Enumerable.Range(0, values.Length)
                    .Where(i => double.IsFinite(values[i]) && values[i] >= c - half - slack && values[i] <= c + half + slack)
                    .Select(i => (int)Math.Round(trials[i]))];
            }
            // A value on a shared edge belongs to the bar on its right, as the histogram counts it.
            for (int p = 0; p + 1 < bars.Length; p++)
                bars[p] = [.. bars[p].Except(bars[p + 1])];
            t.ElementTrials = bars;
            t.ElementKind   = TrialElements.Bars;
            return;
        }

        if (OwnSampleAxis(t) is not { Name: Evaluator.TrialAxis } axis) return;
        var points = new int[t.Points.Count][];
        for (int p = 0; p < points.Length; p++)
        {
            int s = t.SampleIndexOf(p);
            points[p] = s >= 0 && s < axis.Values.Length ? [(int)Math.Round(axis.Values[s])] : [];
        }
        t.ElementTrials = points;
        t.ElementKind   = TrialElements.Points;
    }

    /// <summary>The axis a single-curve trace's samples are on: the one a "versus" X replaced, or its own X.</summary>
    private static Axis? OwnSampleAxis(Trace t)
        => t.SampleAxis ?? (t.CubeXValues is { } xs && !string.IsNullOrEmpty(t.CubeXAxisName)
                               ? new Axis(t.CubeXAxisName, [.. xs]) : null);

    // ── colour by (R-ya9-1) ──────────────────────────────────────────────────────

    private static void ColourBy(Trace t, DataSet ds)
    {
        if (t.ColorBy is not { Length: > 0 } by) return;
        string? memberAxis = t.IsFamily ? t.FamilyAxisName : OwnSampleAxis(t)?.Name;
        if (memberAxis is null) return;
        int members = t.IsFamily ? t.FamilyCurves.Count : t.Points.Count;

        if (by == Trace.ColorByCorner)
        {
            if (t.IsFamily && memberAxis == "corner") t.MemberSlots = [.. Enumerable.Range(0, members)];
            return;
        }

        string? spec = by == Trace.ColorByPass ? TraceStatistics.PassSpec(ds) : by;
        if (spec is null || !ds.Contains(spec)) return;
        var cube = ds[spec];
        if (cube.Rank != 1 || cube.DataKind != DataKind.Real || cube.Axes[0].Name != memberAxis) return;
        var values = cube.RealValues;

        // A trial that did not evaluate is drawn not at all, whatever its pass reads (a fail under nonconverged=fail).
        string statusSpec = spec.Contains('.') ? spec[..spec.LastIndexOf('.')] + ".status" : "status";
        double[]? status = ds.Contains(statusSpec) && ds[statusSpec] is { Rank: 1 } sc && sc.Axes[0].Length == values.Length
            ? sc.RealValues : null;

        TrialCategory Of(int i) =>
            i < 0 || i >= values.Length              ? TrialCategory.NotEvaluated
            : status is not null && status[i] != 0   ? TrialCategory.NotEvaluated
            : double.IsNaN(values[i])                ? TrialCategory.NotEvaluated
            : values[i] == 1                         ? TrialCategory.Pass
            : values[i] == 0                         ? TrialCategory.Fail
            :                                          TrialCategory.Other;

        t.MemberCategories = t.IsFamily
            ? [.. Enumerable.Range(0, members).Select(Of)]
            : [.. Enumerable.Range(0, members).Select(p => Of(t.SampleIndexOf(p)))];

        var all = Enumerable.Range(0, values.Length).Select(Of).ToList();
        if (!all.Contains(TrialCategory.Other))
            t.Counts = new TrialCounts(all.Count(c => c == TrialCategory.Pass), all.Count(c => c == TrialCategory.Fail),
                                       all.Count(c => c == TrialCategory.NotEvaluated));
    }

    // ── the nominal (R-ya9-2) ────────────────────────────────────────────────────

    private static void Nominal(Trace t, DataSet ds, PlotType plotType, FreqUnit freqUnit)
    {
        if (!t.ShowNominal || !t.IsFamily || t.IsVersus || t.FamilyAxisName != Evaluator.TrialAxis
            || t.CubeName is null || t.Slice is null) return;
        if (NominalOf(t.CubeName, ds) is not { } name) return;

        // The trace itself, read from the nominal's cube — the same slice with the trial axis gone.
        var scratch = new Trace(t, includeMarkers: false)
        {
            ColorBy = null, ShowNominal = false, Envelope = TrialEnvelope.Off, ShowFitLine = false,
            CubeName = name,
            Slice    = [.. t.Slice.Where(s => s.AxisName != Evaluator.TrialAxis)],
        };
        scratch.Expression = scratch.BuildPickerExpression();
        TraceResolve.SetCubeDataFrom(scratch, ds, plotType, freqUnit);
        if (scratch.ExpressionError is null && !scratch.IsFamily) t.NominalPoints = [.. scratch.Points];
    }

    /// <summary>
    /// The nominal's cube for the family cube <paramref name="cubeName"/>: <c>nominal.&lt;address&gt;</c>. A measurement
    /// is spelled bare on a trace (<c>S21dB</c>) but stored by its group (<c>nominal.measurements.S21dB</c>), so a bare
    /// name that names a measurement is qualified first (brief-yield-16 R-ya16-9); a cube that really is in the default
    /// group keeps its bare address. Null when the result kept no nominal of it.
    /// </summary>
    public static string? NominalOf(string cubeName, DataSet ds)
    {
        if (!cubeName.Contains('.') && ds.ContainsGroup(DataSet.MeasurementsGroup)
            && ds.CubesIn(DataSet.MeasurementsGroup).ContainsKey(cubeName)
            && ds.Contains($"{NominalGroup}.{DataSet.MeasurementsGroup}.{cubeName}"))
            return $"{NominalGroup}.{DataSet.MeasurementsGroup}.{cubeName}";
        string name = $"{NominalGroup}.{cubeName}";
        return ds.Contains(name) ? name : null;
    }

    // ── the envelope (R-ya9-3) ───────────────────────────────────────────────────

    /// <summary>
    /// The three expressions an envelope draws — its lower edge, upper edge and centre line — of
    /// <paramref name="operand"/> over <paramref name="axis"/>: percentiles 0/100 and the median for min–max (so a
    /// trial that did not evaluate, a NaN, is skipped as every statistic skips it), percentiles p/100−p and the
    /// median for a percentile band, the mean ± kσ and the mean for a σ band.
    /// </summary>
    public static (string Lo, string Hi, string Centre) EnvelopeExpressions(TrialEnvelope envelope, string operand, string axis)
    {
        string a = $"\"{axis}\"";
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return envelope.Kind switch
        {
            EnvelopeKind.MinMax     => ($"pctl_over({operand}, 0, {a})", $"pctl_over({operand}, 100, {a})", $"median_over({operand}, {a})"),
            EnvelopeKind.Percentile => ($"pctl_over({operand}, {R(envelope.Parameter)}, {a})",
                                        $"pctl_over({operand}, {R(100 - envelope.Parameter)}, {a})", $"median_over({operand}, {a})"),
            _                       => ($"mean_over({operand}, {a}) - {R(envelope.Parameter)}*std_over({operand}, {a})",
                                        $"mean_over({operand}, {a}) + {R(envelope.Parameter)}*std_over({operand}, {a})",
                                        $"mean_over({operand}, {a})"),
        };
    }

    /// <summary>What a family reads, as a value over its family axis AND its X axis — the operand of its envelope —
    /// with the trace's transform applied. Null for a trace that is not a picker-authored family.</summary>
    public static string? FamilyOperand(Trace t)
    {
        if (!t.IsFamily || t.CubeName is null || t.Slice is null) return null;
        var both = t.Slice.Select(s => s.Role == AxisRole.FamilyIterate ? new AxisSlice(s.AxisName, AxisRole.KeepAsX, 0) : s).ToArray();
        var scratch = new Trace(t, includeMarkers: false) { Slice = both };
        if (scratch.PickerBody(forExpression: true) is not { } body) return null;
        return t.Transform == CubeTransform.None ? body : $"{Trace.TransformFunctionName(t.Transform)}({body})";
    }

    private static void Envelope(Trace t, DataSet ds, PlotType plotType, FreqUnit freqUnit)
    {
        if (!t.Envelope.IsOn || !t.IsFamily) return;
        if (plotType != PlotType.Rect) { t.EnvelopeRefusal = ComplexPlaneRefusal; return; }
        if (FamilyOperand(t) is not { } op || t.FamilyAxisName is not { } axis) return;

        var (lo, hi, centre) = EnvelopeExpressions(t.Envelope, op, axis);
        double[]? Eval(string expr, out double[]? x)
        {
            x = null;
            if (!TraceExpression.TryEvaluateValue(expr, ds, out var v, out _) || v.Kind != ValueKind.Cube) return null;
            var c = v.AsCube();
            if (c.Rank != 1 || c.DataKind != DataKind.Real) return null;
            x = c.Axes[0].Values;
            return c.RealValues;
        }
        if (Eval(lo, out var xs) is not { } l || Eval(hi, out _) is not { } h || Eval(centre, out _) is not { } m || xs is null) return;
        double scale = t.CubeXDisplayScale(freqUnit);
        t.Band = new EnvelopeBand([.. xs.Select(x => x * scale)], l, h, m);
    }

    // ── a scatter's fit (R-ya9-4) ────────────────────────────────────────────────

    private static void FitLine(Trace t, PlotType plotType)
    {
        if (!t.ShowFitLine || t.IsFamily || plotType != PlotType.Rect) return;
        var pts = t.Points.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToList();
        if (pts.Count < 3) return;
        var x = pts.Select(p => new[] { (double)p.X }).ToList();
        var y = pts.Select(p => (double)p.Y).ToList();
        double sx = SampleStatistics.StdDev([.. x.Select(r => r[0])]), sy = SampleStatistics.StdDev([.. y]);
        if (!(sx > 0)) return;
        var fit = Regression.Fit(x, y);
        // The standardized slope back in the plot's own units.
        double slope = fit.Coefficients[0] * sy / sx;
        double intercept = y.Average() - slope * x.Average(r => r[0]);
        t.Fit = new ScatterFit(slope, intercept, fit.RSquared);
    }
}
