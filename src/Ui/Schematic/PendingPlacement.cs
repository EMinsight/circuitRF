namespace CircuitRF.Ui.Schematic;

/// <summary>
/// App-level armed-placement state: the component type + orientation currently being placed.
/// Null on <see cref="PlacementService"/> means nothing is armed.
/// </summary>
public sealed record PendingPlacement(
    SymbolKind Kind,
    int PortCount,
    SymbolRotation Rotation = SymbolRotation.R0,
    /// <summary>
    /// Set when the armed entry came from an imported kit rather than the built-in library.
    /// Null for every built-in placement, which is therefore completely unaffected by it.
    /// </summary>
    PdkPartRef? Pdk = null,
    /// <summary>
    /// Set when the armed entry is an ordinary CELL rather than a palette component — the absolute
    /// cell folder to place, exactly what a tree drag carries and what
    /// <c>SchematicViewModel.CommitCellPlacementAsync</c> takes.
    ///
    /// <para>A separate field from <see cref="Pdk"/>'s own <c>CellDir</c> on purpose: a kit part
    /// carries a kit+part identity that the palette compares placements by, and a workspace cell has
    /// none. Reusing <c>PdkPartRef</c> for one would put a cell in the palette's armed-tile
    /// comparison under a kit name nobody imported.</para>
    /// </summary>
    string? CellDir = null,
    /// <summary>The armed ghost's mirror flag — M / Shift+M before the click, in the placed part's own
    /// mirror-then-rotate convention (<see cref="Commands.Schematic.MirrorCommand"/>).</summary>
    bool MirrorX = false,
    /// <summary>Set when the armed entry is a VENDOR LIBRARY part (brief vendorlib-2 R-vl2-2b). It is
    /// placed as <see cref="SymbolKind.SpiceModel"/>, the kind every vendor part shares, so the palette
    /// compares placements by this identity and never by the kind.</summary>
    VendorPartRef? Vendor = null);
