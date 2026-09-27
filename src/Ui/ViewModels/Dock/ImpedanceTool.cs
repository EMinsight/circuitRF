using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Impedance;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Impedance panel (brief-impedance-3 R-imp3-1a) — <see cref="DrcTool"/>'s shape:
/// it follows the active LAYOUT document, and <see cref="SetActiveLayout"/> is called from the same
/// places. The result it shows belongs to the layout that was analysed (it is held on that layout's
/// view model), so switching documents switches results rather than showing one board's rows beside
/// another's artwork.
/// </summary>
public sealed class ImpedanceTool : Tool
{
    public ImpedanceTool()
    {
        Id    = DockPanelIds.Impedance;
        Title = "Impedance";
    }

    /// <summary>The panel itself — settings, run, results, export.</summary>
    public ImpedancePanelViewModel Panel { get; } = new();

    public void SetActiveLayout(LayoutEditorViewModel? vm) => Panel.SetEditor(vm);
}
