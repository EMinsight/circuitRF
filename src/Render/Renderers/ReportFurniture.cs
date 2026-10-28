// The furniture every circuitRF review report is built from — the page, its colours and type, the running
// header and footer, wrapped text, a table's header and rows, the numbered finding marker, the accepted check,
// the counted badge and the scale bar. Factored out of TraceImpedanceReportDocument when the Layout Review's
// report (brief-review-7) needed the same page: two reports filed side by side must look like one product, and
// two copies of a marker drift the first time either is touched.

using SkiaSharp;

namespace CircuitRF.Render;

internal static class ReportFurniture
{
    // A4 landscape, in points.
    public const int PageW = 842;
    public const int PageH = 595;
    public const float Margin = 32f;

    /// <summary>Where a page's content must end: above the footer rule.</summary>
    public const float Bottom = PageH - Margin - 14;

    public static readonly SKColor Ink        = new(0x1d, 0x23, 0x2b);
    public static readonly SKColor Muted      = new(0x5f, 0x6b, 0x78);
    public static readonly SKColor Rule       = new(0xd5, 0xdb, 0xe1);
    public static readonly SKColor Band       = new(0xf3, 0xf5, 0xf7);
    public static readonly SKColor Accent     = new(0x1f, 0x5f, 0xa8);
    public static readonly SKColor CopperFill = new(0xdc, 0xdf, 0xe3);
    public static readonly SKColor CopperEdge = new(0xb9, 0xbf, 0xc6);
    public static readonly SKColor PassInk    = new(0x1e, 0x7b, 0x3c);
    public static readonly SKColor FailInk    = new(0xb4, 0x23, 0x18);
    public static readonly SKColor WarnInk    = new(0xb0, 0x6a, 0x00);
    public static readonly SKColor Stripe     = new(0xfa, 0xfb, 0xfc);
    /// <summary>An accepted finding's marker: grey, with a check — kept, not removed.</summary>
    public static readonly SKColor AcceptedMark = new(0x8a, 0x91, 0x99);

    public static SKFont Font(SKTypeface face, float size) =>
        new(face, size) { Edging = SKFontEdging.Antialias, Subpixel = false };

    public static readonly SKFont BadgeFont = Font(SkiaFonts.PlexSemiBold, 4.6f);

