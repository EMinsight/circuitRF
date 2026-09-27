// brief-em3d-28 R-em3d28-5 — the 3D view as a Dock document, opened from a 3D .cem's panel (Show 3D)
// or from a Palace/openEMS run directory in the Project Tree. Read only: nothing here is saved, so it
// is never dirty and carries no undo. Its camera is saved in the workspace's window state (the
// .cwsuser), not in the .cem. It is file-backed in the one sense the tab menu asks about: Reveal shows the
// .cem it draws (brief-em3d-43; it has no Save route — see WorkspaceViewModel.TabSave.HasSaveRoute).
//
// 3D editor bugs round 5 — it is DRAWN by the 3D editor's view (C3dEditorView), through Editor: the editor's view model
// built around this view's own Viewer3DViewModel, with nothing to edit (C3dEditorViewModel.IsViewOnly). The dock
// document stays this class, so every shell route that edits, saves or runs still never resolves to it.

using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ThreeD;
using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DDocument : Document, IFileBackedDocument
{
    /// <summary>The key a 3D view is registered under among the open documents — distinct from the
    /// .cem's own, so the setup and its view are two tabs.</summary>
    public static string KeyFor(string cemPath) => "3d:" + Path.GetFullPath(cemPath);

    public Viewer3DViewModel ViewModel { get; }

    /// <summary>What the editor's view binds to: never docked, never saved — the holder of the view-only editor model.</summary>
    public C3dEditorDocument Editor { get; }
    public string CemPath => ViewModel.CemPath;

    /// <summary>The setup this view draws — what the tab's Reveal shows.</summary>
    public string? FilePath => ViewModel.CemPath;

    public Viewer3DDocument(Viewer3DViewModel vm)
    {
        ViewModel = vm;
        Editor = new C3dEditorDocument(new C3dEditorViewModel(vm));
        Id = KeyFor(vm.CemPath);
        Title = vm.Title;
    }
}
