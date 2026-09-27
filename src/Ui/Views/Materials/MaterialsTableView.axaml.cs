using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace CircuitRF.Ui.Views.Materials;

/// <summary>brief-em3d-53 R-em3d53-4 — the Materials table. Its only code is Enter-commits-the-field; every rule
/// is the view model's.</summary>
public partial class MaterialsTableView : UserControl
{
    public MaterialsTableView() => InitializeComponent();

    /// <summary>Enter commits a field as leaving it does (the bindings update on LostFocus); Escape puts the
    /// shown value back.</summary>
    private void OnCellKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }
}
