using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// The part-search grammar, shared by the palette and <c>circuitrf find --parts</c> so the two cannot
/// disagree about what a query finds (brief vendorlib-2 R-vl2-4a; written here by brief 1 so the CLI
/// had it first).
///
/// <para>The query splits on whitespace and EVERY token must match. A token matches an entry when it is
/// a case-insensitive substring of the part name, the description, the file stem or a folder
/// segment — or when it READS AS A VALUE (<c>10p</c>, <c>10pF</c>, <c>2.2n</c>, <c>4n7</c>,
/// <c>1u</c>) and the entry's stated value equals it within 0.5 %. So <c>10p</c> finds a part whose
/// header says <c>1.00e-05[uF]</c>.</para>
///
/// <para><b>It reads only the in-memory entry.</b> A keystroke must read no file.</para>
/// </summary>
public static class VendorLibrarySearch
{
    /// <summary>The relative tolerance a value token matches within.</summary>
    public const double ValueTolerance = 0.005;

    /// <summary>A parsed query, so a list filtered per keystroke parses the query once rather than per entry.</summary>
    public sealed record Query(IReadOnlyList<(string Text, double? Value)> Tokens)
    {
        public bool IsEmpty => Tokens.Count == 0;
    }

    public static Query Parse(string? query)
        => new([.. (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => (t, TryReadValue(t, out double v) ? v : (double?)null))]);

    public static bool Match(VendorLibraryEntry entry, string? query) => Match(entry, Parse(query));

    public static bool Match(VendorLibraryEntry entry, Query query)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var (text, value) in query.Tokens)
            if (!TokenMatches(entry, text, value)) return false;
        return true;
    }

    private static bool TokenMatches(VendorLibraryEntry e, string token, double? value)
    {
        if (Contains(e.Part, token) || Contains(e.FileStem, token) || Contains(e.Header.Description, token))
            return true;
        foreach (string f in e.Folders)
            if (Contains(f, token)) return true;

        return value is { } v && e.Header.Value is { } have && have != 0
            && Math.Abs(have - v) <= ValueTolerance * Math.Abs(have);
    }

    private static bool Contains(string? hay, string needle)
        => hay is not null && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ── Values ────────────────────────────────────────────────────────────────

    private static readonly Regex Plain =
        new(@"^(\d+\.?\d*|\.\d+)([TGMkKmunpfµμ]|MEG|meg)?(F|H|Ohm|ohm|Ω|R)?$", RegexOptions.CultureInvariant);

    // 4n7 → 4.7n: the prefix letter standing where the decimal point would be.
    private static readonly Regex Infix =
        new(@"^(\d+)([TGMkKmunpfµμR])(\d+)(F|H|Ohm|ohm|Ω)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads a search token as a value in SI, or false when it is not one. A bare number is not a
    /// value here: <c>0402</c> or <c>100</c> is far more likely a size or a fragment of a part name,
    /// so a token needs an engineering prefix or a unit to be read as a value.
    /// </summary>
    public static bool TryReadValue(string token, out double value)
    {
        value = 0;
        var m = Plain.Match(token);
        if (m.Success && (m.Groups[2].Success || m.Groups[3].Success))
            return Compose(m.Groups[1].Value, m.Groups[2].Value, out value);

        m = Infix.Match(token);
        if (m.Success)
            return Compose($"{m.Groups[1].Value}.{m.Groups[3].Value}",
                           m.Groups[2].Value == "R" ? "" : m.Groups[2].Value, out value);
        return false;
    }

    private static bool Compose(string number, string prefix, out double value)
    {
        value = 0;
        int exp = prefix switch
        {
            ""                  => 0,
            "T"                 => 12,
            "G"                 => 9,
            "M" or "MEG" or "meg" => 6,
            "k" or "K"          => 3,
            "m"                 => -3,
            "u" or "µ" or "μ"   => -6,
            "n"                 => -9,
            "p"                 => -12,
            "f"                 => -15,
            _                   => int.MinValue,
        };
        if (exp == int.MinValue) return false;
        return double.TryParse($"{number}e{exp}", NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
