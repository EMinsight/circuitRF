using CircuitRF.WBond;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// Whether a placed wBond's own wires still match the layout's <c>.wBond</c> — wbond.md §9.7 as
/// revised on 2026-10-05.
///
/// <para><b>The schematic always simulates its own copy.</b> A wBond is like every other component:
/// the schematic is what runs, and the layout is checked against it. Until 2026-10-05 a
/// <c>Linked</c> instance simulated the FILE instead, which made a layout edit that added an array
/// unsimulatable (the symbol's pins come from the schematic's copy, so the model had more terminals
/// than the symbol had pins) and gave the wBond rules no other component shares. A mismatch is now
/// a WARNING, controlled per instance by <see cref="WBondPlacement.WarnUnsyncedParameter"/>.</para>
///
/// <h3>What counts as "not synced"</h3>
/// <para>The WIRES: the array list, each array's wires, the material definitions and the ground
/// plane. Both sides are compared after the instance's own controlling parameters
/// (<c>LoopHeight</c>, <c>Diameter</c>, <c>Material</c> and their per-array forms) are applied to
/// each, because that is what a run sees — and because Update Layout from Schematic BAKES those
/// overrides into the file while leaving the schematic's copy raw, so comparing raw copies would warn
/// immediately after the command whose purpose was to sync them.</para>
///
/// <para>The instance-level settings — <c>Temp</c>, <c>er</c>, <c>IncludeCapacitance</c> — are
/// NOT compared. They belong to the schematic component, the run applies them over either copy, and
/// changing one of them is the ordinary way to explore a design without touching the layout.
/// <c>GroundPlane</c> is compared only while the instance leaves it to the design. What only the
/// editor reads (view state, embedded artwork, a wire's lock, the readout frequency) is ignored.</para>
///
/// <para><b>The file on disk, not an open editor's live copy.</b> This runs below the UI firewall
/// and cannot see an editor. An unsaved layout edit therefore reads as "in sync" until it is saved,
/// which is why every sentence here says "saved".</para>
/// </summary>
public static class WBondSync
{
    public enum Status
    {
        /// <summary>The instance names no layout file — the ordinary state before Update Layout from
        /// Schematic has run. Nothing to compare and nothing to say.</summary>
        NoLayoutFile,

        /// <summary>A file is named and is not on disk.</summary>
        LayoutFileMissing,

        /// <summary>A file is on disk and could not be read.</summary>
        LayoutFileUnreadable,

        /// <summary>The instance's own payload could not be decoded. Reported elsewhere (the extractor
        /// skips the instance); this check says nothing about it.</summary>
        PayloadUnreadable,

        InSync,
        Differs,
    }

    /// <param name="StoredPath">The <c>File</c> value as stored (relative to the schematic), or null.</param>
    /// <param name="Differences">What differs, phrased as list items — only for <see cref="Status.Differs"/>.</param>
    /// <param name="Detail">The read error, for <see cref="Status.LayoutFileUnreadable"/>.</param>
    public sealed record Result(
        Status Status, string? StoredPath, IReadOnlyList<string> Differences, string? Detail = null)
    {
        /// <summary>The run's warning, or null when there is nothing to warn about.</summary>
        public string? Warning(string instanceName) => Status switch
        {
            Status.LayoutFileMissing =>
                $"wBond '{instanceName}': its layout wire file '{StoredPath}' was not found, so it cannot " +
                "be checked against the layout. The schematic's own wires were simulated.",
            Status.LayoutFileUnreadable =>
                $"wBond '{instanceName}': its layout wire file '{StoredPath}' could not be read ({Detail}), " +
                "so it cannot be checked against the layout. The schematic's own wires were simulated.",
            Status.Differs =>
                $"wBond '{instanceName}' is not synced to the saved layout wires in '{StoredPath}': " +
                $"{string.Join("; ", Differences)}. The schematic's wires were simulated. Run Design ▸ " +
                "Update Schematic from Layout to bring the layout's wires in, or clear \"Warn if " +
                "Schematic Not Synced to Layout\" on the component.",
            _ => null,
        };

        /// <summary>The one-line state the parameter panel shows under the checkbox.</summary>
        public string Note => Status switch
        {
            Status.NoLayoutFile =>
                "No layout wires yet. Update Layout from Schematic writes them into the cell's layout.",
            Status.LayoutFileMissing =>
                $"Not found: \"{StoredPath}\". The schematic's wires still simulate.",
            Status.LayoutFileUnreadable =>
                $"\"{StoredPath}\" could not be read: {Detail}",
            Status.PayloadUnreadable => "",
            Status.InSync =>
                $"In sync with the saved layout wires (\"{StoredPath}\").",
            _ =>
                $"Not synced with \"{StoredPath}\": {string.Join("; ", Differences)}. The schematic's " +
                "wires are what simulate; Update Schematic from Layout brings the layout's in.",
        };
    }

