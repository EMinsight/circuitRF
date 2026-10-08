// ================================================================
//  Trace.Statistics.cs  —  what a trace carries for the statistical
//  pictures (brief-yield-8): what the Statistics menu rewrote it FROM,
//  the normal-fit toggle, and the resolve-time numbers the renderer
//  draws — the bar width, the fit, the goals' spec lines.
//
//  None of the numbers is computed here. The bar width is the histogram
//  function's own `width` companion, the fit's mean and σ are
//  `mean_over`/`std_over` of the histogram's operand, and a spec line
//  is a goal the result recorded — all filled in by TraceResolve, the
//  one resolve the window and the CLI share.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore;

namespace CircuitRF.Render.DataDisplay
{
    /// <summary>
    /// A trace as it was before the Statistics menu rewrote it — every field that decides what it reads and how it
    /// draws, so "Back to curves" restores it exactly (brief-yield-8 R-ya8-2).
    /// </summary>
    public sealed record TraceStatisticsOrigin(
        string?        Expression,
        string?        CubeName,
        AxisSlice[]?   Slice,
        CubeTransform  Transform,
        TraceDrawStyle DrawStyle);

    /// <summary>The fitted normal curve over a histogram (R-ya8-4): the operand's mean and σ, and the histogram's
    /// area (Σ height × bin width) the density is scaled to, all in the operand's base unit.</summary>
    public sealed record NormalFitCurve(double Mean, double Sigma, double Area);

    /// <summary>
    /// One limit of one goal, as a line on a plot (R-ya8-3). <see cref="Vertical"/>: a line at X = <see cref="X0"/>
    /// across the whole window — a limit drawn on a histogram, CDF or normal plot of the goal's value. Otherwise a
    /// line from (<see cref="X0"/>, <see cref="Y0"/>) to (<see cref="X1"/>, <see cref="Y1"/>) — the limit across the
    /// goal's range, sloped where the goal slopes; an X that is NaN runs to the window's edge (a goal with no range).
    /// X is in the trace's X axis's BASE unit, which the renderer scales exactly as the trace's own points.
    /// </summary>
    public sealed record SpecLine(string Goal, string Label, bool Vertical, double X0, double X1, double Y0, double Y1);

    public partial class Trace
    {
        /// <summary>What the Statistics menu rewrote this trace from; null when it is not a statistics view.
        /// Persisted, so "Back to curves" works on a display that was saved and reopened.</summary>
        public TraceStatisticsOrigin? StatisticsOrigin { get; set; }

        /// <summary>Draw a fitted normal curve over this histogram (R-ya8-4). Off by default.</summary>
        public bool ShowNormalFit { get; set; }

        /// <summary>The bar width the expression's own <c>width</c> companion gave, in the X axis's base unit;
        /// null when it gave none (the renderer then takes the X spacing). Set by the resolve.</summary>
        public double? BarWidth { get; internal set; }

        /// <summary>The fitted normal curve, when <see cref="ShowNormalFit"/> is on and this trace is a histogram
        /// whose operand has a spread. Set by the resolve.</summary>
        public NormalFitCurve? NormalFit { get; internal set; }

        /// <summary>The limits of the goals this trace draws (R-ya8-3), resolved from the goals its source records.
        /// Empty for a trace that draws no goal's quantity.</summary>
        public IReadOnlyList<SpecLine> SpecLines { get; internal set; } = Array.Empty<SpecLine>();

        /// <summary>
        /// A live histogram's bin range, held after the first frame with at least
        /// <see cref="TraceStatistics.HoldAfterTrials"/> trials so the bars grow rather than jump (R-ya8-7); null
        /// when nothing is held. Cleared — and so re-derived — once the source is no longer live.
        /// </summary>
        public (double Lo, double Hi)? HeldBinRange { get; set; }

        /// <summary>True when this trace draws bars or steps rather than a line (R-ya8-1).</summary>
        public bool DrawsBars => Properties.DrawStyle != TraceDrawStyle.Line;

        /// <summary>
        /// The width of one bar on the plot, in the X axis's display unit: the <see cref="BarWidth"/> companion when
        /// the expression gave one, otherwise the smallest spacing between the points' X values (a histogram's bins
        /// are equal, so the two agree), otherwise one display unit for a lone point.
        /// </summary>
        public double DisplayBarWidth(FreqUnit freqUnit)
        {
            if (BarWidth is double w && w > 0 && double.IsFinite(w)) return w * CubeXDisplayScale(freqUnit);
            var pts = IsFamily && FamilyCurves.Count > 0 ? FamilyCurves[0].Points : Points;
            var xs = pts.Select(p => (double)p.X).Distinct().OrderBy(x => x).ToArray();
            double gap = double.PositiveInfinity;
            for (int i = 1; i < xs.Length; i++) gap = Math.Min(gap, xs[i] - xs[i - 1]);
            return double.IsFinite(gap) && gap > 0 ? gap : 1.0;
        }

        /// <summary>A bar trace's extent for autoscale: its points' box widened by half a bar each side and taken
        /// down (or up) to zero, so every bar stands on the axis it is measured from (R-ya8-1).</summary>
        internal PlotRect BarBounds(PlotRect box, FreqUnit freqUnit)
        {
            if (box.Width <= 0 && box.Height <= 0 && Points.Count == 0 && FamilyCurves.Count == 0) return box;
            double half = DisplayBarWidth(freqUnit) / 2;
            double lo = Math.Min(box.Top, 0), hi = Math.Max(box.Bottom, 0);
            return new PlotRect(box.Left - half, lo, box.Width + 2 * half, hi - lo);
        }

        /// <summary>The factor this trace's X values are multiplied by on the way to the plot — a frequency axis
        /// in the plot's unit, anything else as stored. What a spec line and a normal fit are scaled by.</summary>
        internal double CubeXDisplayScale(FreqUnit freqUnit) => IsFreqUnit(_cubeXUnit) ? freqUnit.Scale() : 1.0;
    }
}
