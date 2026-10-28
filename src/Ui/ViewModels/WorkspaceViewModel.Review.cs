using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  The Review panel (brief-review-6). Design ▸ Review Layout… and the layout toolbar's Review button put it
//  on screen for the active layout the way the Z0 tile puts the Impedance panel there: where it was if
//  anywhere is remembered, otherwise docked beside a side-column panel — its checks and rows need height.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WorkspaceViewModel
{
    /// <summary>Points the Review panel at <paramref name="layout"/> (or none), and gives it the way to the
    /// Impedance panel its impedance row opens.</summary>
    private void SetReviewLayout(LayoutEditorViewModel? layout)
    {
        if (_factory.ReviewTool is not { } tool) return;
        tool.Panel.OpenImpedancePanel ??= ShowImpedancePanel;
        tool.SetActiveLayout(layout);
    }

    /// <summary>Design ▸ Review Layout… — the Review panel on the active layout. Running it is the panel's Run:
    /// a review of a large board takes a while, and the reviewer first chooses which checks and thresholds.</summary>
    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ReviewLayout()
    {
        if (ResolveActiveDocumentForCommands() is LayoutDocument doc) ShowReviewPanel(doc.ActiveViewModel);
    }

    /// <summary>Shows and focuses the Review panel, pointed at <paramref name="layout"/>.</summary>
    public void ShowReviewPanel(LayoutEditorViewModel layout)
    {
        if (_factory.ReviewTool is not { } tool) return;
        SetReviewLayout(layout);

        try
        {
            if (_factory.IsToolAutoHidden(tool) || _factory.TryFindTool(tool, out _, out _))
            {
                ShowToolPanelCore(DockPanelIds.Review);
                return;
            }
            if (RestorePanelToItsHome(DockPanelIds.Review, tool)) return;
            if (DockPanelBesideSibling(tool, _factory.ImpedanceTool, _factory.PropertiesTool, _factory.AnalysesTool, _factory.ProjectTreeTool)) return;

            ShowToolPanelCore(DockPanelIds.Review);
        }
        finally
        {
            RaiseToolPanelVisibilityChanged();
        }
    }
}
