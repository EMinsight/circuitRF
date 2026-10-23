using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// Replaces every <c>&lt;text&gt;</c> in an SVG Skia wrote with the outlines of the glyphs it names, so
/// the file draws identically in any viewer whether or not that viewer has the font.
///
/// <para><b>The defect it removes.</b> Skia's SVG device writes text as live <c>&lt;text&gt;</c> naming
/// a family — <c>font-family="IBM Plex Sans"</c> — and nothing else. A viewer that does not have the
/// family installed, or an importer that ignores <c>@font-face</c>, substitutes a face of its own
/// (usually a serif) and says nothing: the picture still draws, with different letter shapes and
/// widths. That is exactly the case of an SVG handed to another tool, which is what <c>render</c> and
/// <c>plot</c> write SVG for.</para>
///
/// <para><b>Why after the fact, rather than a Skia flag.</b> Skia's own SVG canvas has a
/// convert-text-to-paths flag, but SkiaSharp 3.119 exposes only the flagless
/// <c>sk_svgcanvas_create_with_stream</c> (checked against the native library's exports), so the
/// conversion is done here, on the finished document, as <see cref="SvgFontNormalizer"/>'s repairs
/// are.</para>
///
/// <para><b>The glyphs are the renderer's own.</b> Each run's face is resolved through
/// <see cref="SvgFontNormalizer.Resolve"/> to one of the faces embedded in this assembly — the ones
/// <see cref="SkiaFonts"/> hands every renderer — and each glyph's outline comes from Skia's own
/// <see cref="SKFont.GetGlyphPath(ushort)"/> at the position Skia wrote for it. A run whose family is
/// not one of those faces (a platform font Skia fell back to for an uncovered glyph) is <b>left as
/// text and reported</b>, never drawn in a guessed face.</para>
///
/// <para>Everything else on the run — its fill, its transform, any attribute this class does not
/// read — moves to the <c>&lt;g&gt;</c> that replaces it, so nothing but the letters changes form.</para>
/// </summary>
public static class SvgTextOutliner
{
    private static readonly Regex TextTagRx =
        new(@"<text(?<attrs>[^>]*)>(?<body>.*?)</text>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex AttrRx = new(@"(?<name>[\w:-]+)\s*=\s*""(?<value>[^""]*)""", RegexOptions.Compiled);

    /// <summary>The attributes that describe the TEXT and are consumed by outlining; every other one
    /// is carried onto the replacement group.</summary>
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "x", "y", "font-family", "font-size", "font-weight", "font-style",
    };

    /// <summary>
    /// Outlines every run whose face resolves to an embedded one. <paramref name="unresolved"/> lists
    /// each family that did not, once, in the order met; those runs are left as they were.
    /// </summary>
    public static string Outline(string svg, out IReadOnlyList<string> unresolved)
    {
        var missed = new List<string>();
        var fonts = new Dictionary<(SKTypeface, float), SKFont>();
        try
        {
            string result = TextTagRx.Replace(svg, m =>
            {
                var attrs = AttrRx.Matches(m.Groups["attrs"].Value)
                                  .Select(a => (Name: a.Groups["name"].Value, Value: a.Groups["value"].Value))
                                  .ToList();
                string Get(string name) => attrs.FirstOrDefault(a => a.Name == name).Value ?? "";

                string family = Get("font-family");
                var face = Face(family, Get("font-weight"), Get("font-style"));
                if (face is null)
                {
                    if (!missed.Contains(family)) missed.Add(family);
                    return m.Value;
                }

                string text = Body(m.Groups["body"].Value);
                if (!float.TryParse(Get("font-size"), NumberStyles.Float, CultureInfo.InvariantCulture, out float size)
                    || !(size > 0))
                    return m.Value;                    // not a run this class can place; leave it exactly as written

                if (!fonts.TryGetValue((face, size), out var font))
                    fonts[(face, size)] = font = new SKFont(face, size);

                ushort[] glyphs = font.GetGlyphs(text);
                float[] xs = Numbers(Get("x")), ys = Numbers(Get("y"));
                if (glyphs.Length == 0 || xs.Length == 0 || ys.Length == 0) return Group(attrs, "");

                // Skia writes one x per glyph; a single x is a run laid out from its origin by advance.
                if (xs.Length < glyphs.Length)
                {
                    float[] advances = font.GetGlyphWidths(glyphs);
                    var laid = new float[glyphs.Length];
                    float x = xs[0];
                    for (int i = 0; i < glyphs.Length; i++) { laid[i] = x; x += advances[i]; }
                    xs = laid;
                }

                using var outline = new SKPath();
                for (int i = 0; i < glyphs.Length; i++)
                {
                    using var glyph = font.GetGlyphPath(glyphs[i]);
                    if (glyph is null || glyph.IsEmpty) continue;
                    outline.AddPath(glyph, xs[i], ys[Math.Min(i, ys.Length - 1)]);
                }
                return Group(attrs, outline.IsEmpty ? "" : outline.ToSvgPathData());
            });

            unresolved = missed;
            return result;
        }
        finally
        {
            foreach (var f in fonts.Values) f.Dispose();
        }
    }

    /// <summary>The run as a group carrying every attribute outlining did not consume, around one path.</summary>
    private static string Group(List<(string Name, string Value)> attrs, string pathData)
    {
        var sb = new StringBuilder("<g");
        foreach (var (name, value) in attrs)
            if (!TextAttributes.Contains(name)) sb.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
        sb.Append('>');
        if (pathData.Length > 0) sb.Append("<path d=\"").Append(pathData).Append("\"/>");
        return sb.Append("</g>").ToString();
    }

    /// <summary>
    /// The run's characters. Skia's pretty-printed XML puts the text on its own indented line, so a
    /// leading newline-plus-indent and a trailing newline-plus-indent are layout, not content; spaces
    /// inside the run are content and are kept.
    /// </summary>
    private static string Body(string raw)
    {
        string s = Regex.Replace(raw, @"^\r?\n[ \t]*", "");
        s = Regex.Replace(s, @"\r?\n[ \t]*$", "");
        return WebUtility.HtmlDecode(s);
    }

    private static float[] Numbers(string list)
        => list.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
               .Select(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : float.NaN)
               .Where(float.IsFinite)
               .ToArray();

    /// <summary>
    /// The embedded face a run was drawn with, or null when its family is not one this assembly ships.
    /// The family list goes through <see cref="SvgFontNormalizer.Resolve"/>, which reads the weight and
    /// slant out of Skia's full-name form (<c>"IBM Plex Sans SemiBold, IBM Plex Sans"</c>); otherwise the
    /// run's own <c>font-weight</c> and <c>font-style</c> say which.
    /// </summary>
    private static SKTypeface? Face(string familyAttr, string weightAttr, string styleAttr)
    {
        if (familyAttr.Length == 0) return null;
        string family; int? weight; string? style;
        try { (family, weight, style, string? substituted) = SvgFontNormalizer.Resolve(familyAttr); if (substituted is not null) return null; }
        catch (InvalidOperationException) { return null; }   // a face-name word nobody can weigh: leave the run as text

        int w = weight
             ?? (int.TryParse(weightAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wa) ? wa
                : weightAttr.Equals("bold", StringComparison.OrdinalIgnoreCase) ? 700 : 400);
        bool italic = (style ?? styleAttr) is "italic" or "oblique";

        return family switch
        {
            "IBM Plex Sans" when italic => SkiaFonts.RealPlexItalic,   // the one italic Plex face embedded
            "IBM Plex Sans" => w <= 350 ? SkiaFonts.RealPlexLight
                             : w <= 500 ? SkiaFonts.RealPlexRegular
                             : w <= 650 ? SkiaFonts.RealPlexSemiBold
                             : SkiaFonts.RealPlexBold,
            "DejaVu Sans" => (w >= 600, italic) switch
            {
                (true, true)  => SkiaFonts.RealDejaVuBoldOblique,
                (true, false) => SkiaFonts.RealDejaVuBold,
                (false, true) => SkiaFonts.RealDejaVuOblique,
                _             => SkiaFonts.RealDejaVuRegular,
            },
            _ => null,                                                   // a shipped family this assembly does not embed (Inter)
        };
    }
}
