// A point on a traced curve, and what the curve reads there — brief-pdg-3-snap-to-curve.md R-pdg3-1.
//
// SNAPPED TO THE CURVE, NOT TO A SAMPLE. A picture point is projected perpendicularly onto every segment of every trace's
// polyline (its samples' pixels — a joined trace's too), the nearest wins, and the value is read at the projected point:
// x through the x calibration, y through the trace's OWN axis (brief-pdg-1). So the readout is continuous along the curve,
// never stepped to pixel columns. A chart trace reads its complex value there, and a frequency interpolated between the
// segment's two samples when the trace has one.
//
// AT AN X, NEVER BEYOND. AtX interpolates a rectangular trace's samples linearly between the two that bracket x — in
// log x on a log axis — and is null outside the trace's range: a probe never extrapolates, as a fit never does.
//
// NO MORE DIGITS THAN THE PICTURE RESOLVES. Every text here prints a value to about one pixel's worth of its axis
// (AxisCalibration.PerPixel at the point), so a readout never claims a precision the picture does not have.
//
// A PLOT DRAWN BY HAND (brief-pdg-4-digitise-by-hand.md R-pdg4-1) is read in its box's own space: a picture point is
// carried into it before it is projected, and the hit's pixel carried back, so a turned box reads like any other.
//
// Pure: no picture, no reader state. The dialog's hover, its Point tool and the CLI's --probe all call it.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>A point on a traced curve.</summary>
/// <param name="Trace">The trace's session number.</param>
/// <param name="Plot">Its plot's session number.</param>
/// <param name="Px">The snapped point, picture pixels.</param>
/// <param name="X">A rectangular trace's x there (hertz on a frequency axis); a chart trace's frequency, or NaN.</param>
/// <param name="Y">A rectangular trace's y there, in its axis's units; a chart trace's magnitude.</param>
/// <param name="Value">A chart trace's complex value there (Γ on a Smith chart); null on rectangular axes.</param>
/// <param name="FrequencyHz">The frequency there: x on a frequency axis, a chart trace's interpolated one; else null.</param>
/// <param name="DistancePx">How far the point asked was from the curve, pixels.</param>
public sealed record TraceProbeHit(int Trace, int Plot, double Px, double Py, double X, double Y, Complex? Value,
                                   double? FrequencyHz, double DistancePx)
{
    public string Name => $"T{Trace}";
}

/// <summary>A rectangular trace read at an x (<see cref="TraceProbe.Across"/>): the hit, its text and readout, and its fit
/// there when one is made.</summary>
public sealed record TraceProbeReading(TraceProbeHit Hit, TraceProbeText Text, string Readout, double? Fit);

/// <summary>A hit as text, to the picture's resolution: <c>2.437</c>, <c>GHz</c>, <c>-12.31</c>, <c>dB</c>. ASCII signs —
/// what a copied table holds.</summary>
public sealed record TraceProbeText(string X, string XUnit, string Y, string YUnit);

public static class TraceProbe
{
    /// <summary>The nearest traced curve to picture point (<paramref name="px"/>, <paramref name="py"/>) on picture
    /// <paramref name="picture"/> (0-based), within <paramref name="maxPx"/>; null when none. Traces of excluded plots,
    /// traces the user ignored and traces with no values (an uncalibrated plot) are passed over. Equal distances go to
    /// <paramref name="preferPlot"/> — the selected trace's plot.</summary>
    public static TraceProbeHit? Nearest(PlotSession s, int picture, double px, double py, double maxPx, int? preferPlot = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        TraceProbeHit? best = null;
        foreach (var t in s.Traces)
        {
            if (t.Plot < 1 || t.Plot > s.Plots.Count) continue;
            var plot = s.Plots[t.Plot - 1];
            if (plot.Picture != picture || plot.Excluded || t.Assignment.Ignored || t.Samples.Count == 0) continue;
            var (bx, by) = plot.FromPicture(px, py);
            var (k, u, d) = Project(t.Samples, bx, by);
            if (d > maxPx) continue;
            bool nearer = best is null || d < best.DistancePx - 1e-9;
            bool tie = best is not null && Math.Abs(d - best.DistancePx) <= 1e-9 && preferPlot == t.Plot && best.Plot != preferPlot;
            if ((nearer || tie) && At(t, plot, k, u, d) is { } hit) best = OnPicture(hit, plot);
        }
        return best;
    }

