// File ▸ Export ▸ Bill of materials, from a SCHEMATIC — designer report, round 15.
//
// The board export (BoardCompanions) projects a bill of materials from a layout's placed footprints, so an imported
// Gerber board — flattened copper, no placements — has none to give, and that is exactly the board Create Schematic
// from Artwork's BOM… button wants a bill of materials for. The schematic the designer drew states every value; this
// writes it in BomWriter's columns, the same file the board export writes, so one file reads back through the BOM…
// button whichever editor it came from (RecognitionBom reads it, and BomFile's header rule accepts it).
//
// Each column is what the schematic DRAWS — PdnSchematicNets.ValueOf's value, the stored footprint and the type
// label — read the same way the board export reads the schematic beside a board, so the two cannot disagree.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Interchange;

public static class SchematicBom
{
    public const string Provenance = "Bill of materials projected by circuitRF from the schematic's components.";

    /// <summary>
    /// One row per part to be fitted: every resistor, capacitor, inductor and ferrite bead, and any other component that
    /// names a footprint — never a port, a source, a line or a via. A part disabled as Open is not fitted and is listed
    /// with the value <c>DNP</c>; one disabled as Short is a link, not a part, and is left out.
    /// </summary>
    public static IReadOnlyList<BomEntry> Entries(SchematicEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rows = new List<BomEntry>();
        foreach (var c in model.Components)
        {
            if (c.InstanceName is not { Length: > 0 } refdes || c.Disable == DisableState.Short) continue;
            var kind = Lvs.DeviceTypes.Of(c.Symbol);
            bool discrete = kind is Lvs.DeviceKind.Resistor or Lvs.DeviceKind.Capacitor or Lvs.DeviceKind.Inductor
                            || c.Symbol == SymbolKind.Bead;
            bool footprinted = c.Footprint is { Length: > 0 }
                               && kind is not (Lvs.DeviceKind.TransmissionLine or Lvs.DeviceKind.Via);
            if (!discrete && !footprinted) continue;
            string? value = c.Disable == DisableState.Open ? "DNP" : PdnSchematicNets.ValueOf(c);
            rows.Add(new BomEntry(refdes, value, c.Footprint, c.TypeLabelText()));
        }
        rows.Sort((a, b) => Recognition.PartsTable.NaturalCompare(a.Refdes, b.Refdes));
        return rows;
    }

    /// <summary>The bill of materials as text — <see cref="BomWriter"/>'s columns, named for <paramref name="name"/>.</summary>
    public static string Text(SchematicEditModel model, string name) =>
        BomWriter.Write(Entries(model), new BomWriteOptions(Board: name, Provenance: Provenance));
}
