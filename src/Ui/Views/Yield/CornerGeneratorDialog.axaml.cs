using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.Views.Yield;

/// <summary>Generate… (brief-yield-10 R-ya10-9). Everything it does is on <see cref="CornerGeneratorViewModel"/>;
/// Write closes only when the panel accepted the corners.</summary>
public partial class CornerGeneratorDialog : Window
{
    private readonly CornerGeneratorViewModel? _vm;

    public CornerGeneratorDialog() => InitializeComponent();

    public CornerGeneratorDialog(CornerGeneratorViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private void OnWrite(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.WriteCommand.CanExecute(null)) return;
        _vm.WriteCommand.Execute(null);
        if (_vm.Refusal is null) Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
