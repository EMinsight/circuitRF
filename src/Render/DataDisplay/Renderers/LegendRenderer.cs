// ================================================================
//  LegendRenderer.cs  —  a plot's legend (brief-dfr26-6 R-dfr26-6-2)
//
//  One entry per trace: a short sample of its line, in its colour and
//  line style, beside the name its Y axis gives it. Two placements:
//  INSIDE, a box in the plot box's top-right corner, and BELOW, a band
//  under the plot laid out as a grid of equal cells.
//
//  The band's HEIGHT depends only on the trace count and the canvas,
//  never on the label text. It decides where the plot box ends, and
//  the box is also computed by every hit test through
//  PlotRenderer.BuildTransforms — which has no alias resolver and no
//  source-prefix setting to measure the text with. Text that does not
//  fit its cell is cut short with an ellipsis instead.
//
//  A dashed or dotted sample is emitted as SEGMENTS, not through
//  SKPathEffect, for StatisticsRenderer's reason: Skia's SVG device
//  drops a path effect on a stroke, and the three exports must agree.
// ================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    public static class LegendRenderer
    {
        /// <summary>The traces a legend lists — every one that names an axis: not a contour, and not a
        /// curve its plot's host derived (<see cref="Trace.ExcludeFromAxisLabels"/>).</summary>
        public static IEnumerable<Trace> Listed(Plot plot)
        {
            foreach (var t in plot.Traces)
                if (!t.IsContourTrace && !t.ExcludeFromAxisLabels) yield return t;
        }

        /// <summary>Whether <paramref name="plot"/> draws a legend at all: it asks for one, it is a kind
        /// that has a plot box, and something is listed.</summary>
        public static bool Draws(Plot plot)
        {
            if (plot.Legend == LegendPlacement.Off) return false;
            if (plot.PlotType is not (PlotType.Rect or PlotType.Smith or PlotType.Polar)) return false;
            foreach (var _ in Listed(plot)) return true;
            return false;
        }

        /// <summary>The height, in canvas pixels, of the band a BELOW legend takes under the plot; zero
        /// for any other placement.</summary>
        public static double BelowBandPx(Plot plot, (double W, double H) canvas)
            => BelowBandPx(plot, canvas.W, Math.Min(canvas.W, canvas.H) / 200.0);

        /// <summary><see cref="BelowBandPx(Plot, ValueTuple{double, double})"/> at a stated line-width
        /// unit — what <see cref="PlotCanvasGeometry"/> sizes a Smith or Polar canvas with, before that
        /// canvas exists.</summary>
        public static double BelowBandPx(Plot plot, double width, double lw)
        {
            if (plot.Legend != LegendPlacement.Below || !Draws(plot)) return 0;
            var m = Metrics.For(plot, lw);
            if (m.FontPx < 4.0) return 0;
            var (_, rows) = Grid(m, width, Count(plot));
            return rows * m.RowH + m.Pad;
        }

        public static void Draw(
            SKCanvas canvas, (double W, double H) canvasSize, Plot plot, TransformSet tf, RenderTheme theme,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            if (!Draws(plot)) return;
            var m = Metrics.For(plot, Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            if (m.FontPx < 4.0) return;

            var entries = Entries(plot, alwaysShowSource ?? AppSettings.Current.AlwaysDisplayDataSourcePrefix, aliasFor);

            using var font     = new SKFont(SkiaFonts.PlexRegular, (float)m.FontPx);
            using var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size);
            using var text     = new SKPaint { IsAntialias = true, Color = theme.TextColor };

            float w = (float)canvasSize.W, h = (float)canvasSize.H;
            float rowH = (float)m.RowH, pad = (float)m.Pad, sample = (float)m.SampleW, gap = (float)m.Gap;
            float baseline = font.Size * 0.35f;

            if (plot.Legend == LegendPlacement.Below)
            {
                float band = (float)BelowBandPx(plot, canvasSize);
                var (cols, _) = Grid(m, canvasSize.W, entries.Count);
                float availW = w - 2f * pad;
                float cellW  = availW / cols;
                for (int i = 0; i < entries.Count; i++)
                {
                    int   r  = i / cols, c = i % cols;
                    float x  = pad + c * cellW;
                    float cy = h - band + pad * 0.5f + rowH * (r + 0.5f);
                    DrawEntry(entries[i], x, cy, cellW - gap - sample - gap);
                }
                return;
            }

            // INSIDE: a box against the plot box's top-right corner, one entry per row.
            float vpLeft  = (float)(tf.Viewport.X * w), vpTop = (float)(tf.Viewport.Y * h);
            float vpRight = (float)((tf.Viewport.X + tf.Viewport.Width) * w);
            float maxText = Math.Max(0f, (vpRight - vpLeft) * 0.6f - sample - gap - 2f * pad);
            float widest  = 0f;
            foreach (var e in entries)
                widest = Math.Max(widest, Math.Min(maxText, RendererText.MeasureTextWithFallback(e.Label, font, fallback)));

            float boxW = pad * 2f + sample + gap + widest;
            float boxH = entries.Count * rowH + pad;
            var   box  = SKRect.Create(vpRight - pad - boxW, vpTop + pad, boxW, boxH);
            // Dressed as a marker info box is, and by the same setting: no fill unless the user has turned the
            // boxes' transparent background off. The plot itself paints no background — the host or the export
            // decides it — so a box with a fill of its own is a white patch on any page that is not white.
            if (!AppSettings.Current.MarkerBoxTransparentBackground)
                using (var fill = new SKPaint { Style = SKPaintStyle.Fill,
                                                Color = RenderTheme.WithOpacity(theme.BackgroundColor, 0.90) })
                    canvas.DrawRect(box, fill);
            using (var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f, Color = theme.BorderColor })
                canvas.DrawRect(box, edge);
            // The text's room is the width the box was sized from, not one worked back out of the box: in
            // floats that comes back a hair short of the widest label and cut every one of them short.
            for (int i = 0; i < entries.Count; i++)
                DrawEntry(entries[i], box.Left + pad, box.Top + pad * 0.5f + rowH * (i + 0.5f), maxText);

            void DrawEntry((Trace Trace, string Label) e, float x, float cy, float textRoom)
            {
                DrawSample(canvas, e.Trace, x, x + sample, cy, (float)m.Lw, theme);
                string s = Fit(e.Label, textRoom, font, fallback);
                RendererText.DrawLeftTextWithFallback(canvas, s, x + sample + gap, cy + baseline, font, fallback, text);
            }
        }

        /// <summary>Each listed trace and the name its axis gives it — on a Rect plot the Y-axis label,
        /// on Smith and Polar the label strip's.</summary>
        internal static List<(Trace Trace, string Label)> Entries(Plot plot, bool showSource, Func<Trace, string?>? aliasFor)
        {
            var minimal = TraceLabeler.ComputeMinimalLabels(plot.Traces, showSource, aliasFor);
            var list    = new List<(Trace, string)>();
            for (int i = 0; i < plot.Traces.Count; i++)
            {
                var t = plot.Traces[i];
                if (t.IsContourTrace || t.ExcludeFromAxisLabels) continue;
                string name = i < minimal.Count ? minimal[i] : t.ShortDescription;
                list.Add((t, plot.PlotType == PlotType.Rect ? t.RectYLabel(name, false) : name));
            }
            return list;
        }

        // ---- Layout ------------------------------------------------------

        private readonly record struct Metrics(double Lw, double FontPx, double RowH, double Pad, double SampleW, double Gap)
        {
            // The Y-axis label's own size, so an entry reads at the size of the name it repeats.
            public static Metrics For(Plot plot, double lw)
            {
                double f = plot.Axes.FontSizeTicks * 0.9 * lw;
                return new Metrics(lw, f, f * 1.5, f * 0.5, f * 2.2, f * 0.5);
            }
        }

        private static int Count(Plot plot)
        {
            int n = 0;
            foreach (var _ in Listed(plot)) n++;
            return n;
        }

        /// <summary>Columns and rows of the BELOW grid: as many equal cells across as leave each room
        /// for a sample and about fifteen characters.</summary>
        private static (int Cols, int Rows) Grid(Metrics m, double width, int n)
        {
            if (n <= 0) return (1, 0);
            double cell = m.SampleW + m.Gap + m.FontPx * 8.0;
            int    cols = (int)Math.Clamp(Math.Floor((width - 2 * m.Pad) / cell), 1, n);
            return (cols, (n + cols - 1) / cols);
        }

        private static string Fit(string s, float room, SKFont font, SKFont fallback)
        {
            if (room <= 0f) return "";
            if (RendererText.MeasureTextWithFallback(s, font, fallback) <= room) return s;
            while (s.Length > 1 && RendererText.MeasureTextWithFallback(s + "…", font, fallback) > room) s = s[..^1];
            return s.TrimEnd() + "…";
        }

        /// <summary>A short horizontal run of the trace's line — its colour, width and line type — or,
        /// for a trace drawn as points only, one point.</summary>
        private static void DrawSample(SKCanvas canvas, Trace trace, float x0, float x1, float y, float lw, RenderTheme theme)
        {
            var   props = trace.Properties;
            var   color = theme.ToSKColor(props.LineColor, props.LineOpacity);
            float sw    = Math.Max(0.5f, lw * (float)props.LineWidth);

            if (!props.LineEnabled)
            {
                using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = color };
                canvas.DrawCircle((x0 + x1) / 2f, y, Math.Max(sw, lw * 1.5f), dot);
                return;
            }

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke, IsAntialias = true, Color = color, StrokeWidth = sw,
                StrokeCap = props.LineType == LineType.Dotted ? SKStrokeCap.Round : SKStrokeCap.Butt,
            };
            switch (props.LineType)
            {
                case LineType.Dashed:
                    for (float x = x0; x < x1; x += sw * 5f)
                        canvas.DrawLine(x, y, Math.Min(x + sw * 3f, x1), y, paint);
                    break;
                case LineType.Dotted:
                    for (float x = x0 + sw / 2f; x <= x1; x += sw * 2.5f)
                        canvas.DrawLine(x, y, x + 0.01f, y, paint);
                    break;
                default:
                    canvas.DrawLine(x0, y, x1, y, paint);
                    break;
            }
        }
    }
}
