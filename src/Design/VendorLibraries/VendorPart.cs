using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// One vendor library part, as the palette arms it, a drag carries it and Recently Used remembers it
/// (brief vendorlib-2 R-vl2-2b/2d).
///
/// <para><b>Identified by library + part, never by <see cref="SymbolKind"/></b>: every vendor part is
/// placed as <see cref="SymbolKind.SpiceModel"/>, so comparing kinds would arm every row at once. The
/// other fields are what placing it needs without a look-up — the file it lives in, the glyph, and the
/// case pair the footprint comes from — so a part remembered from an earlier session places correctly
/// while its library is still being indexed.</para>
/// </summary>
/// <param name="Library">The library's name in the workspace's <c>.cws</c>.</param>
/// <param name="Part">The subcircuit's name.</param>
/// <param name="File">The file, relative to the library root, with <c>/</c> separators.</param>
/// <param name="Glyph"><c>C</c>, <c>L</c> or empty — see <see cref="VendorPartPlacement.GlyphOf"/>.</param>
public sealed record VendorPartRef(
    string Library,
    string Part,
    string File,
    string Glyph,
    string? CaseMetric = null,
    string? CaseImperial = null)
{
    /// <summary>The part for one index entry of the library named <paramref name="library"/>.</summary>
    public static VendorPartRef For(string library, VendorLibraryEntry e)
        => new(library, e.Part, e.File, VendorPartPlacement.GlyphOf(e), e.Header.CaseMetric, e.Header.CaseImperial);

    /// <summary>True when <paramref name="other"/> is the same part of the same library. Case-insensitive,
    /// as the index and the <c>.cws</c> look names up.</summary>
    public bool IsSamePart(VendorPartRef? other)
        => other is not null
        && string.Equals(Library, other.Library, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Part, other.Part, StringComparison.OrdinalIgnoreCase);

    /// <summary>A key for <see cref="IsSamePart"/>, for a dictionary or a de-dupe.</summary>
    public string IdentityKey => Library.ToUpperInvariant() + "/" + Part.ToUpperInvariant();

    /// <summary>The stored <c>File</c> parameter: <c>vendorlib://&lt;library&gt;/&lt;file&gt;</c>.</summary>
    public string FileRef => VendorLibraryRefs.RefFor(Library, File);

    private const string Marker = "vendorpart";

    /// <summary>
    /// One line of text, for a drag payload and the persisted Recently Used list. Tab-separated: no
    /// field can hold a tab (a library name, a SPICE name, a case code), while a path can hold almost
    /// anything else.
    /// </summary>
    public string Serialize()
        => string.Join('\t', Marker, Library, Part, File, Glyph, CaseMetric ?? "", CaseImperial ?? "");

    public static bool TryParse(string? text, out VendorPartRef result)
    {
        result = null!;
        if (text is null) return false;
        var f = text.Split('\t');
        if (f.Length != 7 || f[0] != Marker || f[1].Length == 0 || f[2].Length == 0 || f[3].Length == 0) return false;
        result = new VendorPartRef(f[1], f[2], f[3], f[4], f[5].Length == 0 ? null : f[5], f[6].Length == 0 ? null : f[6]);
        return true;
    }
}

/// <summary>
/// What placing a vendor part writes (brief vendorlib-2 R-vl2-3) — one function each, called by the
/// palette's placement and by <c>circuitrf find --parts</c>, so an agent writing a <c>.csch</c> by
/// hand writes exactly what a drop would have.
/// </summary>
public static class VendorPartPlacement
{
    /// <summary>
    /// The <c>Glyph</c> parameter for a part: <c>C</c> or <c>L</c> when its header states a
    /// capacitance or an inductance AND it has two ports, otherwise blank.
    ///
    /// <para><b>Two ports is part of the rule</b>, so a stored <c>C</c> or <c>L</c> always means a
    /// two-terminal part. That is what lets the symbol be drawn from the instance alone when the
    /// library is missing — there is then no index to ask for the port count.</para>
    /// </summary>
    public static string GlyphOf(VendorLibraryEntry e)
        => e.PortCount != 2 ? "" : e.Header.Quantity switch
        {
            VendorPartQuantity.Capacitance => "C",
            VendorPartQuantity.Inductance  => "L",
            _                              => "",
        };

    /// <summary>The built-in kind a glyph draws as, or null for a blank or unknown glyph.</summary>
    public static SymbolKind? GlyphKind(string? glyph) => (glyph ?? "").Trim().ToUpperInvariant() switch
    {
        "C" => SymbolKind.Capacitor,
        "L" => SymbolKind.Inductor,
        _   => null,
    };

    /// <summary>
    /// What a part with this glyph draws as before it is placed (the ghost): the built-in C or L, or
    /// the unconfigured SPICE box for a part with none. The placed instance resolves the same answer
    /// through <see cref="SpiceModelSymbolProvider"/>.
    /// </summary>
    public static Symbol.Symbol GhostSymbol(string? glyph)
        => GlyphKind(glyph) is { } kind
            ? BuiltInSymbols.Primitives(kind, 2)
            : SpiceModelSymbolProvider.UnconfiguredSymbol(Symbol.SnpPinConfig.Standard, Symbol.SnpPitch.Loose);

    /// <summary>The instance-name prefix: <c>C</c> / <c>L</c> for those glyphs, else null for the
    /// SpiceModel's own (R-vl2-3c).</summary>
    public static string? InstancePrefix(string? glyph) => GlyphKind(glyph) switch
    {
        SymbolKind.Capacitor => "C",
        SymbolKind.Inductor  => "L",
        _                    => null,
    };

    /// <summary>The instance parameters a placed part carries, in the order they are written.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parameters(VendorPartRef part)
        =>
        [
            new(SpiceModelSymbolProvider.FileParameter,  part.FileRef),
            new(SpiceModelSymbolProvider.NameParameter,  part.Part),
            new(SpiceModelSymbolProvider.GlyphParameter, part.Glyph),
        ];

    /// <summary>The <c>Footprint</c> a placed part takes on <paramref name="technology"/>, or null for
    /// None — <see cref="FootprintDefaults.ForCaseCodes"/>.</summary>
    public static string? Footprint(VendorPartRef part, Technology? technology)
        => FootprintDefaults.ForCaseCodes(part.CaseMetric, part.CaseImperial, technology);
}
