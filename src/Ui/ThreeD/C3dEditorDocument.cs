// brief-em3d-43 R-em3d43-1a — the 3D editor as a Dock document. It mirrors EmSetupDocument: a .c3d is a
// cell's view, so it is never scratch — FilePath is set once and the tab carries the dirty mark every other
// editor does. The 3D pane inside is brief 28's, whose GPU device and buffers live in the view model's
// session, so a float or a re-dock re-imports three images and uploads no geometry (R-em3d28-1d).

using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.ThreeD;

public sealed class C3dEditorDocument : Document, IUndoableDocument, IActivatableDocument, IFileBackedDocument
{
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;
    private bool _isDirty;

    public C3dEditorViewModel ViewModel { get; }
    public UndoRedoStack UndoRedo => ViewModel.UndoRedo;
    /// <summary>The TOP document's file — the tab's, whatever frame is pushed in (brief-em3d-48).</summary>
    public string FilePath => ViewModel.TopFilePath;

    /// <summary>The key a .c3d is registered under among the open documents: its full path.</summary>
    public static string KeyFor(string path) => Path.GetFullPath(path);

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
        }
    }

    /// <summary>Save As landed: the tab follows the new file.</summary>
    public void FollowSavedAs()
    {
        _baseTitle = Path.GetFileName(ViewModel.TopFilePath);
        Id = KeyFor(ViewModel.TopFilePath);
        Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }

    public C3dEditorDocument(C3dEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        _baseTitle = Path.GetFileName(viewModel.FilePath);
        Id = KeyFor(viewModel.FilePath);
        Title = _baseTitle;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(C3dEditorViewModel.IsDirty)) IsDirty = ViewModel.IsDirty;
        };
    }
}
