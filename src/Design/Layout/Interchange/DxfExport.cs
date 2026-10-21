// DXF export orchestrator (docs/sonnet-briefs/brief-L4b-dxf-interchange.md). Mirrors GdsiiExport's own
// shape exactly (§2.3-equivalent): walks a design's cell hierarchy from a root cell folder into
// InterchangeStructures, then runs the SAME DxfWriter.Write path (a dry run into TextWriter.Null) to
// produce the pre-flight fidelity plan the export dialog shows BEFORE any bytes are written.
//
// Unlike GDSII, DXF coordinates are plain doubles with no 32-bit integer ceiling — there is no
// coordinate-overflow block here; ExportPlan.CanWrite exists anyway, always true, purely so the UI
// dialog shape stays identical across both formats (never a special-cased "DXF has no CanWrite" path).

using CircuitRF.WBond;

using CircuitRF.Design.Cells;

using CircuitRF.Design.Layout.Footprints;

namespace CircuitRF.Design.Layout.Interchange;

public static class DxfExport
{
    /// <summary>What the export dialog shows before writing: curve/hole/bitmap counts, the
    /// block-name mapping, and any instance whose <c>CellRef</c> does not resolve within this design
    /// (it still exports — as a dangling reference to a BLOCK name absent from the file — but never
    /// silently).</summary>
    public sealed record ExportPlan(
        IReadOnlyList<string> UnresolvedInstanceReferences,
        IReadOnlyDictionary<string, string> BlockNameByCellName,
        IReadOnlyList<InterchangeStructure> Structures,
        string RootStructureName,
        Technology? Tech,
        int DbuPerMicron,
        /// <summary>docs/design/layout-view.md §9B.10 — the ROOT cell's ruler annotations. Root-only:
        /// rulers are cell-local (§9B.7) and do not render through an instance placement, so a
        /// sub-cell's working notes are not scattered across every design that reuses it.</summary>
        IReadOnlyList<RulerAnnotation>? Rulers = null,
        LayoutUnit DisplayUnit = LayoutUnit.Um,
        /// <summary>brief-bsp-3 R-bsp3-4 — what the bottom-side placements did to the file; see
        /// <c>GdsiiExport.ExportPlan.Notes</c>.</summary>
        IReadOnlyList<string>? Notes = null)
    {
        public bool CanWrite => true;

        public IReadOnlyList<string> SideNotes => Notes ?? [];

        /// <summary>R-via-10: any via at all means this export is geometry-only, never a manufacturable
        /// PCB deliverable (DXF carries no drill table) — mirrors <c>GdsiiExport.ExportPlan.HasVias</c>.</summary>
        public bool HasVias => Structures.Any(s => s.Shapes.Any(sh => sh is ViaShape));
    }

    /// <summary>Walks <paramref name="rootCellDir"/>'s hierarchy — no bytes written yet. Identical
    /// hierarchy-collection shape to <c>GdsiiExport.Analyze</c> (same BFS over <c>CellFolder.
    /// ResolvePrimary</c> + <c>LayoutInstance.CellRef</c>), duplicated here rather than shared because
    /// the brief's own guardrail forbids widening GDSII's file beyond wiring, and the two walks now
    /// produce format-specific <see cref="InterchangeStructure"/> naming (DXF block names, not GDSII
    /// structure names).</summary>
    /// <summary><paramref name="rootView"/> (brief-layout-testing-fixes.md item 5/R-fix-4): when the
    /// root cell is open in the editor, pass its live, possibly-unsaved <c>LayoutView</c> here so the
    /// export reflects what is on screen rather than the last save — mirrors <c>GdsiiExport.Analyze</c>'s
    /// own parameter exactly. Null (the project-tree/no-open-document path) reads from disk as before.</summary>
    public static ExportPlan Analyze(string rootCellDir, Technology? tech, int dbuPerMicron, LayoutView? rootView = null)
    {
        var (structures, nameByCellName, unresolvedRefs, rootName, rootRulers, displayUnit, notes) =
            CollectHierarchy(rootCellDir, rootView, tech);
        return new ExportPlan(unresolvedRefs, nameByCellName, structures, rootName, tech, dbuPerMicron,
                              rootRulers, displayUnit, notes);
    }

