using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Tuning panel (brief-tuneopt-4). Follows the focused schematic exactly as the
/// Instances panel does — cleared first and set again only for a <c>.csch</c> — and tabbed behind
/// Analyses by default. The panel itself is <see cref="TuningPanelViewModel"/>, which knows nothing of
/// Dock.
/// </summary>
public sealed class TuningTool : Tool
{
    public TuningTool()
    {
        Id    = DockPanelIds.Tuning;
        Title = "Tuning";
    }

    public TuningPanelViewModel Panel { get; } = new();
}
