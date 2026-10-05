using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Materials;

/// <summary>brief-em3d-53 R-em3d53-4a — the <c>.cmat</c> document's view: a header, with Help at its right, and the Materials
/// table.</summary>
public partial class MaterialsEditorView : UserControl
{
    public MaterialsEditorView() => InitializeComponent();

    private void OnHelp(object? sender, RoutedEventArgs e) => DocLauncher.Open(MaterialsTableViewModel.HelpPage);
}