    /// <summary>
    /// Dry-runs the write into <see cref="TextWriter.Null"/> — the SAME code path the real export
    /// takes, so the fidelity dialog can never disagree with what actually gets written.
    /// </summary>
    /// <param name="wires">
    /// Bond wires to include (wbond.md §9.4). Null — the Layout Editor's own case — writes exactly
    /// what it always did.
    /// </param>
    public static DxfExportSummary Preview(ExportPlan plan, DxfExportOptions options, WBondDesign? wires = null) =>
        DxfWriter.Write(TextWriter.Null, plan.Structures, plan.RootStructureName, plan.Tech, plan.DbuPerMicron,
                        options, wires, plan.Rulers, plan.DisplayUnit);

    /// <inheritdoc cref="Preview(ExportPlan, DxfExportOptions, WBondDesign?)"/>
    public static DxfExportSummary Write(string filePath, ExportPlan plan, DxfExportOptions options, WBondDesign? wires = null)
    {
        using var stream = new StreamWriter(filePath, append: false);
        return DxfWriter.Write(stream, plan.Structures, plan.RootStructureName, plan.Tech, plan.DbuPerMicron,
                               options, wires, plan.Rulers, plan.DisplayUnit);
    }

    private static (List<InterchangeStructure> Structures, IReadOnlyDictionary<string, string> NameByCellName,
        IReadOnlyList<string> UnresolvedRefs, string RootName,
        IReadOnlyList<RulerAnnotation> RootRulers, LayoutUnit DisplayUnit, IReadOnlyList<string> Notes) CollectHierarchy(
            string rootCellDir, LayoutView? rootView, Technology? tech)
    {
        // brief-bsp-3 R-bsp3-4 — the walk GdsiiExport shares: every reachable cell, plus a bottom
        // variant block of each cell a bottom placement uses, and only of those.
        var walk = InterchangeHierarchy.Collect(Path.GetFullPath(rootCellDir), rootView, tech, LoadPrimaryLayout);

        var cellNames = walk.Tops.Select(n => n.CellName).ToList();
        var blockNameByCellName = DxfNaming.MangleForExport(cellNames);

        var nameOf = new Dictionary<InterchangeHierarchy.Node, string>();
        foreach (var node in walk.Tops) nameOf[node] = blockNameByCellName[node.CellName];

        var mapping = new Dictionary<string, string>(blockNameByCellName);
        if (walk.Variants.Count > 0)
        {
            // DXF block names compare without case (DxfNaming's own rule).
            var used = new HashSet<string>(nameOf.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var node in walk.Variants)
            {
                nameOf[node] = InterchangeHierarchy.VariantName(blockNameByCellName[node.CellName], used, DxfNaming.MaxLength);
                mapping[$"{node.CellName} (bottom)"] = nameOf[node];
            }
        }

        var unresolvedRefs = new List<string>();
        var structures = new List<InterchangeStructure>();
        foreach (var node in walk.All)
        {
            var view = node.View;
            var layoutDir = node.LayoutDir;
            var instances = new List<LayoutInstance>(view.Instances.Count);
            for (int i = 0; i < view.Instances.Count; i++)
            {
                var inst = view.Instances[i];
                if (node.Targets[i] is not { } target || !nameOf.TryGetValue(target, out var targetBlockName))
                {
                    targetBlockName = inst.CellRef;
                    if (node.Side == PlacementSide.Top)
                        unresolvedRefs.Add($"{node.CellName}: instance referencing \"{inst.CellRef}\" does not resolve.");
                }
                instances.Add(InterchangeHierarchy.Exported(inst, targetBlockName));
            }

            // brief-footprint-4b R-fp4b-4b — mirrors GdsiiExport.CollectHierarchy exactly, and out of
            // the same one function: a structure carries the designators of its OWN placements.
            var shapes = new List<LayoutShape>(view.Shapes);
            shapes.AddRange(FootprintLabel.ShapesFor(view, layoutDir, tech, null));

            structures.Add(new InterchangeStructure(nameOf[node], shapes, instances));
        }

        var notes = new List<string>(walk.Notes);
        if (walk.Variants.Count > 0) notes.Add(InterchangeHierarchy.VariantNote(walk.Variants.Count, "block"));

        // §9B.7: cell-LOCAL. Only the root's own rulers travel; a sub-cell's stay with that cell.
        var root = walk.Tops[0];
        return (structures, mapping, unresolvedRefs, nameOf[root],
                [.. root.View.Rulers], root.View.DisplayUnit, notes);
    }

    private static LayoutView LoadPrimaryLayout(string cellDir)
    {
        var res = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || res.ResolvedName is null)
            return new LayoutView();

        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        try
        {
            return LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, res.ResolvedName));
        }
        catch
        {
            return new LayoutView();
        }
    }
}
