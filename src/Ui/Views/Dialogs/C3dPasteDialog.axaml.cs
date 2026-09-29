using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-em3d-95 — the one Paste dialog for a 3D paste (<see cref="C3dPasteDialogViewModel"/>). Paste and Paste Without
/// Creating first ask <c>refusal</c> — Apply on a copy — so a cycle a Reuse closes is said HERE and the dialog stays open for a
/// rename; Cancel (Esc, or the window's close) pastes nothing and creates nothing.
/// </summary>
public partial class C3dPasteDialog : Window
{
    private readonly C3dPasteDialogViewModel? _vm;
    private readonly Func<C3dPasteChoices, string?>? _refusal;

    public C3dPasteDialog() => InitializeComponent();

    public C3dPasteDialog(C3dPasteDialogViewModel vm, Func<C3dPasteChoices, string?> refusal) : this()
    {
        _vm = vm;
        _refusal = refusal;
        DataContext = vm;
    }

    private void OnPasteClick(object? sender, RoutedEventArgs e) => Commit(C3dPasteDialogAnswer.Paste, create: true);

    private void OnPasteWithoutCreatingClick(object? sender, RoutedEventArgs e) => Commit(C3dPasteDialogAnswer.PasteWithoutCreating, create: false);

    private void OnChooseTechnologyClick(object? sender, RoutedEventArgs e)
        => Close(new C3dPasteDialogResult(C3dPasteDialogAnswer.ChooseTechnology, new C3dPasteChoices()));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void Commit(C3dPasteDialogAnswer answer, bool create)
    {
        if (_vm is null || !_vm.IsValid) return;
        var choices = _vm.Choices(create);
        if (_refusal?.Invoke(choices) is { } why)
        {
            _vm.Refusal = why;
            return;
        }
        Close(new C3dPasteDialogResult(answer, choices));
    }
}
