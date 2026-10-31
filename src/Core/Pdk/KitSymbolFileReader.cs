using System.Globalization;
using System.Text;

namespace CircuitRF.Core.Pdk;

/// <summary>What one plain-text record symbol FILE was found to declare.</summary>
/// <param name="Pins">In declaration order — see the reader's note on ordering.</param>
/// <param name="Parameters">From the symbol's own default template, in declaration order.</param>
/// <param name="TypeWord">
/// The kit's own word for what kind of device this is, or empty. Used as the palette category,
/// because it is the kit's grouping rather than one circuitRF invents.
/// </param>
/// <param name="Body">
/// The DRAWN artwork, in the file's own coordinates. Possibly empty — a symbol may declare
/// terminals and nothing else — but never null for a file this reader recognised, which is what
/// lets a consumer tell a part that came from a drawing apart from one that came from a symbol
/// library and has no drawing at all.
/// </param>
/// <param name="ExtraTerminals">
/// Template attributes the symbol's netlist line places AMONG ITS TERMINALS, after the drawn pins —
/// a body connection written as <c>body=sub!</c> is a net, not a value. Empty for nearly every symbol.
/// </param>
/// <param name="Element">
/// The netlist line's element letter, upper-case: the template's <c>spiceprefix</c> when it sets one,
/// else the first letter of its <c>name</c>. <c>X</c> is a subcircuit call; <c>D</c>, <c>Q</c>,
/// <c>R</c>, <c>C</c> and <c>M</c> are primitive elements on a model card. Null when the template
/// names no instance.
/// </param>
/// <param name="CalledName">
/// What the netlist line calls — the format's model token with <c>@model</c> substituted from the
/// template, any literal prefix kept. Null when the format states no such token or it does not
/// resolve to a plain name.
/// </param>
/// <param name="ElementArguments">
/// The format's text after that token — the instance's own <c>key=value</c> words, still holding their
/// <c>@name</c> placeholders. Null when there is none.
/// </param>
public sealed record KitSymbolFile(
    IReadOnlyList<KitSymbolPin>      Pins,
    IReadOnlyList<PdkPartParameter>  Parameters,
    string                           TypeWord,
    IReadOnlyList<KitSymbolShape>    Body,
    IReadOnlyList<string>?           ExtraTerminals = null,
    string?                          Element = null,
    string?                          CalledName = null,
    string?                          ElementArguments = null);

/// <summary>
/// Reads a schematic symbol from a plain-text RECORD format: one symbol per file, each line a
/// single-letter record followed by its fields and a braced attribute block.
///
/// <para><b>This reads a FORMAT.</b> The record letters and attribute names below are the format's
/// own grammar, exactly as <see cref="KitSymbolDefinitionReader"/>'s <c>create_parm</c> is its
/// definition language's. Nothing here names a supplier, a kit, a part or a model family, and every
/// fixture that exercises it is synthetic.</para>
///
/// <para><b>It is simpler than the drawing formats beside it, and that is the point.</b> The whole
/// of what circuitRF needs — the terminals, their names, the part's parameter interface with the
/// kit's own defaults, and the drawn body — is stated in plain text with no compilation and no
/// binary records to walk. So this reader is small, and what it recovers is complete rather than
/// best-effort.</para>
///
/// <para><b>The drawn body IS read, and TEXT records deliberately are not.</b> A symbol's text in
/// this format is almost entirely SUBSTITUTION PLACEHOLDERS — the instance name, the model name,
/// one row per parameter — which circuitRF already draws itself from the placed instance. Rendering
/// them would put the placeholder tokens themselves on the schematic beside circuitRF's own correct
/// labels, saying the same things twice and one of them wrong. So the geometry is the kit's and the
/// lettering is circuitRF's.</para>
///
/// <para><b>A terminal is a rectangle record whose attributes declare a NAME, not one on a
/// particular layer.</b> The layer number is the format's display convention: it decides what colour
/// the editor draws the box, and a kit is free to choose another. The attribute is what carries the
/// meaning. Keying on the layer would read a kit that renumbered its display layers as a symbol with
/// no pins at all — which still imports, still appears in the palette, and cannot be wired to
/// anything.</para>
///
/// <para><b>Pin ORDER is declaration order, and that is stated rather than assumed.</b> The format
/// does not oblige a symbol to declare its terminals in the order the netlist lists them. Names are
/// carried for every pin, so a consumer that knows the subcircuit's own terminal order can bind by
/// name; until something does, declaration order is what a reader can honestly report. <b>Unverified
/// against a kit</b> — see this file's entry in <c>src/Core/CLAUDE.md</c>.</para>
/// </summary>
public static class KitSymbolFileReader
{
    /// <summary>A rectangle. Carries a terminal when its attributes name one, artwork otherwise.</summary>
    private const char RectangleRecord = 'B';

