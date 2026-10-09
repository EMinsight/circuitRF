// The characters a silkscreen glyph is matched against — brief-artsch-10-silkscreen-ocr.md R-as10-3, R-as10-5.
//
// Two kinds of template, both normalised as StrokeGlyphs normalises a glyph (cap height 1, baseline 0, centred):
//   - BUILT IN: generated from a public-domain stroke font, carried as an embedded resource with its licence beside it
//     in the tree (src/Design/resources/silkscreen-glyphs/), plus the few variants of it CAD plotter fonts commonly
//     draw instead, built from the font's own strokes. CAD stroke fonts are close relatives of it.
//   - TAUGHT: glyphs a user corrected in the parts table and asked to learn, kept in the per-user state directory
//     (silkscreen-glyphs/taught.json) and used on every later run. Nothing is written to a workspace.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>One template: a character and its strokes.</summary>
/// <param name="Source">Where it came from: the font's file name, or <see cref="GlyphTemplates.TaughtSource"/>.</param>
public sealed record GlyphTemplate(char Char, Glyph Glyph, string Source);

/// <summary>A set of templates.</summary>
public sealed class GlyphTemplates
{
    /// <summary>The per-user folder taught glyphs are kept in (under <see cref="UserStateDirectory"/>).</summary>
    public const string TaughtFolder = "silkscreen-glyphs";

    /// <summary>The file in it.</summary>
    public const string TaughtFile = "taught.json";

    /// <summary>The <see cref="GlyphTemplate.Source"/> of a taught glyph.</summary>
    public const string TaughtSource = "taught";

    private const string FontResource = "CircuitRF.Design.SilkscreenGlyphs.hershey-roman-simplex.txt";
    private const string FontSource = "hershey-roman-simplex";
    private const string VariantResource = "CircuitRF.Design.SilkscreenGlyphs.hershey-variants.txt";
    private const string VariantSource = "hershey-variants";

    // The font in its own units: y down, the cap line at -12 and the baseline at 9.
    private const double FontBaseline = 9, FontCapHeight = 21;

    private readonly Dictionary<char, (int Left, int Right, List<double[]> Strokes)> _font;

    private GlyphTemplates(IReadOnlyList<GlyphTemplate> all, Dictionary<char, (int, int, List<double[]>)> font)
    {
        All = all;
        _font = font;
    }

    /// <summary>Every template.</summary>
    public IReadOnlyList<GlyphTemplate> All { get; }

    private static readonly Lazy<GlyphTemplates> BuiltInSet = new(LoadBuiltIn);

    /// <summary>The built-in font's templates alone.</summary>
    public static GlyphTemplates BuiltIn => BuiltInSet.Value;

    /// <summary>The built-in templates and whatever this user has taught, read from the per-user state directory —
    /// what a recognition matches against, in the GUI, the CLI and the MCP tool alike.</summary>
    public static GlyphTemplates ForUser() => BuiltIn.With(ReadTaught(UserStateDirectory.SubDir(TaughtFolder)));

    /// <summary>This set with <paramref name="more"/> added.</summary>
    public GlyphTemplates With(IEnumerable<GlyphTemplate> more)
    {
        var extra = more.ToList();
        return extra.Count == 0 ? this : new GlyphTemplates([.. All, .. extra], _font);
    }

    /// <summary>
    /// A taught glyph matches only within this, in cap heights. It is the board's own font, drawn by the same plotter
    /// instructions every time, so the same character matches it to within rounding (0.000 on a field board, where the
    /// font's other characters stood 0.017 and more away). A looser match is another character in the same style —
    /// and taken, it would read every glyph of that font as the few characters taught.
    /// </summary>
    public const double TaughtReach = 0.01;

    /// <summary>Every template's distance from <paramref name="glyph"/>, nearest first, each character once (its
    /// nearest template). A taught template farther than <see cref="TaughtReach"/> takes no part.</summary>
    public IReadOnlyList<GlyphMatch> Match(Glyph glyph)
    {
        var best = new Dictionary<char, double>();
        foreach (var t in All)
        {
            double d = Glyph.Distance(glyph, t.Glyph);
            if (d > TaughtReach && t.Source == TaughtSource) continue;
            if (!best.TryGetValue(t.Char, out double was) || d < was) best[t.Char] = d;
        }
        return [.. best.Select(kv => new GlyphMatch(kv.Key, kv.Value)).OrderBy(m => m.Distance).ThenBy(m => m.Char)];
    }

