// brief-em3d-49 — simulate from the document: the Setup Analyses dialog on the embedded setups, ports drawn and inferred by
// contact, the active setup's air box drawn and edited, boundaries on the faces of solids, Simulate, and the fields over
// the run's geometry.
//
// EVERY EDIT HERE IS A DOCUMENT EDIT (R-em3d49-1b): one undo entry (C3dRecordsEdit — the ports, face boundaries and
// setups as the file spells them, before and after), the document dirty, saved with the .c3d. The setup editor is the
// .cem panel itself (EmSetupEditorViewModel), bound to an embedded setup with its commit handed here: one editor, two
// containers, and its own undo stack stays empty.
//
// WHAT IS DRAWN IS WHAT A RUN GETS (overview §0). The air box is the active setup's, padded by the one rule a run pads
// by (C3dProblemAssembly.AirBox → Em3dGenerator.PaddedAirBox); the ports are resolved by the one inference a run makes
// (C3dPorts), against that box and that setup's ground set — so a wave port off the box, or a port touching three
// conductors, is refused on screen with the words a run would refuse it with.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>
/// One row of the 3D view's Setup Analyses dialog: an embedded setup (by its index in the document), or the external
/// <c>.cem</c>. Drawn as a schematic's analysis card is — a type badge, the name, a one-line summary — with the ACTIVE
/// mark where a schematic card has its Enabled box: a 3D view runs one setup, not every enabled one.
/// </summary>
public sealed partial class C3dSetupItem(string name, int index, string? refusal, string? externalPath, EmSetup? setup = null) : ObservableObject
{
    /// <summary>The card's badge: what the setup solves, in the analysis cards' two- or three-letter spelling.</summary>
    public string TypeLabel => setup is null ? "?" : setup.Problem3D switch
    {
        Em3dProblemType.Electrostatic => "ES",
        Em3dProblemType.Magnetostatic => "MS",
        Em3dProblemType.Eigenmode     => "EIG",
        _                             => "SP",
    };

    /// <summary>The card's second line: solver, problem and sweep — or why it cannot run, which is all that matters then.</summary>
    public string Summary
    {
        get
        {
            if (Refusal is { } why) return why;
            if (setup is null) return "";
            string solver = setup.Solver3D switch { Em3dSolver.OpenEms => "openEMS", Em3dSolver.Both => "Palace + openEMS", _ => "Palace" };
            var f = setup.Frequency;
            string body = setup.Problem3D switch
            {
                Em3dProblemType.Electrostatic => "electrostatic",
                Em3dProblemType.Magnetostatic => "magnetostatic",
                Em3dProblemType.Eigenmode     => $"eigenmodes above {f.StartExpr} {f.StartUnit}",
                _ => f.NumPoints is { } n
                    ? $"{f.StartExpr}–{f.StopExpr} {f.StopUnit}, {n} pts"
                    : $"{f.StartExpr}–{f.StopExpr} {f.StopUnit}, step {f.StepExpr} {f.StepUnit}",
            };
            return $"{solver} · {body}" + (IsExternal ? $" · read-only, from {Path.GetFileName(ExternalPath)}" : "");
        }
    }

    /// <summary>The card menu names the card it acts on, as a schematic card's does.</summary>
    public string RunLabel => $"Run {Name}";
    public string RemoveLabel => $"Remove {Name}";

    public string Name { get; } = name;
    public int Index { get; } = index;
    public string? Refusal { get; } = refusal;
    public string? ExternalPath { get; } = externalPath;
    public bool IsExternal => ExternalPath is not null;

    [ObservableProperty] private bool _isActive;

    /// <summary>brief-em3d-65 R-em3d65-4d — what this setup's solver will not respect of the document's kernel solids,
    /// recomputed with the scene (the document or the setup changed). Shown under the card; Simulate is not blocked by it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFidelity))] private IReadOnlyList<C3dFidelityRow> _fidelity = [];

    public bool HasFidelity => Fidelity.Count > 0;

    public string Label => (IsActive ? "● " : "   ") + Name + (IsExternal ? $"  (external: {Path.GetFileName(ExternalPath)})" : "") +
                           (Refusal is null ? "" : "  — cannot run");

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(Label));
}

/// <summary>brief-em3d-65 — one fidelity row under a setup's card: a warning (the answer near the feature is the grid's, or
/// the loss model's) or a note.</summary>
public sealed record C3dFidelityRow(bool IsWarning, string Text)
{
    public bool IsNote => !IsWarning;
}

public sealed partial class C3dEditorViewModel
{
    // ── brief-em3d-65 R-em3d65-4d — each setup's fidelity rows, computed with the scene ─────────

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, IReadOnlyDictionary<string, IReadOnlyList<C3dFidelityRow>>> _fidelity = new();
    private IReadOnlyDictionary<string, IReadOnlyList<C3dFidelityRow>> _fidelityNow = new Dictionary<string, IReadOnlyList<C3dFidelityRow>>();

