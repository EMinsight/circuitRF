using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// 3D vector copy and drawing export (2026-09-27) — Export Drawing…'s choices. Holds no state of its own
/// (<see cref="Drawing3DDialogViewModel"/>); returns true with the view model's Request set, and the caller asks where
/// to write it.
/// </summary>
public partial class Drawing3DDialog : Window
{
    public Drawing3DDialog() => InitializeComponent();

    public Drawing3DDialog(Drawing3DDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += ok => Close(ok);
    }
}