    /// <summary>
    /// <paramref name="text"/> drawn in the built-in font as centre-line strokes: reading along +x from x = 0, the
    /// baseline on y = 0, cap height <paramref name="capHeight"/>, each character advancing by the font's own bounds.
    /// A space advances by two thirds of the cap height. What a CAD tool's plotter font writes, for tests and for
    /// anyone checking a reading by eye.
    /// </summary>
    public IReadOnlyList<double[]> Draw(string text, double capHeight)
    {
        double s = capHeight / FontCapHeight, pen = 0;
        var strokes = new List<double[]>();
        foreach (char ch in text)
        {
            if (!_font.TryGetValue(ch, out var g)) { pen += 2.0 / 3 * capHeight; continue; }
            foreach (var st in g.Strokes)
            {
                var xy = new double[st.Length];
                for (int i = 0; i + 1 < st.Length; i += 2)
                {
                    xy[i] = pen + (st[i] - g.Left) * s;
                    xy[i + 1] = (FontBaseline - st[i + 1]) * s;
                }
                strokes.Add(xy);
            }
            pen += (g.Right - g.Left) * s;
        }
        return strokes;
    }

    // ── the built-in font ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The font, and the variants of it CAD plotter fonts commonly draw instead (a flagless 1, a serifed 1, a
    /// round-topped 3) — templates only: <see cref="Draw"/> writes the font itself.</summary>
    private static GlyphTemplates LoadBuiltIn()
    {
        var font = new Dictionary<char, (int, int, List<double[]>)>();
        var templates = new List<GlyphTemplate>();
        foreach (var (ch, left, right, strokes) in ReadFont(FontResource))
        {
            font[ch] = (left, right, strokes);
            templates.Add(new GlyphTemplate(ch, Normalised(strokes), FontSource));
        }
        foreach (var (ch, _, _, strokes) in ReadFont(VariantResource))
            templates.Add(new GlyphTemplate(ch, Normalised(strokes), VariantSource));
        return new GlyphTemplates(templates, font);
    }

    private static IEnumerable<(char Char, int Left, int Right, List<double[]> Strokes)> ReadFont(string resource)
    {
        using var stream = typeof(GlyphTemplates).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"the embedded stroke font '{resource}' is missing from {typeof(GlyphTemplates).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var glyphs = new List<(char, int, int, List<double[]>)>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line[2..].Split(' ', 3);
            var strokes = parts[2].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(st => st.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .SelectMany(p => p.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)))
                                .ToArray())
                .ToList();
            glyphs.Add((line[0], int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), strokes));
        }
        return glyphs;
    }

    /// <summary>Font-unit strokes as a glyph: cap height 1, baseline 0, y up, centred on the glyph's own box.</summary>
    private static Glyph Normalised(List<double[]> strokes)
    {
        double minX = strokes.SelectMany(Xs).Min(), maxX = strokes.SelectMany(Xs).Max(), cx = (minX + maxX) / 2;
        return new Glyph([.. strokes.Select(st =>
        {
            var n = new double[st.Length];
            for (int i = 0; i + 1 < st.Length; i += 2)
            {
                n[i] = (st[i] - cx) / FontCapHeight;
                n[i + 1] = (FontBaseline - st[i + 1]) / FontCapHeight;
            }
            return n;
        })]);
    }

    private static IEnumerable<double> Xs(double[] s) { for (int i = 0; i < s.Length; i += 2) yield return s[i]; }

    // ── taught glyphs ───────────────────────────────────────────────────────────────────────────────────

    private sealed record TaughtFileModel(int Format, List<TaughtGlyph> Glyphs);
    private sealed record TaughtGlyph(string Char, List<double[]> Strokes);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>The glyphs taught in <paramref name="directory"/>; none when it has no file or the file cannot be
    /// read (a damaged file costs the taught glyphs, never a recognition).</summary>
    public static IReadOnlyList<GlyphTemplate> ReadTaught(string? directory)
    {
        if (directory is null) return [];
        string path = Path.Combine(directory, TaughtFile);
        if (!File.Exists(path)) return [];
        try
        {
            var model = JsonSerializer.Deserialize<TaughtFileModel>(File.ReadAllText(path), Json);
            return model?.Glyphs is not { } glyphs ? [] :
                [.. glyphs.Where(g => g.Char is { Length: 1 } && g.Strokes is { Count: > 0 })
                          .Select(g => new GlyphTemplate(g.Char[0], new Glyph(g.Strokes), TaughtSource))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Adds <paramref name="glyphs"/> to the taught set in <paramref name="directory"/> (R-as10-5), creating it; a
    /// glyph already taught as the same character with the same strokes is not added twice. Returns how many were
    /// added.
    /// </summary>
    public static int Learn(string directory, IEnumerable<(char Char, Glyph Glyph)> glyphs)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var known = ReadTaught(directory).ToList();
        var list = known.Select(t => new TaughtGlyph(t.Char.ToString(), [.. t.Glyph.Strokes])).ToList();
        int added = 0;
        foreach (var (ch, glyph) in glyphs)
        {
            if (known.Any(t => t.Char == ch && Glyph.Distance(t.Glyph, glyph) < 1e-9)) continue;
            list.Add(new TaughtGlyph(ch.ToString(), [.. glyph.Strokes.Select(s => s.Select(v => Math.Round(v, 5)).ToArray())]));
            known.Add(new GlyphTemplate(ch, glyph, TaughtSource));
            added++;
        }
        if (added == 0) return 0;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, TaughtFile);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new TaughtFileModel(1, list), Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return added;
    }
}
