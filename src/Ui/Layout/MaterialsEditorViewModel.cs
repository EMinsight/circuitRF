using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// brief-em3d-53 R-em3d53-4a — a <c>.cmat</c> material library as its own document: the Materials table
/// over the file's list, with a dirty mark, Save and whole-list snapshot undo, exactly as
/// <see cref="TechEditorViewModel"/> snapshots a technology.
///
/// <para>Every committed edit raises <see cref="LibraryLiveChanged"/> with a deep copy — the workspace's cue
/// to install it as the cache's live library override, so every technology naming this library re-resolves
/// against the unsaved edit (R-em3d53-1f). A save raises <see cref="LibrarySaved"/>.</para>
/// </summary>
public sealed partial class MaterialsEditorViewModel : ObservableObject
{
    public string FilePath { get; private set; }

    /// <summary>The library's records — replaced wholesale by undo and redo.</summary>
    public List<TechMaterial> Working { get; private set; }

    public UndoRedoStack UndoRedo { get; } = new();
    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    public IRelayCommand SaveCommand { get; }

    [ObservableProperty] private bool _isDirty;

    /// <summary>The table — the same view model the technology editor's Materials tab hosts.</summary>
    public MaterialsTableViewModel Table { get; }

    /// <summary>The technologies in the workspace that name this library (the R-em3d53-1e walk) — an edit
    /// here changes all of them, which the header says.</summary>
    [ObservableProperty] private IReadOnlyList<string> _namedBy = [];

    public string NamedByText => NamedBy.Count == 0
        ? "No technology in this workspace names this library yet — a .ctech lists it under MaterialLibraries."
        : "Named by " + string.Join(", ", NamedBy.Select(Path.GetFileName)) +
          ". An edit here changes every one of them.";

    partial void OnNamedByChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(NamedByText));

    public event Action<string, IReadOnlyList<TechMaterial>>? LibraryLiveChanged;
    public event Action<string>? LibrarySaved;
    public event Action<string, string>? LibrarySavedAs;
    public event Action<string>? SaveError;

    public MaterialsEditorViewModel(string filePath, List<TechMaterial> materials)
    {
        FilePath = Path.GetFullPath(filePath);
        Working = materials;
        Table = new MaterialsTableViewModel(() => Working, Commit, "this library", FilePath) { OffersBuiltIns = true };

        UndoCommand = new RelayCommand(() => UndoRedo.Undo(), () => UndoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => UndoRedo.Redo(), () => UndoRedo.CanRedo);
        SaveCommand = new RelayCommand(Save);
        UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.IsModified)) IsDirty = UndoRedo.IsModified;
        };
    }

    internal string SnapshotJson() => MaterialLibraryPersistence.Serialize(Working);

    /// <summary>The table's commit: the mutation runs on the live list, then one snapshot entry is pushed.
    /// No-op when nothing changed.</summary>
    private void Commit(Action mutate, string description)
    {
        string before = SnapshotJson();
        mutate();
        string after = SnapshotJson();
        if (after == before) return;
        UndoRedo.Execute(new Snapshot(this, before, after, description));
    }

    /// <summary>Pushes one entry that replaces the list — for an edit decided outside the table (a rename
    /// that spans files, R-em3d53-4c; a row added from the 3D editor's New Material…).</summary>
    public void CommitEdit(Action<List<TechMaterial>> mutate, string description) => Commit(() => mutate(Working), description);

    internal void ApplySnapshot(string json)
    {
        Working = MaterialLibraryPersistence.Deserialize(json);
        Table.Rebuild();
        LibraryLiveChanged?.Invoke(FilePath, MaterialLibraryPersistence.Deserialize(json));
    }

    private void Save()
    {
        try { MaterialLibraryPersistence.SaveToFile(FilePath, Working); }
        catch (Exception ex) { SaveError?.Invoke($"Couldn't save material library to '{FilePath}': {ex.Message}"); return; }
        UndoRedo.MarkSaved();
        LibrarySaved?.Invoke(FilePath);
    }

    /// <summary>Writes the library to a different <c>.cmat</c> and follows it. Technologies naming the old file go
    /// on naming it — repointing them is an edit to each technology, not to this file.</summary>
    public void SaveAs(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return;
        newPath = Path.GetFullPath(newPath);
        try { MaterialLibraryPersistence.SaveToFile(newPath, Working); }
        catch (Exception ex) { SaveError?.Invoke($"Couldn't save material library to '{newPath}': {ex.Message}"); return; }
        string old = FilePath;
        FilePath = newPath;
        UndoRedo.MarkSaved();
        LibrarySavedAs?.Invoke(old, newPath);
    }

    private sealed class Snapshot(MaterialsEditorViewModel owner, string before, string after, string description) : IUiCommand
    {
        public string Description { get; } = description;
        public void Execute() => owner.ApplySnapshot(after);
        public void Undo() => owner.ApplySnapshot(before);
    }
}
