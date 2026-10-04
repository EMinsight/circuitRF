using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-111 — File ▸ Export ▸ glTF…: the options after the save dialog, the summary, then Export. Holds no state of its own:
/// <see cref="GltfExportDialogViewModel"/> builds and writes through GltfExport. Returns true once the file is written.
/// </summary>
public partial class GltfExportDialog : Window
{
    public GltfExportDialog() => InitializeComponent();

    public GltfExportDialog(GltfExportDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
        Opened += async (_, _) => await vm.PlanAsync();
    }
}