    /// <summary>Rectangular trace <paramref name="trace"/>'s value at axis x <paramref name="x"/> (hertz on a frequency
    /// axis), interpolated linearly between the samples that bracket it — in log x on a log axis; null outside its range,
    /// for a chart trace, and for a trace with no values.</summary>
    public static TraceProbeHit? AtX(PlotSession s, int trace, double x)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!double.IsFinite(x) || s.Traces.FirstOrDefault(t => t.Number == trace) is not { } t || t.Parametric
            || t.Plot < 1 || t.Plot > s.Plots.Count) return null;
        var plot = s.Plots[t.Plot - 1];
        if (plot.Rect is not { X: { } xa } r || r.AxisOn(t.Axis) is not { } ya) return null;
        bool logX = xa.Scale == AxisScale.Log, logY = ya.Scale == AxisScale.Log;
        if (logX && !(x > 0)) return null;
        var p = t.Samples;
        double slack = 1e-12 * Math.Abs(x);
        for (int i = 0; i < p.Count; i++)
        {
            if (Math.Abs(p[i].X - x) <= slack)
                return OnPicture(new TraceProbeHit(t.Number, t.Plot, p[i].Px, p[i].Py, x, p[i].Y, null, r.XIsFrequency ? x : null, 0), plot);
            if (i + 1 >= p.Count) break;
            double x0 = p[i].X, x1 = p[i + 1].X;
            if (!(Math.Min(x0, x1) <= x && x <= Math.Max(x0, x1)) || x0 == x1) continue;
            double u = logX && x0 > 0 && x1 > 0 ? Math.Log(x / x0) / Math.Log(x1 / x0) : (x - x0) / (x1 - x0);
            double y0 = p[i].Y, y1 = p[i + 1].Y;
            double y = logY && y0 > 0 && y1 > 0 ? y0 * Math.Pow(y1 / y0, u) : y0 + u * (y1 - y0);
            return OnPicture(new TraceProbeHit(t.Number, t.Plot, Lerp(p[i].Px, p[i + 1].Px, u), Lerp(p[i].Py, p[i + 1].Py, u), x, y, null,
                                               r.XIsFrequency ? x : null, 0), plot);
        }
        return null;
    }

    /// <summary>
    /// Every rectangular trace whose range covers <paramref name="x"/>, read there through <see cref="AtX"/> — the CLI's
    /// <c>--probe</c>. The pieces of one curve are joined first (TraceJoin), as the Touchstone joins them, so a curve
    /// answers once; excluded plots and ignored traces are passed over. <paramref name="fits"/> is keyed by trace name
    /// (<see cref="TraceFit.Session"/>).
    /// </summary>
    public static IReadOnlyList<TraceProbeReading> Across(PlotSession s, double x, IReadOnlyDictionary<string, TraceFitResult>? fits = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var (joined, _) = TraceJoin.Session(s);
        var result = new List<TraceProbeReading>();
        foreach (var t in joined.Traces)
        {
            if (t.Parametric || t.Assignment.Ignored || joined.Plots[t.Plot - 1].Excluded || AtX(joined, t.Number, x) is not { } hit) continue;
            var fit = fits?.GetValueOrDefault(t.Name);
            double? y = fit is { Fitted: true } && TraceFit.EvaluateNear(fit, hit.X, hit.Y) is var v && double.IsFinite(v) ? v : null;
            result.Add(new TraceProbeReading(hit, Columns(joined, hit), Readout(joined, hit, fit), y));
        }
        return result;
    }

    /// <summary>A hit's pixel carried from its plot's own space back onto the picture.</summary>
    private static TraceProbeHit OnPicture(TraceProbeHit h, PlotRead plot)
    {
        if (plot.Manual is not { Axial: false }) return h;
        var (x, y) = plot.ToPicture(h.Px, h.Py);
        return h with { Px = x, Py = y };
    }

    /// <summary>An x as the axis rows take it: a plain number in the axis's own units (hertz on a frequency axis), or a
    /// frequency with its unit (<c>2.4 GHz</c>); null otherwise.</summary>
    public static double? ParseX(string? text)
    {
        string t = (text ?? "").Trim().Replace(" ", "", StringComparison.Ordinal).Replace('−', '-');
        if (t.Length == 0) return null;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return double.IsFinite(d) ? d : null;
        return RecognitionSweep.Parse(t)?.Hz;
    }

    // ── text ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hit's x and y, each to about one pixel's worth of its axis, in display units; a chart trace's y is
    /// <c>0.41∠-63°</c> and its unit <c>Γ</c> (or the entry, on a polar chart), its x the frequency when it has one.</summary>
    public static TraceProbeText Columns(PlotSession s, TraceProbeHit h)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(h);
        var plot = s.Plots[h.Plot - 1];
        var t = s.Traces.FirstOrDefault(t => t.Number == h.Trace);
        if (h.Value is { } v)
        {
            double perMag = plot.Smith?.PerPixel ?? plot.Polar?.PerPixel ?? t?.Resolution ?? 0.01;
            double perDeg = v.Magnitude > perMag ? perMag / v.Magnitude * 180 / Math.PI : 90;
            string y = $"{Fixed(v.Magnitude, perMag, false)}∠{Fixed(v.Phase * 180 / Math.PI, perDeg, false)}°";
            string unit = plot.Type == PlotType.Smith ? "Γ" : t is { Assignment.Assigned: true } a ? a.Assignment.Entry : "S";
            if (h.FrequencyHz is not { } f) return new TraceProbeText("", "", y, unit);
            double scale = FrequencyScale(f);
            return new TraceProbeText(Fixed(f / scale, FrequencyPerPixel(t, h) / scale, false), FrequencyUnit(scale), y, unit);
        }
        var r = plot.Rect!;
        double xScale = r.XIsFrequency ? r.XUnit : 1;
        double perX = r.X is { } xa ? xa.PerPixel(h.Px) : 0;
        double perY = r.AxisOn(t?.Axis ?? r.DefaultSide) is { } ya ? ya.PerPixel(h.Py) : t?.Resolution ?? 0;
        return new TraceProbeText(Fixed(h.X / xScale, perX / xScale, false), r.XIsFrequency ? FrequencyUnit(r.XUnit) : "",
                                  Fixed(h.Y, perY, false), t?.ResolutionUnit ?? "");
    }

    /// <summary>The readout beside the crosshair and in the Points list: <c>T3 · 2.437 GHz · −12.31 dB</c>, a chart trace's
    /// <c>T3 · Γ 0.41 ∠ −63° · 2.40 GHz</c> (or <c>· no frequency</c>), with <c>· fit −12.29 dB</c> after it when
    /// <paramref name="fit"/> is a fit made.</summary>
    public static string Readout(PlotSession s, TraceProbeHit h, TraceFitResult? fit = null)
    {
        var c = Columns(s, h);
        if (h.Value is not null)
        {
            int angle = c.Y.IndexOf('∠');
            string chart = $"{h.Name} · {c.YUnit} {Minus(c.Y[..angle])} ∠ {Minus(c.Y[(angle + 1)..])}";
            return chart + (c.X.Length > 0 ? $" · {Minus(c.X)} {c.XUnit}" : " · no frequency");
        }
        string text = $"{h.Name} · {Minus(c.X)}{TraceFit.UnitAfter(c.XUnit)} · {Minus(c.Y)}{TraceFit.UnitAfter(c.YUnit)}";
        if (fit is { Fitted: true } && TraceFit.EvaluateNear(fit, h.X, h.Y) is var y && double.IsFinite(y))
        {
            var plot = s.Plots[h.Plot - 1];
            var t = s.Traces.FirstOrDefault(t => t.Number == h.Trace);
            double perY = plot.Rect is { } r && r.AxisOn(t?.Axis ?? r.DefaultSide) is { } ya ? ya.PerPixel(h.Py) : t?.Resolution ?? 0;
            text += $" · fit {Fixed(y, perY)}{TraceFit.UnitAfter(c.YUnit)}";
        }
        return text;
    }

    /// <summary><paramref name="v"/> to the decimals one pixel's worth <paramref name="perPixel"/> resolves — the finest
    /// decimal step no smaller than a pixel. An unknown resolution prints four significant figures.</summary>
    public static string Fixed(double v, double perPixel, bool unicodeMinus = true)
    {
        if (!double.IsFinite(v)) return "?";
        string text;
        if (!(perPixel > 0) || !double.IsFinite(perPixel)) text = v.ToString("G4", CultureInfo.InvariantCulture);
        else
        {
            // The last digit printed steps by no less than a pixel: 0.04 dB a pixel prints tenths, never hundredths.
            int digits = (int)Math.Clamp(Math.Floor(-Math.Log10(perPixel) + 1e-6), 0, 12);
            double rounded = Math.Round(v, digits);
            text = (rounded == 0 ? 0 : rounded).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        return unicodeMinus ? Minus(text) : text;
    }

    // ── the projection ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The nearest point of the samples' polyline: segment index, position along it (0…1) and distance.</summary>
    private static (int Segment, double U, double Distance) Project(IReadOnlyList<PlotSample> p, double x, double y)
    {
        if (p.Count == 1) return (0, 0, Math.Sqrt((p[0].Px - x) * (p[0].Px - x) + (p[0].Py - y) * (p[0].Py - y)));
        int best = 0;
        double bu = 0, bd = double.MaxValue;
        for (int i = 0; i + 1 < p.Count; i++)
        {
            double ax = p[i].Px, ay = p[i].Py, dx = p[i + 1].Px - ax, dy = p[i + 1].Py - ay;
            double len2 = dx * dx + dy * dy;
            double u = len2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1) : 0;
            double ex = ax + u * dx - x, ey = ay + u * dy - y;
            double d = ex * ex + ey * ey;
            if (d < bd) (best, bu, bd) = (i, u, d);
        }
        return (best, bu, Math.Sqrt(bd));
    }

    /// <summary>Trace <paramref name="t"/> read at position <paramref name="u"/> of segment <paramref name="k"/>; null when
    /// its plot has no calibration for it.</summary>
    private static TraceProbeHit? At(PlotTraceRead t, PlotRead plot, int k, double u, double d)
    {
        var p = t.Samples;
        var a = p[k];
        var b = k + 1 < p.Count ? p[k + 1] : a;
        double qx = Lerp(a.Px, b.Px, u), qy = Lerp(a.Py, b.Py, u);
        switch (plot.Type)
        {
            case PlotType.Rectangular when plot.Rect is { X: { } xa } r && r.AxisOn(t.Axis) is { } ya:
            {
                double x = xa.ValueAt(qx);
                return new TraceProbeHit(t.Number, t.Plot, qx, qy, x, ya.ValueAt(qy), null, r.XIsFrequency ? x : null, d);
            }
            case PlotType.Smith when plot.Smith is { } sc:
                return Chart(sc.GammaAt(qx, qy));
            case PlotType.Polar when plot.Polar is { } pc:
                return Chart(pc.ValueAt(qx, qy));
            default:
                return null;
        }

        TraceProbeHit Chart(Complex v)
        {
            double? f = double.IsFinite(a.X) && double.IsFinite(b.X) ? Lerp(a.X, b.X, u) : null;
            return new TraceProbeHit(t.Number, t.Plot, qx, qy, f ?? double.NaN, v.Magnitude, v, f, d);
        }
    }

    /// <summary>The frequency one pixel spans along a chart trace where <paramref name="h"/> is: its nearest segment's.</summary>
    private static double FrequencyPerPixel(PlotTraceRead? t, TraceProbeHit h)
    {
        if (t is null || t.Samples.Count < 2) return 0;
        var (k, _, _) = Project(t.Samples, h.Px, h.Py);
        var a = t.Samples[k];
        var b = t.Samples[Math.Min(k + 1, t.Samples.Count - 1)];
        double len = Math.Sqrt((b.Px - a.Px) * (b.Px - a.Px) + (b.Py - a.Py) * (b.Py - a.Py));
        return len > 0 && double.IsFinite(a.X) && double.IsFinite(b.X) ? Math.Abs(b.X - a.X) / len : 0;
    }

    private static double Lerp(double a, double b, double u) => a + u * (b - a);

    private static string Minus(string s) => s.Replace('-', '−');

    private static double FrequencyScale(double hz) => Math.Abs(hz) switch
    {
        >= 1e12 => 1e12,
        >= 1e9 => 1e9,
        >= 1e6 => 1e6,
        >= 1e3 => 1e3,
        _ => 1,
    };

    private static string FrequencyUnit(double multiplier) => multiplier switch
    {
        >= 1e12 => "THz",
        >= 1e9 => "GHz",
        >= 1e6 => "MHz",
        >= 1e3 => "kHz",
        _ => "Hz",
    };
}