    /// <summary>A straight segment: <c>&lt;layer&gt; &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt;</c>.</summary>
    private const char LineRecord = 'L';

    /// <summary>
    /// A run of points: <c>&lt;layer&gt; &lt;count&gt; &lt;x1&gt; &lt;y1&gt; …</c>. Closed when it
    /// repeats its first point at the end, which is how this format states closure.
    /// </summary>
    private const char PathRecord = 'P';

    /// <summary>
    /// A circular arc: <c>&lt;layer&gt; &lt;cx&gt; &lt;cy&gt; &lt;r&gt; &lt;start&gt; &lt;sweep&gt;</c>,
    /// angles in degrees.
    /// </summary>
    private const char ArcRecord = 'A';

    /// <summary>The symbol's global attribute block: its type word and its default template.</summary>
    private const char GlobalRecord = 'K';

    /// <summary>Attribute naming a terminal. Its presence is what makes a rectangle a pin.</summary>
    private const string PinNameAttribute = "name";

    /// <summary>
    /// Attribute stating a terminal's position in the netlist's own terminal ORDER.
    ///
    /// <para><b>Optional, and that is why declaration order is still the fallback.</b> Measured on a
    /// kit: 21 of its 38 pin-bearing symbols declare it. Where every pin of a symbol has one it
    /// is authoritative and is used; where any pin lacks one the set is incomplete and ordering by it
    /// would interleave numbered and unnumbered pins arbitrarily, which is worse than the order the
    /// file was written in.</para>
    /// </summary>
    private const string PinOrderAttribute = "sim_pinnumber";

    /// <summary>Attribute carrying the symbol's default parameter assignments.</summary>
    private const string TemplateAttribute = "template";

    /// <summary>Attribute stating how an instance is written into a netlist, terminals first.</summary>
    private const string FormatAttribute = "format";

    /// <summary>The token in <see cref="FormatAttribute"/> that stands for the drawn pins, in order.</summary>
    private const string PinListToken = "@pinlist";

    /// <summary>Attribute carrying the kit's own word for what kind of device this is.</summary>
    private const string TypeAttribute = "type";

    /// <summary>Attribute marking a closed shape as solid rather than outlined.</summary>
    private const string FillAttribute = "fill";

    /// <summary>
    /// Template keys describing how an instance is WRITTEN INTO A NETLIST rather than what the
    /// device is: its own name, and the letter its netlist line is prefixed with. Offering either as
    /// a parameter puts a box in the editor for something the user cannot usefully change, beside
    /// the real parameters. Both are observed on every device of a kit.
    /// </summary>
    private static readonly HashSet<string> NetlistingKeys =
        new(StringComparer.OrdinalIgnoreCase) { "name", "spiceprefix" };

    private const long MaxFileBytes = 4 * 1024 * 1024;

