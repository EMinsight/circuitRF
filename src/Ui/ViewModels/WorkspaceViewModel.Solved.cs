// brief-em3d-98 — the shell's half of "is this 3D view solved?": the workspace tree's .c3d rows, a floating window's title, and
// the one-line question before a run would replace a complete, current result. Every answer is C3dSolveStatus's (src/Design);
// nothing here decides whether a result is current.
//
// NO CHECK DELAYS AN OPEN (R-em3d98-6). The tree is shown first; then one background pass, at low priority, fills in the rows
// it has no answer for, each as it finishes — no spinner, no placeholder glyph. An open document's row shows its editor's
// in-memory answer; a closed one's comes from its file, cached per (path, time, size) for the session. A save of anything a
// 3D view can be solved from (a layout, a technology, a material library, a schematic) clears that cache, and the hash cache
// under the check means only the changed files are re-read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    private readonly Dictionary<string, (DateTime Time, long Length, SolveBadgeSet Badges)> _treeSolveCache =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _treeSolveCts;
    private FileSystemWatcher? _solveWatcher;
    private string? _solveWatcherRoot;
    private DispatcherTimer? _solveDebounce;

    /// <summary>The files whose save can change whether a 3D view's result is current, beside the ones its editor watches.</summary>
    private static readonly string[] SolveInputs = [".c3d", ".clay", ".ctech", ".wbond", ".cmat", ".csch"];

    /// <summary>Once, from the constructor: the tree's rescans and the active document's changes.</summary>
    private void HookSolveBadges()
    {
        Controls.WindowMotion.Install();      // the checks below hold off while a window is resized or moved
        Dock.ProjectTreeTool.RowsRebuilt += tool =>
        {
            if (ReferenceEquals(tool, _factory.ProjectTreeTool))
                Dispatcher.UIThread.Post(RefreshTreeSolveBadges, DispatcherPriority.Background);
        };
        _factory.ActiveDockableChanged += (_, _) => RefreshFloatingTitles();
    }

    /// <summary>The open 3D editor showing <paramref name="path"/> (as its top document), or null.</summary>
    private C3dEditorViewModel? OpenC3dEditorFor(string path)
        => _openDocsByPath.TryGetValue(C3dEditorDocument.KeyFor(path), out var d) && d is C3dEditorDocument c ? c.ViewModel : null;

    /// <summary>An open editor answered: its row shows the in-memory answer, and a floating window showing it follows.</summary>
    private void OnC3dSolveStatusesChecked(C3dEditorViewModel vm)
    {
        _factory.ProjectTreeTool?.SetSolveBadges(vm.TopFilePath, vm.SolveBadges);
        RefreshFloatingTitles();
    }

    /// <summary>
    /// Every <c>.c3d</c> row of the tree: an open document's from its editor; a closed one's from the cache when its file has
    /// not moved, else checked in the background (one pass, below-normal priority, rows filled in as each finishes).
    /// </summary>
    internal void RefreshTreeSolveBadges()
    {
        if (_factory.ProjectTreeTool is not { } tree || CurrentWorkspacePath is null) return;
        EnsureSolveWatcher();
        string root = EmResultsRoot();
        var todo = new List<string>();
        foreach (string path in tree.C3dFilePaths())
        {
            if (OpenC3dEditorFor(path) is { } vm) { tree.SetSolveBadges(path, vm.SolveBadges); continue; }
            if (Stamp(path) is { } st && _treeSolveCache.TryGetValue(path, out var c) && c.Time == st.Time && c.Length == st.Length)
            { tree.SetSolveBadges(path, c.Badges); continue; }
            todo.Add(path);
        }
        if (todo.Count == 0) return;
        _treeSolveCts?.Cancel();
        var cts = _treeSolveCts = new CancellationTokenSource();
        var ct = cts.Token;
        var worker = new Thread(() =>
        {
            foreach (string path in todo)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    Controls.WindowMotion.WaitForQuiet(ct);         // never mid-resize: each answer redraws a tree row
                    if (Stamp(path) is not { } stamp) continue;
                    var doc = C3dPersistence.LoadFromFile(path);
                    var badges = new SolveBadgeSet(C3dSolveStatus.Of(doc, path, root, ct),
                                                   doc.ActiveSetup ?? C3dSetups.Read(doc).FirstOrDefault(s => s.Refusal is null)?.Name);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        _treeSolveCache[path] = (stamp.Time, stamp.Length, badges);
                        if (OpenC3dEditorFor(path) is null) _factory.ProjectTreeTool?.SetSolveBadges(path, badges);
                    }, DispatcherPriority.Background);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is not OutOfMemoryException) { /* unreadable: no glyphs, as `check` would say */ }
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "circuitRF solved glyphs" };
        worker.Start();
    }

    /// <summary>Forgets the tree's answer for <paramref name="path"/> (null: every one) and asks again.</summary>
    internal void InvalidateTreeSolve(string? path = null)
    {
        if (path is null) _treeSolveCache.Clear();
        else _treeSolveCache.Remove(Path.GetFullPath(path));
        RefreshTreeSolveBadges();
    }

    private static (DateTime Time, long Length)? Stamp(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? (f.LastWriteTimeUtc, f.Length) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// One watcher on the open workspace for what a save can change: a file a 3D view is solved from, or a run's
    /// <c>status.json</c> (a run elsewhere — <c>circuitrf em</c> — finishing). Debounced a second, then the tree's cache is
    /// cleared and every open 3D view re-checks.
    /// </summary>
    private void EnsureSolveWatcher()
    {
        string? root = WorkspaceRootDir;
        if (root == _solveWatcherRoot) return;
        _solveWatcher?.Dispose();
        _solveWatcher = null;
        _solveWatcherRoot = root;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            void OnChange(object? _, FileSystemEventArgs e)
            {
                string name = Path.GetFileName(e.FullPath);
                if (!name.Equals(C3dRunStatus.FileName, StringComparison.OrdinalIgnoreCase) &&
                    !SolveInputs.Contains(Path.GetExtension(name).ToLowerInvariant())) return;
                Dispatcher.UIThread.Post(SolveInputsChanged);
            }
            w.Changed += OnChange;
            w.Created += OnChange;
            w.Deleted += OnChange;
            w.Renamed += (s, e) => OnChange(s, e);
            w.EnableRaisingEvents = true;
            _solveWatcher = w;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { /* the next open re-checks */ }
    }

    private void SolveInputsChanged()
    {
        _solveDebounce ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (Controls.WindowMotion.SinceLast < Controls.WindowMotion.Quiet) return;     // still resizing: the timer ticks again
            _solveDebounce!.Stop();
            foreach (var doc in _openDocsByPath.Values.OfType<C3dEditorDocument>()) doc.ViewModel.ScheduleSolveStatus();
            InvalidateTreeSolve();
        });
        _solveDebounce.Stop();
        _solveDebounce.Start();
    }

    // ── a floating window's title (R-em3d98-5 item 4) ─────────────────────────────────────────────

    private readonly Dictionary<Window, string> _floatingBaseTitles = [];

    /// <summary>
    /// A torn-off window whose active document is a 3D view carries its glyphs as TEXT — the title bar is the operating
    /// system's: <c>Connector.c3d — solved (FEM, thermal)</c>, only the kinds whose glyph is solid filled, nothing when none
    /// is. Any other window, and the main one (it names the workspace, which holds many documents), keeps its title.
    /// </summary>
    private void RefreshFloatingTitles()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var window in desktop.Windows.OfType<Dock.CrfHostWindow>().Where(w => w.PlatformImpl is not null && WindowFloatsNoTool(w)))
        {
            if (FindAnyDocumentInWindow(window) is C3dEditorDocument c3d)
            {
                string name = Path.GetFileName(c3d.FilePath);
                window.Title = c3d.SolveTitleSuffix is { } suffix ? $"{name} — {suffix}" : name;
                _floatingBaseTitles.TryAdd(window, name);
            }
            else if (_floatingBaseTitles.Remove(window, out _) && FindAnyDocumentInWindow(window) is { Title: { } t })
                window.Title = t.TrimStart('•', ' ');
        }
        foreach (var gone in _floatingBaseTitles.Keys.Where(w => w.PlatformImpl is null).ToList()) _floatingBaseTitles.Remove(gone);

        static bool WindowFloatsNoTool(Window w) => !WindowFloatsATool(w);
    }

    // ── the question before a re-run (R-em3d98-7) ─────────────────────────────────────────────────

    /// <summary>
    /// True when the run may go ahead: the setup has no complete, current result to replace, or the user said Run Again. A
    /// partial or out-of-date result runs with no question. When the background check has not answered, this setup alone is
    /// checked now — the question must not be skipped because the glyphs were slow.
    /// </summary>
    private async Task<bool> ConfirmRerunAsync(C3dEditorViewModel c3d, string setupName)
    {
        var legs = c3d.SolveStatusOf(setupName);
        if (!C3dSolveStatus.AllCurrent(legs)) return true;
        var doc = _openDocsByPath.Values.OfType<C3dEditorDocument>().FirstOrDefault(d => ReferenceEquals(d.ViewModel, c3d));
        if (doc is null || HostWindowOf(doc) is not { } window) return true;
        var dlg = new SaveChangesDialog(SolveBadgeRules.RerunQuestion(setupName, legs),
                                        saveLabel: "Run Again", dontSaveLabel: null, cancelLabel: "Cancel", title: "Result Is Current");
        await dlg.ShowDialog(window);
        return dlg.Result == SaveChangesResult.Save;
    }
}
