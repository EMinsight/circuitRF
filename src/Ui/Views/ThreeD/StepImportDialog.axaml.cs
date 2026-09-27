using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-68 — File ▸ Import ▸ STEP… and 3D ▸ Import STEP…: the file's parts as a table, then OK. Holds no state of
/// its own: <see cref="StepImportDialogViewModel"/> reads and keeps the table; the editor commits. Returns true on OK.
/// Closing the window any other way cancels a read in flight.
/// </summary>
public partial class StepImportDialog : Window
{
    public StepImportDialog() => InitializeComponent();

    public StepImportDialog(StepImportDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
        Opened += async (_, _) => await vm.StartAsync();
        Closing += (_, _) => { if (vm.Reading) vm.CancelRead(); };
    }
}
