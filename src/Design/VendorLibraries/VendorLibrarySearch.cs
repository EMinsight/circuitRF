using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// The part-search grammar, shared by the palette and <c>circuitrf find --parts</c> so the two cannot
/// disagree about what a query finds (brief vendorlib-2 R-vl2-4a; written here by brief 1 so the CLI
/// had it first).
///
/// <para>The query splits on whitespace and EVERY token must match. A token shaped like a case code
/// (<c>0603</c>, or <c>1608M</c> for the metric code) matches the header's stated case pair exactly,
/// when the part states one. Any other token matches an entry when it is
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
    /// <param name="Tokens">Each word, the value it reads as (or null), and the quantity its unit names
    /// (<see cref="VendorPartQuantity.None"/> for a value written with a prefix only).</param>
    public sealed record Query(IReadOnlyList<(string Text, double? Value, VendorPartQuantity Unit)> Tokens)
    {
        public bool IsEmpty => Tokens.Count == 0;
    }

    public static Query Parse(string? query)
        => new([.. (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => TryReadValue(t, out double v, out var unit)
                                   ? (t, (double?)v, unit)
                                   : (t, (double?)null, VendorPartQuantity.None))]);

    public static bool Match(VendorLibraryEntry entry, string? query) => Match(entry, Parse(query));

    public static bool Match(VendorLibraryEntry entry, Query query)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var (text, value, unit) in query.Tokens)
            if (!TokenMatches(entry, text, value, unit)) return false;
        return true;
    }

    private static bool TokenMatches(VendorLibraryEntry e, string token, double? value, VendorPartQuantity unit)
    {
        // A CASE CODE matches the stated case pair as a whole code, never as a substring: a header
        // writes a size as "0603M(0201)", so "0603" — the imperial 0603 a user means — would otherwise
        // find every imperial 0201 part through its metric twin. Bare digits are the imperial code,
        // digits with a trailing M the metric one, as the header itself marks them. A part whose
        // header states no case pair falls through to the ordinary text match below.
        if (CaseCode.Match(token) is { Success: true } code && e.Header.CaseImperial is { } imperial)
            return code.Groups[2].Success
                ? string.Equals(code.Groups[1].Value, e.Header.CaseMetric, StringComparison.Ordinal)
                : string.Equals(code.Groups[1].Value, imperial, StringComparison.Ordinal);

        if (Contains(e.Part, token) || Contains(e.FileStem, token)) return true;

        // A token that reads as a VALUE is matched by value, never as text in the description, where
        // "2pF" would otherwise be found inside "32pF". Every other token is also matched with the
        // description's unit brackets dropped, since nobody types "100[V]".
        if (value is null && (Contains(e.Header.Description, token) || Contains(e.Header.DescriptionPlain, token)))
            return true;
        foreach (string f in e.Folders)
            if (Contains(f, token)) return true;

        // A unit, when the token states one, must be the part's own quantity: "10nH" is never a 10 nF
        // capacitor, though the two numbers are the same.
        return value is { } v && e.Header.Value is { } have && have != 0
            && (unit == VendorPartQuantity.None || unit == e.Header.Quantity)
            && Math.Abs(have - v) <= ValueTolerance * Math.Abs(have);
    }

    private static readonly Regex CaseCode = new(@"^(\d{3,6})([mM])?$", RegexOptions.CultureInvariant);

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
    public static bool TryReadValue(string token, out double value) => TryReadValue(token, out value, out _);

    /// <inheritdoc cref="TryReadValue(string, out double)"/>
    /// <param name="unit">The quantity the token's unit names, or <see cref="VendorPartQuantity.None"/>
    /// when it states a prefix only (<c>10p</c>).</param>
    public static bool TryReadValue(string token, out double value, out VendorPartQuantity unit)
    {
        value = 0;
        unit  = VendorPartQuantity.None;
        var m = Plain.Match(token);
        if (m.Success && (m.Groups[2].Success || m.Groups[3].Success))
        {
            unit = UnitOf(m.Groups[3].Value);
            return Compose(m.Groups[1].Value, m.Groups[2].Value, out value);
        }

        m = Infix.Match(token);
        if (m.Success)
        {
            // 4R7 is ohms; a unit after the digits says the rest.
            unit = m.Groups[2].Value == "R" ? VendorPartQuantity.Resistance : UnitOf(m.Groups[4].Value);
            return Compose($"{m.Groups[1].Value}.{m.Groups[3].Value}",
                           m.Groups[2].Value == "R" ? "" : m.Groups[2].Value, out value);
        }
        return false;
    }

    private static VendorPartQuantity UnitOf(string unit) => unit switch
    {
        "F"                                   => VendorPartQuantity.Capacitance,
        "H"                                   => VendorPartQuantity.Inductance,
        "Ohm" or "ohm" or "Ω" or "R"          => VendorPartQuantity.Resistance,
        _                                     => VendorPartQuantity.None,
    };

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