    public static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = c };
    public static SKPaint Stroke(SKColor c, float w) =>
        new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = c, StrokeWidth = w };

    // ── the page ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A white page with the running header: the accent tick, the report's title left and the section
    /// right. Returns where the content starts.</summary>
    public static float BeginPage(SKCanvas c, string title, string section, SKFont small)
    {
        using (var bg = Fill(SKColors.White)) c.DrawRect(0, 0, PageW, PageH, bg);
        using var muted = Fill(Muted);
        using var accent = Fill(Accent);
        c.DrawRect(Margin, Margin - 10, 26, 3, accent);
        c.DrawText(title, Margin + 32, Margin - 5, SKTextAlign.Left, small, muted);
        c.DrawText(section, PageW - Margin, Margin - 5, SKTextAlign.Right, small, muted);
        return Margin + 12;
    }

    /// <summary>The footer: a rule, <paramref name="text"/> left and the page number right.</summary>
    public static void Footer(SKCanvas c, string text, int page, SKFont small)
    {
        using var muted = Fill(Muted);
        using var rule = Stroke(Rule, 0.6f);
        c.DrawLine(Margin, PageH - Margin + 2, PageW - Margin, PageH - Margin + 2, rule);
        c.DrawText(text, Margin, PageH - Margin + 13, SKTextAlign.Left, small, muted);
        c.DrawText($"Page {page}", PageW - Margin, PageH - Margin + 13, SKTextAlign.Right, small, muted);
    }

    /// <summary>A one-line notice in a pale orange band — a stale or cancelled report says so in one. Returns the
    /// y under it.</summary>
    public static float Notice(SKCanvas c, string text, float x, float y, float width, SKFont bold)
    {
        using var warn = Fill(new SKColor(0xfd, 0xf1, 0xe6));
        using var warnInk = Fill(new SKColor(0x9a, 0x4a, 0x00));
        c.DrawRoundRect(new SKRect(x, y, x + width, y + 20), 3, 3, warn);
        c.DrawText(Clip(text, bold, width - 16), x + 8, y + 13.5f, SKTextAlign.Left, bold, warnInk);
        return y + 28;
    }

    // ── text ────────────────────────────────────────────────────────────────────────────────────

    public static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureText(candidate) <= width || line.Length == 0) line = candidate;
                else { lines.Add(line); line = word; }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Wrapped text from <paramref name="y"/> down; a line that would pass <paramref name="maxY"/> is
    /// not drawn, and the last one drawn ends in an ellipsis. Returns the last baseline.</summary>
    public static float Paragraph(SKCanvas c, string text, float x, float y, float width, SKFont font, SKColor color,
                                  float lead = 1.4f, float maxY = float.MaxValue)
    {
        using var ink = Fill(color);
        var lines = Wrap(text, font, width);
        for (int i = 0; i < lines.Count; i++)
        {
            if (y + font.Size * lead > maxY) break;
            y += font.Size * lead;
            bool last = i + 1 < lines.Count && y + font.Size * lead > maxY;
            c.DrawText(last ? Clip(lines[i] + " …", font, width) : lines[i], x, y, SKTextAlign.Left, font, ink);
        }
        return y;
    }

    public static string Clip(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            string c = text[..n] + "…";
            if (font.MeasureText(c) <= width) return c;
        }
        return "…";
    }

    // ── tables ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A table's header band. Returns the y under it.</summary>
    public static float TableHeader(SKCanvas c, float[] cols, string[] heads, float y, float right = PageW - Margin)
    {
        using var band = Fill(Band);
        using var muted = Fill(Muted);
        c.DrawRect(cols[0], y, right - cols[0], 15, band);
        using var headFont = Font(SkiaFonts.PlexSemiBold, 7.5f);
        for (int i = 0; i < heads.Length; i++)
            c.DrawText(heads[i], cols[i] + 3, y + 10.5f, SKTextAlign.Left, headFont, muted);
        return y + 15;
    }

    /// <summary>One table row, each cell clipped to its column, ruled underneath. Returns the y under it.</summary>
    public static float TableRow(SKCanvas c, float[] cols, string[] cells, SKColor[] colors, SKFont body, float y,
                                 SKColor? background = null, float right = PageW - Margin)
    {
        if (background is { } bg) using (var p = Fill(bg)) c.DrawRect(cols[0], y, right - cols[0], 13, p);
        for (int i = 0; i < cells.Length; i++)
        {
            float width = (i + 1 < cols.Length ? cols[i + 1] : right) - cols[i] - 6;
            using var ink = Fill(colors[Math.Min(i, colors.Length - 1)]);
            c.DrawText(Clip(cells[i], body, width), cols[i] + 3, y + 9.5f, SKTextAlign.Left, body, ink);
        }
        y += 13;
        using var rule = Stroke(Rule, 0.4f);
        c.DrawLine(cols[0], y, right, y, rule);
        return y;
    }

    // ── markers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A numbered finding marker: FILLED in its colour with a white number for a fail, HOLLOW — white
    /// inside a ring of the colour, the number in that colour — for a warning.</summary>
    public static void Marker(SKCanvas c, SKPoint at, float radius, string n, SKColor color, bool fails, float textSize)
    {
        using var f = Font(SkiaFonts.PlexSemiBold, textSize);
        if (fails)
        {
            using var disc = Fill(color);
            using var ring = Stroke(SKColors.White, 0.7f);
            c.DrawCircle(at, radius, disc);
            c.DrawCircle(at, radius, ring);
            using var white = Fill(SKColors.White);
            c.DrawText(n, at.X, at.Y + 0.35f * textSize, SKTextAlign.Center, f, white);
        }
        else
        {
            using var inside = Fill(SKColors.White);
            using var ring = Stroke(color, Math.Max(0.8f, radius * 0.22f));
            c.DrawCircle(at, radius, inside);
            c.DrawCircle(at, radius - 0.4f, ring);
            using var ink = Fill(color);
            c.DrawText(n, at.X, at.Y + 0.35f * textSize, SKTextAlign.Center, f, ink);
        }
    }

    /// <summary>An accepted finding's marker: a grey disc with a white check, where its number was — kept on the
    /// map, not removed.</summary>
    public static void Check(SKCanvas c, SKPoint at, float radius)
    {
        using var disc = Fill(AcceptedMark);
        c.DrawCircle(at, radius, disc);
        using var tick = Stroke(SKColors.White, Math.Max(0.8f, radius * 0.28f));
        tick.StrokeCap = SKStrokeCap.Round;
        using var path = new SKPath();
        path.MoveTo(at.X - 0.45f * radius, at.Y);
        path.LineTo(at.X - 0.1f * radius, at.Y + 0.4f * radius);
        path.LineTo(at.X + 0.5f * radius, at.Y - 0.4f * radius);
        c.DrawPath(path, tick);
    }

    /// <summary>A badge: a pill in place of markers that would overlap, FILLED when one of them fails and HOLLOW
    /// when they only warn, like a marker; grey when every one is accepted.</summary>
    public static void Badge(SKCanvas c, SKRect box, string label, SKColor color, bool fails, bool accepted)
    {
        var r = new SKRect(box.Left + 0.2f, box.Top + 0.2f, box.Right - 0.2f, box.Bottom - 0.2f);
        float rad = 0.5f * r.Height;
        if (fails || accepted)
        {
            using var fill = Fill(color);
            using var ring = Stroke(SKColors.White, 0.7f);
            c.DrawRoundRect(r, rad, rad, fill);
            c.DrawRoundRect(r, rad, rad, ring);
            using var white = Fill(SKColors.White);
            c.DrawText(label, r.MidX, r.MidY + 0.35f * BadgeFont.Size, SKTextAlign.Center, BadgeFont, white);
        }
        else
        {
            using var inside = Fill(SKColors.White);
            using var ring = Stroke(color, 0.95f);
            c.DrawRoundRect(r, rad, rad, inside);
            c.DrawRoundRect(new SKRect(r.Left + 0.4f, r.Top + 0.4f, r.Right - 0.4f, r.Bottom - 0.4f), rad - 0.4f, rad - 0.4f, ring);
            using var ink = Fill(color);
            c.DrawText(label, r.MidX, r.MidY + 0.35f * BadgeFont.Size, SKTextAlign.Center, BadgeFont, ink);
        }
    }

    /// <summary>A badge's width for <paramref name="label"/>: never narrower than a marker.</summary>
    public static float BadgeWidth(string label) => Math.Max(8.6f, BadgeFont.MeasureText(label) + 4.4f);

    // ── maps ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The scale bar's length on the page and the round length it stands for.</summary>
    public static (float Width, double Length) ScaleBar(double extW, double s)
    {
        double target = extW * 0.2;
        double nice = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (double m in (ReadOnlySpan<double>)[5, 2, 1]) if (m * nice <= target) { nice *= m; break; }
        return ((float)(nice * s), nice);
    }

    /// <summary>The scale bar in a map's bottom-left corner, labelled <paramref name="text"/>.</summary>
    public static void DrawScaleBar(SKCanvas c, SKRect box, float barW, string text, SKFont small)
    {
        float bx = box.Left + 10, by = box.Bottom - 10;
        using (var bar = Stroke(Ink, 1.2f))
        {
            c.DrawLine(bx, by, bx + barW, by, bar);
            c.DrawLine(bx, by - 3, bx, by + 1, bar);
            c.DrawLine(bx + barW, by - 3, bx + barW, by + 1, bar);
        }
        using var ink = Fill(Ink);
        c.DrawText(text, bx + barW + 5, by + 2.5f, SKTextAlign.Left, small, ink);
    }
}
