using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.Views.Yield;

/// <summary>The Correlations… grid (brief-yield-10 R-ya10-3). Everything it does is on
/// <see cref="CorrelationsEditorViewModel"/>; OK closes only when the panel accepted the lines.</summary>
public partial class CorrelationsDialog : Window
{
    private readonly CorrelationsEditorViewModel? _vm;

    public CorrelationsDialog() => InitializeComponent();

    public CorrelationsDialog(CorrelationsEditorViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.CommitCommand.CanExecute(null)) return;
        _vm.CommitCommand.Execute(null);
        if (_vm.Refusal is null) Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