    /// <summary>
    /// On the scene's worker, under the elaboration lock: every embedded setup's rows, from the problem a run of it would
    /// assemble. Nothing is assembled for a document with no kernel object — every existing document pays nothing.
    /// </summary>
    private void ComputeFidelity(long generation, C3dDocument doc, string path, string? workspaceCws)
    {
        var rows = new Dictionary<string, IReadOnlyList<C3dFidelityRow>>(StringComparer.Ordinal);
        if (C3dKernelUse.Of(doc).Count > 0)
            foreach (var embedded in C3dSetups.Read(doc))
            {
                if (embedded is not { Refusal: null, Setup: { Is3D: true } s }) continue;
                try
                {
                    var run = C3dSetups.ForRun(s, path);
                    if (C3dProblemAssembly.Assemble(run, doc, path, workspaceCws, _elaborator).Problem is not { } problem) continue;
                    rows[embedded.Name] = [.. Em3dFidelityReport.For(problem, run).Select(r =>
                        new C3dFidelityRow(r.Severity == Em3dFidelitySeverity.Warning, $"{Em3dFidelityReport.SolverName(r.Solver)}: {r.Sentence}"))];
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { /* the run says it */ }
            }
        _fidelity[generation] = rows;
    }

    /// <summary>The adopted scene's rows onto the setup cards.</summary>
    private void AdoptFidelity(long generation)
    {
        foreach (long old in _fidelity.Keys.Where(k => k < generation).ToList()) _fidelity.TryRemove(old, out _);
        if (!_fidelity.TryRemove(generation, out var rows)) return;
        _fidelityNow = rows;
        foreach (var item in SetupItems) item.Fidelity = item.IsExternal ? [] : rows.GetValueOrDefault(item.Name) ?? [];
    }

    // ── records: ports, face boundaries and setups, as one undoable document edit ──────────────

    /// <summary>
    /// One document edit of the records (R-em3d49-1b): <paramref name="mutate"/> changes the document's ports, face
    /// boundaries or setups; the change is one undo entry, or none when nothing changed.
    /// </summary>
    public bool ChangeRecords(string description, Action<C3dDocument> mutate)
    {
        string before = C3dRecordsEdit.Of(Document);
        mutate(Document);
        string after = C3dRecordsEdit.Of(Document);
        if (after == before) return false;
        Push(new C3dRecordsEdit(description, before, after, ApplyRecords, alreadyApplied: true));
        RecordsChanged();
        return true;
    }

    /// <summary>An undo or redo put the records back.</summary>
    private void ApplyRecords(string text)
    {
        DocumentWrites++;
        C3dRecordsEdit.Apply(Document, text);
        RecordsChanged();
    }

    private void RecordsChanged()
    {
        DocumentChanged();
        RebuildSetupItems();
        ReloadSetupEditor();
        RefreshFieldsStale();
    }

    // ── setups (R-em3d49-1) ──────────────────────────────────────────────────────────────────

    /// <summary>The Setup Analyses dialog's rows: the embedded setups, in file order, then the external one.</summary>
    public ObservableCollection<C3dSetupItem> SetupItems { get; } = [];

    [ObservableProperty] private C3dSetupItem? _selectedSetupItem;

    /// <summary>
    /// The shell opens Simulate ▸ Setup Analyses… on this 3D view — the toolbar's tune button asks through here, so
    /// the button and the menu open the one dialog. Set by the workspace.
    /// </summary>
    public Action<C3dEditorViewModel>? SetupAnalysesRequested { get; set; }

    /// <summary>The toolbar's tune button: Simulate ▸ Setup Analyses… for this 3D view.</summary>
    [RelayCommand]
    public void OpenSetupAnalyses() => SetupAnalysesRequested?.Invoke(this);

    /// <summary>The active setup's name — editor state, per user (the shell stores it in the <c>.cwsuser</c>).</summary>
    public string? ActiveSetupName { get; private set; }

    /// <summary>Raised when the active setup changes: the shell remembers it, the scene redraws its box.</summary>
    public event Action? ActiveSetupChanged;

    /// <summary>The panel on the selected setup: <c>EmSetupEditorView</c>'s own document, never docked.</summary>
    [ObservableProperty] private EmSetupDocument? _setupEditor;

    /// <summary>R-em3d49-1c — a <c>.cem</c> naming this <c>.c3d</c>: shown read-only, marked with its path.</summary>
    public (string Path, EmSetup Setup)? ExternalSetup { get; private set; }

    /// <summary>The shell runs a setup: its name (null: the active one). Set by the workspace.</summary>
    public Func<C3dEditorViewModel, string?, Task>? RunRequested { get; set; }

    public string SetupsHeading => SetupItems.Count == 0
        ? "No setups. Add one to simulate this 3D view."
        : $"{SetupItems.Count} setup{(SetupItems.Count == 1 ? "" : "s")} · active: {ActiveSetupName ?? "none"}";

    /// <summary>Called by the shell with the stored active setup, before the first scene.</summary>
    public void RestoreActiveSetup(string? name)
    {
        ActiveSetupName = name;
        RebuildSetupItems();
        if ((ActiveSetupName is null || !SetupItems.Any(i => i.Name == ActiveSetupName)) && SetupItems.FirstOrDefault(i => i.Refusal is null) is { } first)
            ActiveSetupName = first.Name;
        RebuildSetupItems();
        Viewer.SetRunSetup(ActiveRunSetup);
        RefreshFieldsStale();
    }

    /// <summary>R-em3d49-1c — Show 3D from a <c>.cem</c> pointing here: that <c>.cem</c> joins the list, read-only, active.</summary>
    public void ShowExternalSetup(string cemPath, EmSetup setup)
    {
        ExternalSetup = (Path.GetFullPath(cemPath), setup);
        RebuildSetupItems();
        SetActiveSetup(ExternalItemName);
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.IsExternal);
    }

    private string ExternalItemName => ExternalSetup is { } x ? (x.Setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(x.Path)) : "";

