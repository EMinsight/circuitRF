using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>brief-em3d-53 R-em3d53-5 — the 3D view's Materials dialog. Closes true on an accepted OK; every rule is the
/// view model's.</summary>
public partial class MaterialPickerDialog : Window
{
    public MaterialPickerDialog() => InitializeComponent();

    public MaterialPickerDialog(MaterialPickerViewModel vm) : this()
    {
        DataContext = vm;
        Opened += (_, _) => vm.Begin();
        // Ctrl/⌘+Z and Ctrl/⌘+Shift+Z on the dialog's own history — railRF's handler, for railRF's reason: a key binding would
        // take the key from a text box, whose own undo it is while one has focus.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Key != Key.Z || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
                return;
            if (CircuitRF.Ui.RailRf.RailKeyboardGate.IsTextEntry(FocusManager?.GetFocusedElement())) return;
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? vm.RedoCommand : vm.UndoCommand;
            if (!command.CanExecute(null)) return;
            command.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Bubble);
    }

    private MaterialPickerViewModel? Vm => DataContext as MaterialPickerViewModel;

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // A field still being typed in commits on focus loss; take it before validating.
        (sender as Control)?.Focus();
        if (vm.Accept() is { } why) { vm.Refusal = why; return; }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnHelp(object? sender, RoutedEventArgs e) => DocLauncher.Open(CircuitRF.Ui.Layout.MaterialsTableViewModel.HelpPage);
}
