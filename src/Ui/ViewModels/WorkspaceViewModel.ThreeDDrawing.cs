// 3D vector copy and drawing export (2026-09-27) — File ▸ Export ▸ Drawing…: the 3D pane's own Export Drawing…, reached
// from the menu bar. Enabled only while a 3D document (the .c3d editor, or a setup's read-only 3D view) is the one the
// shell's commands act on; the dialog, the sheet and the file are Viewer3DVectorExport's, the same code path as the pane's
// context menu.

using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.Ui.Views.Viewer3D;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>The active 3D document's file: the .c3d, or the setup a read-only 3D view shows.</summary>
    private string? Active3DDocumentPath() => ResolveActiveDocumentForCommands() switch
    {
        C3dEditorDocument e => e.ViewModel.FilePath,
        Viewer3DDocument v  => v.CemPath,
        _                   => null,
    };

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private async Task ExportDrawing(Window? owner)
    {
        if (Active3DPane() is not { } pane || Active3DDocumentPath() is not { } path) return;
        if (ResolveOwner(owner) is not { } window) return;
        await Viewer3DVectorExport.ExportDrawingAsync(window, pane, path, text =>
        {
            if (text.StartsWith("Exported", StringComparison.Ordinal)) Messages.Success(text, path);
            else Messages.Info(text, path);
        });
    }
}
