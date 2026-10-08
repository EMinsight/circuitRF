using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Yield panel (brief-yield-10). Follows the focused schematic exactly as Tuning and the Optimizer
/// do, and is tabbed behind the Optimizer by default. The panel itself is <see cref="YieldPanelViewModel"/>, which
/// knows nothing of Dock.
/// </summary>
public sealed class YieldTool : Tool
{
    public YieldTool()
    {
        Id    = DockPanelIds.Yield;
        Title = "Yield";
    }

    public YieldPanelViewModel Panel { get; } = new();
}
