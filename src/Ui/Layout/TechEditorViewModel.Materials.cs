using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Layout;

/// <summary>One library a technology names, as the Materials tab lists it: where the reference lands and
/// whether it loaded.</summary>
/// <param name="Reference">As the <c>.ctech</c> stores it.</param>
/// <param name="ResolvedPath">Where it lands, relative to the <c>.ctech</c>'s directory.</param>
/// <param name="State">"14 materials", or why it did not load.</param>
public sealed record MaterialLibraryRow(string Reference, string ResolvedPath, string State, bool Loaded)
{
    public string FileName => Path.GetFileName(ResolvedPath);
}

/// <summary>
/// brief-em3d-53 R-em3d53-4b — the technology editor's fifth tab, <i>Materials</i>. The technology's OWN rows
/// are edited in place, on the technology's snapshot undo; library rows are shown below them read-only and
/// edited in their own <c>.cmat</c> document (M4). Adding, creating and removing a library reference, and
/// <i>Add Generic Materials</i>, are each ONE technology undo entry.
///
/// <para><b>A technology whose libraries refuse to load still opens here</b> (§1d): the editor holds the
/// file's own content, and the refusal is shown as this tab's problems, each under its own <c>check</c> id.
/// Nobody could fix a conflict otherwise.</para>
/// </summary>
public sealed partial class TechEditorViewModel
{
    /// <summary>The tab index of Materials — the fifth tab.</summary>
    public const int MaterialsTabIndex = 4;

    private Func<string, MaterialLibraryLoader> _libraryLoader = p => MaterialLibraries.Disk(p);
    private IReadOnlyList<MaterialLibraryProblem> _libraryProblems = [];

    /// <summary>The Materials table over <see cref="Technology.Materials"/> — the own list.</summary>
    public MaterialsTableViewModel MaterialsTable { get; private set; } = null!;

    /// <summary>The libraries this technology names, with each one's load state.</summary>
    public ObservableCollection<MaterialLibraryRow> MaterialLibraryRows { get; } = [];

    [ObservableProperty] private MaterialLibraryRow? _selectedMaterialLibrary;

    /// <summary>The refusal a library resolution produced, or null — also listed as this tab's problems.</summary>
    [ObservableProperty] private string? _libraryRefusal;

    public string MaterialsTabHeader => TabHeader("Materials", TechProblemArea.Materials);

