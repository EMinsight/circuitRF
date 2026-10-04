// brief-em3d-29 R-em3d29-5 — Export picture… and Copy: the GPU's own pixels (read back from an offscreen image the
// view's backend drew with its own buffers), with — when asked — the legend and the caption painted over
// them, encoded as PNG. The picture is the one on screen at the chosen size; nothing here re-renders the
// scene. No video: every route to one is a native dependency (the brief's scope).

using SkiaSharp;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>
/// A picture of the 3D view read back from the GPU, with what is to be painted over it — taken on the UI
/// thread (the read-back holds the render lock), composed and encoded wherever the caller likes: at 4× a
/// window the composing and the encoding are the slow part, not the GPU. brief-em3d-96 — the legends are the view's
/// stack, one per colour range, top first.
/// </summary>
public sealed record FieldPictureShot(byte[] Rgba, int Width, int Height, float Scale, IReadOnlyList<FieldPictureLegend> Legends,
                                      string? Caption, bool Dark)
{
    /// <summary>3D editor bugs round 6 — the view's 2D chrome (the axis indicator, the scale bar, a measurement, the
    /// selection's highlight), drawn at this picture's size to lay over the GPU's pixels; null for none.</summary>
    public FieldPictureLayer? Layer { get; init; }

    /// <summary>brief-em3d-107 R-em3d107-5b — drawn over nothing: <see cref="Rgba"/> is PREMULTIPLIED with real alpha, and the PNG is
    /// straightened before it is encoded (PNG stores straight alpha).</summary>
    public bool Transparent { get; init; }

    /// <summary>R-em3d107-5a — how many times this size each side the picture was drawn at before it was brought down (1: not).</summary>
    public int Supersample { get; init; } = 1;

    /// <summary>brief-em3d-109 R-em3d109-4 — <c>Lit Fields</c>, <c>Blended Fields</c> or both (<see cref="Look.RealisticLook.FieldIndicator"/>),
    /// painted under the legend stack whatever the legend and caption options say; null for none.</summary>
    public string? Indicator { get; init; }

    /// <summary>brief-em3d-110 — the typeface the legends, the caption and the indicator are set in; null for the system's default (the
    /// window's pictures). `render` passes the embedded face, so its bytes do not depend on the machine's fonts.</summary>
    public SKTypeface? Typeface { get; init; }

    /// <summary>The pixels with the layer, the legends, the caption and the indicator painted on (the caller disposes it).</summary>
    public SKBitmap Compose() => FieldPicture.Compose(Rgba, Width, Height, Scale, Legends, Caption, Dark, Layer, Indicator, Typeface);

    public byte[] Png()
    {
        using var bmp = Compose();
        return Transparent ? FieldPicture.EncodeStraight(bmp) : FieldPicture.Encode(bmp);
    }
}

/// <summary>One legend of a picture: its lines (the first above the colour bar), its map and its range.</summary>
public sealed record FieldPictureLegend(IReadOnlyList<string> Lines, ColorMap3D Map, FieldColorScale? Range);

/// <summary>A picture-sized layer to paint over a <see cref="FieldPictureShot"/>: premultiplied, rows top first, BGRA when
/// <paramref name="Bgra"/> and RGBA otherwise (the byte order of whatever drew it).</summary>
public sealed record FieldPictureLayer(byte[] Pixels, bool Bgra);

public static class FieldPicture
{
    /// <summary>The largest side a picture may have: every backend's largest 2D texture (Metal and D3D11
    /// both 16,384), which the offscreen image has to be.</summary>
    public const int MaxSide = 16384;
    /// <summary>
    /// <paramref name="rgba"/> (<paramref name="width"/> × <paramref name="height"/>, rows top first) as a
    /// PNG, with <paramref name="legend"/>'s lines and a colour bar in <paramref name="map"/> at the top
    /// right and <paramref name="caption"/> at the bottom left, scaled by <paramref name="scale"/> (the
    /// export's multiple of the window, so the text is the size it is on screen).
    /// </summary>
    public static byte[] Png(byte[] rgba, int width, int height, float scale, IReadOnlyList<string> legend, ColorMap3D? map,
                             FieldColorScale? range, string? caption, bool dark, FieldPictureLayer? layer = null)
    {
        using var bmp = Compose(rgba, width, height, scale, Legends(legend, map, range), caption, dark, layer);
        return Encode(bmp);
    }

