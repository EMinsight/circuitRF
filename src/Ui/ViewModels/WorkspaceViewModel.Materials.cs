// brief-em3d-53 — material libraries in the workspace: a .cmat as its own document (R-em3d53-4a), its live edits
// reaching every technology that names it through the cache (R-em3d53-1f), the technology editor's Materials tab
// (R-em3d53-4b), renames that span files (R-em3d53-4c), and the 3D editor's entry points (R-em3d53-5).

using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ThreeD;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    // ── the .cmat document ────────────────────────────────────────────────────────────────────

    /// <summary>Opens (or focuses) a <c>.cmat</c> as its own Materials document, with <paramref name="select"/>'s row
    /// selected. Modelled on the part library's open path: one session per path, a dirty mark, Save, Save As.</summary>
    public void OpenOrActivateMaterials(string absolutePath, string? select = null)
    {
        absolutePath = Path.GetFullPath(absolutePath);
        if (ActivateIfOpen(absolutePath))
        {
            if (select is not null && _openDocsByPath.TryGetValue(absolutePath, out var open) && open is MaterialsDocument m)
                m.ViewModel.Table.Select(select);
            return;
        }

        try
        {
            // An unsaved edit left in the cache (a document closed while its override stood) is what every technology
            // is resolving against; open on it rather than on the file.
            var materials = _techCache.LiveLibrary(absolutePath) is { } live
                ? MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize(live))
                : MaterialLibraryPersistence.LoadFromFile(absolutePath);
            var vm = new MaterialsEditorViewModel(absolutePath, materials);
            var doc = new MaterialsDocument(Path.GetFileName(absolutePath), vm, absolutePath);

            vm.SaveError += m => Messages.Error(m);
            vm.LibraryLiveChanged += (path, list) =>
            {
                _techCache.SetLiveLibrary(path, list);
                NotifyTechEditorsOfLibrary(path);
            };
            vm.LibrarySaved += path =>
            {
                Messages.Success("Saved", path);
                _techCache.ClearLiveLibrary(path);
                NotifyTechEditorsOfLibrary(path);
            };
            vm.LibrarySavedAs += (oldPath, newPath) =>
            {
                _openDocsByPath.Remove(oldPath);
                _openDocsByPath[newPath] = doc;
                _techCache.ClearLiveLibrary(oldPath);
                NotifyTechEditorsOfLibrary(oldPath);
                vm.NamedBy = LibraryUsers(newPath);
                _factory.ProjectTreeTool?.Refresh();
                Messages.Success("Saved", newPath);
            };

            vm.NamedBy = LibraryUsers(absolutePath);
            vm.Table.UsedBy = name => LibraryMaterialUses(vm.FilePath, name);
            vm.Table.RenameAcross = (oldName, newName) => RenameLibraryMaterial(doc, oldName, newName);

            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            HookMaterialsDirty(doc);

            // §1h — a library in a referenced workspace not toggled editable, or a read-only file, opens read-only
            // and says why, through the same per-document question every other document asks.
            if (IsDocumentReadOnly(doc))
            {
                vm.Table.IsReadOnly = true;
                vm.Table.ReadOnlyReason = ReadOnlyDocumentReason(doc) ?? $"'{Path.GetFileName(absolutePath)}' cannot be written.";
                vm.Table.Rebuild();
            }
            if (select is not null) vm.Table.Select(select);
            Messages.Info("Opened", absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open material library '{Path.GetFileName(absolutePath)}': {ex.Message}");
        }
    }

    private void HookMaterialsDirty(MaterialsDocument doc)
    {
        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(MaterialsEditorViewModel.IsDirty)) return;
            _factory.ProjectTreeTool?.SetFileDirty(doc.FilePath, doc.ViewModel.IsDirty);
            RaiseFileMenuEnablementChanged();
        };
    }

    private void SaveMaterialsByPath(string absPath)
    {
        var key = Path.GetFullPath(absPath);
        var doc = _openDocsByPath.Values.OfType<MaterialsDocument>().FirstOrDefault(d =>
            string.Equals(Path.GetFullPath(d.FilePath), key, StringComparison.OrdinalIgnoreCase));
        if (doc is { IsDirty: true }) doc.ViewModel.SaveCommand.Execute(null);
    }

    /// <summary>Don't Save: the library's live override goes, so every technology naming it re-reads the file.</summary>
    private void DiscardLiveMaterials(string absPath)
    {
        _techCache.ClearLiveLibrary(absPath);
        NotifyTechEditorsOfLibrary(absPath);
    }

    /// <summary>An open technology editor naming this library re-resolves (its Materials tab shows the new rows).</summary>
    private void NotifyTechEditorsOfLibrary(string absLibraryPath)
    {
        foreach (var td in _openDocsByPath.Values.OfType<TechDocument>())
            td.ViewModel.OnLibraryChanged(absLibraryPath);
    }

    /// <summary>The technologies in this workspace that name a library — the document header's list.</summary>
    private IReadOnlyList<string> LibraryUsers(string absLibraryPath)
        => MaterialLibraries.TechnologiesNaming(absLibraryPath, WorkspaceRootDir ?? Path.GetDirectoryName(absLibraryPath)!);

    /// <summary>A library material's uses: the stackup entries and bodies of every technology naming the library, and
    /// open 3D objects on those technologies.</summary>
    private IReadOnlyList<string> LibraryMaterialUses(string absLibraryPath, string name)
    {
        var uses = new List<string>();
        foreach (string ctech in LibraryUsers(absLibraryPath))
        {
            Technology? raw = OpenTechEditor(ctech)?.Working;
            if (raw is null) { try { raw = TechPersistence.LoadOwnFromFile(ctech); } catch { continue; } }
            if (raw.Materials.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var l in raw.Stackup.Layers.Where(l => string.Equals(l.Material, name, StringComparison.OrdinalIgnoreCase)))
                uses.Add($"stackup entry '{l.Name}' of {Path.GetFileName(ctech)}");
            foreach (var b in raw.Bodies.Where(b => string.Equals(b.Material, name, StringComparison.OrdinalIgnoreCase)))
                uses.Add($"body '{b.Name}' of {Path.GetFileName(ctech)}");
        }
        uses.AddRange(OpenC3dUsesOf(name, c3d => c3d.Elaboration?.Technology?.ResolvedLibraryPaths
                                                   .Contains(absLibraryPath, StringComparer.OrdinalIgnoreCase) == true));
        return uses;
    }

    private TechEditorViewModel? OpenTechEditor(string ctech)
        => _openDocsByPath.Values.OfType<TechDocument>()
                          .FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(ctech), StringComparison.OrdinalIgnoreCase))?.ViewModel;

    /// <summary>Open 3D objects naming <paramref name="name"/>, in the open .c3d documents <paramref name="which"/> accepts.</summary>
    private IReadOnlyList<string> OpenC3dUsesOf(string name, Func<C3dEditorViewModel, bool> which)
        => [.. _openDocsByPath.Values.OfType<C3dEditorDocument>().Select(d => d.ViewModel).Where(which)
                              .SelectMany(vm => vm.UsesOfMaterial(name))];

    // ── the technology editor's Materials tab ─────────────────────────────────────────────────

    private void HookTechMaterials(TechEditorViewModel vm)
    {
        vm.OpenLibraryRequested += (path, material) => OpenOrActivateMaterials(path, material);
        vm.ExternalMaterialUses = name => OpenC3dUsesOf(name, c3d => SamePath(c3d.Elaboration?.TechnologyPath, vm.FilePath));
        vm.MaterialsTable.RenameAcross = (oldName, newName) => RenameTechnologyMaterial(vm, oldName, newName);
        if (vm.LibraryRefusal is { } refusal)
            Messages.Warning($"'{Path.GetFileName(vm.FilePath)}' does not load: {refusal} It is open so it can be fixed on its Materials tab.");
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ── renames across files (R-em3d53-4c, M5) ────────────────────────────────────────────────
    //
    // There is no cross-document undo, and this does not invent one: each OPEN file that names the material gets one
    // entry on its own stack, named alike. An unopened file is listed, never rewritten — `check` then reports it as an
    // unknown material, which is the truth.

    private string? RenameTechnologyMaterial(TechEditorViewModel tech, string oldName, string newName)
    {
        tech.RenameMaterialReferences(oldName, newName, renameOwn: true);
        foreach (var c3d in _openDocsByPath.Values.OfType<C3dEditorDocument>())
            if (SamePath(c3d.ViewModel.Elaboration?.TechnologyPath, tech.FilePath)) c3d.ViewModel.RenameMaterial(oldName, newName);
        return UnopenedMentions(oldName, [], includeTechnologies: false);
    }

    private string? RenameLibraryMaterial(MaterialsDocument library, string oldName, string newName)
    {
        string lib = library.FilePath;
        library.ViewModel.CommitEdit(list =>
        {
            foreach (var m in list)
                if (string.Equals(m.Name, oldName, StringComparison.OrdinalIgnoreCase)) m.Name = newName;
        }, $"Rename material {oldName} → {newName}");

        var users = LibraryUsers(lib);
        foreach (var td in _openDocsByPath.Values.OfType<TechDocument>())
            if (td.ViewModel.NamesLibrary(lib) && !td.ViewModel.Working.Materials.Any(m => string.Equals(m.Name, oldName, StringComparison.OrdinalIgnoreCase)))
                td.ViewModel.RenameMaterialReferences(oldName, newName, renameOwn: false);
        foreach (var c3d in _openDocsByPath.Values.OfType<C3dEditorDocument>())
            if (c3d.ViewModel.Elaboration?.Technology?.ResolvedLibraryPaths.Contains(lib, StringComparer.OrdinalIgnoreCase) == true)
                c3d.ViewModel.RenameMaterial(oldName, newName);
        return UnopenedMentions(oldName, users, includeTechnologies: true);
    }

    /// <summary>The files not open that still name <paramref name="oldName"/>: technologies among
    /// <paramref name="technologies"/> (by stackup entry or body) and every <c>.c3d</c> under the workspace.</summary>
    private string? UnopenedMentions(string oldName, IReadOnlyList<string> technologies, bool includeTechnologies)
    {
        var listed = new List<string>();
        if (includeTechnologies)
            foreach (string ctech in technologies)
            {
                if (OpenTechEditor(ctech) is not null) continue;
                try
                {
                    var raw = TechPersistence.LoadOwnFromFile(ctech);
                    if (raw.Stackup.Layers.Any(l => string.Equals(l.Material, oldName, StringComparison.OrdinalIgnoreCase))
                        || raw.Bodies.Any(b => string.Equals(b.Material, oldName, StringComparison.OrdinalIgnoreCase)))
                        listed.Add(Path.GetFileName(ctech));
                }
                catch { /* unreadable: check reports it */ }
            }
        if (WorkspaceRootDir is { } root)
        {
            var open = _openDocsByPath.Values.OfType<C3dEditorDocument>().Select(d => Path.GetFullPath(d.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string c3d in Directory.EnumerateFiles(root, "*.c3d", options))
            {
                if (open.Contains(Path.GetFullPath(c3d))) continue;
                try
                {
                    if (C3dPersistence.LoadFromFile(c3d).Objects.Any(o => string.Equals(o.Material, oldName, StringComparison.OrdinalIgnoreCase)))
                        listed.Add(Path.GetFileName(c3d));
                }
                catch { /* not a 3D view, or unreadable */ }
            }
        }
        return listed.Count == 0 ? null
            : $"Not rewritten, because they are not open: {string.Join(", ", listed)} still name '{oldName}', and `check` will report it as unknown there.";
    }

    // ── the 3D editor (R-em3d53-5) ────────────────────────────────────────────────────────────

    /// <summary>Connects a 3D editor's material picker requests to the picker dialog.</summary>
    private void HookC3dMaterials(C3dEditorDocument doc)
        => doc.ViewModel.MaterialPickerRequested += (indices, startNew) => _ = ShowMaterialPickerAsync(doc, indices, startNew);

    private async Task ShowMaterialPickerAsync(C3dEditorDocument doc, IReadOnlyList<int> indices, bool startNew)
    {
        var vm = doc.ViewModel;
        if (HostWindowOf(doc) is not { } window) return;
        string? techPath = vm.Elaboration?.TechnologyPath;
        if (vm.Elaboration?.Technology is not { } tech || techPath is null)
        {
            // A technology that fails to resolve leaves nothing to choose from; the way out is to open it.
            string why = techPath is null
                ? "This 3D view resolves no technology, so there are no materials to choose from. Give its workspace a default technology."
                : $"This 3D view's technology '{Path.GetFileName(techPath)}' did not load, so there are no materials to choose from. It is open to fix.";
            vm.StatusMessage = why;
            Messages.Warning(why);
            if (techPath is not null && File.Exists(techPath)) OpenOrActivateTech(techPath);
            return;
        }

        string? current = indices.Count == 1 ? vm.Document.Objects[indices[0]].Material : vm.CurrentMaterial;
        var picker = new MaterialPickerViewModel(tech, Path.GetFileName(techPath), startNew, current, null);
        bool ok = await new Views.Dialogs.MaterialPickerDialog(picker).ShowDialog<bool>(window);
        if (!ok) return;

        if (picker.IsNew)
        {
            if (SaveNewMaterial(techPath, picker.SelectedTarget!, picker.NewMaterial) is { } refused)
            {
                Messages.Error(refused);
                return;
            }
            ActivateIfOpen(C3dEditorDocument.KeyFor(doc.FilePath));
        }
        vm.ApplyPickedMaterial(indices, picker.ChosenName);
    }

    /// <summary>
    /// Commits a new material to the file the picker named, as ONE entry on that file's own document — opening it if
    /// needed. Nothing reaches disk until that document is saved; the message says which one became dirty.
    /// </summary>
    private string? SaveNewMaterial(string techPath, MaterialSaveTarget target, TechMaterial material)
    {
        if (target.LibraryPath is { } lib)
        {
            OpenOrActivateMaterials(lib);
            if (!_openDocsByPath.TryGetValue(Path.GetFullPath(lib), out var d) || d is not MaterialsDocument library)
                return $"'{Path.GetFileName(lib)}' did not open, so the material was not added.";
            if (library.ViewModel.Table.IsReadOnly) return library.ViewModel.Table.ReadOnlyReason ?? "That library is read-only.";
            library.ViewModel.CommitEdit(list => list.Add(material), $"Add material {material.Name}");
            Messages.Info($"'{material.Name}' was added to {Path.GetFileName(lib)}, which is now unsaved — save it to keep the material.");
            return null;
        }

        OpenOrActivateTech(techPath);
        if (OpenTechEditor(techPath) is not { } editor) return $"'{Path.GetFileName(techPath)}' did not open, so the material was not added.";
        if (editor.AddOwnMaterial(material) is { } why) return why;
        Messages.Info($"'{material.Name}' was added to {Path.GetFileName(techPath)}, which is now unsaved — save it to keep the material.");
        return null;
    }

    /// <summary>
    /// 3D ▸ Materials… — the active 3D view's technology, opened on its Materials tab. Instance technologies are named,
    /// not edited from here (M8): editing a child's process from its parent is editing another design.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDMaterials()
    {
        if (ActiveC3dEditor() is not { } c3d) return;
        if (c3d.Elaboration?.TechnologyPath is not { } techPath || !File.Exists(techPath))
        {
            Messages.Warning("This 3D view resolves no technology, so there are no materials to edit. Give its workspace a default technology.");
            return;
        }
        OpenOrActivateTech(techPath);
        if (OpenTechEditor(techPath) is { } editor) editor.SelectedTabIndex = TechEditorViewModel.MaterialsTabIndex;

        var others = c3d.Elaboration.WalkInstances.Select(s => s.Detail).ToList();
        if (others.Count > 0)
            Messages.Info("Placed cells resolve their own technologies, edited from those technologies, not from here: " +
                          string.Join("; ", others));
    }
}