    public static KitSymbolFile? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
            return Read(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the symbol, or null when the text is not one. Never throws: a file that turns out not
    /// to be a symbol is an ordinary outcome during a kit import, not an error.
    /// </summary>
    public static KitSymbolFile? Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var pins       = new List<(KitSymbolPin Pin, int Order)>();
        var parameters = new List<PdkPartParameter>();
        var body       = new List<KitSymbolShape>();
        string typeWord = "";
        IReadOnlyList<string> extraTerminals = [];
        string? element = null, called = null, arguments = null;
        bool sawRecord  = false;
        bool everyPinOrdered = true;

        foreach (var (letter, fields, attributes) in Records(text))
        {
            sawRecord = true;

            if (letter == GlobalRecord)
            {
                var attrs = ParseAttributes(attributes);
                if (attrs.TryGetValue(TypeAttribute, out var t)) typeWord = t.Value;
                if (attrs.TryGetValue(TemplateAttribute, out var tpl))
                    ReadTemplate(tpl.Value, parameters);
                if (attrs.TryGetValue(FormatAttribute, out var fmt))
                    extraTerminals = ExtraTerminalsIn(fmt.Value);
                if (tpl.Value is { Length: > 0 } templateText)
                {
                    var tplAttrs = ParseAttributes(templateText);
                    element = ElementOf(tplAttrs, fmt.Value);
                    if (fmt.Value is { Length: > 0 } formatText)
                        (called, arguments) = CallIn(formatText, tplAttrs);
                }
                continue;
            }

            if (letter is LineRecord or PathRecord or ArcRecord)
            {
                if (ReadBodyShape(letter, fields, attributes) is { } drawn) body.Add(drawn);
                continue;
            }

            if (letter != RectangleRecord) continue;

            var rect = ParseAttributes(attributes);

            // A rectangle with no terminal name is ARTWORK. Reading it as a pin-less pin would lose
            // it; skipping it entirely used to lose it too — this is where a box drawn as part of
            // the body finally lands.
            if (!rect.TryGetValue(PinNameAttribute, out var name) || name.Value.Length == 0)
            {
                if (ReadBodyShape(letter, fields, attributes) is { } box) body.Add(box);
                continue;
            }

            // Fields are <layer> <x1> <y1> <x2> <y2>. The terminal sits at the rectangle's centre,
            // which is where a wire is expected to meet it.
            if (fields.Count < 5) continue;
            if (!TryNumber(fields[1], out double x1) || !TryNumber(fields[2], out double y1) ||
                !TryNumber(fields[3], out double x2) || !TryNumber(fields[4], out double y2)) continue;

            int order = pins.Count;
            if (rect.TryGetValue(PinOrderAttribute, out var declared) &&
                int.TryParse(declared.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                order = n;
            else
                everyPinOrdered = false;

            pins.Add((new KitSymbolPin(name.Value,
                                       (int)Math.Round((x1 + x2) / 2.0),
                                       (int)Math.Round((y1 + y2) / 2.0)),
                      order));
        }

        // The netlist's own terminal order where the symbol states it, the order it was written in
        // where it does not. A stable sort, so a symbol that numbers its pins with duplicates keeps
        // the file's order among them rather than an arbitrary one.
        var ordered = everyPinOrdered && pins.Count > 0
            ? pins.OrderBy(p => p.Order).Select(p => p.Pin).ToList()
            : pins.Select(p => p.Pin).ToList();

        // No records at all means this is not the format. No PINS, though, is a real symbol that
        // happens to declare none — a title block, a decoration — and is reported as such rather
        // than as a parse failure, so the caller can tell the two apart.
        return sawRecord
            ? new KitSymbolFile(ordered, parameters, typeWord, body, extraTerminals, element, called, arguments)
            : null;
    }

    /// <summary>
    /// One drawn record as a neutral shape, or null when its fields do not read as numbers.
    ///
    /// <para>A malformed record costs ITSELF and nothing else — the rest of the symbol still
    /// imports. That is the same bargain the attribute reader already strikes, and it matters
    /// because a kit does contain the occasional damaged line.</para>
    /// </summary>
    private static KitSymbolShape? ReadBodyShape(char letter, List<string> fields, string attributes)
    {
        // Field 0 is the display layer on every one of these records. It decides what colour the
        // authoring editor drew it in and carries no meaning here — circuitRF draws a symbol in its
        // own theme colour, so the number is read past rather than kept.
        switch (letter)
        {
            case LineRecord when fields.Count >= 5:
                return TryNumber(fields[1], out double lx1) && TryNumber(fields[2], out double ly1) &&
                       TryNumber(fields[3], out double lx2) && TryNumber(fields[4], out double ly2)
                    ? new KitSymbolLine(lx1, ly1, lx2, ly2)
                    : null;

            case RectangleRecord when fields.Count >= 5:
                return TryNumber(fields[1], out double bx1) && TryNumber(fields[2], out double by1) &&
                       TryNumber(fields[3], out double bx2) && TryNumber(fields[4], out double by2)
                    ? new KitSymbolRectangle(bx1, by1, bx2, by2, IsFilled(attributes))
                    : null;

            case ArcRecord when fields.Count >= 6:
                return TryNumber(fields[1], out double cx) && TryNumber(fields[2], out double cy) &&
                       TryNumber(fields[3], out double r)  && TryNumber(fields[4], out double start) &&
                       TryNumber(fields[5], out double sweep) && r > 0
                    ? new KitSymbolArc(cx, cy, r, start, sweep)
                    : null;

            case PathRecord when fields.Count >= 4:
                return ReadPath(fields, attributes);

            default:
                return null;
        }
    }

    /// <summary>
    /// A point run. The declared count is trusted only as far as the fields actually present, so a
    /// truncated record yields the points it really has instead of being thrown away whole.
    /// </summary>
    private static KitSymbolShape? ReadPath(List<string> fields, string attributes)
    {
        if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int declared))
            return null;

        int available = (fields.Count - 2) / 2;
        int count     = Math.Min(declared, available);
        if (count < 2) return null;

        var xy = new List<double>(count * 2);
        for (int i = 0; i < count; i++)
        {
            if (!TryNumber(fields[2 + i * 2], out double x) ||
                !TryNumber(fields[3 + i * 2], out double y)) return null;
            xy.Add(x);
            xy.Add(y);
        }

        // This format states closure by REPEATING the first point, so a run whose ends coincide is
        // closed and the duplicate is dropped. Comparing the numbers rather than trusting a flag is
        // what makes an open run — a bent lead, say — come out open.
        bool closed = xy.Count >= 6 &&
                      NearlyEqual(xy[0], xy[^2]) && NearlyEqual(xy[1], xy[^1]);
        if (closed) xy.RemoveRange(xy.Count - 2, 2);
        if (xy.Count < 4) return null;

        return new KitSymbolPath(xy, closed, closed && IsFilled(attributes));
    }

