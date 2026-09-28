// 3D vector copy and drawing export (2026-09-27) — a DRAWING of a 3D model: several views on one sheet, or one view as
// a picture, in vector form.
//
// A view is an Em3dScene — an outline along a direction (Em3dSectionScene.Outline) or a section (Em3dSectionScene.Build)
// — so nothing here decides what a solid looks like from a side; it lays views out and paints them. The chrome a
// SECTION FIGURE carries (the air box, its faces' labels, port hatching, a three-line caption) is an EM setup's and is
// left out: a drawing is the artwork.
//
// ONE SCALE. Every view on a sheet is drawn at the same scale, and the scale is a round one (1, 2 or 5 times a power
// of ten, paper to model) written in the title block — so a length measured on one view can be compared with another,
// and with a ruler on the printed page.
//
// Colours are the section renderer's: a conductor its layer's colour, a dielectric its hue-turned fill, the ink the
// stackup theme's. The page is white unless it is transparent: a drawing is printed.

using System.Globalization;
using System.Text;
using CircuitRF.Engine.Em3d;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>One view on a sheet: its title (under it) and what it shows.</summary>
public sealed record Em3dDrawingPanel(string Title, Em3dScene Scene);

/// <summary>How a drawing is painted.</summary>
/// <param name="ObjectColours">A conductor's colour by object name (Em3dSectionRenderer.ObjectColours, or the 3D view's).</param>
public sealed record Em3dDrawingStyle(IReadOnlyDictionary<string, SKColor> ObjectColours, ColorTheme Theme, ColorVariant Variant)
{
    /// <summary>A material legend beside the views.</summary>
    public bool Legend { get; init; } = true;

    /// <summary>Labels as glyph outlines (<see cref="Em3dText"/>).</summary>
    public bool TextAsPaths { get; init; } = true;

    /// <summary>Leave the page unpainted (a picture for the clipboard: the destination supplies the background).</summary>
    public bool Transparent { get; init; }

    /// <summary>The title block's document name; null draws no title block (and no sheet border).</summary>
    public string? DocumentName { get; init; }

    /// <summary>Said in the title block after the scale, e.g. "Hidden edges dashed".</summary>
    public string? Remark { get; init; }
}

/// <summary>Where a sheet puts everything, in page units (points for SVG/PDF).</summary>
/// <param name="Scale">Page units per metre of model, the same for every view.</param>
/// <param name="ScaleText">The scale as a drawing states it, paper to model: "5:1", "1:2".</param>
public sealed record Em3dSheetLayout(
    float Width, float Height, float FontSize, double Scale, string ScaleText, int Columns, int Rows,
    IReadOnlyList<SKRect> Views, SKRect Legend, SKRect TitleBlock);

public static class Em3dDrawingSheet
{
    /// <summary>A point in page units per metre on paper: 72 per inch.</summary>
    public const double PointsPerMetre = 72 / 0.0254;

    // ── extents and the scale ─────────────────────────────────────────────────────────────────

    /// <summary>The picture-plane box of what <paramref name="scene"/> actually draws (its regions and lines), or its
    /// frame when it draws nothing.</summary>
    public static (Uv Min, Uv Max) Extent(Em3dScene scene)
    {
        double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
        void Grow(double u, double v) { u0 = Math.Min(u0, u); v0 = Math.Min(v0, v); u1 = Math.Max(u1, u); v1 = Math.Max(v1, v); }
        foreach (var r in scene.Regions.Where(r => r.Role != Em3dRole.Air))
        {
            foreach (var ring in r.Rings) foreach (var q in ring) Grow(q.U, q.V);
            if (r.CircleCentre is { } c) { Grow(c.U - r.CircleRadius, c.V - r.CircleRadius); Grow(c.U + r.CircleRadius, c.V + r.CircleRadius); }
        }
        foreach (var l in scene.Lines.Where(l => l.Role != Em3dRole.Air))
        {
            Grow(l.A.U, l.A.V); Grow(l.B.U, l.B.V);
            if (l.IsSheet && scene.View.Kind is not (Em3dViewKind.Projection or Em3dViewKind.Iso))
            {
                // A sheet crossing a vertical section is drawn as a band of its real thickness.
                Grow(l.A.U, l.A.V + l.WidthM / 2); Grow(l.A.U, l.A.V - l.WidthM / 2);
            }
        }
        if (double.IsInfinity(u0)) return (scene.FrameMin, scene.FrameMax);
        return (new Uv(u0, v0), new Uv(u1, v1));
    }

