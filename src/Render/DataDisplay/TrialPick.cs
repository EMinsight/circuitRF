// ================================================================
//  TrialPick.cs  —  which trial(s) a click on a plot means, and what a
//  trial's values are (brief-yield-9 R-ya9-6)
//
//  A family member, a scatter point or a histogram bar is a trial (a bar
//  is every trial in its bin): TrialResolve recorded which, per element,
//  in Trace.ElementTrials. This file answers the click against the same
//  canvas geometry the renderers draw, and reads one trial's drawn values
//  out of the result for Send to Tuning and Copy values. The SELECTION
//  itself — shared by every display bound to a source — is the window's.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Design.Optimization;
using RfCore.Data;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

/// <summary>A click that hit trials: the trace it hit and every trial it means.</summary>
public sealed record TrialHit(Trace Trace, IReadOnlyList<int> Trials);

public static class TrialPick
{
    /// <summary>How far from a curve or point, in canvas pixels, a click still hits it.</summary>
    public const float Radius = 6f;

    /// <summary>
    /// The trials under canvas point <paramref name="at"/> on <paramref name="plot"/>: the bar containing it, else
    /// the nearest scatter point or family member within <paramref name="radius"/>. Null when it hits none.
    /// </summary>
    public static TrialHit? At(Plot plot, TransformSet tf, SKPoint at, float radius = Radius)
    {
        TrialHit? best = null;
        double bestD = radius;
        foreach (var t in Enumerable.Reverse(plot.Traces))
        {
            if (t.ElementTrials is not { } el) continue;
            switch (t.ElementKind)
            {
                case TrialElements.Bars:
                {
                    var rects = StatisticsRenderer.BarRects(t, tf, plot.FreqUnits);
                    if (rects.Count == 1 && rects[0].Count == el.Count)
                        for (int k = 0; k < el.Count; k++)
                            if (rects[0][k].Contains(at) && el[k].Length > 0) return new TrialHit(t, el[k]);
                    break;
                }
                case TrialElements.Points:
                    for (int p = 0; p < t.Points.Count && p < el.Count; p++)
                    {
                        if (el[p].Length == 0 || NotDrawn(t, p) || !tf.XIsPlottable(t.Points[p].X)) continue;
                        var c = tf.ToCanvas(t.Points[p].X, t.Points[p].Y, t.UseSecondaryAxis);
                        double d = Math.Sqrt((c.X - at.X) * (c.X - at.X) + (c.Y - at.Y) * (c.Y - at.Y));
                        if (d <= bestD) { bestD = d; best = new TrialHit(t, el[p]); }
                    }
                    break;
                case TrialElements.Curves:
                    if (!t.ShowCurves) break;
                    for (int k = 0; k < t.FamilyCurves.Count && k < el.Count; k++)
                    {
                        if (NotDrawn(t, k)) continue;
                        double d = DistanceToPolyline(t.FamilyCurves[k].Points, tf, t.UseSecondaryAxis, at);
                        if (d <= bestD) { bestD = d; best = new TrialHit(t, el[k]); }
                    }
                    break;
            }
        }
        return best;
    }

    /// <summary>The status chip's text: <c>Trial 417</c>, or <c>23 trials</c>.</summary>
    public static string ChipText(IReadOnlyCollection<int> trials)
        => trials.Count == 1 ? $"Trial {trials.First()}" : $"{trials.Count} trials";

    /// <summary>
    /// Trial <paramref name="trial"/>'s drawn value of every statistical entry in <paramref name="ds"/>, keyed as the
    /// tuning entries are and written with the unit the result stores (<c>R1.R → 1012.4 Ohm</c>) — what Send trial to
    /// Tuning loads and Copy values copies. Empty when the result holds no such trial.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ValuesOf(DataSet ds, int trial)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!ds.ContainsGroup("trials")) return values;
        foreach (var (name, cube) in ds.CubesIn("trials"))
        {
            if (!name.StartsWith("stat:", StringComparison.Ordinal) || cube.Rank != 1) continue;
            int i = Array.IndexOf(cube.Axes[0].Values, (double)trial);
            if (i < 0 || !double.IsFinite(cube.RealValues[i])) continue;
            values[name[5..]] = TunableValue.Text(cube.RealValues[i].ToString("R", CultureInfo.InvariantCulture), cube.Unit);
        }
        return values;
    }

    private static bool NotDrawn(Trace t, int k)
        => t.MemberCategories is { } c && k < c.Length && c[k] == TrialCategory.NotEvaluated;

    private static double DistanceToPolyline(IReadOnlyList<System.Numerics.Vector2> pts, TransformSet tf, bool sec, SKPoint at)
    {
        double best = double.PositiveInfinity;
        SKPoint? prev = null;
        foreach (var pt in pts)
        {
            if (!float.IsFinite(pt.Y) || !tf.XIsPlottable(pt.X)) { prev = null; continue; }
            var c = tf.ToCanvas(pt.X, pt.Y, sec);
            best = Math.Min(best, prev is { } a ? Segment(a, c, at) : Segment(c, c, at));
            prev = c;
        }
        return best;
    }

    private static double Segment(SKPoint a, SKPoint b, SKPoint p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        double u = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
        double x = a.X + u * dx - p.X, y = a.Y + u * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }
}
