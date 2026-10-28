using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Review;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Review panel (brief-review-6 R-rev6-1) — <see cref="ImpedanceTool"/>'s shape: it follows
/// the active LAYOUT document, and the result it shows is held on that layout's view model, so switching
/// documents switches results.
/// </summary>
public sealed class ReviewTool : Tool
{
    public ReviewTool()
    {
        Id    = DockPanelIds.Review;
        Title = "Review";
    }

    /// <summary>The panel itself — checks, run, results, acceptances.</summary>
    public ReviewPanelViewModel Panel { get; } = new();

    public void SetActiveLayout(LayoutEditorViewModel? vm) => Panel.SetEditor(vm);
}