    internal static byte[] Encode(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>brief-em3d-107 R-em3d107-5b — a premultiplied picture with real alpha, straightened here (Look.PictureResample.Unpremultiply,
    /// rounded) and encoded as it is, so the encoder converts nothing.</summary>
    internal static byte[] EncodeStraight(SKBitmap bmp)
    {
        var px = new byte[bmp.Width * bmp.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), px, 0, px.Length);
        Look.PictureResample.Unpremultiply(px);
        var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var data0 = SKData.CreateCopy(px);
        using var image = SKImage.FromPixels(info, data0, info.RowBytes);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static IReadOnlyList<FieldPictureLegend> Legends(IReadOnlyList<string> legend, ColorMap3D? map, FieldColorScale? range)
        => legend.Count > 0 && map is not null ? [new FieldPictureLegend(legend, map, range)] : [];

    /// <summary>As <see cref="Png"/>, unencoded: the composed pixels, RGBA8 premultiplied (opaque). <paramref name="layer"/>,
    /// when given, is painted first, under the legends and the caption.</summary>
    public static SKBitmap Compose(byte[] rgba, int width, int height, float scale, IReadOnlyList<FieldPictureLegend> legends,
                                   string? caption, bool dark, FieldPictureLayer? layer = null, string? indicator = null, SKTypeface? typeface = null)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), Math.Min(rgba.Length, width * height * 4));
        using (var canvas = new SKCanvas(bmp))
        {
            if (layer is not null && layer.Pixels.Length >= width * height * 4)
            {
                var info = new SKImageInfo(width, height, layer.Bgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888, SKAlphaType.Premul);
                using var over = new SKBitmap();
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(layer.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    over.InstallPixels(info, handle.AddrOfPinnedObject(), width * 4);
                    canvas.DrawBitmap(over, 0, 0);
                }
                finally { handle.Free(); }
            }
            Paint(canvas, width, height, scale, legends, caption, dark, typeface ?? SKTypeface.Default, indicator);
        }
        return bmp;
    }

    /// <summary>
    /// The legend (<paramref name="legend"/>'s first line, a colour bar in <paramref name="map"/>, the range's ends, the rest of
    /// the lines) in a box at the top right, and <paramref name="caption"/> at the bottom left, onto any canvas: Export
    /// picture's pixels, or — brief-em3d-84 — `render --field`'s SVG, PDF or PNG page, which passes an embedded typeface so the
    /// bytes do not depend on the machine's fonts.
    /// </summary>
    public static void Paint(SKCanvas canvas, int width, int height, float scale, IReadOnlyList<string> legend, ColorMap3D? map,
                             FieldColorScale? range, string? caption, bool dark, SKTypeface typeface)
        => Paint(canvas, width, height, scale, Legends(legend, map, range), caption, dark, typeface);

    /// <summary>
    /// brief-em3d-96 D3 — the legends stacked down the right, one below the next (8 px apart at scale 1), all one width — the
    /// widest's; a legend that would run past the bottom is not drawn, and the last one drawn ends with "+N more".
    /// </summary>
    public static void Paint(SKCanvas canvas, int width, int height, float scale, IReadOnlyList<FieldPictureLegend> legends,
                             string? caption, bool dark, SKTypeface typeface, string? indicator = null)
    {
        var ink = dark ? new SKColor(235, 235, 240) : new SKColor(30, 32, 38);
        var box = dark ? new SKColor(28, 30, 34, 215) : new SKColor(250, 250, 252, 225);
        using var font = new SKFont(typeface, 12 * scale);
        using var text = new SKPaint { Color = ink, IsAntialias = true };
        using var fill = new SKPaint { Color = box, IsAntialias = true };
        float pad = 8 * scale, line = 16 * scale, gap = 8 * scale;
        // brief-em3d-109 — where the stack ends (the indicator's top), and its right edge
        float stackBottom = pad, stackRight = width - pad;

        if (legends.Count > 0)
        {
            float barW = 220 * scale, barH = 12 * scale;
            float w = Math.Max(barW, legends.Max(l => l.Lines.Count == 0 ? 0 : l.Lines.Max(t => font.MeasureText(t)))) + 2 * pad;
            float Height(FieldPictureLegend l, bool more) => pad * 2 + barH + line * (l.Lines.Count + 1 + (more ? 1 : 0));
            int shown = StackCount(legends.Select(l => Height(l, false)).ToList(), line, gap, pad, height - pad);
            float x0 = width - w - pad, y0 = pad;
            for (int k = 0; k < shown; k++)
            {
                var lg = legends[k];
                bool more = k == shown - 1 && shown < legends.Count;
                float h = Height(lg, more);
                canvas.DrawRoundRect(new SKRect(x0, y0, x0 + w, y0 + h), 4 * scale, 4 * scale, fill);
                float y = y0 + pad + line * 0.8f;
                if (lg.Lines.Count > 0) canvas.DrawText(lg.Lines[0], x0 + pad, y, font, text);
                y += line * 0.4f;
                var stops = lg.Map.Stops;
                using var bar = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(x0 + pad, 0), new SKPoint(x0 + pad + barW, 0),
                        [.. stops.Select(s => new SKColor(s.R, s.G, s.B))], [.. stops.Select(s => s.T)], SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(new SKRect(x0 + pad, y, x0 + pad + barW, y + barH), bar);
                y += barH + line * 0.9f;
                if (lg.Range is { } range)
                {
                    string lo = FieldColorScale.G(range.Lo), hi = FieldColorScale.G(range.Hi);
                    canvas.DrawText(lo, x0 + pad, y, font, text);
                    canvas.DrawText(hi, x0 + pad + barW - font.MeasureText(hi), y, font, text);
                    y += line;
                }
                for (int i = 1; i < lg.Lines.Count; i++, y += line) canvas.DrawText(lg.Lines[i], x0 + pad, y, font, text);
                if (more) canvas.DrawText(MoreLine(legends.Count - shown), x0 + pad, y, font, text);
                y0 += h + gap;
            }
            if (shown > 0) stackBottom = y0 - gap + IndicatorGap * scale;
        }
        if (indicator is { Length: > 0 })
        {
            // R-em3d109-4b — small, in the legend's secondary text colour, right under the stack (alone in its corner with no legend)
            using var small = new SKFont(typeface, IndicatorSize * scale);
            using var secondary = new SKPaint { Color = ink.WithAlpha(IndicatorAlpha), IsAntialias = true };
            canvas.DrawText(indicator, stackRight - small.MeasureText(indicator), stackBottom + IndicatorSize * scale, small, secondary);
        }
        if (caption is { Length: > 0 })
        {
            float w = font.MeasureText(caption) + 2 * pad;
            float y0 = height - pad - line - pad;
            canvas.DrawRoundRect(new SKRect(pad, y0, pad + w, y0 + line + pad), 4 * scale, 4 * scale, fill);
            canvas.DrawText(caption, 2 * pad, y0 + line * 0.85f, font, text);
        }
    }

    /// <summary>brief-em3d-109 R-em3d109-4b — the indicator's text size (the legend's is 12, its range 11), its gap below the legend stack,
    /// and the alpha that makes the legend's ink its secondary colour. The live overlay draws it with the same three.</summary>
    public const float IndicatorSize = 10, IndicatorGap = 4;
    public const byte IndicatorAlpha = 160;

    /// <summary>brief-em3d-96 D3 — the last legend drawn says how many more there are.</summary>
    public static string MoreLine(int more) => $"+{more} more";

    /// <summary>
    /// How many of a stack of legends <paramref name="heights"/> tall fit between the top (at <paramref name="top"/>) and
    /// <paramref name="bottom"/>, <paramref name="gap"/> apart — the last one drawn a <paramref name="line"/> taller when some
    /// do not fit, for its "+N more".
    /// </summary>
    public static int StackCount(IReadOnlyList<float> heights, float line, float gap, float top, float bottom)
    {
        for (int m = heights.Count; m > 0; m--)
        {
            float h = top + heights.Take(m).Sum() + gap * (m - 1) + (m < heights.Count ? line : 0);
            if (h <= bottom) return m;
        }
        return 0;
    }
}
