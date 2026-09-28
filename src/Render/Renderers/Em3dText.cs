// 3D vector copy and drawing export (2026-09-27) — ONE place every label of a 3D picture is drawn through, so the
// whole picture's text can be turned into outlines at once.
//
// Why outlines: Skia's SVG device writes a label as <text font-family="IBM Plex Sans">, and the reader's machine
// renders it in whatever it has by that name — on a machine without the face, a serif fallback at different
// metrics, which is what a user sees when an exported figure is opened in a browser or pasted into a slide. A path
// is the glyphs themselves: the same picture everywhere, at the cost of text that can no longer be selected or edited.

using SkiaSharp;

namespace CircuitRF.Render;

public static class Em3dText
{
    /// <summary>
    /// Draws <paramref name="text"/> as <see cref="SKCanvas.DrawText(string, float, float, SKTextAlign, SKFont, SKPaint)"/>
    /// does, or — <paramref name="asPaths"/> — as the outline of its glyphs, placed by the same alignment rule (the
    /// advance width).
    /// </summary>
    public static void Draw(SKCanvas canvas, bool asPaths, string text, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        if (!asPaths)
        {
            canvas.DrawText(text, x, y, align, font, paint);
            return;
        }
        if (text.Length == 0) return;
        float w = font.MeasureText(text);
        float x0 = align switch { SKTextAlign.Center => x - w / 2, SKTextAlign.Right => x - w, _ => x };
        using var path = font.GetTextPath(text, new SKPoint(x0, y));
        var style = paint.Style;
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawPath(path, paint);
        paint.Style = style;
    }
}
