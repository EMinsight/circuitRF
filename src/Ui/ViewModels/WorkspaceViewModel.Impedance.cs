using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  The Impedance panel (brief-impedance-3). The layout toolbar's Z0 tile used to open a modal dialog;
//  it now puts this panel on screen for the layout it was pressed in, the way Design ▸ Find Instance…
//  puts the Instances panel on screen: where it was if anywhere is remembered, otherwise docked beside
//  a side-column panel — its settings and rows need height, which the bottom strip does not have.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WorkspaceViewModel
{
    /// <summary>Shows and focuses the Impedance panel, pointed at <paramref name="layout"/>.</summary>
    public void ShowImpedancePanel(LayoutEditorViewModel layout)
    {
        if (_factory.ImpedanceTool is not { } tool) return;
        tool.SetActiveLayout(layout);

        try
        {
            if (_factory.IsToolAutoHidden(tool) || _factory.TryFindTool(tool, out _, out _))
            {
                ShowToolPanelCore(DockPanelIds.Impedance);
                return;
            }
            if (RestorePanelToItsHome(DockPanelIds.Impedance, tool)) return;
            if (DockPanelBesideSibling(tool, _factory.PropertiesTool, _factory.AnalysesTool, _factory.ProjectTreeTool)) return;

            ShowToolPanelCore(DockPanelIds.Impedance);
        }
        finally
        {
            RaiseToolPanelVisibilityChanged();
        }
    }
}
