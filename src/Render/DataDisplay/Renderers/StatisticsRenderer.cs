// ================================================================
//  StatisticsRenderer.cs  —  the statistical pictures' own marks
//  (brief-yield-8): bars and steps, the fitted normal curve over a
//  histogram, and the goals' spec lines.
//
//  It DRAWS and computes nothing about the data. The bars are the
//  trace's points, the fit's mean and σ were taken by the expression
//  engine at resolve time, and a spec line is a goal the result
//  recorded (Trace.Statistics.cs). Every dashed stroke is emitted as
//  SEGMENTS rather than through SKPathEffect.CreateDash, for
//  PlotRenderer.DrawWspMarginReferenceLines' reason: Skia's SVG device
//  drops a path effect on a stroke, and the three exports must agree.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    public static class StatisticsRenderer
    {
        /// <summary>
        /// The canvas rectangle of every bar a bar trace draws — one list per curve (a single trace has one; a family
        /// has one per member). A family's members stand SIDE BY SIDE within each bin: the bin's width is split
        /// evenly, member m taking the m-th part (R-ya8-1). A point whose bar a log axis cannot place is left out.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<SKRect>> BarRects(Trace trace, TransformSet tf, FreqUnit freqUnit)
        {
            double width = trace.DisplayBarWidth(freqUnit);
            bool   sec   = trace.UseSecondaryAxis;
            var curves = trace.IsFamily
                ? trace.FamilyCurves.Select(c => (IReadOnlyList<System.Numerics.Vector2>)c.Points).ToList()
                : [trace.Points];
            int m = Math.Max(1, curves.Count);
            double part = width / m;

            var result = new List<IReadOnlyList<SKRect>>(curves.Count);
            for (int c = 0; c < curves.Count; c++)
            {
                var rects = new List<SKRect>(curves[c].Count);
                foreach (var p in curves[c])
                {
                    double left  = p.X - width / 2 + c * part;
                    double right = left + part;
                    if (!tf.XIsPlottable(left) || !tf.XIsPlottable(right)) continue;
                    var a = tf.ToCanvas(left, 0, sec);
                    var b = tf.ToCanvas(right, p.Y, sec);
                    rects.Add(new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
                }
                result.Add(rects);
            }
            return result;
        }

        /// <summary>Draws a bar or step trace on a Rect plot.</summary>
        public static void DrawBars(SKCanvas canvas, (double W, double H) canvasSize, Trace trace, TransformSet tf,
                                    RenderTheme theme, FreqUnit freqUnit)
        {
            var   props   = trace.Properties;
            float lw      = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            float strokeW = lw * (float)props.LineWidth * 0.5f;
            // A series the Statistics menu ADDED beside a trace (a yield sensitivity's trials per bin) is the light one.
            double opacity = props.LineOpacity * (TraceStatistics.IsAddedSeries(trace) ? 0.5 : 1.0);

            using var outline = new SKPaint
            {
                Color = RenderTheme.ToSKColor(props.LineColor, opacity), StrokeWidth = strokeW,
                Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Miter,
            };

            if (props.DrawStyle == TraceDrawStyle.Bars)
            {
                using var fill = new SKPaint
                {
                    Color = RenderTheme.ToSKColor(props.LineColor, theme.FillOpacity * opacity),
                    Style = SKPaintStyle.Fill, IsAntialias = true,
                };
                // A selected trial (brief-yield-9 R-ya9-6) lights its bar; the rest dim. Bars are indexed as the
                // points are, and only a single histogram carries the trials each one holds.
                bool sel = trace.HasTrialSelection && trace.ElementKind == TrialElements.Bars;
                foreach (var curve in BarRects(trace, tf, freqUnit))
                    for (int k = 0; k < curve.Count; k++)
                    {
                        var r = curve[k];
                        double dim = sel && !trace.ElementSelected(k) ? TrialRenderer.DimOpacity : 1.0;
                        fill.Color    = RenderTheme.ToSKColor(props.LineColor, theme.FillOpacity * opacity * dim * (sel && dim == 1 ? 2 : 1));
                        outline.Color = RenderTheme.ToSKColor(props.LineColor, opacity * dim);
                        canvas.DrawRect(r, fill);
                        if (props.LineEnabled) canvas.DrawRect(r, outline);
                    }
                return;
            }

            // Step. With a bin width (a histogram drawn as an outline) the path is the bars' tops joined at their
            // shared edges; without one (an empirical CDF) it is the post-step through the points themselves.
            outline.StrokeWidth = lw * (float)props.LineWidth;
            bool sec = trace.UseSecondaryAxis;
            var curves = trace.IsFamily ? trace.FamilyCurves.Select(c => c.Points).ToList() : [trace.Points];
            foreach (var pts in curves)
            {
                if (pts.Count == 0) continue;
                var sorted = pts.OrderBy(p => p.X).ToList();
                using var path = new SKPath();
                if (trace.BarWidth is not null)
                {
                    double half = trace.DisplayBarWidth(freqUnit) / 2;
                    path.MoveTo(tf.ToCanvas(sorted[0].X - half, 0, sec));
                    foreach (var p in sorted)
                    {
                        path.LineTo(tf.ToCanvas(p.X - half, p.Y, sec));
                        path.LineTo(tf.ToCanvas(p.X + half, p.Y, sec));
                    }
                    path.LineTo(tf.ToCanvas(sorted[^1].X + half, 0, sec));
                }
                else
                {
                    path.MoveTo(tf.ToCanvas(sorted[0].X, sorted[0].Y, sec));
                    for (int k = 1; k < sorted.Count; k++)
                    {
                        path.LineTo(tf.ToCanvas(sorted[k].X, sorted[k - 1].Y, sec));
                        path.LineTo(tf.ToCanvas(sorted[k].X, sorted[k].Y, sec));
                    }
                }
                canvas.DrawPath(path, outline);
            }
        }

        /// <summary>
        /// The fitted normal curve over a histogram (R-ya8-4): the density of N(μ, σ) scaled to the histogram's
        /// area, so it sits on the bars — y(x) = A·φ((x − μ)/σ)/σ — sampled across the visible X window.
        /// </summary>
        public static void DrawNormalFit(SKCanvas canvas, (double W, double H) canvasSize, Trace trace, Plot plot,
                                         TransformSet tf)
        {
            if (trace.NormalFit is not { } fit || !(fit.Sigma > 0)) return;
            double scale = trace.CubeXDisplayScale(plot.FreqUnits);
            var    win   = plot.Axes.Window;
            float  lw    = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            const int n  = 200;

            using var path = new SKPath();
            bool first = true;
            for (int i = 0; i <= n; i++)
            {
                double xd = win.Left + (win.Right - win.Left) * i / n;
                if (!tf.XIsPlottable(xd)) { first = true; continue; }
                double z = (xd / scale - fit.Mean) / fit.Sigma;
                double y = fit.Area * Math.Exp(-z * z / 2) / (Math.Sqrt(2 * Math.PI) * fit.Sigma);
                var p = tf.ToCanvas(xd, y, trace.UseSecondaryAxis);
                if (first) { path.MoveTo(p); first = false; } else path.LineTo(p);
            }
            using var paint = new SKPaint
            {
                Color = RenderTheme.ToSKColor(trace.Properties.LineColor, 1.0), StrokeWidth = lw * 1.2f,
                Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round,
            };
            canvas.DrawPath(path, paint);
        }

        /// <summary>
        /// Every trace's spec lines (R-ya8-3), dashed in the theme's limit colour and labelled with the goal and its
        /// limit. A line two traces both carry is drawn once.
        /// </summary>
        public static void DrawSpecLines(SKCanvas canvas, (double W, double H) canvasSize, Plot plot, TransformSet tf,
                                         RenderTheme theme)
        {
            var clip  = PlotRenderer.ViewportClipRect(tf.Viewport, canvasSize);
            float lw  = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            var drawn = new HashSet<(string, bool, double, double, double, double, bool)>();

            using var paint = new SKPaint
            {
                Color = theme.LimitColor, StrokeWidth = Math.Max(1f, lw * 0.8f), Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            using var textPaint = new SKPaint { Color = theme.LimitColor, IsAntialias = true };
            using var font      = new SKFont(SkiaFonts.PlexRegular, (float)(plot.Axes.FontSizeTicks * 0.85 * lw));
            using var fallback  = new SKFont(SkiaFonts.DejaVuRegular, font.Size);

            int labelRow = 0;
            foreach (var trace in plot.Traces)
            {
                double scale = trace.CubeXDisplayScale(plot.FreqUnits);
                bool   sec   = trace.UseSecondaryAxis;
                foreach (var line in trace.SpecLines)
                {
                    if (!drawn.Add((line.Label, line.Vertical, line.X0, line.X1, line.Y0, line.Y1, sec))) continue;

                    if (line.Vertical)
                    {
                        double xd = line.X0 * scale;
                        if (!tf.XIsPlottable(xd)) continue;
                        float x = tf.ToCanvas(xd, 0, sec).X;
                        if (x < clip.Left || x > clip.Right) continue;
                        Dashed(canvas, new SKPoint(x, clip.Top), new SKPoint(x, clip.Bottom), paint, lw);
                        float y = clip.Top + font.Size * (1.3f + 1.2f * labelRow++);
                        float tw = RendererText.MeasureTextWithFallback(line.Label, font, fallback);
                        // Right of the line, unless that runs off the plot.
                        float tx = x + lw * 1.5f + tw <= clip.Right ? x + lw * 1.5f : x - lw * 1.5f - tw;
                        RendererText.DrawLeftTextWithFallback(canvas, line.Label, tx, y, font, fallback, textPaint);
                        continue;
                    }

                    var win = plot.Axes.Window;
                    double x0 = double.IsNaN(line.X0) ? win.Left  : line.X0 * scale;
                    double x1 = double.IsNaN(line.X1) ? win.Right : line.X1 * scale;
                    if (!tf.XIsPlottable(x0) || !tf.XIsPlottable(x1)) continue;
                    var a = tf.ToCanvas(x0, line.Y0, sec);
                    var b = tf.ToCanvas(x1, line.Y1, sec);
                    Dashed(canvas, a, b, paint, lw);
                    var right = a.X > b.X ? a : b;
                    float w = RendererText.MeasureTextWithFallback(line.Label, font, fallback);
                    float lx = Math.Max(clip.Left + lw, Math.Min(right.X, clip.Right) - w - lw);
                    RendererText.DrawLeftTextWithFallback(canvas, line.Label, lx, right.Y - lw * 1.5f, font, fallback, textPaint);
                }
            }
        }

        /// <summary>A dashed stroke from <paramref name="a"/> to <paramref name="b"/>, as segments.</summary>
        private static void Dashed(SKCanvas canvas, SKPoint a, SKPoint b, SKPaint paint, float lw)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (!(len > 0)) return;
            float on = lw * 3f, off = lw * 2f;
            float ux = dx / len, uy = dy / len;
            for (float s = 0; s < len; s += on + off)
            {
                float e = Math.Min(s + on, len);
                canvas.DrawLine(a.X + ux * s, a.Y + uy * s, a.X + ux * e, a.Y + uy * e, paint);
            }
        }
    }
}
