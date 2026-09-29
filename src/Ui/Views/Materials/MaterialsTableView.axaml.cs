using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Materials;

/// <summary>brief-em3d-53 R-em3d53-4 — the Materials editor. Its only code is Enter-commits-the-field and putting the caret
/// in a new material's name; every rule is the view model's.</summary>
public partial class MaterialsTableView : UserControl
{
    private MaterialsTableViewModel? _vm;

    public MaterialsTableView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.NameFocusRequested -= FocusName;
            _vm = DataContext as MaterialsTableViewModel;
            if (_vm is not null) _vm.NameFocusRequested += FocusName;
        };
    }

    /// <summary>A new or duplicated material's name, selected to be typed over — after the form has bound to it.</summary>
    private void FocusName()
        => Dispatcher.UIThread.Post(() =>
        {
            if (!NameBox.IsReadOnly && NameBox.Focus()) NameBox.SelectAll();
        }, DispatcherPriority.Background);

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
