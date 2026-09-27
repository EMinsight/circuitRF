using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool hosting the <see cref="AnalysesListViewModel"/> in the left panel.
/// Tracks the active schematic by mirroring the same wiring as <see cref="PropertiesTool"/> — or, 3D editor round 5,
/// the active .c3d's EM setups (<see cref="C3dEditor"/>).
/// </summary>
public sealed partial class AnalysesTool : Tool
{
    [ObservableProperty]
    private AnalysesListViewModel _listVm = new();

    public AnalysesTool()
    {
        Id    = "Analyses";
        Title = "Analyses";
    }

    /// <summary>
    /// 3D editor round 5 — the .c3d whose EM setups the panel shows instead of the schematic's analyses, or null. Set when a
    /// .c3d becomes the active document and cleared when a schematic does (or the .c3d closes); any other document leaves
    /// it, as it leaves the retained schematic.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSchematic))]
    private CircuitRF.Ui.ThreeD.C3dEditorViewModel? _c3dEditor;

    /// <summary>The schematic's list is shown: no .c3d holds the panel.</summary>
    public bool ShowsSchematic => C3dEditor is null;

    /// <summary>Called by WorkspaceViewModel when the active schematic document changes.</summary>
    public void SetActiveSchematic(SchematicViewModel? vm, string? schematicName = null)
        => ListVm.SetActiveSchematic(vm, schematicName);

    /// <summary>Called by WorkspaceViewModel when the open workspace changes (or is cleared).</summary>
    public void SetWorkspaceDir(string? workspaceDir) => ListVm.SetWorkspaceDir(workspaceDir);

    /// <summary>Called by WorkspaceViewModel whenever the referenced kits are (re)loaded. Empty for
    /// every workspace whose kits declare no corners, which keeps the block out of the panel.</summary>
    public void SetCornerAxes(System.Collections.Generic.IReadOnlyList<Schematic.WorkspaceCornerAxis> axes)
        => ListVm.SetCornerAxes(axes);
}
