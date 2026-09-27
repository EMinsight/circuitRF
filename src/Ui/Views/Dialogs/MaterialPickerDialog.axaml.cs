using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>brief-em3d-53 R-em3d53-5 — the material picker. Closes true on an accepted OK; every rule is the view model's.</summary>
public partial class MaterialPickerDialog : Window
{
    public MaterialPickerDialog() => InitializeComponent();

    public MaterialPickerDialog(MaterialPickerViewModel vm) : this() => DataContext = vm;

    private MaterialPickerViewModel? Vm => DataContext as MaterialPickerViewModel;

    private void OnNew(object? sender, RoutedEventArgs e) { if (Vm is { } vm) vm.IsNew = true; }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // A field still being typed in commits on focus loss; take it before validating.
        (sender as Control)?.Focus();
        if (vm.Accept() is { } why) { vm.Refusal = why; return; }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnChoiceDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (Vm is { IsNew: false, SelectedChoice: not null }) Close(true);
    }
}
