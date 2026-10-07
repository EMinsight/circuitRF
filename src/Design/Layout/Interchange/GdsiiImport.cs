// GDSII import orchestrator (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2.4). A GDSII library
// becomes N proper circuitRF cells with real layout views, never an opaque blob. GdsiiReader stays pure
// bytes-and-records (R15); everything after the read — CellFolder, layer reconciliation, messages — is
// StreamLayoutImport, shared with the gdstk route (brief-oasis-gdstk.md §6a).


using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Interchange;

public static class GdsiiImport
{
    public sealed record ImportResult(
        bool Cancelled,
        IReadOnlyList<string> CreatedCellDirs,
        IReadOnlyDictionary<string, string> CellNameByStructureName,
        IReadOnlyList<LayerDef> LayersToAdd,
        IReadOnlyList<string> Messages,
        /// <summary>brief-layout-testing-fixes.md item 7/R-fix-6: absolute cell-folder paths (a
        /// subset of <see cref="CreatedCellDirs"/>) for every structure NEVER referenced as another
        /// structure's instance <c>CellRef</c> within this same file — the GDSII notion of "top"
        /// (what a fab or a viewer like KLayout opens by default). Ordinarily exactly one; empty only
        /// for a pathological all-structures-mutually-referenced library, where there is genuinely no
        /// well-defined top and the caller should say so rather than guessing.</summary>
        IReadOnlyList<string> TopLevelCellDirs);

    /// <summary>
    /// Imports every structure in <paramref name="gdsiiStream"/> as a real cell folder under
    /// <paramref name="parentDir"/>. <paramref name="resolveLayerMapping"/> is invoked with the
    /// proposed <see cref="LayerMappingRow"/>s only when <see
    /// cref="LayoutLayerMapping.RequiresConfirmation"/> is true (exactly the existing retarget/paste
    /// gating) — return null to abort the whole import (nothing is created), or a settled choices
    /// dictionary to proceed. When null (no interactive dialog available, e.g. non-interactive
    /// contexts), <see cref="LayoutLayerMapping.BuildChoices"/>'s own defaults apply.
    /// </summary>
    /// <remarks>circuitRF's own reader, then the shared <see cref="StreamLayoutImport.Import"/> that the
    /// gdstk route (<c>GdstkImport</c>) calls too (brief-oasis-gdstk §6a).</remarks>
    public static ImportResult Import(
        Stream gdsiiStream,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        bool preferSourceResolution,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        PinInferenceRules? pinRules = null)
    {
        var reader = GdsiiReader.Open(gdsiiStream);
        var rawStructures = reader.ReadStructures().ToList();
        return StreamLayoutImport.Import(
            rawStructures, reader.Units.SourceDbuPerMicron, reader.Diagnostics, "GDSII",
            parentDir, destTech, destDbuPerMicron, preferSourceResolution, resolveLayerMapping, pinRules);
    }
}
