// The stroke font every layout label is drawn in — brief-silkscreen-stroke-font.md R-ssf-1, R-ssf-2.
//
// Hershey Roman Simplex, carried as plain embedded data (src/Design/resources/stroke-font/, its licence beside it):
// one pen of one width, a few centre-line strokes per glyph — what a board shop's plotter font writes, and what
// AS-10's silkscreen reader reads back. Because it is data and not a TrueType face, a label laid out here is the same
// geometry in the application, in `circuitrf render`/`convert`, and in every test: nothing loads through an asset
// system, so there is no headless fallback face to differ from.
//
// This file lays TEXT out, in the text's own frame. Where a LABEL's text lands — alignment, rotation, position, the
// pen — is StrokeText, the one place a label becomes geometry.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Layout.Text;

/// <summary>One glyph in font units: y down, the cap line at −12 and the baseline at 9; <see cref="Left"/> and
/// <see cref="Right"/> bound its advance.</summary>
public sealed record StrokeGlyph(int CodePoint, int Left, int Right, IReadOnlyList<double[]> Strokes);

/// <summary>Text laid out by <see cref="StrokeFont.Layout"/>.</summary>
/// <param name="Strokes">Centre-line polylines as flat x,y pairs, in the text's own frame: baseline on y = 0, y up,
/// reading along +x from x = 0.</param>
/// <param name="Advance">The pen's travel over the whole string.</param>
/// <param name="Unknown">Characters the font has no glyph for, each drawn as a hollow box (D5).</param>
public sealed record StrokeTextLayout(IReadOnlyList<double[]> Strokes, double Advance, int Unknown);

/// <summary>Hershey Roman Simplex: printable ASCII plus <c>Ω µ ° ±</c>.</summary>
public static class StrokeFont
{
    internal const string FontResource = "CircuitRF.Design.StrokeFont.hershey-roman-simplex.txt";

    /// <summary>The cap height in font units (cap line −12, baseline 9).</summary>
    public const double CapUnits = 21;

    /// <summary>The baseline's y in font units.</summary>
    public const double BaselineUnits = 9;

    /// <summary>How far the font's descenders reach below the baseline, in cap heights (a <c>g</c> to y = 16).</summary>
    public const double DescentOverCap = 7.0 / CapUnits;

    /// <summary>The advance of a character the font has no glyph for, in font units — a digit's.</summary>
    private const double BoxAdvanceUnits = 20;

    /// <summary>The box's inset from each side of its advance, in font units.</summary>
    private const double BoxInsetUnits = 3;

    /// <summary>D6: an italic label leans by this, in degrees.</summary>
    public const double ItalicShearDegrees = 12;

    /// <summary>D6: a condensed label's advance — and its glyphs' width — is scaled by this.</summary>
    public const double CondensedScale = 0.8;

    private static readonly Lazy<IReadOnlyDictionary<int, StrokeGlyph>> GlyphSet =
        new(() => ReadGlyphs(FontResource).ToDictionary(g => g.CodePoint));

    /// <summary>Every glyph, by code point.</summary>
    public static IReadOnlyDictionary<int, StrokeGlyph> Glyphs => GlyphSet.Value;

    /// <summary>True when <paramref name="ch"/> draws as itself rather than as a box.</summary>
    public static bool Has(Rune ch) => Glyphs.ContainsKey(ch.Value);

    /// <summary>
    /// <paramref name="text"/> as centre-line strokes at cap height <paramref name="capHeight"/>, in the text's own
    /// frame (baseline y = 0, y up, reading +x from x = 0), each glyph advancing by the font's own bounds. A character
    /// the font lacks advances like a digit and draws as a hollow box from the baseline to the cap line, and is
    /// counted. <paramref name="style"/> applies D6's geometry — the italic lean and the condensed width; Bold is a
    /// pen width, which is <see cref="StrokeText"/>'s.
    /// </summary>
    public static StrokeTextLayout Layout(string text, LabelFontStyle style, double capHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        double s = capHeight / CapUnits;
        double sx = style == LabelFontStyle.Condensed ? s * CondensedScale : s;
        double shear = style == LabelFontStyle.Italic ? Math.Tan(ItalicShearDegrees * Math.PI / 180) : 0;
        double pen = 0;
        int unknown = 0;
        var strokes = new List<double[]>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Glyphs.TryGetValue(rune.Value, out var g))
            {
                foreach (var st in g.Strokes)
                {
                    var xy = new double[st.Length];
                    for (int i = 0; i + 1 < st.Length; i += 2)
                    {
                        double y = (BaselineUnits - st[i + 1]) * s;
                        xy[i] = pen + (st[i] - g.Left) * sx + y * shear;
                        xy[i + 1] = y;
                    }
                    strokes.Add(xy);
                }
                pen += (g.Right - g.Left) * sx;
                continue;
            }

            unknown++;
            double x0 = pen + BoxInsetUnits * sx, x1 = pen + (BoxAdvanceUnits - BoxInsetUnits) * sx;
            strokes.Add([x0, 0, x1, 0, x1 + capHeight * shear, capHeight, x0 + capHeight * shear, capHeight, x0, 0]);
            pen += BoxAdvanceUnits * sx;
        }
        return new StrokeTextLayout(strokes, pen, unknown);
    }

    /// <summary>
    /// A glyph file in this font's line format, read out of this assembly: <c>&lt;char&gt; &lt;left&gt; &lt;right&gt;
    /// &lt;stroke&gt; | &lt;stroke&gt; | …</c>, a stroke being x,y points in font units. <c>&lt;char&gt;</c> is the
    /// character, or <c>U+XXXX</c> (the space, and <c>#</c>, which would otherwise read as a comment). A line
    /// starting <c>#</c> is a comment. AS-10's variants file is read through this too.
    /// </summary>
    internal static IReadOnlyList<StrokeGlyph> ReadGlyphs(string resource)
    {
        using var stream = typeof(StrokeFont).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"the embedded stroke font '{resource}' is missing from {typeof(StrokeFont).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var glyphs = new List<StrokeGlyph>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', 4);
            string token = parts[0];
            int codePoint = token.StartsWith("U+", StringComparison.Ordinal) && token.Length > 2
                ? int.Parse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : char.ConvertToUtf32(token, 0);
            var strokes = parts.Length < 4 ? [] : parts[3]
                .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(st => st.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .SelectMany(p => p.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)))
                                .ToArray())
                .ToList();
            glyphs.Add(new StrokeGlyph(codePoint, int.Parse(parts[1], CultureInfo.InvariantCulture),
                                       int.Parse(parts[2], CultureInfo.InvariantCulture), strokes));
        }
        return glyphs;
    }
}