    /// <summary>The largest round scale at or below <paramref name="pointsPerMetre"/>: paper-to-model 1, 2 or 5 times a
    /// power of ten. Returns the scale in page units per metre and its spelling.</summary>
    public static (double Scale, string Text) RoundScale(double pointsPerMetre)
    {
        double ratio = pointsPerMetre / PointsPerMetre;   // metres of paper per metre of model
        if (!(ratio > 0) || double.IsInfinity(ratio)) return (pointsPerMetre, "not to scale");
        double[] steps = [1, 2, 5];
        if (ratio >= 1)
        {
            double best = 1;
            for (int e = 0; e <= 12; e++) foreach (var s in steps) { double c = s * Math.Pow(10, e); if (c <= ratio * (1 + 1e-9)) best = c; }
            return (best * PointsPerMetre, Num(best) + ":1");
        }
        // Model larger than paper: the smallest 1:n with 1/n at or below the ratio.
        for (int e = 0; e <= 12; e++)
            foreach (var s in steps)
            {
                double n = s * Math.Pow(10, e);
                if (1 / n <= ratio * (1 + 1e-9)) return (PointsPerMetre / n, "1:" + Num(n));
            }
        return (pointsPerMetre, "not to scale");

        static string Num(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // ── the sheet ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Lays <paramref name="panels"/> out on a <paramref name="width"/> × <paramref name="height"/> sheet: the grid
    /// (columns chosen for the largest common scale), the legend column, and the title block.</summary>
    public static Em3dSheetLayout Layout(float width, float height, IReadOnlyList<Em3dDrawingPanel> panels, Em3dDrawingStyle style)
    {
        float fs = Math.Clamp(Math.Min(width, height) / 70f, 7f, 12f);
        float lineH = fs * 1.35f;
        float margin = Math.Max(18f, 0.035f * Math.Min(width, height));
        bool block = style.DocumentName is not null;
        float titleH = block ? lineH * 2.4f : 0;
        bool legend = style.Legend && LegendRows(panels, style).Count > 0;
        float legendW = legend ? Math.Clamp(width * 0.18f, 9 * fs, 17 * fs) : 0;

        var area = new SKRect(margin, margin, width - margin - legendW, height - margin - titleH);
        var tb = block ? new SKRect(margin, height - margin - titleH, width - margin, height - margin) : SKRect.Empty;
        var lg = legend ? new SKRect(area.Right, margin, width - margin, area.Bottom) : SKRect.Empty;

        int n = Math.Max(1, panels.Count);
        float pad = fs, caption = lineH * 1.6f;
        var sizes = panels.Select(p => { var (lo, hi) = Extent(p.Scene); return (W: Math.Max(hi.U - lo.U, 0), H: Math.Max(hi.V - lo.V, 0)); }).ToList();

        // Each column is as wide as its widest view and each row as tall as its tallest, so a flat model's side views
        // take a thin row rather than a cell as tall as its top view. The column count is the one giving the largest scale.
        int bestCols = 1; double bestScale = 0;
        for (int cols = 1; cols <= n; cols++)
        {
            var (colW, rowH) = Tracks(sizes, cols);
            double s = double.PositiveInfinity;
            if (colW.Sum() > 0) s = Math.Min(s, (area.Width - colW.Length * 2 * pad) / colW.Sum());
            if (rowH.Sum() > 0) s = Math.Min(s, (area.Height - rowH.Length * (2 * pad + caption)) / rowH.Sum());
            if (double.IsInfinity(s)) s = 1;
            if (s > bestScale * (1 + 1e-9)) { bestScale = s; bestCols = cols; }
        }
        var (scale, text) = RoundScale(Math.Max(bestScale, 1e-30));

        // The tracks at the round scale, the space left over shared out evenly between them.
        var (cw, rh) = Tracks(sizes, bestCols);
        float[] colPx = [.. cw.Select(w => (float)(w * scale) + 2 * pad)];
        float[] rowPx = [.. rh.Select(h => (float)(h * scale) + 2 * pad + caption)];
        float spareW = Math.Max(0, area.Width - colPx.Sum()) / colPx.Length, spareH = Math.Max(0, area.Height - rowPx.Sum()) / rowPx.Length;
        var views = new List<SKRect>();
        for (int k = 0; k < panels.Count; k++)
        {
            int col = k % bestCols, row = k / bestCols;
            float x = area.Left + colPx.Take(col).Sum() + col * spareW, y = area.Top + rowPx.Take(row).Sum() + row * spareH;
            var cell = new SKRect(x, y, x + colPx[col] + spareW, y + rowPx[row] + spareH);
            views.Add(new SKRect(cell.Left + pad, cell.Top + pad, cell.Right - pad, cell.Bottom - pad - caption));
        }
        return new Em3dSheetLayout(width, height, fs, scale, text, bestCols, rowPx.Length, views, lg, tb);
    }

    /// <summary>With <paramref name="cols"/> columns, each column's widest view and each row's tallest, metres.</summary>
    private static (double[] Cols, double[] Rows) Tracks(List<(double W, double H)> sizes, int cols)
    {
        int rows = (sizes.Count + cols - 1) / cols;
        var c = new double[Math.Min(cols, Math.Max(1, sizes.Count))];
        var r = new double[Math.Max(1, rows)];
        for (int k = 0; k < sizes.Count; k++)
        {
            c[k % cols] = Math.Max(c[k % cols], sizes[k].W);
            r[k / cols] = Math.Max(r[k / cols], sizes[k].H);
        }
        return (c, r);
    }

    /// <summary>Paints a sheet laid out by <see cref="Layout"/>.</summary>
    public static void Draw(SKCanvas canvas, Em3dSheetLayout layout, IReadOnlyList<Em3dDrawingPanel> panels, Em3dDrawingStyle style)
    {
        var st = StackupRenderTheme.FromTheme(style.Theme, style.Variant);
        if (!style.Transparent) canvas.Clear(SKColors.White);
        float fs = layout.FontSize, lineH = fs * 1.35f;
        using var font = new SKFont(SkiaFonts.PlexRegular, fs);
        using var bold = new SKFont(SkiaFonts.PlexSemiBold, fs);
        using var small = new SKFont(SkiaFonts.PlexRegular, fs * 0.85f);
        using var text = new SKPaint { IsAntialias = true, Color = st.LabelInk };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, Color = st.LabelInk };

        for (int k = 0; k < panels.Count && k < layout.Views.Count; k++)
        {
            var rect = layout.Views[k];
            var (lo, hi) = Extent(panels[k].Scene);
            PaintView(canvas, panels[k].Scene, rect, layout.Scale, new Uv((lo.U + hi.U) / 2, (lo.V + hi.V) / 2), style, st);
            Em3dText.Draw(canvas, style.TextAsPaths, panels[k].Title, rect.MidX, rect.Bottom + lineH * 1.25f, SKTextAlign.Center, bold, text);
        }

        if (!layout.Legend.IsEmpty) DrawLegend(canvas, layout.Legend, LegendRows(panels, style), fs, lineH, style, st, font, bold, text, stroke);

        DrawBorderAndTitleBlock(canvas, layout, style, fs, lineH, bold, small, text, stroke);
    }

    private static void DrawBorderAndTitleBlock(SKCanvas canvas, Em3dSheetLayout layout, Em3dDrawingStyle style, float fs, float lineH,
                                                SKFont bold, SKFont small, SKPaint text, SKPaint stroke)
    {
        if (layout.TitleBlock.IsEmpty) return;
        var tb = layout.TitleBlock;
        float margin = tb.Left;
        stroke.StrokeWidth = 1f;
        canvas.DrawRect(new SKRect(margin, margin, layout.Width - margin, tb.Bottom), stroke);
        canvas.DrawLine(tb.Left, tb.Top, tb.Right, tb.Top, stroke);
        float y1 = tb.Top + lineH * 1.05f, y2 = y1 + lineH;
        Em3dText.Draw(canvas, style.TextAsPaths, style.DocumentName ?? "", tb.Left + fs, y1, SKTextAlign.Left, bold, text);
        string detail = "Scale " + layout.ScaleText + "   ·   orthographic views" + (style.Remark is { Length: > 0 } r ? "   ·   " + r : "");
        Em3dText.Draw(canvas, style.TextAsPaths, detail, tb.Left + fs, y2, SKTextAlign.Left, small, text);
        Em3dText.Draw(canvas, style.TextAsPaths, "circuitRF", tb.Right - fs, y1, SKTextAlign.Right, small, text);
    }

    // ── one view as a picture (Copy as Vector, Export as Vector) ──────────────────────────────

    /// <summary>The page a picture of <paramref name="scene"/> takes: its extent fitted in <paramref name="maxSide"/> on its
    /// longer side, plus <paramref name="margin"/> all round. Returns the page and its scale (page units per metre).</summary>
    public static (float W, float H, double Scale) PictureSize(Em3dScene scene, float maxSide, float margin = 12f)
    {
        var (lo, hi) = Extent(scene);
        double ew = Math.Max(hi.U - lo.U, 0), eh = Math.Max(hi.V - lo.V, 0);
        double big = Math.Max(Math.Max(ew, eh), 1e-30);
        double scale = (maxSide - 2 * margin) / big;
        float w = (float)Math.Ceiling(ew * scale + 2 * margin), h = (float)Math.Ceiling(eh * scale + 2 * margin);
        return (Math.Max(w, 2 * margin + 1), Math.Max(h, 2 * margin + 1), scale);
    }

    /// <summary>Paints <paramref name="scene"/> alone on a page made by <see cref="PictureSize"/>.</summary>
    public static void DrawPicture(SKCanvas canvas, float w, float h, double scale, Em3dScene scene, Em3dDrawingStyle style)
    {
        var st = StackupRenderTheme.FromTheme(style.Theme, style.Variant);
        if (!style.Transparent) canvas.Clear(SKColors.White);
        var (lo, hi) = Extent(scene);
        PaintView(canvas, scene, new SKRect(0, 0, w, h), scale, new Uv((lo.U + hi.U) / 2, (lo.V + hi.V) / 2), style, st);
    }

    // ── painting ──────────────────────────────────────────────────────────────────────────────

    private static SKColor Fill(Em3dDrawingStyle style, StackupRenderTheme st, Em3dScene scene, string obj, Em3dRole role, string material)
        => role switch
        {
            Em3dRole.Conductor => style.ObjectColours.TryGetValue(obj, out var c) ? c.WithAlpha(0xFF) : st.BandEdge,
            Em3dRole.Air       => st.Background,
            _                  => Em3dSectionRenderer.DielectricFill(st, scene.DielectricMaterials, material),
        };

    private static void PaintView(SKCanvas canvas, Em3dScene scene, SKRect rect, double scale, Uv centre,
                                  Em3dDrawingStyle style, StackupRenderTheme st)
    {
        SKPoint Map(Uv q) => new((float)(rect.MidX + (q.U - centre.U) * scale), (float)(rect.MidY - (q.V - centre.V) * scale));
        bool outline = scene.View.Kind is Em3dViewKind.Projection or Em3dViewKind.Iso;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var dash = SKPathEffect.CreateDash([3f, 2f], 0);

        foreach (var r in scene.Regions)
        {
            if (r.Role == Em3dRole.Air) continue;
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            if (r.CircleCentre is { } c) { var m = Map(c); path.AddCircle(m.X, m.Y, (float)(r.CircleRadius * scale)); }
            foreach (var ring in r.Rings) if (ring.Count >= 2) path.AddPoly([.. ring.Select(Map)], close: true);
            var colour = Fill(style, st, scene, r.Object, r.Role, r.Material);
            fill.Color = colour;
            canvas.DrawPath(path, fill);
            stroke.Color = Em3dSectionRenderer.Darker(colour);
            stroke.StrokeWidth = 0.75f;
            stroke.PathEffect = null;
            canvas.DrawPath(path, stroke);
        }

        foreach (var l in scene.Lines)
        {
            if (l.Role == Em3dRole.Air) continue;
            var colour = Fill(style, st, scene, l.Object, l.Role, l.Material);
            var ink = l.Role == Em3dRole.Conductor ? colour : st.BandEdge.WithAlpha(0xFF);
            if (l.Hidden)
            {
                stroke.Color = ink.WithAlpha(0x8C);
                stroke.StrokeWidth = 0.5f;
                stroke.PathEffect = dash;
            }
            else
            {
                stroke.Color = ink;
                stroke.PathEffect = null;
                stroke.StrokeWidth = !outline && l.IsSheet ? Math.Max(1.5f, (float)(l.WidthM * scale))
                                   : l.Role == Em3dRole.Conductor ? 1f : 0.75f;
            }
            canvas.DrawLine(Map(l.A), Map(l.B), stroke);
        }
    }

    private static List<(string Material, SKColor Colour, Em3dRole Role)> LegendRows(IReadOnlyList<Em3dDrawingPanel> panels, Em3dDrawingStyle style)
    {
        var st = StackupRenderTheme.FromTheme(style.Theme, style.Variant);
        // One row per MATERIAL: two gold objects on different layers are drawn in their layers' colours, and listing gold
        // once per colour reads as three materials.
        var rows = new List<(string, SKColor, Em3dRole)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in panels)
            foreach (var (obj, role, material) in p.Scene.Regions.Select(r => (r.Object, r.Role, r.Material))
                         .Concat(p.Scene.Lines.Select(l => (l.Object, l.Role, l.Material))))
            {
                if (role == Em3dRole.Air) continue;
                if (seen.Add(material)) rows.Add((material, Fill(style, st, p.Scene, obj, role, material), role));
            }
        return rows;
    }