    private void RebuildSetupItems()
    {
        string? keep = SelectedSetupItem?.Name;
        bool keepExternal = SelectedSetupItem?.IsExternal == true;
        SetupItems.Clear();
        foreach (var s in C3dSetups.Read(Document))
            SetupItems.Add(new C3dSetupItem(s.Name, s.Index, s.Refusal, null, s.Setup)
            {
                IsActive = !IsExternalActive && s.Name == ActiveSetupName, Fidelity = _fidelityNow.GetValueOrDefault(s.Name) ?? [],
            });
        if (ExternalSetup is { } x)
            SetupItems.Add(new C3dSetupItem(ExternalItemName, -1, x.Setup.Is3D ? null : C3dSetups.PlanarRefusal, x.Path, x.Setup) { IsActive = IsExternalActive });
        _syncingSetups = true;
        try { SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == keep && i.IsExternal == keepExternal) ?? SetupItems.FirstOrDefault(i => i.IsActive); }
        finally { _syncingSetups = false; }
        OnPropertyChanged(nameof(SetupsHeading));
        OnPropertyChanged(nameof(HasSetups));
        ReloadSetupEditor();
    }

    public bool HasSetups => SetupItems.Count > 0;

    private bool IsExternalActive => ExternalSetup is not null && _externalActive;
    private bool _externalActive;
    private bool _syncingSetups;

    /// <summary>The active setup as the editor draws it and Simulate runs it: an embedded one, or the external <c>.cem</c>;
    /// null when there is none (or it cannot be read).</summary>
    public EmSetup? ActiveSetup
    {
        get
        {
            if (IsExternalActive) return ExternalSetup!.Value.Setup;
            if (ActiveSetupName is null) return null;
            return C3dSetups.Read(Document).FirstOrDefault(s => s.Name == ActiveSetupName)?.Setup;
        }
    }

    /// <summary>The active setup as its run is named ("&lt;stem&gt; &lt;name&gt;", or the .cem itself) — what the fields are read from.</summary>
    public EmSetup? ActiveRunSetup => IsExternalActive ? ExternalSetup!.Value.Setup
                                    : ActiveSetup is { } s ? C3dSetups.ForRun(s, TopFilePath) : null;

    [RelayCommand]
    public void SetActiveSetup(string? name)
    {
        bool external = ExternalSetup is not null && name == ExternalItemName && SetupItems.Any(i => i.IsExternal && i.Name == name);
        if (name == ActiveSetupName && external == _externalActive) return;
        ActiveSetupName = name;
        _externalActive = external;
        foreach (var i in SetupItems) i.IsActive = i.Name == name && i.IsExternal == external;
        OnPropertyChanged(nameof(SetupsHeading));
        ActiveSetupChanged?.Invoke();
        Viewer.SetRunSetup(ActiveRunSetup);
        Viewer.Regenerate();
        RefreshFieldsStale();
    }

    partial void OnSelectedSetupItemChanged(C3dSetupItem? value)
    {
        if (_syncingSetups) return;
        ReloadSetupEditor();
    }

    /// <summary>Builds the panel for the selected setup: the <c>.cem</c> view model in its embedded mode.</summary>
    private void ReloadSetupEditor()
    {
        if (_suppressEditorReload) return;
        var item = SelectedSetupItem;
        if (item is null) { SetupEditor = null; return; }
        EmSetup? setup = item.IsExternal ? ExternalSetup?.Setup.Clone()
                       : item.Index >= 0 && item.Index < Document.Setups.Count ? TryRead(Document.Setups[item.Index]) : null;
        if (setup is null) { SetupEditor = null; return; }
        // The same view model, the same panel: rebuilt only when the setup is not the one already shown, so an edit made
        // in the panel does not tear the panel down under the user's cursor.
        if (SetupEditor?.ViewModel is { } shown && _editorFor == (item.Name, item.IsExternal) &&
            EmSetupPersistence.Serialize(shown.Working) == EmSetupPersistence.Serialize(setup))
            return;
        var vm = new EmSetupEditorViewModel(item.IsExternal ? item.ExternalPath! : FilePath, setup, embedded: true)
        {
            IsReadOnly = item.IsExternal,
        };
        if (!item.IsExternal)
        {
            string name = item.Name;
            vm.EmbeddedCommit = (_, after, description) => CommitSetupFromPanel(name, after, description);
        }
        vm.RunRequested = _ => RunRequested?.Invoke(this, item.IsExternal ? null : item.Name) ?? Task.CompletedTask;
        _editorFor = (item.Name, item.IsExternal);
        SetupEditor = new EmSetupDocument(item.Name, vm, item.IsExternal ? item.ExternalPath! : FilePath);
    }

    private (string Name, bool External)? _editorFor;

    private static EmSetup? TryRead(JsonElement e)
    {
        try { return EmSetupPersistence.FromEmbedded(e); }
        catch (Exception) { return null; }
    }

    /// <summary>R-em3d49-1b — the panel's edit, as the document's: the setup written back where it is, one entry.</summary>
    private void CommitSetupFromPanel(string name, string afterJson, string description)
    {
        var edited = EmSetupPersistence.Deserialize(afterJson);
        int index = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name)?.Index ?? -1;
        if (index < 0) return;
        bool renamed = edited.Name != name;
        _suppressEditorReload = true;
        try { ChangeRecords($"{description} ({name})", d => d.Setups[index] = EmSetupPersistence.ToEmbedded(edited)); }
        finally { _suppressEditorReload = false; }
        if (renamed && ActiveSetupName == name) SetActiveSetup(edited.Name);
        if (name == ActiveSetupName) { Viewer.SetRunSetup(ActiveRunSetup); }
    }

    private bool _suppressEditorReload;

    /// <summary>Add: a new Palace setup, named S1, S2 …; the first one added becomes the active one.</summary>
    [RelayCommand]
    public void AddSetup()
    {
        var names = C3dSetups.Read(Document).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        string name = Enumerable.Range(1, 10_000).Select(n => $"S{n}").First(n => !names.Contains(n));
        var setup = new EmSetup { Name = name, Solver3D = Em3dSolver.Palace };
        ChangeRecords($"Add setup {name}", d => d.Setups.Add(EmSetupPersistence.ToEmbedded(setup)));
        if (ActiveSetupName is null || ActiveSetup is null) SetActiveSetup(name);
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == name && !i.IsExternal);
    }

    /// <summary>Duplicate: the selected setup, named "&lt;name&gt; copy" (then 2, 3 …).</summary>
    [RelayCommand]
    public void DuplicateSetup()
    {
        if (SelectedSetupItem is not { IsExternal: false } item || item.Index < 0 || TryRead(Document.Setups[item.Index]) is not { } s) return;
        var names = C3dSetups.Read(Document).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        string name = item.Name + " copy";
        for (int n = 2; names.Contains(name); n++) name = $"{item.Name} copy {n}";
        s.Name = name;
        ChangeRecords($"Duplicate setup {item.Name}", d => d.Setups.Insert(item.Index + 1, EmSetupPersistence.ToEmbedded(s)));
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == name && !i.IsExternal);
    }

    /// <summary>Rename the selected setup; null on success, else why not. The active one stays active under its new name.</summary>
    public string? RenameSetup(string newName)
    {
        newName = newName.Trim();
        if (SelectedSetupItem is not { IsExternal: false } item || item.Index < 0) return "Only a setup of this 3D view can be renamed here.";
        if (newName == item.Name) return null;
        if (newName.Length == 0) return "A setup's name cannot be empty: a 3D view's setups are chosen by name.";
        if (C3dSetups.Read(Document).Any(s => s.Name == newName)) return $"This 3D view already has a setup named '{newName}'.";
        if (TryRead(Document.Setups[item.Index]) is not { } s) return "That setup cannot be read, so it cannot be renamed.";
        s.Name = newName;
        bool wasActive = ActiveSetupName == item.Name && !IsExternalActive;
        ChangeRecords($"Rename setup {item.Name} to {newName}", d => d.Setups[item.Index] = EmSetupPersistence.ToEmbedded(s));
        if (wasActive) SetActiveSetup(newName);
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == newName && !i.IsExternal);
        return null;
    }

    /// <summary>Remove the selected setup (undoable). Its results stay where they were written.</summary>
    [RelayCommand]
    public void RemoveSetup()
    {
        if (SelectedSetupItem is not { IsExternal: false } item || item.Index < 0) return;
        bool wasActive = ActiveSetupName == item.Name && !IsExternalActive;
        ChangeRecords($"Remove setup {item.Name}", d => d.Setups.RemoveAt(item.Index));
        if (wasActive) SetActiveSetup(SetupItems.FirstOrDefault(i => i.Refusal is null)?.Name);
    }

    [RelayCommand]
    public void MakeSelectedActive()
    {
        if (SelectedSetupItem is { } item) SetActiveSetup(item.Name);
    }

    /// <summary>Simulate ▸ Run: the active setup through the shell's run path.</summary>
    [RelayCommand]
    public Task SimulateActive() => RunRequested?.Invoke(this, null) ?? Task.CompletedTask;

    /// <summary>Why the active setup cannot be run now, or null.</summary>
    public string? SimulateRefusal()
    {
        if (CanPopOut) return "Pop out to the top 3D view to simulate it: a run solves the document the tab holds.";
        if (IsExternalActive) return null;
        if (ActiveSetupName is null)
            return SetupItems.Count == 0 ? "This 3D view has no setup. Add one in Simulate ▸ Setup Analyses…" : "No setup is active: make one active in Simulate ▸ Setup Analyses…";
        var read = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == ActiveSetupName);
        return read is null ? $"The active setup '{ActiveSetupName}' is no longer in this 3D view." : read.Refusal;
    }

    /// <summary>The setup a run of <paramref name="name"/> (null: the active one) uses, as its run names it — or why not.</summary>
    public (EmSetup? Setup, bool FromCem, string? Refusal) RunSetupFor(string? name)
    {
        if (CanPopOut) return (null, false, SimulateRefusal());
        if (name is null && IsExternalActive) return (ExternalSetup!.Value.Setup.Clone(), true, null);
        name ??= ActiveSetupName;
        if (name is null) return (null, false, SimulateRefusal());
        var read = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name);
        if (read is null) return (null, false, $"This 3D view has no setup named '{name}'.");
        if (read.Refusal is { } why) return (null, false, why);
        return (C3dSetups.ForRun(read.Setup!, TopFilePath), false, null);
    }

    /// <summary>The document as a run reads it: a copy, so an edit during the run changes nothing it solves.</summary>
    public C3dDocument RunDocument() => C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));

    // ── the active setup's box and the ports, as the scene shows them ────────────────────────

    /// <summary>What a scene build resolved for the records: the box, each port, each face boundary (keyed by generation).</summary>
    private sealed record RecordsView(Em3dAirBox? Box, IReadOnlyList<C3dPortResult> Ports,
                                      IReadOnlyList<(C3dFaceBoundary Boundary, IReadOnlyList<Em3dFacePolygon> Pieces, string? Refusal)> Boundaries,
                                      C3dPortContext Context);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, RecordsView> _records = new();
    private RecordsView? _recordsView;

    /// <summary>The ports as the current scene resolved them (each resolved or refused).</summary>
    public IReadOnlyList<C3dPortResult> PortResults => _recordsView?.Ports ?? [];

    /// <summary>The active setup's air box as the current scene drew it, or null.</summary>
    public Em3dAirBox? ShownAirBox => _recordsView?.Box;

    /// <summary>The setup the scene draws with: the active one's full .cem spelling, for the build's snapshot.</summary>
    private string? SceneSetupJson() => ActiveSetup is { } s ? EmSetupPersistence.Serialize(s) : null;

    /// <summary>Runs on the build's thread: the box, the ports and the face boundaries for this elaboration.</summary>
    private RecordsView ResolveRecords(C3dDocument doc, C3dElaboration e, string? setupJson)
    {
        EmSetup? setup = null;
        if (setupJson is not null)
            try { setup = EmSetupPersistence.Deserialize(setupJson); } catch (Exception) { setup = null; }
        // 3D editor round 3 — with no setup, the box a new setup would solve in (owner decision): drawn once there is a solid
        // or a sheet to size it (Extent is null until then), never before.
        var boxSetup = setup ?? (setupJson is null ? new EmSetup { Solver3D = Em3dSolver.Palace } : null);
        var box = boxSetup is not null && e.Ok ? C3dProblemAssembly.AirBox(boxSetup, e, out _) : null;
        var ctx = C3dProblemAssembly.PortContext(setup, doc, e, box);
        var ports = e.Ok ? C3dPorts.Resolve(doc, ctx) : [];
        var boundaries = e.Ok ? C3dProblemAssembly.FaceBoundaryPreview(doc, e) : [];
        return new RecordsView(box, ports, boundaries, ctx);
    }

    /// <summary>
    /// 3D editor round 3 — whether the air box is drawn (faces and edges): the toolbar switch and the tree tick. The user's
    /// choice, kept by the editor and put back on every scene it adopts, so a rebuild never turns a hidden box back on.
    /// On by default: the box is drawn once there is a shape to size it (absorbing faces are clear in the editor, so only
    /// its outline and any wall show).
    /// </summary>
    [ObservableProperty] private bool _airBoxShown = true;

    partial void OnAirBoxShownChanged(bool value) => ApplyAirBoxShown();

    private void ApplyAirBoxShown()
    {
        if (Viewer.ShowBoundaryFaces != AirBoxShown) Viewer.ShowBoundaryFaces = AirBoxShown;
        if (AllTreeItems().FirstOrDefault(t => t.IsAirBox) is { } box) box.Sync(AirBoxShown);
    }

    /// <summary>After a scene is adopted: the records it resolved become the editor's, and the box starts shown when the
    /// active setup has any face that is not absorbing (R-em3d49-3a).</summary>
    private void AdoptRecords(long generation)
    {
        AdoptFidelity(generation);
        foreach (long old in _records.Keys.Where(k => k < generation).ToList()) _records.TryRemove(old, out _);
        if (!_records.TryRemove(generation, out var view)) return;
        _recordsView = view;
        RebuildRecordsTree();
        ApplyAirBoxShown();
    }

    // ── ports (R-em3d49-2) ───────────────────────────────────────────────────────────────────

    /// <summary>The smallest port number the document does not use.</summary>
    public int NextPortNumber()
    {
        var used = Document.Ports.Select(p => p.Number).ToHashSet();
        for (int n = 1; ; n++) if (!used.Contains(n)) return n;
    }

    /// <summary>R-em3d49-2d — what a new port starts as: the next number, and the last port's Z0.</summary>
    public C3dPort NewPortTemplate(Em3dPortKind kind = Em3dPortKind.Lumped)
    {
        int n = NextPortNumber();
        return new C3dPort { Number = n, Name = $"P{n}", Kind = kind, Z0 = Document.Ports.LastOrDefault()?.Z0 ?? "50" };
    }

    /// <summary>Adds a port: one undo entry; the status line says what it joins, or why it cannot be built.</summary>
    public void AddPort(C3dPort port)
    {
        ChangeRecords($"Add port {port.Name}", d => d.Ports.Add(port));
        ToolCommits++;
        var r = C3dPorts.Resolve(port, Document.DbuPerMicron, CurrentPortContext());
        StatusMessage = r.Refusal ?? C3dPortReports.Describe(r);
    }

    /// <summary>The port context for the scene now shown — for a port the user is drawing.</summary>
    private C3dPortContext CurrentPortContext()
        => _recordsView?.Context ?? (Elaboration is { } e ? C3dProblemAssembly.PortContext(ActiveSetup, Document, e, null) : new C3dPortContext(
               new C3dElaboration([], [], [], new Dictionary<string, C3dProvenance>(), [], []), Document.DbuPerMicron, null, []));

    /// <summary>The document port a scene object is (<c>port/N</c>), or null.</summary>
    public C3dPort? PortOf(Scene3DObject o)
        => o.Kind == Scene3DKind.Port ? Document.Ports.FirstOrDefault(p => C3dPorts.ProblemName(p.Number) == o.Name) : null;

    private IReadOnlyList<C3dPort> SelectedPorts()
        => [.. Viewer.SelectedObjects().Select(PortOf).OfType<C3dPort>().Distinct()];

    public void FlipPorts(IReadOnlyList<C3dPort> ports)
    {
        var numbers = ports.Select(p => p.Number).ToHashSet();
        ChangeRecords(ports.Count == 1 ? $"Flip {C3dPorts.Label(ports[0])}" : $"Flip {ports.Count} ports",
                      d => { foreach (var p in d.Ports.Where(p => numbers.Contains(p.Number))) p.Flip = !p.Flip; });
    }

    public void SetPortKind(IReadOnlyList<C3dPort> ports, Em3dPortKind kind)
    {
        var numbers = ports.Select(p => p.Number).ToHashSet();
        ChangeRecords($"Make {(ports.Count == 1 ? C3dPorts.Label(ports[0]) : $"{ports.Count} ports")} {kind.ToString().ToLowerInvariant()}",
                      d => { foreach (var p in d.Ports.Where(p => numbers.Contains(p.Number))) p.Kind = kind; });
    }

    /// <summary>Sets a port's Z0; null on success, else why not.</summary>
    public string? SetPortZ0(int number, string z0)
    {
        if (!C3dPorts.TryParseZ0(z0, out var z) || !(z.Real > 0))
            return $"'{z0}' is not a reference impedance: a number of ohms with a positive real part, or a complex one such as 25+j10.";
        ChangeRecords($"Z0 of port {number}", d => { foreach (var p in d.Ports.Where(p => p.Number == number)) p.Z0 = z0.Trim(); });
        return null;
    }

    public void DeletePorts(IReadOnlyList<C3dPort> ports)
    {
        var numbers = ports.Select(p => p.Number).ToHashSet();
        Viewer.SetSelection([]);
        ChangeRecords(ports.Count == 1 ? $"Delete {C3dPorts.Label(ports[0])}" : $"Delete {ports.Count} ports",
                      d => d.Ports.RemoveAll(p => numbers.Contains(p.Number)));
    }

    /// <summary>
    /// R-em3d49-2d — Make Port on an axis-aligned face: the face's rectangle becomes the port's, on the face's plane. A
    /// face that is not a rectangle in an axis plane is refused, naming why. Returns the refusal, or null.
    /// </summary>
    public string? MakePortFromFace(uint objectId, int face, Em3dPortKind kind)
    {
        if (PortFromFace(objectId, face, kind, out var port) is { } why) return why;
        AddPort(port!);
        return null;
    }

    /// <summary>The port Make Port would add on a face, not added — or why the face cannot take one.</summary>
    private string? PortFromFace(uint objectId, int face, Em3dPortKind kind, out C3dPort? port)
    {
        port = null;
        var scene = Viewer.Scene;
        if (scene.Object(objectId) is not { } o || face < 0) return "Select a face first.";
        var (area, normal) = Scene3DFaces.AreaAndNormal(scene, objectId, face);
        if (normal is not { } n) return $"Face {o.FaceName(face)} is curved: a port is a flat rectangle.";
        int axis = Math.Abs(n.X) > 0.999f ? 0 : Math.Abs(n.Y) > 0.999f ? 1 : Math.Abs(n.Z) > 0.999f ? 2 : -1;
        if (axis < 0) return $"Face {o.FaceName(face)} is not normal to x, y or z: a port lies on a drawing plane.";
        if (scene.FeaturesOf(objectId) is not { Table: { } t } fr || face >= t.FaceCount) return "That face's corners are not known.";
        var pts = Enumerable.Range(t.FaceVertexStart[face], t.FaceVertexStart[face + 1] - t.FaceVertexStart[face])
                            .Select(k => fr.Vertex(t.FaceVertices[k])).ToList();
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        var plane = axis switch { 0 => C3dPlane.YZ, 1 => C3dPlane.XZ, _ => C3dPlane.XY };
        double Get(Point3 q, int a) => a == 0 ? q.X : a == 1 ? q.Y : q.Z;
        int ua = axis == 0 ? 1 : 0, va = axis == 2 ? 1 : 2;
        long u0 = D(pts.Min(q => Get(q, ua))), u1 = D(pts.Max(q => Get(q, ua)));
        long v0 = D(pts.Min(q => Get(q, va))), v1 = D(pts.Max(q => Get(q, va)));
        double rectArea = (u1 - u0) * per * ((v1 - v0) * per);
        if (u1 <= u0 || v1 <= v0 || Math.Abs(rectArea - area) > 1e-6 * rectArea)
            return $"Face {o.FaceName(face)} is not a rectangle: a port takes a rectangular face.";
        port = NewPortTemplate(kind);
        port.Plane = plane;
        port.Offset = D(Get(pts[0], axis));
        port.Rect = new C3dRect { Min = new C3dPoint2(u0, v0), Size = new C3dPoint2(u1 - u0, v1 - v0) };
        return null;
    }

    /// <summary>
    /// 3D editor round 4 — one kind of Make Port on a face, offered only when the port it would add resolves. Which kind a
    /// face can take is decided by WHERE its rectangle lies, never by what it was drawn as: a wave port is a region of an
    /// air-box face (a sheet drawn there is the usual way to state one), and a lumped port bridges two conductors on
    /// opposite edges (a face of a small gap block does exactly that). So the resolver the run uses answers, and its
    /// refusal is the item's tip.
    /// </summary>
    private Viewer3DMenuItem MakePortItem(Scene3DItem item, Em3dPortKind kind)
    {
        string word = kind == Em3dPortKind.Wave ? "Wave" : "Lumped";
        string? why = PortFromFace(item.Object, item.Face, kind, out var port)
                      ?? C3dPorts.Resolve(port!, Document.DbuPerMicron, CurrentPortContext()).Refusal;
        string tip = kind == Em3dPortKind.Wave
            ? "A wave port lies on a face of the active setup's air box — the end of a line that reaches the box."
            : "A lumped port bridges two conductors, one on each of two opposite edges of the face.";
        return new Viewer3DMenuItem(word, () => Report(MakePortFromFace(item.Object, item.Face, kind)), Enabled: why is null,
                                    Tip: why ?? tip);
    }

    // ── the air box (R-em3d49-3) ─────────────────────────────────────────────────────────────

    /// <summary>The air-box face a scene object is (<c>xmin</c> … <c>zmax</c>), or null.</summary>
    public static string? BoxFaceOf(Scene3DObject o)
        => o.Kind == Scene3DKind.Boundary && o.Name.StartsWith("airbox/", StringComparison.Ordinal) ? o.Name["airbox/".Length..] : null;

    /// <summary>R-em3d49-3b — a box face's boundary, written to the ACTIVE setup's AirBox; null on success, else why not.</summary>
    public string? SetAirBoxBoundary(string face, Em3dBoundaryKind kind)
        => EditActiveAirBox(face, f => f is null ? new EmAirBoxFace(null, kind) : f with { Boundary = kind }, $"Air box {face}: {kind}");

    /// <summary>3D editor round 3 — the air box's material choices: the technology's materials, and Air and Vacuum (built in
    /// when the technology lacks them).</summary>
    public IReadOnlyList<string> AirBoxMaterialChoices
    {
        get
        {
            var list = Materials.ToList();
            foreach (string b in new[] { CircuitRF.Design.Layout.Em3d.Em3dGenerator.AirMaterial, C3dProblemAssembly.VacuumMaterial })
                if (!list.Contains(b, StringComparer.OrdinalIgnoreCase)) list.Add(b);
            return list;
        }
    }

    /// <summary>3D editor round 3 — the material that fills the air box (the document's, Air by default); one undo entry.
    /// Air writes nothing (it is the default). Null on success, else why not.</summary>
    public string? SetAirBoxMaterial(string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return "Pick a material for the air box.";
        string? stored = string.Equals(material, CircuitRF.Design.Layout.Em3d.Em3dGenerator.AirMaterial, StringComparison.Ordinal) ? null : material;
        if (Document.AirBoxMaterial == stored) return null;
        ChangeRecords($"Air box material: {material}", d => d.AirBoxMaterial = stored);
        return null;
    }

    /// <summary>R-em3d49-3b — a box face's padding, typed in the display unit; null on success, else why not.</summary>
    public string? SetAirBoxPadding(string face, string text)
    {
        var d = C3dDimension.Parse(text, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value) return d.Why;
        if (d.Dbu < 0) return "A padding is a distance from the geometry: zero or more.";
        double um = (double)LayoutUnits.FromDbu(d.Dbu, LayoutUnit.Um, Document.DbuPerMicron);
        return EditActiveAirBox(face, f => new EmAirBoxFace(um, f?.Boundary), $"Air box {face} padding");
    }

    /// <summary>The padding a box face has now, in the display unit — the Padding… field's prefill.</summary>
    public string AirBoxPaddingText(string face)
    {
        if (ShownAirBox is not { } box || Elaboration?.Extent() is not { } x) return "";
        double m = face switch
        {
            "xmin" => x.X0 - box.Min.X, "xmax" => box.Max.X - x.X1, "ymin" => x.Y0 - box.Min.Y,
            "ymax" => box.Max.Y - x.Y1, "zmin" => x.Z0 - box.Min.Z, _ => box.Max.Z - x.Z1,
        };
        long dbu = (long)Math.Round(m / C3dLowering.Metres(1, Document.DbuPerMicron));
        return C3dDimension.Spell(dbu, Document.DisplayUnit, Document.DbuPerMicron);
    }

    /// <summary>3D editor round 1 — an axis's padding as a percentage of the content's extent along it, on both of the axis's
    /// faces (10 pads a tenth of the extent on each side); one undo entry. Null on success, else why not.</summary>
    public string? SetAirBoxPaddingPercent(char axis, string text)
    {
        string t = text.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) || !double.IsFinite(pct))
            return $"'{text}' is not a percentage: a number such as 10.";
        if (pct < 0) return "A padding is a distance from the geometry: zero or more.";
        string lo = axis + "min", hi = axis + "max";
        return EditActiveAirBox([lo, hi], f => new EmAirBoxFace(null, f?.Boundary, pct),
                                $"Air box {char.ToUpperInvariant(axis)} padding {pct.ToString("G6", CultureInfo.InvariantCulture)} %");
    }

    /// <summary>The active setup as the Inspector names it.</summary>
    public string ActiveSetupLabel => IsExternalActive ? $"{ExternalItemName} ({Path.GetFileName(ExternalSetup!.Value.Path)}, read-only here)"
                                                       : ActiveSetupName ?? "none";

    private string? EditActiveAirBox(string face, Func<EmAirBoxFace?, EmAirBoxFace> change, string description)
        => EditActiveAirBox([face], change, description);

    private string? EditActiveAirBox(IReadOnlyList<string> faces, Func<EmAirBoxFace?, EmAirBoxFace> change, string description)
    {
        if (IsExternalActive) return $"The active setup is the .cem {Path.GetFileName(ExternalSetup!.Value.Path)}: edit its air box in its own panel.";
        if (ActiveSetupName is not { } name || C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name) is not { Setup: { } setup } read)
            return "No setup is active: the air box is a setup's. Add or make one active in Simulate ▸ Setup Analyses…";
        var box = setup.AirBox ?? new EmAirBox();
        foreach (string face in faces)
            box = face switch
            {
                "xmin" => box with { XMin = change(box.XMin) }, "xmax" => box with { XMax = change(box.XMax) },
                "ymin" => box with { YMin = change(box.YMin) }, "ymax" => box with { YMax = change(box.YMax) },
                "zmin" => box with { ZMin = change(box.ZMin) }, _ => box with { ZMax = change(box.ZMax) },
            };
        setup.AirBox = box;
        ChangeRecords(description, d => d.Setups[read.Index] = EmSetupPersistence.ToEmbedded(setup));
        StatusMessage = $"Writes setup '{name}': the air box is the setup's, not the geometry's.";
        return null;
    }

    // ── face boundaries (R-em3d49-4) ─────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d49-4a — a boundary on face <paramref name="face"/> (the document's name) of object <paramref name="obj"/>;
    /// <paramref name="kind"/> null removes it. A conductor's face is refused, naming the object. Null on success.
    /// </summary>
    public string? SetFaceBoundary(string obj, string face, Em3dFaceBoundaryKind? kind, string? material = null)
    {
        if (Elaboration?.Solids.FirstOrDefault(s => s.Name == obj) is { Role: Em3dRole.Conductor })
            return $"'{obj}' is a conductor: it is already a void bounded by its own metal, so a boundary on its face has nothing to add.";
        if (kind is not null && Document.Objects.FirstOrDefault(o => o.Name == obj) is C3dSheet or C3dPolyline or null)
            return $"'{obj}' has no solid face for a boundary: put boundaries on dielectric and air objects.";
        string what = kind switch { null => "None", Em3dFaceBoundaryKind.Pec => "Perfect Conductor", _ => $"Conductive ({material})" };
        ChangeRecords($"Boundary on {obj} {face}: {what}", d =>
        {
            d.FaceBoundaries.RemoveAll(b => b.Object == obj && b.Face == face);
            if (kind is { } k) d.FaceBoundaries.Add(new C3dFaceBoundary { Object = obj, Face = face, Kind = k, Material = k == Em3dFaceBoundaryKind.Conductive ? material : null });
        });
        return null;
    }

    /// <summary>The metals a Conductive face may be: the technology's materials that conduct.</summary>
    public IReadOnlyList<string> Metals
        => Elaboration?.Technology?.ResolvedMaterials.Where(m => m.Sigma20 is > 0).Select(m => m.Name).ToList() ?? (IReadOnlyList<string>)[];

    // ── the context menu (R-em3d49-2, -3b, -4) ───────────────────────────────────────────────

    private IEnumerable<Viewer3DMenuItem> SimulateMenuItems()
    {
        var sel = Viewer.Selection;
        if (Viewer.SelectMode == Scene3DSelectMode.Object && SelectedPorts() is { Count: > 0 } ports)
        {
            yield return new Viewer3DMenuItem(ports.Count == 1 ? $"Flip {C3dPorts.Label(ports[0])}" : "Flip Ports", () => FlipPorts(ports),
                                              Tip: "Swap the port's + and − ends: the arrow turns round.");
            yield return new Viewer3DMenuItem("Port Kind", Children:
            [
                new Viewer3DMenuItem("Lumped", () => SetPortKind(ports, Em3dPortKind.Lumped)),
                new Viewer3DMenuItem("Wave", () => SetPortKind(ports, Em3dPortKind.Wave),
                                     Tip: "A wave port lies on a face of the active setup's air box."),
            ]);
            if (ports.Count == 1)
            {
                var p = ports[0];
                yield return new Viewer3DMenuItem($"Z0… ({p.Z0} Ω)", () => TextRequested?.Invoke($"Z0 of {C3dPorts.Label(p)}",
                    "Reference impedance, Ω (a complex one as 25+j10):", p.Z0, text => SetPortZ0(p.Number, text)));
            }
            yield return new Viewer3DMenuItem(ports.Count == 1 ? $"Delete {C3dPorts.Label(ports[0])}" : "Delete Ports", () => DeletePorts(ports));
            yield return Viewer3DMenuItem.Separator;
        }
        if (Viewer.SelectMode != Scene3DSelectMode.Face || sel.Count != 1 || sel[0].Face < 0) yield break;
        var item = sel[0];
        if (Viewer.Scene.Object(item.Object) is not { } o) yield break;

        if (BoxFaceOf(o) is { } boxFace)
        {
            var current = ShownAirBox is { } bx ? FaceKindOf(bx, boxFace) : Em3dBoundaryKind.Absorbing;
            string tip = ActiveSetupName is { } n ? $"Writes setup '{n}': the air box is the setup's." : "No setup is active.";
            yield return new Viewer3DMenuItem("Boundary", Tip: tip, Children:
            [
                .. new[] { Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Symmetry }
                    .Select(k => new Viewer3DMenuItem((k == current ? "● " : "") + k switch
                    {
                        Em3dBoundaryKind.Absorbing => "Absorbing", Em3dBoundaryKind.Pec => "PEC", Em3dBoundaryKind.Pmc => "PMC", _ => "Symmetry",
                    }, () => Report(SetAirBoxBoundary(boxFace, k)), Tip: tip)),
            ]);
            yield return new Viewer3DMenuItem("Padding…", () => TextRequested?.Invoke($"Air box {boxFace} padding",
                $"Distance from the geometry to the {boxFace} face ({LayoutUnits.Suffix(Document.DisplayUnit)}):", AirBoxPaddingText(boxFace),
                text => SetAirBoxPadding(boxFace, text)), Tip: tip);
            yield return Viewer3DMenuItem.Separator;
            yield break;
        }
        if (InstanceOf(o) is not null || o.Kind == Scene3DKind.Port) yield break;
        string faceName = o.FaceName(item.Face);
        bool conductor = Elaboration?.Solids.FirstOrDefault(s => s.Name == o.Name) is { Role: Em3dRole.Conductor };
        bool solid = Document.Objects.FirstOrDefault(d => d.Name == o.Name) is not (C3dSheet or C3dPolyline or null);
        var existing = Document.FaceBoundaries.FirstOrDefault(b => b.Object == o.Name && b.Face == faceName);
        if (solid)
        {
            string? why = conductor ? $"'{o.Name}' is a conductor: a boundary on its face has nothing to add." : null;
            var metals = Metals;
            yield return new Viewer3DMenuItem("Boundary", Enabled: why is null, Tip: why, Children:
            [
                new Viewer3DMenuItem((existing?.Kind == Em3dFaceBoundaryKind.Pec ? "● " : "") + "Perfect Conductor",
                                     () => Report(SetFaceBoundary(o.Name, faceName, Em3dFaceBoundaryKind.Pec))),
                new Viewer3DMenuItem((existing?.Kind == Em3dFaceBoundaryKind.Conductive ? "● " : "") + "Conductive Surface",
                                     Enabled: metals.Count > 0, Tip: metals.Count > 0 ? null : "The technology defines no conducting material.",
                                     Children: [.. metals.Select(m => new Viewer3DMenuItem((existing?.Material == m ? "● " : "") + m,
                                         () => Report(SetFaceBoundary(o.Name, faceName, Em3dFaceBoundaryKind.Conductive, m))))]),
                new Viewer3DMenuItem((existing is null ? "● " : "") + "None", () => Report(SetFaceBoundary(o.Name, faceName, null))),
            ]);
        }
        yield return new Viewer3DMenuItem("Make Port", Children: [MakePortItem(item, Em3dPortKind.Lumped), MakePortItem(item, Em3dPortKind.Wave)]);
        yield return Viewer3DMenuItem.Separator;
    }

    private static Em3dBoundaryKind FaceKindOf(Em3dAirBox b, string face) => face switch
    {
        "xmin" => b.Faces.XMin, "xmax" => b.Faces.XMax, "ymin" => b.Faces.YMin,
        "ymax" => b.Faces.YMax, "zmin" => b.Faces.ZMin, _ => b.Faces.ZMax,
    };

    private void Report(string? refusal)
    {
        if (refusal is not null) StatusMessage = refusal;
    }

    /// <summary>A text the view should ask for: title, prompt, the prefill, and the commit (null on success, else why not).</summary>
    public event Action<string, string, string, Func<string, string?>>? TextRequested;

    // ── the overlay: numbers, refusals, the port tool's live inference ───────────────────────

    private void FillSimulateOverlay(Viewer3DDrawOverlay overlay)
    {
        int dbu = Document.DbuPerMicron;
        foreach (var r in PortResults)
        {
            if (r.Resolved is { } p)
            {
                overlay.Labels.Add((Centre(p.Min, p.Max), r.Port.Number.ToString(CultureInfo.InvariantCulture)));
                continue;
            }
            var (min, max) = Rectangle(r.Port, dbu);
            AddRect(overlay.Crossing, r.Port, dbu);
            overlay.Labels.Add((Centre(min, max), $"{C3dPorts.Label(r.Port)}: refused"));
        }
        if (_tool is PortTool pt && pt.Preview(CursorInput()) is { } draft)
        {
            var r = C3dPorts.Resolve(draft, dbu, CurrentPortContext());
            var (min, max) = Rectangle(draft, dbu);
            if (r.Resolved is { } p)
            {
                var c = Centre(min, max);
                double side = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) * 0.3;
                var d = p.Direction;
                var from = new Point3(c.X - d.X * side, c.Y - d.Y * side, c.Z - d.Z * side);
                var to = new Point3(c.X + d.X * side, c.Y + d.Y * side, c.Z + d.Z * side);
                overlay.Rubber.Add(new Render.Scene3D.Edit.DrawSegment(from, to));
                overlay.Labels.Add((to, $"+ {p.PositiveObject}"));
                overlay.Labels.Add((from, $"− {p.NegativeObject}"));
            }
            else overlay.Labels.Add((Centre(min, max), r.Refusal ?? ""));
        }
    }

    private static Point3 Centre(Point3 a, Point3 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

    private static (Point3 Min, Point3 Max) Rectangle(C3dPort p, int dbu)
    {
        double M(long v) => C3dLowering.Metres(v, dbu);
        var a = C3dLowering.OnPlane(p.Plane, M(p.Rect.Min.U), M(p.Rect.Min.V), M(p.Offset));
        var b = C3dLowering.OnPlane(p.Plane, M(p.Rect.Min.U + p.Rect.Size.U), M(p.Rect.Min.V + p.Rect.Size.V), M(p.Offset));
        return (new Point3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Point3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    private static void AddRect(List<Render.Scene3D.Edit.DrawSegment> into, C3dPort p, int dbu)
    {
        double M(long v) => C3dLowering.Metres(v, dbu);
        long u0 = p.Rect.Min.U, v0 = p.Rect.Min.V, u1 = u0 + p.Rect.Size.U, v1 = v0 + p.Rect.Size.V;
        var c = new[] { (u0, v0), (u1, v0), (u1, v1), (u0, v1) }
            .Select(q => C3dLowering.OnPlane(p.Plane, M(q.Item1), M(q.Item2), M(p.Offset))).ToArray();
        for (int k = 0; k < 4; k++) into.Add(new Render.Scene3D.Edit.DrawSegment(c[k], c[(k + 1) % 4]));
    }

    // ── the tree: the ports, and each object's face boundaries (R-em3d49-4d) ─────────────────

    private void RebuildRecordsTree()
    {
        foreach (var g in Tree.Where(g => g.Role == C3dTreeGroupRole.Ports).ToList())
        {
            DetachExpansion([g]);
            Tree.Remove(g);
        }
        RebuildAirBoxItem();
        var ports = PortResults.Select(r => new C3dTreeItem(this, C3dPorts.ProblemName(r.Port.Number), "Port",
            r.Resolved is { } p ? $"{C3dPorts.Label(r.Port)} {(p.Kind == Em3dPortKind.Wave ? "wave" : "lumped")}: {p.NegativeObject} → {p.PositiveObject}"
                                : $"{C3dPorts.Label(r.Port)}: refused", -1, -1, true) { IsReadOnly = true }).ToList();
        if (ports.Count > 0) Tree.Add(new C3dTreeGroup("Ports", ports, C3dTreeGroupRole.Ports));
        foreach (var item in Tree.Where(g => g.Role is C3dTreeGroupRole.Objects or C3dTreeGroupRole.Construction).SelectMany(g => g.Items))
        {
            foreach (var c in item.Children.Where(c => c.Kind == "Boundary").ToList()) item.Children.Remove(c);
            foreach (var b in Document.FaceBoundaries.Where(b => b.Object == item.Name))
                item.Children.Add(new C3dTreeItem(this, Scene3DBuilder.FaceTintPrefix + b.Object + "/" + b.Face, "Boundary",
                    $"{b.Face}: {(b.Kind == Em3dFaceBoundaryKind.Conductive ? $"Conductive ({b.Material})" : b.Kind.ToString())}", -1, -1, true)
                    { IsReadOnly = true });
        }
        RestoreExpansion();
    }

    /// <summary>
    /// 3D editor round 1 — the active setup's air box, first under Boxes: selectable (the Properties Inspector shows its
    /// boundaries and padding), never deletable or duplicable — it is the setup's, not the geometry's. Its tick is the
    /// toolbar's air-box switch. Round 2: by material there is no Boxes group to share. Round 3: by material it is listed
    /// under the material that fills it (<see cref="CircuitRF.Design.Layout.Em3d.Em3dGenerator.AirMaterial"/>, beside any object of that material,
    /// in the material groups' name order), and it is listed whenever the document has content — with no active setup
    /// too, when there is no box to draw yet and its row says so: a run always solves in one, and a tree that left it
    /// out until a setup existed read as "there is no air box".
    /// </summary>
    private void RebuildAirBoxItem()
    {
        // 3D editor round 3 — listed when a box is drawn (with no setup, once a solid or a sheet exists), and with a setup
        // whose box could not be built, so its row can say so. Headed, by material, by what fills it (Air by default).
        bool want = ShownAirBox is not null || (ActiveSetup is not null && (Document.Objects.Count > 0 || Document.Instances.Count > 0));
        string detail = ActiveSetup is null ? "no setup yet: a new setup's default"
                      : $"setup {(IsExternalActive ? ExternalItemName : ActiveSetupName)}{(ShownAirBox is null ? ": not built" : "")}";
        string air = C3dProblemAssembly.BoxFill(Document);
        if (Tree.FirstOrDefault(g => g.Items.Any(i => i.IsAirBox)) is { } home)
        {
            var old = home.Items.First(i => i.IsAirBox);
            if (want && old.Detail == detail && (TreeGrouping == C3dTreeGrouping.Primitive || string.Equals(home.Header, air, StringComparison.OrdinalIgnoreCase)))
            {
                old.Sync(AirBoxShown);
                return;
            }
            home.Items.Remove(old);
            if (home.Items.Count == 0) { DetachExpansion([home]); Tree.Remove(home); }
        }
        if (!want) return;
        var item = new C3dTreeItem(this, AirBoxName, C3dTreeItem.AirBoxKind, detail, -1, -1, AirBoxShown) { IsReadOnly = true };
        if (TreeGrouping == C3dTreeGrouping.Primitive)
        {
            if (Tree.FirstOrDefault(g => g.Role == C3dTreeGroupRole.Objects && g.Header == "Boxes") is { } boxes) boxes.Items.Insert(0, item);
            else Tree.Insert(0, new C3dTreeGroup("Boxes", [item]));
            return;
        }
        if (Tree.FirstOrDefault(g => g.Role == C3dTreeGroupRole.Objects && string.Equals(g.Header, air, StringComparison.OrdinalIgnoreCase)) is { } shared)
        {
            shared.Items.Insert(0, item);
            return;
        }
        // Its own group, where the material groups' name order puts it ("No material" leads them).
        int at = 0;
        while (at < Tree.Count && Tree[at].Role == C3dTreeGroupRole.Objects &&
               (Tree[at].Header == NoMaterialHeader || string.Compare(Tree[at].Header, air, StringComparison.OrdinalIgnoreCase) < 0))
            at++;
        Tree.Insert(at, new C3dTreeGroup(air, [item], C3dTreeGroupRole.AirBox));
    }

    /// <summary>The air box's name in the tree and in the scene (its faces are <c>airbox/xmin</c> …).</summary>
    public const string AirBoxName = "airbox";

    /// <summary>The scene objects that are the air box's faces.</summary>
    public IReadOnlyList<Scene3DObject> AirBoxFaceObjects()
        => [.. Viewer.Scene.Objects.Where(o => BoxFaceOf(o) is not null)];

    // ── fields: from the run's own directory, and a banner when the model has moved on (R-em3d49-5b) ──

    /// <summary>The file a run's directory keeps the document it solved in — what the stale-fields banner compares with.</summary>
    public const string RunDocumentFile = "document.c3d";

    [ObservableProperty] private string? _fieldsStaleText;

    private ((string, DateTime) Stamp, string Text)? _solvedCache;

    /// <summary>The shell's results root (the workspace's <c>results</c> folder, or the session's).</summary>
    public Func<string?>? ResultsRootProvider { get; set; }

    /// <summary>The run directories the active setup's run writes, first Palace's.</summary>
    private IEnumerable<string> ActiveRunDirectories()
    {
        if (ActiveRunSetup is not { } s || ResultsRootProvider?.Invoke() is not { } root) yield break;
        if (s.Solver3D is Em3dSolver.Palace or Em3dSolver.Both) yield return Em3dRunService.RunDirectory(root, s, Em3dSolver.Palace);
        if (s.Solver3D is Em3dSolver.OpenEms or Em3dSolver.Both) yield return Em3dRunService.RunDirectory(root, s, Em3dSolver.OpenEms);
    }

    /// <summary>A run finished: the document it solved is kept in its directory, and the fields are read again.</summary>
    public void RunFinished(EmSetup runSetup, string documentText)
    {
        if (ResultsRootProvider?.Invoke() is { } root)
            foreach (var solver in new[] { Em3dSolver.Palace, Em3dSolver.OpenEms })
                if (runSetup.Solver3D == solver || runSetup.Solver3D == Em3dSolver.Both)
                {
                    string dir = Em3dRunService.RunDirectory(root, runSetup, solver);
                    if (!Directory.Exists(dir)) continue;
                    try { File.WriteAllText(Path.Combine(dir, RunDocumentFile), documentText); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StatusMessage = $"The run's document could not be kept: {e.Message}"; }
                }
        Viewer.SetRunSetup(ActiveRunSetup);
        RefreshFieldsStale();
    }

    /// <summary>R-em3d49-5b — the banner: fields from a run whose document differs from the one being edited now. They are
    /// still shown — the solver's own geometry, never re-mapped onto a model it did not see.</summary>
    public void RefreshFieldsStale()
    {
        string? text = null;
        foreach (string dir in ActiveRunDirectories())
        {
            string f = Path.Combine(dir, RunDocumentFile);
            if (!File.Exists(f)) continue;
            // The solved document, read once per file version: this runs on every edit.
            var stamp = (f, File.GetLastWriteTimeUtc(f));
            if (_solvedCache?.Stamp != stamp)
            {
                try { _solvedCache = (stamp, File.ReadAllText(f)); } catch (Exception) { continue; }
            }
            if (_solvedCache!.Value.Text != C3dPersistence.Serialize(Document))
                text = $"Fields are from the run at {File.GetLastWriteTime(f):HH:mm}; the model has changed since. They are drawn on the " +
                       "geometry that run solved.";
            break;
        }
        FieldsStaleText = text;
    }
}
