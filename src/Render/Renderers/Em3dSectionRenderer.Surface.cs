// brief-em3d-89 — the page a Surfaces or Faces field plot is drawn on: Em3dSurfaceField's depth-buffered picture in the frame,
// the hot spot marked where it can be seen, each object's material on its visible piece (--labels), the field's legend where the
// materials' would be, and the caption the layer carries. The section page's own chrome (the axis indicator, the scale bar) is
// drawn over it by Draw, as on any page.

using SkiaSharp;

namespace CircuitRF.Render;

public static partial class Em3dSectionRenderer
{
    private static void DrawSurfacePage(SKCanvas canvas, int width, int height, Em3dScene scene, Em3dRenderStyle style, Em3dFieldLayer field)
    {
        var st = StackupRenderTheme.FromTheme(style.Theme, style.Variant);
        var page = Layout(width, height, scene, style.Margin, style.Tight);
        if (!style.Transparent) canvas.Clear(st.Background);

        // a PNG's canvas is in device pixels; a scaled one draws the picture at its own resolution
        float devicePerUnit = Math.Max(1f, canvas.TotalMatrix.ScaleX);
        var raster = Em3dSurfaceField.Raster(field, page, devicePerUnit, st.LabelInk);
        using (raster.Image)
        {
            canvas.Save();
            canvas.ClipRect(page.Frame);
            using (var image = SKImage.FromBitmap(raster.Image))
                canvas.DrawImage(image, raster.Rect, new SKSamplingOptions(SKFilterMode.Nearest));

            using var small = new SKFont(SkiaFonts.PlexRegular, page.FontSize * 0.85f);
            if (raster.HotSpot is { } hot)
            {
                float r = page.FontSize * 0.45f;
                using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, Color = SKColors.White };
                using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.Black };
                canvas.DrawCircle(hot, r, halo);
                canvas.DrawCircle(hot, r, ring);
                canvas.DrawLine(hot.X - r * 1.8f, hot.Y, hot.X - r * 0.5f, hot.Y, ring);
                canvas.DrawLine(hot.X + r * 0.5f, hot.Y, hot.X + r * 1.8f, hot.Y, ring);
            }
            if (style.Labels) DrawSurfaceLabels(canvas, field.Surface!, raster, style, st, small);
            canvas.Restore();
        }

        if (field.Legend.Count > 0) PaintLegend(canvas, width, height, page, style, field);
        if (style.Tight) return;
        using var bold = new SKFont(SkiaFonts.PlexSemiBold, page.FontSize);
        using var body = new SKFont(SkiaFonts.PlexRegular, page.FontSize * 0.85f);
        using var text = new SKPaint { IsAntialias = true, Color = st.LabelInk };
        DrawLines(canvas, field.Surface!.Caption, style, height, page.CaptionHeight, page.LineHeight, page.Pad, bold, body, text);
    }

    /// <summary>brief-em3d-88 Q2 carried over — each object's material, once, at the centre of what can be seen of it, where the
    /// words fit inside that piece and clear of the labels already placed (largest piece first).</summary>
    private static void DrawSurfaceLabels(SKCanvas canvas, Em3dSurfaceLayer surface, Em3dSurfaceRaster raster, Em3dRenderStyle style,
                                          StackupRenderTheme st, SKFont font)
    {
        using var ink = new SKPaint { IsAntialias = true, Color = st.LabelInk };
        var placed = new List<SKRect>();
        float h = font.Size;
        foreach (var (obj, centre, area, bounds) in raster.Visible)
        {
            if (!surface.Materials.TryGetValue(obj, out string? label) || string.IsNullOrEmpty(label)) continue;
            float w = font.MeasureText(label);
            if (bounds.Width < w + 4 || bounds.Height < h * 1.2f || area < 2 * w * h) continue;
            var box = new SKRect(centre.X - w / 2 - 2, centre.Y - h * 0.8f, centre.X + w / 2 + 2, centre.Y + h * 0.3f);
            if (placed.Any(p => p.IntersectsWith(box))) continue;
            placed.Add(box);
            Halo(canvas, style, label, centre.X, centre.Y + h * 0.3f, SKTextAlign.Center, font, ink, st.Background);
        }
    }
}