    private static void DrawLegend(SKCanvas canvas, SKRect box, List<(string Material, SKColor Colour, Em3dRole Role)> rows,
                                   float fs, float lineH, Em3dDrawingStyle style, StackupRenderTheme st,
                                   SKFont font, SKFont bold, SKPaint text, SKPaint stroke)
    {
        float lx = box.Left + fs, ly = box.Top + fs;
        Em3dText.Draw(canvas, style.TextAsPaths, "Materials", lx, ly + fs, SKTextAlign.Left, bold, text);
        ly += lineH * 1.4f;
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        int fit = (int)Math.Max(0, (box.Bottom - ly - lineH) / lineH);
        int shown = rows.Count > fit ? Math.Max(0, fit - 1) : rows.Count;
        foreach (var (material, colour, role) in rows.Take(shown))
        {
            var swatch = SKRect.Create(lx, ly, fs, fs);
            fill.Color = colour;
            canvas.DrawRect(swatch, fill);
            stroke.Color = Em3dSectionRenderer.Darker(colour);
            stroke.StrokeWidth = 0.75f;
            canvas.DrawRect(swatch, stroke);
            Em3dText.Draw(canvas, style.TextAsPaths, material, lx + fs * 1.5f, ly + fs * 0.85f, SKTextAlign.Left, font, text);
            ly += lineH;
        }
        if (rows.Count > shown)
            Em3dText.Draw(canvas, style.TextAsPaths, $"+{rows.Count - shown} more", lx, ly + fs * 0.85f, SKTextAlign.Left, font, text);
        stroke.Color = st.LabelInk;
    }

