// The overlay flattened onto the picture — brief-img-11-cli-and-mcp.md R-im11-3.
//
// `--overlay out.png` (convert and recognize) and the MCP tools' image content are this: the source picture with every
// ImageOverlayItem drawn over it, in the classes' colours the dialog's canvas uses (Colours below — the canvas reads
// the same constants), unknowns in amber. The canvas draws the same items live and lets the legend hide a class; the
// file is every class at once, because nobody is there to toggle one.
//
// Labels are drawn in circuitRF's own stroke font (StrokeFont, Hershey Roman Simplex), never a system typeface: it is
// shipped in this assembly, it is the same on every machine, and so the same reading flattens to the same PNG bytes.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Text;
using SkiaSharp;

namespace CircuitRF.Design.Imaging;

public sealed partial class ImageOverlay
{
    /// <summary>Each class's colour, 0xRRGGBB — the dialog's canvas and the flattened picture both read these.</summary>
    public static class Colours
    {
        public const int Amber = 0xE0A010, Accent = 0x1E88E5, Part = 0x7B1FA2, Line = 0x00897B, Port = 0xC62828,
                         Drill = 0x37474F, Wire = 0x1565C0, Text = 0x5D4037, Copper = 0xB87333, Ignored = 0x707070,
                         Connected = 0x2E9D4A;
    }

    /// <summary>The colour <paramref name="item"/> is drawn in when it is not selected: amber when something about it
    /// was not read, its layer's own colour for copper, else its class's.</summary>
    public static int ColourOf(ImageOverlayItem item) => item.Unknown ? Colours.Amber : item.Class switch
    {
        ImageOverlayClass.Copper => item.Rgb ?? Colours.Copper,
        ImageOverlayClass.Drill => item.Rgb ?? Colours.Drill,
        ImageOverlayClass.Ignored => item.Rgb ?? Colours.Ignored,
        ImageOverlayClass.Part => Colours.Part,
        ImageOverlayClass.Line => Colours.Line,
        ImageOverlayClass.Port => Colours.Port,
        ImageOverlayClass.Wire => Colours.Wire,
        ImageOverlayClass.Text => item.Rgb ?? Colours.Text,
        ImageOverlayClass.Crossing => item.Rgb ?? Colours.Ignored,
        ImageOverlayClass.Plot => Colours.Accent,
        ImageOverlayClass.Trace => item.Rgb ?? Colours.Line,
        ImageOverlayClass.Marker => Colours.Port,
        ImageOverlayClass.Annotation => Colours.Ignored,
        _ => Colours.Amber,
    };

