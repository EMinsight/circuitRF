namespace CircuitRF.Ui.Schematic;

/// <summary>
/// One entry of the palette's Recently Used list: a built-in kind, or a vendor library part (brief
/// vendorlib-2 R-vl2-2d). One list for both, so a designer who places the same 100 pF part twenty
/// times finds it one click away beside the resistor they placed in between.
/// </summary>
public sealed record RecentPlacement(SymbolKind Kind, VendorPartRef? Vendor = null)
{
    public static RecentPlacement Of(VendorPartRef part) => new(SymbolKind.SpiceModel, part);

    /// <summary>What de-dupes the list: the kind, or the vendor part's library + part.</summary>
    public string Key => Vendor is { } v ? "vendor:" + v.IdentityKey : Kind.ToString();

    /// <summary>The persisted spelling — the kind's name, exactly as before vendor parts, or the part's
    /// own one-line form.</summary>
    public string Serialize() => Vendor?.Serialize() ?? Kind.ToString();

    /// <summary>Reads a persisted entry; null for one this build does not know.</summary>
    public static RecentPlacement? Parse(string? stored)
    {
        if (VendorPartRef.TryParse(stored, out var part)) return Of(part);
        return Enum.TryParse<SymbolKind>(stored, ignoreCase: true, out var k) ? new RecentPlacement(k) : null;
    }

    /// <summary>Moves <paramref name="item"/> to the front, dropping any earlier copy, and caps the list.</summary>
    public static void Push(List<RecentPlacement> list, RecentPlacement item, int cap)
    {
        list.RemoveAll(e => e.Key == item.Key);
        list.Insert(0, item);
        if (list.Count > cap) list.RemoveRange(cap, list.Count - cap);
    }
}