    // ── encoding ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A page as SVG text, repaired as every SVG this repository writes is (SvgFontNormalizer.RepairPositionLists).</summary>
    public static string Svg(float w, float h, Action<SKCanvas> draw)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, w, h), stream)) draw(canvas);
        return SvgFontNormalizer.RepairPositionLists(Encoding.UTF8.GetString(stream.DetachAsData().ToArray()));
    }

    /// <summary>A page as PDF; the faces are embedded by Skia.</summary>
    public static byte[] Pdf(float w, float h, Action<SKCanvas> draw)
    {
        var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
        using var stream = new SKDynamicMemoryWStream();
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            var canvas = document.BeginPage(w, h);
            draw(canvas);
            document.EndPage();
            document.Close();
        }
        return stream.DetachAsData().ToArray();
    }

    /// <summary>A page as PNG at <paramref name="pixelsPerUnit"/> device pixels per page unit.</summary>
    public static byte[] Png(float w, float h, float pixelsPerUnit, Action<SKCanvas> draw)
    {
        int pw = Math.Max(1, (int)Math.Ceiling(w * pixelsPerUnit)), ph = Math.Max(1, (int)Math.Ceiling(h * pixelsPerUnit));
        using var bitmap = new SKBitmap(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap)) { canvas.Scale(pixelsPerUnit); draw(canvas); }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? [];
    }
}
