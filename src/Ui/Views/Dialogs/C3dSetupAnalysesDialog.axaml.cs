using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Simulate ▸ Setup Analyses… for a 3D view: its embedded EM setups as analysis cards (<see cref="CircuitRF.Ui.Views.ThreeD.C3dSetupAnalysesView"/>,
/// which the Analyses panel shows for an active .c3d too) and a Close button. Open via
/// <c>WorkspaceViewModel.SetupAnalysesCommand</c> (or the 3D editor's tune button).
/// </summary>
public partial class C3dSetupAnalysesDialog : Window
{
    public C3dSetupAnalysesDialog() => InitializeComponent();

    public C3dSetupAnalysesDialog(C3dEditorViewModel vm) : this()
    {
        DataContext = vm;
        Title = $"Setup Analyses — {Path.GetFileName(vm.TopFilePath)}";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
