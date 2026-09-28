using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// A parts table pasted onto the schematic, or a <c>.csv</c> dropped on it: every resistor,
/// capacitor and inductor it lists is placed, named by its reference designator, with its value and
/// case size. The reading is <see cref="BomTablePaste"/>'s; everything here is placement.
///
/// <para><b>Each part is built by <see cref="BuildPlacedComponent"/></b>, the construction a palette
/// drop uses, so a pasted capacitor has the same parameters, labels and default footprint as a
/// dropped one — this file only overwrites the value and the footprint the table stated. The whole
/// table lands as ONE <see cref="SchematicPasteCommand"/>: one undo removes it, and an instance name
/// already on the sheet is renumbered the way any paste renumbers it.</para>
/// </summary>
public sealed partial class SchematicViewModel
{
    /// <summary>Column pitch of a placed row, in schematic units — a two-pin part is 400 tall
    /// (pins at ±200) and its name/value/footprint labels need about this much width.</summary>
    internal const double BomColumnPitch = 600;

    /// <summary>Row pitch: the part, its label block under it, and a gap.</summary>
    internal const double BomRowPitch = 1200;

    /// <summary>
    /// Reads <paramref name="text"/> as a parts table and places its parts. Returns false, having
    /// done nothing, when the text is not a parts table — so a caller can fall through to whatever
    /// it would otherwise have done.
    /// </summary>
    /// <param name="source">Where the text came from, for the Messages line ("the clipboard",
    /// a file name).</param>
    /// <param name="anchor">The world position of the first part — a drop point. Null places the
    /// block below everything already on the sheet.</param>
    public bool PlaceBomTable(string? text, string source, (double X, double Y)? anchor = null)
    {
        if (BomTablePaste.TryParse(text) is not { } table) return false;

        var comps = new List<EditableComponent>(table.Parts.Count);
        bool footprintsDropped = false;
        foreach (var part in table.Parts)
        {
            var comp = BuildPlacedComponent(part.Kind, 0, SymbolRotation.R0, 0, 0);
            comp.InstanceName = part.Refdes;

            if (part.Value is { } value &&
                comp.Parameters.FirstOrDefault(p => p.Name == ValueParameterOf(part.Kind)) is { } vp)
            {
                vp.Expression = value;
                vp.Unit = part.Unit ?? vp.Unit;
            }

            if (part.Case is { } smtCase)
            {
                // The footprint parameter exists only where placement gives one (a board
                // technology — FootprintDefaults), and the table's case replaces that default. Off a
                // board there is no footprint to set, and inventing one here would be a second
                // footprint policy.
                var fp = comp.Parameters.FirstOrDefault(p => p.Name.Equals(
                    ArtworkParameters.FootprintName, StringComparison.OrdinalIgnoreCase));
                if (fp is not null)
                {
                    fp.Expression = FootprintRef.For(smtCase).ToString();
                    // Owner decision (round 9): a case the TABLE named is shown on the sheet, so the
                    // reading can be checked at a glance; a default footprint stays hidden.
                    comp.ShowFootprintLabel = true;
                }
                else footprintsDropped = true;
            }
            else comp.ShowFootprintLabel = false;
            comps.Add(comp);
        }

        var renamed = ResolveBomNameCollisions(comps);
        LayOutBomBlock(comps, table.Parts, anchor);

        Execute(new SchematicPasteCommand(
            EditModel, comps, [], [],
            ids => Selection.SetAll(ids),
            messageSink: _messageSink));

        Report(table, comps.Count, source, footprintsDropped, renamed);
        return true;
    }

    /// <summary>
    /// A reference already on the sheet is renumbered — but only that one. Every name the TABLE
    /// uses is reserved first, so renaming a clashing C1 cannot land on the table's own C2 and set
    /// off a cascade that renumbers parts which never clashed (the paste command's own resolution
    /// walks the list in order and would).
    /// </summary>
    private List<(string From, string To)> ResolveBomNameCollisions(List<EditableComponent> comps)
    {
        var existing = EditModel.Components.Select(c => c.InstanceName).ToHashSet();
        var taken = new HashSet<string>(existing);
        taken.UnionWith(comps.Select(c => c.InstanceName));
        var renamed = new List<(string, string)>();
        foreach (var comp in comps.Where(c => existing.Contains(c.InstanceName)))
        {
            string from = comp.InstanceName;
            comp.InstanceName = SchematicEditModel.NextAvailableName(
                taken, ComponentTypeRegistry.InstancePrefix(comp.Symbol));
            taken.Add(comp.InstanceName);
            renamed.Add((from, comp.InstanceName));
        }
        return renamed;
    }

    private static string ValueParameterOf(SymbolKind kind) => kind switch
    {
        SymbolKind.Capacitor => "C",
        SymbolKind.Resistor => "R",
        _ => "L",
    };

    /// <summary>
    /// One row per part type, in the order the table first names each type, parts in table order
    /// along the row. Below the existing content (or at the drop point), on the connection grid.
    /// </summary>
    private void LayOutBomBlock(List<EditableComponent> comps, IReadOnlyList<BomPastePart> parts,
                                (double X, double Y)? anchor)
    {
        double x0, y0;
        if (anchor is { } a)
        {
            (x0, y0) = (a.X, a.Y);
        }
        else if (SchematicPasteGeometry.BoundingBox(EditModel.Components, EditModel.Wires,
                                                    EditModel.CanvasObjects) is { } bb)
        {
            (x0, y0) = (bb.MinX, bb.MaxY + BomRowPitch);
        }
        else if (ViewportProvider?.Invoke() is { } view)
        {
            (x0, y0) = (view.MinX + BomColumnPitch, view.MinY + BomRowPitch / 2);
        }
        else
        {
            (x0, y0) = (0, 0);
        }

        var rows = parts.Select(p => p.Kind).Distinct().ToList();
        var column = new int[rows.Count];
        for (int i = 0; i < comps.Count; i++)
        {
            int row = rows.IndexOf(parts[i].Kind);
            comps[i].X = EditModel.SnapToGrid(x0 + column[row]++ * BomColumnPitch);
            comps[i].Y = EditModel.SnapToGrid(y0 + row * BomRowPitch);
        }
    }

    private void Report(BomPasteResult table, int placed, string source, bool footprintsDropped,
                        List<(string From, string To)> renamed)
    {
        if (_messageSink is null) return;

        _messageSink.Info($"Placed {placed} part(s) from the parts table in {source}" +
                          (table.Skipped.Count > 0 ? $"; {table.Skipped.Count} row(s) skipped." : "."));
        foreach (var skip in table.Skipped)
            _messageSink.Info($"Parts table line {skip.Line}: {skip.What} not placed — {skip.Reason}.");
        foreach (string note in table.Notes)
            _messageSink.Info("Parts table: " + note);
        if (renamed.Count > 0)
            _messageSink.Info("Parts table: already on the sheet, so renamed " +
                              string.Join(", ", renamed.Select(r => $"{r.From} → {r.To}")) + ".");
        if (footprintsDropped)
            _messageSink.Info("Parts table: this workspace's technology is not a board, so the case " +
                              "sizes were not applied as footprints.");
    }
}