    /// <summary>
    /// Compares <paramref name="comp"/>'s own wires with the layout file its <c>File</c> parameter
    /// names. Reads the file every call — it is small, and a cached answer would go stale behind a save.
    /// </summary>
    public static Result Check(EditableComponent comp, string? schematicDirectory)
    {
        ArgumentNullException.ThrowIfNull(comp);

        string? stored = WBondPlacement.LinkedPathOf(comp);
        string? path = WBondPlacement.ResolveLinkedPath(comp, schematicDirectory);
        if (stored is null || path is null) return new Result(Status.NoLayoutFile, stored, []);

        if (!File.Exists(path)) return new Result(Status.LayoutFileMissing, stored, []);

        WBondDesign layout;
        try { layout = WBondIo.ReadFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or System.Text.Json.JsonException)
        {
            return new Result(Status.LayoutFileUnreadable, stored, [], ex.Message);
        }

        string payload = comp.Parameters
            .FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression ?? "";
        if (!WBondEmbedding.TryDecode(payload, out var schematic) || schematic is null)
            return new Result(Status.PayloadUnreadable, stored, []);

        var differences = Compare(comp, schematic, layout);
        return new Result(differences.Count == 0 ? Status.InSync : Status.Differs, stored, differences);
    }

    private static List<string> Compare(EditableComponent comp, WBondDesign schematic, WBondDesign layout)
    {
        var differences = new List<string>();

        string[] ours = [.. schematic.Arrays.Select(a => a.Name)];
        string[] theirs = [.. layout.Arrays.Select(a => a.Name)];

        // A different array list is the difference that matters most — the symbol's pins move — and
        // comparing wires array-by-array across two different lists would only add noise to it.
        if (!ours.SequenceEqual(theirs, StringComparer.Ordinal))
        {
            differences.Add(
                $"arrays {Names(ours)} in the schematic, {Names(theirs)} in the layout");
            return differences;
        }

        // The same overrides on both sides: what a run would see from either copy.
        var read = WBondPlacement.ReadControllingParameters(comp);
        try
        {
            ControllingParameters.ApplyTo(schematic, read.Overrides);
            ControllingParameters.ApplyTo(layout, read.Overrides);
        }
        catch (InvalidOperationException)
        {
            // A refused override (a zero, an undeclared metal) is the run's to report by name. Compare
            // the drawn wires rather than inventing an answer for the refused value.
        }

        var changed = new List<string>();
        for (int i = 0; i < ours.Length; i++)
            if (ArrayText(schematic.Arrays[i]) != ArrayText(layout.Arrays[i]))
                changed.Add(ours[i]);
        if (changed.Count > 0)
            differences.Add($"the wires in {Names([.. changed])} differ");

        if (MaterialsText(schematic) != MaterialsText(layout))
            differences.Add("the material definitions differ");

        if (!InstanceSets(comp, "GroundPlane") && schematic.GroundPlane.Enabled != layout.GroundPlane.Enabled)
            differences.Add($"the ground plane is {(schematic.GroundPlane.Enabled ? "on" : "off")} in the " +
                            $"schematic and {(layout.GroundPlane.Enabled ? "on" : "off")} in the layout");

        return differences;
    }

    private static string Names(string[] names) => names.Length == 0 ? "(none)" : string.Join(", ", names);

    private static bool InstanceSets(EditableComponent comp, string name) =>
        !string.IsNullOrWhiteSpace(comp.Parameters.FirstOrDefault(p => p.Name == name)?.Expression);

    /// <summary>One array as the file format writes it, with the editor-only lock cleared — the same
    /// serializer on both sides, so every field the format carries is compared and none is forgotten.</summary>
    private static string ArrayText(WireArray array)
    {
        foreach (var wire in array.Wires) wire.Locked = false;
        return WBondIo.Write(new WBondDesign { Arrays = [array], Materials = [] });
    }

    private static string MaterialsText(WBondDesign design) =>
        WBondIo.Write(new WBondDesign { Materials = design.Materials });
}