    /// <summary>Shown while the technology does not already name circuitRF's generic library.</summary>
    public bool CanAddGenericMaterials =>
        !(Working.MaterialLibraries ?? []).Any(r => string.Equals(Path.GetFileName(Core.RefPath.ToNative(r)),
                                                                   MaterialLibraries.GenericFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Uses outside this technology — open 3D objects — which the workspace supplies. Null: none.</summary>
    public Func<string, IReadOnlyList<string>>? ExternalMaterialUses { get; set; }

    /// <summary>Raised by <i>Open Library</i>: the library's absolute path, and the material to select (or null).</summary>
    public event Action<string, string?>? OpenLibraryRequested;

    /// <summary>A gesture of this tab was refused — the sentence to show.</summary>
    [ObservableProperty] private string? _materialsRefusal;

    /// <summary>Installs the loader libraries are read through — the workspace passes its cache's, so a library
    /// open with unsaved edits is read as edited — and re-resolves.</summary>
    public void UseLibraryLoader(Func<string, MaterialLibraryLoader> loader)
    {
        _libraryLoader = loader;
        ResolveLibrariesInPlace();
        RebuildAll();
    }

    private void InitMaterials()
    {
        MaterialsTable = new MaterialsTableViewModel(() => Working.Materials, (mutate, description) =>
        {
            var before = SnapshotJson();
            mutate();
            CommitEdit(before, description);
        }, "this technology")
        {
            UsedBy = UsesOf,
            OpenLibrary = row => { if (row.LibrarySource is { } lib) OpenLibraryRequested?.Invoke(lib, row.Name); },
        };
        ResolveLibrariesInPlace();
    }

    /// <summary>Resolves <see cref="Working"/>'s libraries; a refusal is kept as this tab's problems, and the
    /// technology is left with its own materials only. Re-applies the named materials either way.</summary>
    private void ResolveLibrariesInPlace()
    {
        try
        {
            TechPersistence.ResolveLibraries(Working, _libraryLoader(FilePath), FilePath);
            _libraryProblems = [];
            LibraryRefusal = null;
        }
        catch (MaterialLibraryException ex)
        {
            _libraryProblems = ex.Problems;
            LibraryRefusal = ex.Message;
            TechPersistence.ResolveMaterials(Working);
        }
    }

    private void RebuildMaterials()
    {
        MaterialLibraryRows.Clear();
        foreach (string reference in Working.MaterialLibraries ?? [])
        {
            string path = MaterialLibraries.ResolvePath(FilePath, reference);
            int n = Working.LibraryMaterials.Count(m => string.Equals(m.SourcePath, path, StringComparison.OrdinalIgnoreCase));
            var problem = _libraryProblems.FirstOrDefault(p => p.Id == MaterialLibraries.LibraryMissingId && p.Message.Contains(path, StringComparison.Ordinal));
            bool loaded = Working.ResolvedLibraryPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
            MaterialLibraryRows.Add(new MaterialLibraryRow(reference, path,
                problem is not null ? "does not load" : loaded ? $"{n} material{(n == 1 ? "" : "s")}" : "not resolved (see the problems above)",
                loaded));
        }
        MaterialsTable?.Rebuild(Working.LibraryMaterials);
        OnPropertyChanged(nameof(CanAddGenericMaterials));
        OnPropertyChanged(nameof(MaterialsTabHeader));
    }

    /// <summary>The problems the libraries produced, as this tab's.</summary>
    private IEnumerable<TechProblem> LibraryProblems()
        => _libraryProblems.Select(p => new TechProblem(TechProblemArea.Materials, p.Message, Id: p.Id, Severity: DiagnosticSeverity.Error));

    /// <summary>Where a material is used: this technology's stackup entries and bodies, and whatever the workspace
    /// adds (open 3D objects).</summary>
    private IReadOnlyList<string> UsesOf(string name)
    {
        var uses = new List<string>();
        foreach (var l in Working.Stackup.Layers)
            if (string.Equals(l.Material, name, StringComparison.OrdinalIgnoreCase)) uses.Add($"stackup entry '{l.Name}'");
        foreach (var b in Working.Bodies)
            if (string.Equals(b.Material, name, StringComparison.OrdinalIgnoreCase)) uses.Add($"body '{b.Name}'");
        if (ExternalMaterialUses?.Invoke(name) is { } more) uses.AddRange(more);
        return uses;
    }

    // ── library references: each ONE technology undo entry ───────────────────────────────────

    /// <summary><i>Add Library…</i> — names an existing <c>.cmat</c>. Refused for a file that is not one, or one
    /// already named.</summary>
    public bool AddLibrary(string path)
    {
        MaterialsRefusal = null;
        path = Path.GetFullPath(path);
        try { MaterialLibraryPersistence.LoadFromFile(path); }
        catch (Exception ex) { MaterialsRefusal = $"'{Path.GetFileName(path)}' is not a material library: {ex.Message}"; return false; }
        return AddReference(MaterialLibraries.RelativeReference(FilePath, path), $"Add material library {Path.GetFileName(path)}");
    }

    /// <summary><i>New Library…</i> — writes an empty <c>.cmat</c> at once (as New Cell writes its file) and names
    /// it. Undo removes the reference and leaves the file.</summary>
    public bool NewLibrary(string path)
    {
        MaterialsRefusal = null;
        string reference;
        try { reference = MaterialLibraryCreate.Create(path, FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MaterialsRefusal = ex.Message; return false; }
        return AddReference(reference, $"New material library {Path.GetFileName(path)}");
    }

    /// <summary><i>Add Generic Materials</i> (R-em3d53-8d) — copies circuitRF's generic library beside the
    /// <c>.ctech</c> (refused if a different file of that name is there) and names it.</summary>
    [RelayCommand]
    public void AddGenericMaterials()
    {
        MaterialsRefusal = null;
        string reference;
        try { reference = MaterialLibraries.CopyGenericBeside(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MaterialsRefusal = ex.Message; return; }
        AddReference(reference, "Add Generic Materials");
    }

    /// <summary><i>Remove Library</i> — the reference only, never the file.</summary>
    [RelayCommand]
    public void RemoveLibrary(MaterialLibraryRow? row)
    {
        row ??= SelectedMaterialLibrary;
        if (row is null) return;
        var before = SnapshotJson();
        Working.MaterialLibraries?.Remove(row.Reference);
        if (Working.MaterialLibraries is { Count: 0 }) Working.MaterialLibraries = null;
        CommitEdit(before, $"Remove material library {row.FileName}");
    }

    /// <summary><i>Open Library</i> — the library's own document, with <paramref name="material"/> selected.</summary>
    [RelayCommand]
    public void OpenLibrary(MaterialLibraryRow? row)
    {
        row ??= SelectedMaterialLibrary;
        if (row is null) return;
        OpenLibraryRequested?.Invoke(row.ResolvedPath, MaterialsTable.SelectedRow is { IsLibrary: true } sel
            && string.Equals(sel.LibrarySource, row.ResolvedPath, StringComparison.OrdinalIgnoreCase) ? sel.Name : null);
    }

    /// <summary>Opens the library the selected (read-only) row came from.</summary>
    [RelayCommand]
    public void OpenSelectedRowLibrary()
    {
        if (MaterialsTable.SelectedRow is not { LibrarySource: { } source } row) return;
        OpenLibraryRequested?.Invoke(source, row.Name);
    }

    private bool AddReference(string reference, string description)
    {
        if ((Working.MaterialLibraries ?? []).Any(r => string.Equals(
                MaterialLibraries.ResolvePath(FilePath, r), MaterialLibraries.ResolvePath(FilePath, reference), StringComparison.OrdinalIgnoreCase)))
        {
            MaterialsRefusal = $"This technology already names '{reference}'.";
            return false;
        }
        var before = SnapshotJson();
        (Working.MaterialLibraries ??= []).Add(reference);
        CommitEdit(before, description);
        return true;
    }

    /// <summary>
    /// Adds <paramref name="material"/> to the technology's own list as ONE undo entry — the 3D editor's
    /// <i>New Material…</i> saving here (R-em3d53-5). Refused for a name already defined.
    /// </summary>
    public string? AddOwnMaterial(TechMaterial material)
    {
        if (MaterialValidation.NameRefusal(material.Name) is { } why) return why;
        if (Working.FindMaterial(material.Name) is not null) return $"'{material.Name}' is already a material of this technology.";
        var before = SnapshotJson();
        Working.Materials.Add(material);
        CommitEdit(before, $"Add material {material.Name}");
        return null;
    }

    /// <summary>
    /// Replaces the technology's own list with <paramref name="materials"/> as ONE undo entry — the 3D view's Materials
    /// dialog committing what it edited here. Refused when the list would not validate (a duplicate or illegal name).
    /// </summary>
    public string? ReplaceOwnMaterials(IReadOnlyList<TechMaterial> materials, string description)
    {
        if (MaterialValidation.Validate(materials).FirstOrDefault(p => p.Severity == DiagnosticSeverity.Error) is { } p) return p.Message;
        var before = SnapshotJson();
        Working.Materials.Clear();
        Working.Materials.AddRange(materials);
        CommitEdit(before, description);
        return null;
    }

    /// <summary>
    /// Renames a material everywhere THIS technology names it — its own list (when it defines it) and every stackup
    /// entry and body — as ONE entry on this technology's stack (R-em3d53-4c). Returns whether anything changed.
    /// </summary>
    public bool RenameMaterialReferences(string oldName, string newName, bool renameOwn)
    {
        var before = SnapshotJson();
        if (renameOwn)
            foreach (var m in Working.Materials)
                if (string.Equals(m.Name, oldName, StringComparison.OrdinalIgnoreCase)) m.Name = newName;
        foreach (var l in Working.Stackup.Layers)
            if (string.Equals(l.Material, oldName, StringComparison.OrdinalIgnoreCase)) l.Material = newName;
        foreach (var b in Working.Bodies)
            if (string.Equals(b.Material, oldName, StringComparison.OrdinalIgnoreCase)) b.Material = newName;
        if (SnapshotJson() == before) return false;
        CommitEdit(before, $"Rename material {oldName} → {newName}");
        return true;
    }

    /// <summary>Whether this technology names the library at <paramref name="absLibraryPath"/>.</summary>
    public bool NamesLibrary(string absLibraryPath)
        => (Working.MaterialLibraries ?? []).Any(r => string.Equals(MaterialLibraries.ResolvePath(FilePath, r),
                                                                     Path.GetFullPath(absLibraryPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a stackup entry or body of this technology names <paramref name="name"/>.</summary>
    public bool ReferencesMaterial(string name)
        => Working.Stackup.Layers.Any(l => string.Equals(l.Material, name, StringComparison.OrdinalIgnoreCase))
        || Working.Bodies.Any(b => string.Equals(b.Material, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A library this technology names changed (a live edit, a save): re-resolve and redraw the tab.</summary>
    public void OnLibraryChanged(string absLibraryPath)
    {
        if (!NamesLibrary(absLibraryPath)) return;
        ResolveLibrariesInPlace();
        RebuildAll();
    }

    /// <summary>Save As moves the <c>.ctech</c>; a relative library reference is re-pointed so it names the same file
    /// from the new place (R-em3d53-1e), as a layout's Save As re-points its references.</summary>
    private void RepointLibraries(string oldPath, string newPath)
    {
        if (Working.MaterialLibraries is not { Count: > 0 } refs) return;
        for (int i = 0; i < refs.Count; i++)
        {
            if (Path.IsPathRooted(Core.RefPath.ToNative(refs[i]))) continue;
            refs[i] = MaterialLibraries.RelativeReference(newPath, MaterialLibraries.ResolvePath(oldPath, refs[i]));
        }
    }
}