    /// <summary><paramref name="picture"/> with every item drawn over it, as PNG bytes, at the picture's own size.</summary>
    public byte[] Png(RasterImage picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        int w = picture.Width, h = picture.Height;
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(picture.Rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                using var source = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), w * 4);
                canvas.DrawImage(source, 0, 0);
            }
            finally { handle.Free(); }
            Draw(canvas, Math.Max(1.0, Math.Max(w, h) / 800.0), h);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Writes <see cref="Png"/> to <paramref name="path"/>, making its folder.</summary>
    public void WritePng(string path, RasterImage picture)
    {
        byte[] bytes = Png(picture);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Every item, in picture pixels; <paramref name="u"/> is one display pixel, so marks stay legible on a
    /// large picture.</summary>
    private void Draw(SKCanvas canvas, double u, int height)
    {
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        SKColor C(int rgb, double alpha = 1) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)Math.Round(255 * alpha));

        foreach (var item in Items)
        {
            int colour = ColourOf(item);
            float pen = (float)((item.Class == ImageOverlayClass.Copper ? 1.25 : 1.75) * u);
            switch (item.Class)
            {
                // A via is the circle of its drill; a hole kept open is its own outline (the default case, R-dfr30-3-2).
                case ImageOverlayClass.Drill when item.Circle is { } c && item.Paths.Count == 0:
                    // A correction dashed over what it corrects (brief-dfr29-1 R-dfr29-1-3).
                    stroke.PathEffect = item.Id.StartsWith(CorrectionPrefix, StringComparison.Ordinal)
                        ? SKPathEffect.CreateDash([(float)(3 * u), (float)(2 * u)], 0) : null;
                    Circle(c.X, c.Y, c.R, null, colour, pen);
                    stroke.PathEffect = null;
                    break;
                case ImageOverlayClass.Port when item.Circle is { } c:
                {
                    // The schematic's port glyph: a circle, a bar through it, the number beside it.
                    double r = 7 * u;
                    Circle(c.X, c.Y, r, (0xFFFFFF, 0.85), colour, pen);
                    stroke.Color = C(colour);
                    canvas.DrawLine((float)(c.X - r * 0.6), (float)c.Y, (float)(c.X + r * 0.6), (float)c.Y, stroke);
                    if (item.Label is { } label) Text(label, c.X + r + 2 * u, c.Y + 4.5 * u, colour);
                    break;
                }
                case ImageOverlayClass.Wire when item.Circle is { } c:
                    // A junction dot filled; a dangling end a ring.
                    Circle(c.X, c.Y, c.R, item.Id.StartsWith("dot:", StringComparison.Ordinal) ? (colour, 1) : (0xFFFFFF, 0.6), colour, pen);
                    break;
                case ImageOverlayClass.Plot when item.Circle is { } c:
                    Circle(c.X, c.Y, c.R, null, colour, pen);
                    break;
                case ImageOverlayClass.Marker when item.Circle is { } c:
                    Circle(c.X, c.Y, c.R, (0xFFFFFF, 0.6), colour, pen);
                    if (item.Label is { } ml) Text(ml, c.X + c.R + 2 * u, c.Y - c.R, colour);
                    break;
                case ImageOverlayClass.Crossing when item.Circle is { } c:
                    Circle(c.X, c.Y, c.R, item.Rgb == Colours.Connected ? (colour, 0.35) : null, colour, (float)(2 * u));
                    break;
                default:
                {
                    using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                    foreach (var ring in item.Paths)
                    {
                        if (ring.Count == 0) continue;
                        path.MoveTo((float)ring[0].X, (float)ring[0].Y);
                        for (int i = 1; i < ring.Count; i++) path.LineTo((float)ring[i].X, (float)ring[i].Y);
                        if (item.Closed) path.Close();
                    }
                    if (item.Class == ImageOverlayClass.Copper) { fill.Color = C(colour, 0.18); canvas.DrawPath(path, fill); }
                    // A halo under copper and part outlines: an outline the colour of what it outlines cannot be seen.
                    if (item.Class is ImageOverlayClass.Copper or ImageOverlayClass.Part)
                    {
                        stroke.Color = C(0xFFFFFF, 0.85);
                        stroke.StrokeWidth = pen + (float)(2 * u);
                        canvas.DrawPath(path, stroke);
                    }
                    // A trace is drawn on a white halo so it shows over its own curve; a bridged gap dashed.
                    bool fitted = item.Id.StartsWith(FitPrefix, StringComparison.Ordinal);
                    if (item.Class == ImageOverlayClass.Trace)
                    {
                        stroke.Color = C(0xFFFFFF, 0.9);
                        stroke.StrokeWidth = pen + (float)(2 * u);
                        if (!item.Id.StartsWith(GapPrefix, StringComparison.Ordinal) && !fitted) canvas.DrawPath(path, stroke);
                    }
                    stroke.Color = C(colour);
                    // A fitted curve thin, so the trace's own colour shows beside it (brief-pdg-2 R-pdg2-4).
                    stroke.StrokeWidth = fitted ? (float)u : pen;
                    // A bridged gap, a fitted curve and an annotation stroke dashed: not read as line work.
                    stroke.PathEffect = item.Id.StartsWith(GapPrefix, StringComparison.Ordinal) || fitted || item.Class == ImageOverlayClass.Annotation
                                        || item.Id.StartsWith(CorrectionPrefix, StringComparison.Ordinal)
                        ? SKPathEffect.CreateDash([(float)(4 * u), (float)(3 * u)], 0) : null;
                    canvas.DrawPath(path, stroke);
                    stroke.PathEffect = null;
                    break;
                }
            }
            if (item.Label is { Length: > 0 } text && item.Class is ImageOverlayClass.Part or ImageOverlayClass.Line or ImageOverlayClass.Text
                                                                         or ImageOverlayClass.Trace or ImageOverlayClass.Plot or ImageOverlayClass.Annotation)
            {
                double cap = 9 * u;
                if (item.Class is ImageOverlayClass.Line or ImageOverlayClass.Trace or ImageOverlayClass.Annotation && item.Paths is [{ Count: > 0 } first, ..])
                {
                    var (mx, my) = Midpoint(first);
                    double width = StrokeFont.Layout(text, LabelFontStyle.Regular, cap).Advance;
                    Text(text, mx - width / 2, my - 4 * u, colour);
                }
                else Text(text, item.Bounds.Left, item.Bounds.Top - 3 * u, colour);
            }
        }

        // The legend, bottom left: what every mark above was read at.
        for (int k = 0; k < Legend.Count; k++)
            Text(Legend[k], 4 * u, height - 4 * u - (Legend.Count - 1 - k) * 13 * u - 9 * u * StrokeFont.DescentOverCap, Colours.Text);

        void Circle(double x, double y, double r, (int Rgb, double Alpha)? inside, int colour, float pen)
        {
            if (inside is { } i) { fill.Color = C(i.Rgb, i.Alpha); canvas.DrawCircle((float)x, (float)y, (float)r, fill); }
            stroke.Color = C(colour);
            stroke.StrokeWidth = pen;
            canvas.DrawCircle((float)x, (float)y, (float)r, stroke);
        }

        // Stroke-font text on a white backing, its baseline at (x, baseline).
        void Text(string text, double x, double baseline, int colour)
        {
            double cap = 9 * u;
            var layout = StrokeFont.Layout(text, LabelFontStyle.Regular, cap);
            fill.Color = C(0xFFFFFF, 0.8);
            canvas.DrawRect(SKRect.Create((float)(x - 2 * u), (float)(baseline - cap - 2 * u),
                                          (float)(layout.Advance + 4 * u), (float)(cap * (1 + StrokeFont.DescentOverCap) + 4 * u)), fill);
            stroke.Color = C(colour);
            stroke.StrokeWidth = (float)Math.Max(1, 1.1 * u);
            using var path = new SKPath();
            foreach (var s in layout.Strokes)
            {
                if (s.Length < 4) continue;
                path.MoveTo((float)(x + s[0]), (float)(baseline - s[1]));
                for (int i = 2; i + 1 < s.Length; i += 2) path.LineTo((float)(x + s[i]), (float)(baseline - s[i + 1]));
            }
            canvas.DrawPath(path, stroke);
        }
    }

    private static (double X, double Y) Midpoint(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count == 1) return path[0];
        int i = path.Count / 2;
        return ((path[i - 1].X + path[i].X) / 2, (path[i - 1].Y + path[i].Y) / 2);
    }
}
