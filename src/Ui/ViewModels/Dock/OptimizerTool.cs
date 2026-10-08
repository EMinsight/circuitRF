using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Optimization;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Optimizer panel (brief-tuneopt-10). Follows the focused schematic exactly as the
/// Tuning panel does, and is tabbed behind it by default. The panel itself is
/// <see cref="OptimizerPanelViewModel"/>, which knows nothing of Dock.
/// </summary>
public sealed class OptimizerTool : Tool
{
    public OptimizerTool()
    {
        Id    = DockPanelIds.Optimizer;
        Title = "Optimizer";
    }

    public OptimizerPanelViewModel Panel { get; } = new();
}