    /// <summary>Coordinates are drawing units, so a hair's-breadth tolerance is all this needs.</summary>
    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static bool IsFilled(string attributes)
        => ParseAttributes(attributes).TryGetValue(FillAttribute, out var f) &&
           f.Value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the text reads as this format. Structural: a record grammar plus at least one
    /// construct only a symbol has — a global attribute block, or a rectangle that names a terminal.
    ///
    /// <para>Deliberately NOT keyed on the file extension, which several unrelated formats share,
    /// nor on the tool name in the first line — that would put a particular editor's identity into
    /// circuitRF, and would fail the moment a kit's files were written by anything else.</para>
    /// </summary>
    public static bool LooksLikeSymbolFile(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        int records = 0;
        bool hasGlobal = false, hasNamedRectangle = false;

        foreach (var (letter, _, attributes) in Records(text))
        {
            records++;
            if (letter == GlobalRecord) hasGlobal = true;
            else if (letter == RectangleRecord &&
                     ParseAttributes(attributes).ContainsKey(PinNameAttribute)) hasNamedRectangle = true;

            if (records > 200) break;                 // enough to decide; the rest is drawing
        }

        return records >= 2 && (hasGlobal || hasNamedRectangle);
    }

    // ── the record grammar ────────────────────────────────────────────────────

