// brief-em3d-43 — where the 3D editor opens, what it follows, and how it is saved.
//
//   * a double-click on a .c3d in the Project Tree opens it (R-em3d43-1a), one tab per file;
//   * a placed cell's .c3d or .clay (or a technology) changing on disk re-elaborates — the elaborator's
//     child cache is keyed by the file's stamp, so only that instance is rebuilt — with no prompt;
//   * the open document itself changing on disk reloads it when it is clean, and asks when it is dirty,
//     as the layout editor does (R-em3d43-1d);
//   * Save, Save As, Save All, the close prompt and the quit prompt reach it as every other editor.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.Ui.Views.Dialogs;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    private readonly Dictionary<C3dEditorDocument, FileSystemWatcher> _c3dWatchers = [];

    /// <summary>Extensions whose change on disk re-elaborates an open 3D editor (a placed cell, a technology).</summary>
    private static readonly string[] C3dInputs = [".c3d", ".clay", ".ctech", ".wbond"];

    /// <summary>Opens (or focuses) the 3D editor on <paramref name="path"/>.</summary>
    public void OpenOrActivateC3dEditor(string path)
    {
        string full = Path.GetFullPath(path);
        string key = C3dEditorDocument.KeyFor(full);
        if (ActivateIfOpen(key)) return;

        C3dDocument document;
        try { document = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) when (ex is C3dReadException or IOException or UnauthorizedAccessException)
        {
            Messages.Error($"Could not open the 3D view '{Path.GetFileName(full)}': {ex.Message}");
            return;
        }

        try
        {
            var vm = new C3dEditorViewModel(full, document, Viewer3DBackends.Create, () => CurrentWorkspacePath,
                                            a => Dispatcher.UIThread.Post(a), _techCache);
            var doc = new C3dEditorDocument(vm);
            // brief-em3d-45 R-em3d45-1a — the drawing plane is window state: put back where it was left.
            if (StoredDrawingPlane(full) is { } plane) vm.SetPlane(plane);
            vm.DrawingPlaneChanged += () => RememberDrawingPlane(vm);
            vm.ExternalChangeWhileDirty += () => _ = AskReloadC3dAsync(doc);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not nameof(C3dEditorViewModel.IsDirty)) return;
                _factory.ProjectTreeTool?.SetFileDirty(vm.FilePath, vm.IsDirty);
                RaiseFileMenuEnablementChanged();
            };
            _factory.OpenDocument(doc);
            _openDocsByPath[key] = doc;
            WatchC3d(doc);
            vm.Start();
            RaiseThreeDMenuChanged();
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not open the 3D editor: {ex.Message}");
        }
    }

    private void WatchC3d(C3dEditorDocument doc)
    {
        string root = WorkspaceRootDir ?? Path.GetDirectoryName(doc.FilePath)!;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            void OnChange(object? _, FileSystemEventArgs e)
            {
                if (!C3dInputs.Contains(Path.GetExtension(e.FullPath).ToLowerInvariant())) return;
                bool own = string.Equals(Path.GetFullPath(e.FullPath), doc.FilePath, StringComparison.OrdinalIgnoreCase);
                Dispatcher.UIThread.Post(own ? doc.ViewModel.OnFileChangedOnDisk : doc.ViewModel.OnChildChanged);
            }
            w.Changed += OnChange;
            w.Created += OnChange;
            w.Renamed += (s, e) => OnChange(s, e);
            w.EnableRaisingEvents = true;
            _c3dWatchers[doc] = w;
        }
        catch (Exception ex)
        {
            Messages.Warning($"The 3D editor will not follow changes to its placed cells on disk: {ex.Message}");
        }
    }

    /// <summary>The open document changed on disk while it has unsaved edits: ask, as the layout editor does.</summary>
    private async Task AskReloadC3dAsync(C3dEditorDocument doc)
    {
        if (HostWindowOf(doc) is not { } window) return;
        var dlg = new Views.Dialogs.SaveChangesDialog(
            $"'{Path.GetFileName(doc.FilePath)}' changed on disk. Reload it and discard your unsaved changes?",
            saveLabel: "Keep Mine", dontSaveLabel: "Reload", cancelLabel: "Cancel", title: "File Changed on Disk");
        await dlg.ShowDialog(window);
        if (dlg.Result == SaveChangesResult.DontSave)
        {
            if (doc.ViewModel.Reload() is { } why) Messages.Error($"Could not reload '{doc.FilePath}': {why}");
        }
    }

    /// <summary>Writes a 3D view; reports either way. True when it was written.</summary>
    private bool SaveC3d(C3dEditorDocument doc)
    {
        if (doc.ViewModel.Save() is { } why)
        {
            Messages.Error($"Failed to save '{doc.FilePath}': {why}");
            return false;
        }
        Messages.Success("Saved", doc.FilePath);
        return true;
    }

    /// <summary>Save As for a 3D view: a new .c3d beside the old one by default, which the tab then follows.</summary>
    internal async Task SaveC3dAs(C3dEditorDocument doc, Window owner)
    {
        var start = await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetDirectoryName(doc.FilePath)!));
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save 3D View As",
            SuggestedFileName = Path.GetFileName(doc.FilePath),
            SuggestedStartLocation = start,
            DefaultExtension = "c3d",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("3D view") { Patterns = ["*.c3d"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        string oldKey = C3dEditorDocument.KeyFor(doc.FilePath);
        if (doc.ViewModel.SaveAs(path) is { } why) { Messages.Error($"Failed to save '{path}': {why}"); return; }
        _openDocsByPath.Remove(oldKey);
        _openDocsByPath[C3dEditorDocument.KeyFor(path)] = doc;
        doc.FollowSavedAs();
        Messages.Success("Saved", path);
    }

    // ── the drawing plane in the window state (brief-em3d-45 R-em3d45-1a) ─────────────────────

    private Dictionary<string, CwsDrawingPlane>? _drawingPlanes;

    private Dictionary<string, CwsDrawingPlane> LoadStoredDrawingPlanes()
        => CurrentWorkspacePath is { } cws && TryLoadCws(cws).C3dDrawingPlanes is { } stored
            ? new Dictionary<string, CwsDrawingPlane>(stored, StringComparer.Ordinal)
            : new Dictionary<string, CwsDrawingPlane>(StringComparer.Ordinal);

    private DrawingPlane? StoredDrawingPlane(string c3dPath)
    {
        _drawingPlanes ??= LoadStoredDrawingPlanes();
        return CameraKey(c3dPath) is { } k && _drawingPlanes.TryGetValue(k, out var p) && Enum.TryParse<C3dPlane>(p.Plane, out var plane)
            ? new DrawingPlane(plane, p.OffsetDbu) : null;
    }

    /// <summary>The plane as it is now; the default plane is no row at all.</summary>
    private void RememberDrawingPlane(C3dEditorViewModel vm)
    {
        if (CameraKey(vm.FilePath) is not { } k) return;
        _drawingPlanes ??= LoadStoredDrawingPlanes();
        if (vm.Plane == DrawingPlane.Default) _drawingPlanes.Remove(k);
        else _drawingPlanes[k] = new CwsDrawingPlane { Plane = vm.Plane.Plane.ToString(), OffsetDbu = vm.Plane.OffsetDbu };
    }

    /// <summary>Every 3D editor's drawing plane for the <c>.cwsuser</c> — null when none is off the default.</summary>
    private Dictionary<string, CwsDrawingPlane>? DrawingPlanesToPersist()
    {
        _drawingPlanes ??= LoadStoredDrawingPlanes();
        foreach (var doc in _openDocsByPath.Values.OfType<C3dEditorDocument>()) RememberDrawingPlane(doc.ViewModel);
        return _drawingPlanes.Count > 0 ? _drawingPlanes : null;
    }

    private void ClosedC3dEditor(C3dEditorDocument doc)
    {
        RememberDrawingPlane(doc.ViewModel);
        if (_c3dWatchers.Remove(doc, out var w)) w.Dispose();
        _factory.ProjectTreeTool?.SetFileDirty(doc.FilePath, false);
        doc.ViewModel.Dispose();
        RaiseThreeDMenuChanged();
    }

    // ── the 3D menu (owner decision D9, overview §1m) ─────────────────────────────────────────
    //
    // ONE top-level menu, always present: on macOS a window's NativeMenu is fixed for its lifetime, so a
    // menu that came and went with the active document would be the fragile path. Every item acts on the
    // active 3D pane — the editor's or the read-only viewer's, which share their view model — and is
    // enabled only when there is one. Nothing here is a second implementation: each item calls the
    // command the pane's own toolbar and keys call.

    /// <summary>The active 3D pane's view model, or null.</summary>
    private Viewer3DViewModel? Active3DPane() => ResolveActiveDocumentForCommands() switch
    {
        C3dEditorDocument e => e.ViewModel.Viewer,
        Viewer3DDocument v  => v.ViewModel,
        _                   => null,
    };

    private bool HasActive3DPane() => Active3DPane() is not null;

    /// <summary>Re-evaluates every 3D menu item — called from both of the shell's enablement fan-outs.</summary>
    private void RaiseThreeDMenuChanged()
    {
        ThreeDSelectModeCommand.NotifyCanExecuteChanged();
        ThreeDFitCommand.NotifyCanExecuteChanged();
        ThreeDStandardViewCommand.NotifyCanExecuteChanged();
        ThreeDPerspectiveCommand.NotifyCanExecuteChanged();
        ThreeDClipPlaneCommand.NotifyCanExecuteChanged();
        ThreeDAxisIndicatorCommand.NotifyCanExecuteChanged();
        ThreeDShowAllCommand.NotifyCanExecuteChanged();
        ThreeDSnapCommand.NotifyCanExecuteChanged();
        ThreeDDrawCommand.NotifyCanExecuteChanged();
        ThreeDDrawingPlaneCommand.NotifyCanExecuteChanged();
        ThreeDExtrudeCommand.NotifyCanExecuteChanged();
        ThreeDModifyCommand.NotifyCanExecuteChanged();
        ThreeDMeasureCommand.NotifyCanExecuteChanged();
    }

    // brief-em3d-45 — drawing needs the editor, not the read-only viewer.
    private C3dEditorViewModel? ActiveC3dEditor() => ResolveActiveDocumentForCommands() is C3dEditorDocument e ? e.ViewModel : null;

    private bool HasActiveC3dEditor() => ActiveC3dEditor() is not null;

    /// <summary>3D ▸ Draw ▸ Box … Cylinder — the same arming the toolbar and the Shift+A popup do.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDDraw(string kind)
    {
        if (ActiveC3dEditor() is { } e && Enum.TryParse<CircuitRF.Ui.ThreeD.Tools.C3dToolKind>(kind, out var k)) e.Arm(k);
    }

    /// <summary>3D ▸ Drawing Plane ▸ XY / YZ / XZ (keeping the offset), and Show Grid.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDDrawingPlane(string which)
    {
        if (ActiveC3dEditor() is not { } e) return;
        if (which == "Grid") { e.ShowDrawingGrid = !e.ShowDrawingGrid; return; }
        if (Enum.TryParse<CircuitRF.Design.ThreeD.C3dPlane>(which, out var p)) e.SetPlane(e.Plane with { Plane = p });
    }

    /// <summary>3D ▸ Modify ▸ Extrude.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDExtrude() => ActiveC3dEditor()?.Extrude();

    /// <summary>brief-em3d-46 — 3D ▸ Modify's object operations, by name: the same functions the context menu and the
    /// keys call (C3dEditorViewModel.RunModify).</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDModify(string which) => ActiveC3dEditor()?.RunModify(which);

    /// <summary>brief-em3d-46 R-em3d46-6 — 3D ▸ Measure, in the editor and the read-only viewer alike.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDMeasure() => Active3DPane()?.ToggleMeasure();

    /// <summary>3D ▸ Select Mode ▸ Object / Face / Vertex.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDSelectMode(string mode)
    {
        if (Active3DPane() is { } p && Enum.TryParse<Scene3DSelectMode>(mode, out var m)) p.SelectMode = m;
    }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDFit() => Active3DPane()?.FitCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDStandardView(StandardView3D view) => Active3DPane()?.StandardViewCommand.Execute(view);

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDPerspective() { if (Active3DPane() is { } p) p.IsPerspective = !p.IsPerspective; }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDClipPlane() { if (Active3DPane() is { } p) p.ClipEnabled = !p.ClipEnabled; }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDAxisIndicator() { if (Active3DPane() is { } p) p.ShowAxisIndicator = !p.ShowAxisIndicator; }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDShowAll() => Active3DPane()?.ShowAll();

    /// <summary>brief-em3d-44 R-em3d44-5 — 3D ▸ Snap: <c>All</c> is the master switch, any other a kind's toggle —
    /// the same properties the editor's toolbar toggles bind to.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDSnap(string kind)
    {
        if (Active3DPane() is not { } p) return;
        switch (kind)
        {
            case "All":        p.SnapEnabled = !p.SnapEnabled; break;
            case "Vertex":     p.SnapVertex = !p.SnapVertex; break;
            case "Midpoint":   p.SnapMidpoint = !p.SnapMidpoint; break;
            case "Edge":       p.SnapEdge = !p.SnapEdge; break;
            case "FaceCentre": p.SnapFaceCentre = !p.SnapFaceCentre; break;
            case "Grid":       p.SnapGridOn = !p.SnapGridOn; break;
        }
    }

    /// <summary>A workspace switch drops the dock tree without closing documents one by one.</summary>
    private void ReleaseC3dEditorsOfOutgoingWorkspace()
    {
        foreach (var doc in _openDocsByPath.Values.OfType<C3dEditorDocument>().ToList()) ClosedC3dEditor(doc);
        _drawingPlanes = null;
    }
}
