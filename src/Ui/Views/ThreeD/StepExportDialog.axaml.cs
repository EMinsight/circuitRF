using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-69 — File ▸ Export ▸ STEP… and 3D ▸ Export STEP…: the options after the save dialog, the summary, then Export.
/// Holds no state of its own: <see cref="StepExportDialogViewModel"/> plans and writes through StepExport. Returns true once
/// the file is written. Closing the window any other way cancels an export in flight, which then writes nothing.
/// </summary>
public partial class StepExportDialog : Window
{
    public StepExportDialog() => InitializeComponent();

    public StepExportDialog(StepExportDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
        Opened += async (_, _) => await vm.PlanAsync();
        Closing += (_, _) => { if (vm.Writing) vm.CancelWrite(); };
    }
}
