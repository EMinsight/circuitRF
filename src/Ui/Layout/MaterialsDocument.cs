using System;
using System.IO;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Dock Document for an open <c>.cmat</c> material library (brief-em3d-53 R-em3d53-4a) — modelled on the part
/// library's document: a file that exists on disk before it is a document, opened through the workspace's
/// own open-or-activate path, which gives it one session per path, a dirty mark, Save, Save As and undo by
/// construction.
/// </summary>
public sealed class MaterialsDocument : Document, IUndoableDocument, IActivatableDocument, IFileBackedDocument
{
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    private string _baseTitle;

    public MaterialsEditorViewModel ViewModel { get; }
    public UndoRedoStack              UndoRedo  => ViewModel.UndoRedo;

    /// <summary>Absolute on-disk path of the <c>.cmat</c>. Never null — see class header.</summary>
    public string FilePath { get; private set; }

    private bool _isDirty;

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

    public MaterialsDocument(string title, MaterialsEditorViewModel viewModel, string filePath)
    {
        _baseTitle = title;
        Id         = title;
        Title      = title;
        FilePath   = filePath;
        ViewModel  = viewModel;
        _isDirty   = false;

        ViewModel.LibrarySavedAs += OnSavedAs;

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MaterialsEditorViewModel.IsDirty))
                IsDirty = ViewModel.IsDirty;
        };
    }

    /// <summary>Save As landed: follow the new file.</summary>
    private void OnSavedAs(string oldPath, string newPath)
    {
        FilePath   = newPath;
        _baseTitle = Path.GetFileName(newPath);
        Id         = _baseTitle;
        Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
    }
}