    /// <summary>
    /// Splits the text into records: a single letter, its whitespace-separated fields, and a braced
    /// attribute block that MAY SPAN LINES — a template routinely does, and a line-at-a-time reader
    /// would take the first line of one as the whole of it and lose every parameter after it.
    /// </summary>
    private static IEnumerable<(char Letter, List<string> Fields, string Attributes)> Records(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            char letter = text[i];
            if (!char.IsAsciiLetter(letter)) { i = SkipLine(text, i); continue; }

            // A record's letter stands alone: the next character ends it. Anything else is prose.
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != '{')
            { i = SkipLine(text, i); continue; }

            i++;
            var fields = new List<string>();
            string attributes = "";

            while (i < text.Length)
            {
                while (i < text.Length && text[i] is ' ' or '\t') i++;
                if (i >= text.Length) break;

                if (text[i] == '{') { (attributes, i) = ReadBraced(text, i); break; }
                if (text[i] == '\n' || text[i] == '\r') break;

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{') i++;
                fields.Add(text[start..i]);
            }

            yield return (letter, fields, attributes);
        }
    }

    /// <summary>
    /// Reads a braced block, counting nesting and honouring quotes — a template's value contains
    /// braces of its own often enough that a first-closing-brace rule truncates it.
    ///
    /// <para><b>A block also ends at the start of the next RECORD, whatever the quote state says,
    /// and that is damage control rather than grammar.</b> Quote tracking is only as good as the
    /// file: one unterminated quote inside an attribute block makes every following brace look
    /// quoted, so the block runs on and swallows the rest of the symbol — the terminals included.
    /// Measured, one device's symbol has exactly that typo (a <c>template="…</c> whose
    /// closing quote is missing), and without this bound it imports as a device with NO PINS: it
    /// still appears in the palette and cannot be wired to anything.</para>
    ///
    /// <para>A record always begins at the start of a line in this format, as a lone letter followed
    /// by whitespace or a brace. No legitimate continuation line looks like that — a template's do
    /// not, since they are <c>key=value</c>, and a licence header's start with a comment mark — so
    /// stopping there costs a well-formed file nothing and recovers a malformed one.</para>
    /// </summary>
    private static (string Text, int Next) ReadBraced(string text, int open)
    {
        int depth = 0;
        bool inQuotes = false;
        bool atLineStart = false;
        var sb = new StringBuilder();

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];

            if (atLineStart && depth >= 1 && StartsARecord(text, i))
                return (sb.ToString(), i);              // malformed: bound the damage to this record

            atLineStart = c == '\n';

            if (c == '"') { if (!IsEscapedQuote(text, i)) inQuotes = !inQuotes; }
            else if (!inQuotes && c == '{')
            {
                depth++;
                if (depth == 1) continue;               // the outer brace is not content
            }
            else if (!inQuotes && c == '}')
            {
                depth--;
                if (depth == 0) return (sb.ToString(), i + 1);
            }

            if (depth >= 1) sb.Append(c);
        }

        return (sb.ToString(), text.Length);            // unterminated: take what there is
    }

    /// <summary>A lone record letter followed by whitespace or its attribute block.</summary>
    private static bool StartsARecord(string text, int i)
        => char.IsAsciiLetter(text[i])
        && i + 1 < text.Length
        && (text[i + 1] is ' ' or '\t' or '{');

    private static int SkipLine(string text, int i)
    {
        int nl = text.IndexOf('\n', i);
        return nl < 0 ? text.Length : nl + 1;
    }

    // ── attributes ────────────────────────────────────────────────────────────

    private readonly record struct Attribute(string Value, bool Quoted);

    /// <summary>
    /// Reads <c>key=value</c> pairs from an attribute block. A value may be quoted, in which case it
    /// may contain spaces and newlines; otherwise it runs to the next whitespace.
    /// </summary>
    private static Dictionary<string, Attribute> ParseAttributes(string block)
    {
        var result = new Dictionary<string, Attribute>(StringComparer.OrdinalIgnoreCase);
        if (block.Length == 0) return result;

        int i = 0;
        while (i < block.Length)
        {
            while (i < block.Length && (char.IsWhiteSpace(block[i]) || block[i] == ',')) i++;
            if (i >= block.Length) break;

            int nameStart = i;
            while (i < block.Length && block[i] != '=' && !char.IsWhiteSpace(block[i])) i++;
            string key = block[nameStart..i].Trim();

            if (i >= block.Length || block[i] != '=') { if (key.Length == 0) i++; continue; }
            i++;                                                 // past '='

            bool quoted = i < block.Length && block[i] == '"';
            string value;
            if (quoted)
            {
                i++;
                int start = i;
                while (i < block.Length && (block[i] != '"' || IsEscapedQuote(block, i))) i++;
                value = block[start..i];
                if (i < block.Length) i++;
            }
            else if (block.AsSpan(i).StartsWith(EscapedQuote))
            {
                // A value delimited by ESCAPED quotes — how a quoted value is written inside a quoted
                // template. Read up to the matching one, so an expression with spaces stays whole.
                quoted = true;
                i += EscapedQuote.Length;
                int end = block.IndexOf(EscapedQuote, i, StringComparison.Ordinal);
                if (end < 0) end = block.Length;
                value = block[i..end];
                i = Math.Min(block.Length, end + EscapedQuote.Length);
            }
            else
            {
                int start = i;
                while (i < block.Length && !char.IsWhiteSpace(block[i])) i++;
                value = block[start..i];
            }

            if (key.Length > 0) result.TryAdd(key, new Attribute(value, quoted));
        }

        return result;
    }

    /// <summary>
    /// The escaped quote: a quote preceded by exactly two backslashes. <b>The only spelling observed</b>
    /// — 738 occurrences across three kits' symbol trees, no other form — and it is matched exactly
    /// rather than by any backslash, so a value ending in a single backslash keeps the meaning it had.
    /// Read as a delimiter it cut a value short at its first escaped quote, and the two-character
    /// remainder was then forwarded to the device as that parameter's value (brief-gdf-1).
    /// </summary>
    private const string EscapedQuote = @"\\""";

    private static bool IsEscapedQuote(string text, int quoteAt)
        => quoteAt >= 2 && text[quoteAt - 1] == '\\' && text[quoteAt - 2] == '\\'
           && (quoteAt < 3 || text[quoteAt - 3] != '\\');

    /// <summary>
    /// The template attributes a netlist format places among the terminals: the <c>@name</c> tokens
    /// between <c>@pinlist</c> and the last positional token, which is the model or subcircuit being
    /// called. <c>@pinlist @body @model w=@w</c> yields <c>body</c>. Anything else between them, or no
    /// <c>@pinlist</c>, yields nothing — this reads the one shape kits use, and guesses at no other.
    /// </summary>
    private static IReadOnlyList<string> ExtraTerminalsIn(string format)
    {
        var tokens = format.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int at = Array.FindIndex(tokens, t => t.Equals(PinListToken, StringComparison.Ordinal));
        if (at < 0) return [];

        var positional = tokens.Skip(at + 1).TakeWhile(t => !t.Contains('=')).ToList();
        if (positional.Count < 2) return [];

        var extras = new List<string>();
        foreach (string t in positional.Take(positional.Count - 1))
        {
            if (t.Length < 2 || t[0] != '@' || !t.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return [];
            extras.Add(t[1..]);
        }
        return extras;
    }

    /// <summary>
    /// The element letter the netlist line is written with, read off the FORMAT's first word, which is
    /// what writes it: <c>@spiceprefix</c> gives the template's <c>spiceprefix</c> (and, when that is
    /// blank, whatever follows it), <c>@name</c> the first letter of the instance <c>name</c>, and a
    /// literal letter itself — <c>X@name</c> is a subcircuit call whatever the name begins with, and a
    /// format that never writes <c>@spiceprefix</c> is not changed by the template setting one.
    /// With no format, or a first word that is none of these, the template decides as it always has.
    /// </summary>
    private static string? ElementOf(Dictionary<string, Attribute> template, string? format)
    {
        string? Prefix() => template.TryGetValue("spiceprefix", out var p) && p.Value.Length > 0
            ? p.Value[..1].ToUpperInvariant() : null;
        string? NameLetter() => template.TryGetValue("name", out var n) && n.Value.Length > 0 && char.IsAsciiLetter(n.Value[0])
            ? n.Value[..1].ToUpperInvariant() : null;

        string first = (format ?? "").TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
                                     .FirstOrDefault() ?? "";
        while (first.Length > 0)
        {
            if (first.StartsWith("@spiceprefix", StringComparison.Ordinal))
            {
                if (Prefix() is { } prefix) return prefix;
                first = first["@spiceprefix".Length..];
                continue;
            }
            if (first.StartsWith("@name", StringComparison.Ordinal)) return NameLetter();
            if (char.IsAsciiLetter(first[0])) return first[..1].ToUpperInvariant();
            break;
        }
        return Prefix() ?? NameLetter();
    }

    /// <summary>
    /// The name the format's netlist line calls, and the text after it. The called token is the last
    /// positional word after <c>@pinlist</c> — the same reading <see cref="ExtraTerminalsIn"/> makes
    /// of the words before it. <c>@model</c> inside it is replaced by the template's <c>model</c>, so
    /// a literal prefix (<c>&lt;prefix&gt;@model</c>) is kept; any other placeholder left in it means the
    /// name is not knowable from the symbol alone, and nothing is returned.
    /// </summary>
    private static (string? Called, string? Arguments) CallIn(string format, Dictionary<string, Attribute> template)
    {
        var words = new List<(string Text, int End)>();
        int i = 0;
        while (i < format.Length)
        {
            while (i < format.Length && char.IsWhiteSpace(format[i])) i++;
            int start = i;
            while (i < format.Length && !char.IsWhiteSpace(format[i])) i++;
            if (i > start) words.Add((format[start..i], i));
        }

        int at = words.FindIndex(w => w.Text.Equals(PinListToken, StringComparison.Ordinal));
        if (at < 0) return (null, null);

        var positional = words.Skip(at + 1).TakeWhile(w => !w.Text.Contains('=')).ToList();
        if (positional.Count == 0) return (null, null);

        var (token, end) = positional[^1];
        if (token.Contains("@model", StringComparison.Ordinal))
        {
            if (!template.TryGetValue("model", out var model) || model.Value.Length == 0) return (null, null);
            token = token.Replace("@model", model.Value, StringComparison.Ordinal);
        }
        if (token.Length == 0 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return (null, null);

        string rest = format[end..].Trim();
        return (token, rest.Length > 0 ? rest : null);
    }

    /// <summary>
    /// Reads a symbol's default template into the parameter interface. The defaults are the KIT's,
    /// carried verbatim — circuitRF never invents one.
    /// </summary>
    private static void ReadTemplate(string template, List<PdkPartParameter> into)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, attr) in ParseAttributes(template))
        {
            if (NetlistingKeys.Contains(key)) continue;
            if (!seen.Add(key)) continue;

            // IsText marks a value the user should not be offered a numeric editor for. The field's
            // stated rule is "quoted rather than a number"; a bare word that is not a number — a
            // model name, a mode — is the same thing said without quotes, and treating it as numeric
            // would put a spinner in front of a name.
            bool isText = attr.Quoted || !TryNumber(attr.Value, out _);
            into.Add(new PdkPartParameter(key, attr.Value, isText));
        }
    }

    /// <summary>
    /// A plain number, with an optional engineering suffix ignored — a template writes lengths as
    /// <c>0.13u</c>, and the value is carried verbatim regardless. This only decides whether it IS
    /// numeric, never what it is worth.
    /// </summary>
    private static bool TryNumber(string s, out double value)
    {
        value = 0.0;
        if (s.Length == 0) return false;

        int end = s.Length;
        while (end > 0 && char.IsAsciiLetter(s[end - 1])) end--;
        if (end == 0) return false;

        return double.TryParse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
