// brief-em3d-43 — the 3D editor: a .c3d document, and the 3D pane drawing its elaboration.
//
// WHAT THE EDITOR SHOWS IS WHAT THE SOLVER GETS (overview §0). The scene is built from C3dElaborator's
// output — the same elaboration C3dProblemAssembly hands a solver — through Scene3DBuilder, under brief
// 28's generation numbers, never from a second picture of the document. An edit changes the document,
// which is elaborated again; the elaborator's per-object cache makes that ONE object's work, the builder's
// tessellation cache makes it one object's tessellation, and the session's patch makes it one object's
// upload (R-em3d43-1b, gate 6). The document has no setup yet (brief 49), so no air box is drawn.
//
// THE PANE IS THE VIEWER'S (R-em3d43-1a): Viewer3DViewModel with this class as its IViewer3DEditHost. The
// build runs on the thread pool from a SNAPSHOT (the document's own text) taken on the UI thread, so the
// document is never read while it is being edited; the elaborator is serialised by a lock, since two
// generations may overlap for a moment and its caches are not thread-safe.
//
// UNDO (R-em3d43-1c): one entry per user action, storing only what it changed (C3dEdit). A GESTURE — a
// drag, from brief 46 on — edits the document as it goes and commits ONE entry on release (BeginGesture).
// The display unit is a document PREFERENCE, not geometry (owner decision D4): changing it dirties the
// document and is saved, but adds no undo entry, elaborates nothing and moves nothing (gate 10).

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>The UI thread's snapshot for one scene build: the document as its file would say it.</summary>
/// <para>brief-em3d-48 — pushed into a child, the TOP document's text and path, the pushed element's path in it and the
/// element's transform (the child's metres to the top's): the dimmed context.</para>
/// <para>brief-em3d-49 — <paramref name="SetupJson"/> is the active setup's full .cem spelling: its air box is drawn and the
/// ports are resolved against it.</para>
public sealed record C3dSceneInputs(string DocumentText, string Path, string? WorkspaceCws, ColorTheme Theme, ColorVariant Variant,
                                    (string Text, string Path, string Exclude, C3dTransform ToTop)? Context = null,
                                    string? SetupJson = null);

public sealed partial class C3dEditorViewModel : ObservableObject, IViewer3DEditHost, IDisposable
{
    private readonly C3dElaborator _elaborator;
    private readonly C3dElaborator _contextElaborator;
    private readonly object _elaborating = new();
    private readonly Scene3DTessellationCache _tessellations = new();
    private readonly ConcurrentDictionary<long, C3dElaboration> _elaborations = new();
    private readonly Func<string?> _workspaceCws;
    private (double X, double Y, double Z)? _origin;
    private bool _preferenceDirty;
    private bool _loading;
    private string? _savedStamp;

    public string FilePath { get; private set; }
    public C3dDocument Document { get; private set; }
    /// <summary>The ACTIVE frame's history (brief-em3d-48: each pushed-in child has its own, as a layout frame does).</summary>
    public UndoRedoStack UndoRedo { get; private set; } = new();

    /// <summary>The pane's view model — the read-only viewer's own class.</summary>
    public Viewer3DViewModel Viewer { get; }

    public ObservableCollection<C3dTreeGroup> Tree { get; } = [];
    public C3dPropertiesViewModel Properties { get; }

    /// <summary>The elaboration the current scene was built from (UI thread).</summary>
    public C3dElaboration? Elaboration { get; private set; }

    /// <summary>Undo entries pushed — gate 10 reads that a unit change adds none.</summary>
    public int UndoEntries { get; private set; }

    /// <summary>Tessellations the scene builder had to make for this document — gate 6's counter.</summary>
    public long TessellationMisses => _tessellations.Misses;

    /// <summary>The generation whose scene has been adopted and applied (tree, Hidden, Properties).</summary>
    public long AdoptedGeneration => Interlocked.Read(ref _adoptedGeneration);
    private long _adoptedGeneration;

    /// <summary>Objects the elaborator lowered because its cache missed — gate 6's counter.</summary>
    public long ObjectsElaborated { get { lock (_elaborating) return _elaborator.ObjectsElaborated; } }

    /// <summary>brief-em3d-46 gate 2 — placed cells read and built because the child cache missed: moving, rotating or
    /// arraying an instance must leave it where it was.</summary>
    public long ChildrenElaborated { get { lock (_elaborating) return _elaborator.ChildrenElaborated; } }

    public bool IsDirty => UndoRedo.IsModified || _preferenceDirty || OtherFramesDirty;

    /// <summary>Raised when the file changed on disk while the document is dirty — the shell asks.</summary>
    public event Action? ExternalChangeWhileDirty;

    /// <summary>Raised when the Properties panel should show, and focus its name for a rename.</summary>
    public event Action<bool>? PropertiesRequested;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private LayoutUnit _displayUnit;
    [ObservableProperty] private bool _propertiesVisible = true;
    [ObservableProperty] private bool _showTree = true;

    public static IReadOnlyList<LayoutUnit> AllUnits => Layout.LayoutEditorViewModel.AllUnits;

    public C3dEditorViewModel(string path, C3dDocument document, Func<Viewer3DBackend> backend, Func<string?> workspaceCws,
                              Action<Action> post, TechnologyCache? technologies = null)
    {
        FilePath = Path.GetFullPath(path);
        Document = document;
        _workspaceCws = workspaceCws;
        _elaborator = new C3dElaborator(technologies);
        _contextElaborator = new C3dElaborator(technologies);
        _savedStamp = Stamp(FilePath);
        Viewer = new Viewer3DViewModel(FilePath, Path.GetFileName(FilePath), Snapshot, Build, backend, () => ResultsRootProvider?.Invoke(), post)
        {
            EditHost = this,
        };
        Viewer.SceneAdopted += OnSceneAdopted;
        Viewer.SelectionChanged += OnViewerSelectionChanged;
        // brief-em3d-44 R-em3d44-5 — the editor snaps; its switches are the user's, stored per user.
        var (snapOn, kinds) = Snap3DPreference.Preferred;
        Viewer.SnapKinds = kinds;
        Viewer.SnapEnabled = snapOn;
        Viewer.SnapTogglesChanged += () => Snap3DPreference.Preferred = (Viewer.SnapEnabled, Viewer.SnapKinds);
        Viewer.FrameRequested += OnViewerFrame;
        Viewer.CursorResolved += OnCursorResolvedForOperation;
        ApplySnapGrid();
        Properties = new C3dPropertiesViewModel(this);
        WatchStack(UndoRedo);
        InitFrames();
        _loading = true;
        DisplayUnit = document.DisplayUnit;
        _loading = false;
        ApplyLengthFormat();
        RebuildTree();
        SyncPlaneTexts();
    }

    /// <summary>Builds the first scene.</summary>
    public void Start() => Viewer.Regenerate();

    // ── the scene: elaboration → problem → scene ─────────────────────────────────────────────

    private object Snapshot()
        => new C3dSceneInputs(DocumentText(), FilePath, _workspaceCws(), ThemeService.Active, ThemeService.CurrentVariant, ContextSnapshot(),
                              SceneSetupJson());

    /// <summary>
    /// The document as its file would say it — with, while a face or vertex gesture runs, the gesture's edited object
    /// standing in for the document's own (brief-em3d-47 R-em3d47-6). The document's list is swapped for a copy for the
    /// length of one serialisation; its objects are never written.
    /// </summary>
    private string DocumentText()
    {
        if (_facePreview is not { } p || p.Index >= Document.Objects.Count) return C3dPersistence.Serialize(Document);
        var objects = Document.Objects;
        var shown = new List<C3dObject>(objects) { [p.Index] = p.Object };
        Document.Objects = shown;
        try { return C3dPersistence.Serialize(Document); }
        finally { Document.Objects = objects; }
    }

    /// <summary>The origin moves only when the content has moved further than its own size from it, so an
    /// ordinary edit leaves every other object's vertex bytes as they were (gate 6).</summary>
    private static bool NearEnough((double X, double Y, double Z) o, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) e)
    {
        double size = Math.Max(Math.Max(e.X1 - e.X0, e.Y1 - e.Y0), e.Z1 - e.Z0);
        double cx = (e.X0 + e.X1) / 2, cy = (e.Y0 + e.Y1) / 2, cz = (e.Z0 + e.Z1) / 2;
        return Math.Abs(cx - o.X) <= size && Math.Abs(cy - o.Y) <= size && Math.Abs(cz - o.Z) <= size;
    }

    private Scene3DModel Build(long generation, object? state, CancellationToken ct)
    {
        var inputs = (C3dSceneInputs)state!;
        var doc = C3dPersistence.Deserialize(inputs.DocumentText);
        lock (_elaborating)
        {
            ct.ThrowIfCancellationRequested();
            var e = _elaborator.Elaborate(doc, inputs.Path, inputs.WorkspaceCws);
            _elaborations[generation] = e;
            var extent = e.Extent() ?? (-5e-4, -5e-4, -5e-4, 5e-4, 5e-4, 5e-4);
            if (_origin is not { } o || !NearEnough(o, extent))
                _origin = ((extent.X0 + extent.X1) / 2, (extent.Y0 + extent.Y1) / 2, (extent.Z0 + extent.Z1) / 2);
            var a = Em3dBoundaryKind.Absorbing;
            var faces = new Em3dFaces(a, a, a, a, a, a);
            // brief-em3d-49 — the active setup's box, ports and face boundaries, resolved as a run resolves them.
            var records = ResolveRecords(doc, e, inputs.SetupJson);
            _records[generation] = records;
            var box = records.Box ?? new Em3dAirBox(new Point3(extent.X0, extent.Y0, extent.Z0), new Point3(extent.X1, extent.Y1, extent.Z1), faces);
            IReadOnlyList<Em3dSolid> solids = e.Solids;
            IReadOnlyList<Em3dSheet> sheets = e.Sheets;
            IReadOnlyList<Em3dMaterial> materials = e.Materials;
            // brief-em3d-48 R-em3d48-4a — pushed in: the top document around the child, in the child's frame, dimmed.
            if (inputs.Context is { } ctx)
            {
                var top = _contextElaborator.Elaborate(C3dPersistence.Deserialize(ctx.Text), ctx.Path, inputs.WorkspaceCws);
                var (cs, csh, cm) = Context(top, ctx.Exclude, ctx.ToTop, e.Solids.Count + e.Sheets.Count + 1);
                solids = [.. e.Solids, .. cs];
                sheets = [.. e.Sheets, .. csh];
                materials = [.. e.Materials, .. cm.Where(m => !e.Materials.Any(x => x.Name == m.Name))];
            }
            var problem = new Em3dProblem(solids, sheets, materials, [.. records.Ports.Select(r => r.Resolved).OfType<Em3dPort>()], box,
                                          new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), EmSetup.DefaultOperatingTempC);
            var notes = new List<string>(e.Refusals);
            notes.AddRange(e.Warnings);
            notes.AddRange(e.Notes);
            return Scene3DBuilder.Build(problem, generation, e.Origins, e.Technology, inputs.Theme, inputs.Variant, notes,
                new Scene3DBuildOptions(name => e.Provenance.TryGetValue(name, out var p) ? p.FaceNames : null,
                                        _tessellations, DrawAirBox: records.Box is not null, Origin: _origin,
                                        FeatureShare: name => e.Provenance.TryGetValue(name, out var p) ? ShareOf(p) : null,
                                        Instancing: InstancingFor(doc, e),
                                        Context: inputs.Context is null ? null : IsContext,
                                        EditorBoundaries: true,
                                        FaceTints: [.. records.Boundaries.Where(b => b.Refusal is null)
                                                           .Select(b => new Scene3DFaceTint(b.Boundary.Object + "/" + b.Boundary.Face, b.Boundary.Kind, b.Pieces))]));
        }
    }

    /// <summary>
    /// brief-em3d-44 R-em3d44-3b — objects of one child document under element transforms with the same
    /// rotation are one mesh moved by a translation, so they share one feature table: the key is (file,
    /// object, rotation), the translation is the element's. The document's own objects have their own.
    /// </summary>
    private static Scene3DFeatureShare? ShareOf(C3dProvenance p)
    {
        if (p.Element is not { } w) return null;
        string key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{p.DocumentPath}|{p.ObjectName}|{w.M00:R},{w.M01:R},{w.M02:R},{w.M10:R},{w.M11:R},{w.M12:R},{w.M20:R},{w.M21:R},{w.M22:R}");
        return new Scene3DFeatureShare(key, w.Tx, w.Ty, w.Tz);
    }

    private void OnSceneAdopted()
    {
        long gen = Viewer.Scene.Generation;
        if (_elaborations.TryGetValue(gen, out var e)) Elaboration = e;
        AdoptRecords(gen);
        ApplySnapGrid();
        ApplySnapExclusion();
        foreach (long old in _elaborations.Keys.Where(k => k <= gen).ToList()) _elaborations.TryRemove(old, out _);
        ApplyHiddenFlags();
        RefreshTreeVisibility();
        RebuildInstanceChildren();
        RememberInstanceBounds();
        if (_fitOnAdopt && Viewer.Scene.Objects.Length > 0)
        {
            _fitOnAdopt = false;
            Viewer.FitCommand.Execute(null);
        }
        Properties.Reload();
        OnPropertyChanged(nameof(Materials));
        SyncCurrentMaterial();
        RefreshGridText();
        Interlocked.Exchange(ref _adoptedGeneration, gen);
        ReleaseHeldPreview(gen);
        ReselectFace();
    }

    /// <summary>A document object's <c>Hidden</c> is document state (brief 41 §2a): the pane follows it.</summary>
    private void ApplyHiddenFlags()
    {
        foreach (var o in Document.Objects)
            if (SceneObject(o.Name) is { } s) Viewer.SetVisibleEverywhere(s.Id, !o.Hidden);
    }

    /// <summary>The scene object a document object became, by its name, or null (a polyline, a refusal).</summary>
    public Scene3DObject? SceneObject(string name) => Viewer.Scene.Objects.FirstOrDefault(o => o.Name == name);

    /// <summary>The document index of a scene object that is the document's own, or −1.</summary>
    public int DocumentIndex(Scene3DObject o) => InstanceOf(o) is null ? Document.Objects.FindIndex(d => d.Name == o.Name) : -1;

    // ── IViewer3DEditHost ────────────────────────────────────────────────────────────────────

    public string KindOf(Scene3DObject o)
    {
        if (InstanceOf(o) is { } inst) return $"Part of instance {inst}";
        return Document.Objects.FirstOrDefault(d => d.Name == o.Name) is { } obj ? C3dObject.KindOf(obj) : o.Kind.ToString();
    }

    public string? InstanceOf(Scene3DObject o)
        => Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true && p.InstancePath.Length > 0 ? p.InstancePath : null;

    public IReadOnlyList<string> Materials
        => Elaboration?.Technology?.Materials.Select(m => m.Name).ToList() ?? (IReadOnlyList<string>)[];

    public bool SetHidden(IReadOnlyList<Scene3DObject> objects, bool hidden, string description)
    {
        var indices = objects.Select(DocumentIndex).Where(i => i >= 0).Distinct().ToList();
        if (indices.Count == 0) return false;
        ChangeObjects(description, indices, o => o.Hidden = hidden);
        // An instance's contents are not the document's to hide: the view hides them for this session.
        foreach (var o in objects.Where(o => DocumentIndex(o) < 0)) Viewer.SetVisibleEverywhere(o.Id, !hidden);
        if (hidden) Viewer.SetSelection([]);
        return true;
    }

    public bool ShowAll(IReadOnlyList<Scene3DObject>? keep)
    {
        var keepNames = keep?.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var indices = Enumerable.Range(0, Document.Objects.Count)
            .Where(i => Document.Objects[i].Hidden != (keepNames is not null && !keepNames.Contains(Document.Objects[i].Name)))
            .ToList();
        if (indices.Count > 0)
            ChangeObjects(keep is null ? "Show all" : "Isolate", indices,
                          o => o.Hidden = keepNames is not null && !keepNames.Contains(o.Name));
        foreach (var s in Viewer.Scene.Objects.Where(s => s.Pickable && DocumentIndex(s) < 0))
            Viewer.SetVisibleEverywhere(s.Id, keepNames is null || keepNames.Contains(s.Name));
        return true;
    }

    public void SetMaterial(IReadOnlyList<Scene3DObject> objects, string material)
    {
        var indices = objects.Select(DocumentIndex).Where(i => i >= 0).Distinct().ToList();
        if (indices.Count == 0) return;
        ChangeObjects(indices.Count == 1 ? $"Material of {Document.Objects[indices[0]].Name}" : $"Material of {indices.Count} objects",
                      indices, o => o.Material = material);
    }

    public bool DeleteSelection()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Object) return false;
        // brief-em3d-49 — a selected port is a document record: deleted as one.
        if (SelectedPorts() is { Count: > 0 } ports) { DeletePorts(ports); return true; }
        var objects = Viewer.SelectedObjects();
        var indices = objects.Select(DocumentIndex).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        if (indices.Count == 0)
        {
            if (objects.Count > 0) StatusMessage = "Nothing deletable is selected: an instance's contents belong to its own cell.";
            return objects.Count > 0;
        }
        var slots = indices.Select(i => new C3dEditSlot(false, i, C3dPersistence.SerializeObject(Document.Objects[i]), null)).ToList();
        Viewer.SetSelection([]);
        Push(new C3dEdit(indices.Count == 1 ? $"Delete {Document.Objects[indices[0]].Name}" : $"Delete {indices.Count} objects",
                         slots, ApplySlots));
        return true;
    }

    public void ShowProperties(bool rename)
    {
        PropertiesVisible = true;
        PropertiesRequested?.Invoke(rename);
    }

    // ── edits ────────────────────────────────────────────────────────────────────────────────

    /// <summary>One undoable edit of the objects at <paramref name="indices"/>: each is copied, the copy is
    /// changed, and only the ones whose file spelling changed are in the entry. Nothing changed, no entry.</summary>
    public void ChangeObjects(string description, IReadOnlyList<int> indices, Action<C3dObject> mutate)
    {
        var slots = new List<C3dEditSlot>();
        foreach (int i in indices.Distinct().OrderBy(i => i))
        {
            string before = C3dPersistence.SerializeObject(Document.Objects[i]);
            var copy = C3dPersistence.DeserializeObject(before);
            mutate(copy);
            string after = C3dPersistence.SerializeObject(copy);
            if (after != before) slots.Add(new C3dEditSlot(false, i, before, after));
        }
        if (slots.Count > 0) Push(new C3dEdit(description, slots, ApplySlots));
    }

    /// <summary>A rename: validated here, one entry, and the pane keeps the object selected under its new name.</summary>
    public string? Rename(int index, string name)
    {
        name = name.Trim();
        var obj = Document.Objects[index];
        if (name == obj.Name) return null;
        if (name.Length == 0) return "A name cannot be empty.";
        if (string.Equals(name, "airbox", StringComparison.OrdinalIgnoreCase)) return "'airbox' is reserved: the air box's faces are named after it.";
        if (name.Contains('/')) return "A name cannot hold '/': an instance's contents are named '<instance>/<object>'.";
        if (Document.Objects.Any(o => o != obj && o.Name == name) || Document.Instances.Any(i => i.Name == name))
            return $"'{name}' is already the name of something in this 3D view.";
        Viewer.ExpectRename(obj.Name, name);
        ChangeObjects($"Rename {obj.Name} to {name}", [index], o => o.Name = name);
        return null;
    }

    private void Push(IUiCommand edit)
    {
        UndoRedo.Execute(edit);
        UndoEntries++;
        StatusMessage = "";
    }

    /// <summary>Times the document's objects were written — brief-em3d-47 gate 7 reads that a drag writes none.</summary>
    public int DocumentWrites { get; private set; }

    /// <summary>The one place the document's objects change, for every entry, forward and back.</summary>
    private void ApplySlots(IReadOnlyList<C3dEditSlot> slots, bool forward)
    {
        DocumentWrites++;
        C3dEdit.Apply(Document, slots, forward);
        DocumentChanged();
    }

    /// <summary>The document changed: elaborate again (the caches make it the changed objects' work), and
    /// bring the tree and the panel up to date.</summary>
    private void DocumentChanged()
    {
        Viewer.Regenerate();
        RebuildTree();
        Properties.Reload();
        OnPropertyChanged(nameof(IsDirty));
        RefreshFieldsStale();
    }

    /// <summary>
    /// R-em3d43-1c — a gesture (a drag) changes the objects at <paramref name="indices"/> as it goes and is
    /// ONE undo entry, pushed by <see cref="C3dGesture.Commit"/>. Each <see cref="C3dGesture.Update"/> re-elaborates
    /// the changed objects only, so a drag previews through the same path an edit takes.
    /// </summary>
    /// <para>brief-em3d-44 R-snpf-4: what the gesture moves never attracts the snap, however the gesture started —
    /// the objects themselves unless <paramref name="excludeObjects"/> is false (a face or vertex drag, which
    /// names what it moves with <see cref="C3dGesture.ExcludeFace"/> / <see cref="C3dGesture.ExcludeVertex"/>).</para>
    public C3dGesture BeginGesture(string description, IReadOnlyList<int> indices, bool excludeObjects = true)
    {
        var g = new C3dGesture(this, description, indices);
        if (excludeObjects)
        {
            foreach (int i in indices) _snapExcludedObjects.Add(Document.Objects[i].Name);
            ApplySnapExclusion();
        }
        return g;
    }

    public sealed class C3dGesture
    {
        private readonly C3dEditorViewModel _owner;
        private readonly string _description;
        private readonly List<(int Index, string Before)> _before;
        private bool _done;

        internal C3dGesture(C3dEditorViewModel owner, string description, IReadOnlyList<int> indices)
        {
            _owner = owner;
            _description = description;
            _before = [.. indices.Distinct().OrderBy(i => i).Select(i => (i, C3dPersistence.SerializeObject(owner.Document.Objects[i])))];
        }

        /// <summary>
        /// A face drag: face <paramref name="face"/> of <paramref name="objectName"/> never attracts the snap while
        /// the gesture lasts — its corners, edges and centre wherever the scene now has them, and its corners
        /// where they are NOW (the old position, until the scene has caught up with the drag).
        /// </summary>
        public void ExcludeFace(string objectName, int face)
        {
            _owner._snapExcludedFaces.Add((objectName, face));
            if (_owner.SceneObject(objectName) is { } o && _owner.Viewer.Scene.FeaturesOf(o.Id) is { Table: { } t } fr && face >= 0 && face < t.FaceCount)
                for (int k = t.FaceVertexStart[face]; k < t.FaceVertexStart[face + 1]; k++)
                    _owner._snapExcludedPoints.Add(fr.Vertex(t.FaceVertices[k]));
            _owner.ApplySnapExclusion();
        }

        /// <summary>A vertex drag: the corner at <paramref name="world"/> (metres) never attracts the snap.</summary>
        public void ExcludeVertex(Point3 world)
        {
            _owner._snapExcludedPoints.Add(world);
            _owner.ApplySnapExclusion();
        }

        /// <summary>One step of the gesture: the objects change in the document, no entry is pushed.</summary>
        public void Update(Action<C3dObject> mutate)
        {
            if (_done) throw new InvalidOperationException("This gesture has ended.");
            _owner.DocumentWrites++;
            foreach (var (i, _) in _before) mutate(_owner.Document.Objects[i]);
            _owner.DocumentChanged();
        }

        /// <summary>The release: one entry holding each object's first before and last after.</summary>
        public void Commit()
        {
            if (_done) return;
            _done = true;
            _owner.ClearSnapExclusion();
            var slots = _before.Select(b => new C3dEditSlot(false, b.Index, b.Before, C3dPersistence.SerializeObject(_owner.Document.Objects[b.Index])))
                               .Where(s => s.Before != s.After).ToList();
            if (slots.Count > 0) _owner.Push(new C3dEdit(_description, slots, _owner.ApplySlots, alreadyApplied: true));
        }

        /// <summary>Esc: every object back as it was, no entry.</summary>
        public void Cancel()
        {
            if (_done) return;
            _done = true;
            _owner.ClearSnapExclusion();
            foreach (var (i, before) in _before) _owner.Document.Objects[i] = C3dPersistence.DeserializeObject(before);
            _owner.DocumentChanged();
        }
    }

    // ── snapping (brief-em3d-44) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d44-3c / brief-em3d-45 R-em3d45-2d — the grid the snap uses: the drawing plane, at the document's snap
    /// step (its technology's default when the document states none) — not necessarily the drawn minor spacing.
    /// The pane's drawn grid follows the same plane.
    /// </summary>
    private void ApplySnapGrid()
    {
        Viewer.SnapGrid = new Snap3DGrid(_plane.Plane, _plane.OffsetDbu, SnapPitch, Document.DbuPerMicron);
        ApplyDrawingGrid();
    }

    /// <summary>
    /// R-em3d44-4 — a snapped point as a DBU point of this document, and whether it IS one exactly: the object's
    /// placements up to this document are all integral at this document's DBU (the elaboration says so), and
    /// the point converts to integers. Otherwise the point is metres and <see cref="C3dSnapPoint.Dbu"/> is it
    /// rounded — which a tool uses only at the moment it commits.
    /// </summary>
    public C3dSnapPoint ToDocumentPoint(in Snap3DResult snap)
    {
        bool chain = snap.Kind == Snap3DKind.Grid
                     || (Viewer.Scene.Object(snap.Object) is { } o &&
                         (IsContext(o.Name) ? _frames.Count > 0 && _frames[^1].Exact
                                            : Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true && p!.Exact));
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        (long D, bool Whole) Of(double m)
        {
            double d = m / per, r = Math.Round(d);
            return ((long)r, Math.Abs(d - r) <= 1e-6 + 1e-12 * Math.Abs(d));
        }
        var (x, wx) = Of(snap.World.X);
        var (y, wy) = Of(snap.World.Y);
        var (z, wz) = Of(snap.World.Z);
        return new C3dSnapPoint(new C3dPoint3(x, y, z), snap.World, chain && wx && wy && wz);
    }

    /// <summary>R-em3d44-5 — <c>(x, y, z) µm</c> in the display unit, with <c>≈</c> when not exact.</summary>
    public string? SnapPointText(Snap3DResult snap)
    {
        var p = ToDocumentPoint(snap);
        string F(long dbu) => LayoutUnits.Format(dbu, Document.DisplayUnit, Document.DbuPerMicron);
        return (p.Exact ? "" : "≈ ") + $"({F(p.Dbu.X)}, {F(p.Dbu.Y)}, {F(p.Dbu.Z)}) {LayoutUnits.Suffix(Document.DisplayUnit)}";
    }

    // R-snpf-4 — what a gesture moves, by NAME, so it survives every regeneration the gesture causes.
    private readonly HashSet<string> _snapExcludedObjects = new(StringComparer.Ordinal);
    private readonly List<(string Object, int Face)> _snapExcludedFaces = [];
    private readonly List<Point3> _snapExcludedPoints = [];

    /// <summary>The pane's exclusion, from the names, for the scene it now shows.</summary>
    private void ApplySnapExclusion()
    {
        if (_snapExcludedObjects.Count == 0 && _snapExcludedFaces.Count == 0 && _snapExcludedPoints.Count == 0)
        {
            Viewer.SnapExclusion = null;
            return;
        }
        var ex = new Snap3DExclusion();
        foreach (string n in _snapExcludedObjects) if (SceneObject(n) is { } o) ex.Objects.Add(o.Id);
        foreach (var (n, f) in _snapExcludedFaces) if (SceneObject(n) is { } o) ex.Faces.Add((o.Id, f));
        ex.Points.AddRange(_snapExcludedPoints);
        Viewer.SnapExclusion = ex;
    }

    private void ClearSnapExclusion()
    {
        _snapExcludedObjects.Clear();
        _snapExcludedFaces.Clear();
        _snapExcludedPoints.Clear();
        Viewer.SnapExclusion = null;
    }

    // ── the display unit (owner decision D4: a preference, not geometry) ────────────────────

    partial void OnDisplayUnitChanged(LayoutUnit value)
    {
        if (_loading) return;
        Document.DisplayUnit = value;
        _preferenceDirty = true;
        OnPropertyChanged(nameof(IsDirty));
        ApplyLengthFormat();
        Properties.Reload();
        ApplyDrawingGrid();
        SyncPlaneTexts();
    }

    private void ApplyLengthFormat()
    {
        var f = EmLengthFormat.For(Document.DisplayUnit, Document.DbuPerMicron);
        Viewer.SetLengthFormat(m => f(m));
        Viewer.SetMeasureUnits(Document.DisplayUnit, Document.DbuPerMicron);
    }

    /// <summary>A length in DBU, spelled in the display unit with its suffix.</summary>
    public string Length(long dbu)
        => $"{LayoutUnits.Format(dbu, Document.DisplayUnit, Document.DbuPerMicron)} {LayoutUnits.Suffix(Document.DisplayUnit)}";

    // ── save, reload, external change (R-em3d43-1c / -1d) ────────────────────────────────────

    /// <summary>Writes the document — and, pushed in, every other frame with unsaved edits (brief-em3d-48 R-em3d48-4c:
    /// the save the schematic and layout hierarchies have); null on success, else why not.</summary>
    public string? Save()
    {
        try { C3dPersistence.SaveToFile(FilePath, Document); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
        _savedStamp = Stamp(FilePath);
        UndoRedo.MarkSaved();
        _preferenceDirty = false;
        foreach (var f in _frames.Take(Math.Max(0, _frames.Count - 1)).Where(f => f.UndoRedo.IsModified || f.PreferenceDirty))
        {
            try { C3dPersistence.SaveToFile(f.FilePath, f.Document); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{Path.GetFileName(f.FilePath)}: {ex.Message}"; }
            f.SavedStamp = Stamp(f.FilePath);
            f.UndoRedo.MarkSaved();
            f.PreferenceDirty = false;
        }
        OnPropertyChanged(nameof(IsDirty));
        return null;
    }

    /// <summary>Writes the document to <paramref name="path"/> and follows it there; null on success.</summary>
    public string? SaveAs(string path)
    {
        if (CanPopOut) return "Pop out to the top document first: Save As writes the tab's own file.";
        string was = FilePath;
        FilePath = Path.GetFullPath(path);
        if (Save() is { } why) { FilePath = was; return why; }
        Viewer.Regenerate();         // the document's own path is where its relative references resolve from
        return null;
    }

    /// <summary>The file changed on disk. Our own save is ignored; a clean document reloads without asking; a
    /// dirty one raises <see cref="ExternalChangeWhileDirty"/> and the shell asks, as for a layout.</summary>
    public void OnFileChangedOnDisk()
    {
        if (CanPopOut) { Viewer.Invalidate(); return; }       // pushed in: the top file is context, redrawn
        string? stamp = Stamp(FilePath);
        if (stamp is null || stamp == _savedStamp) return;
        if (IsDirty) { ExternalChangeWhileDirty?.Invoke(); return; }
        Reload();
    }

    /// <summary>A placed cell's .c3d or .clay changed: elaborate again. The elaborator's child cache is keyed
    /// by the file's stamp, so only that instance is rebuilt, and nothing is asked.</summary>
    public void OnChildChanged() => Viewer.Invalidate();

    /// <summary>Reads the file again, dropping the history (it described a document that is gone).</summary>
    public string? Reload()
    {
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(FilePath); }
        catch (Exception ex) { return ex.Message; }
        Document = doc;
        _savedStamp = Stamp(FilePath);
        UndoRedo.Reset();
        InitFrames();
        _preferenceDirty = false;
        _loading = true;
        DisplayUnit = doc.DisplayUnit;
        _loading = false;
        ApplyLengthFormat();
        Viewer.SetSelection([]);
        SetTool(null);
        ApplySnapGrid();
        SyncPlaneTexts();
        DocumentChanged();
        return null;
    }

    private static string? Stamp(string path)
    {
        try
        {
            var f = new FileInfo(path);
            return f.Exists ? $"{f.LastWriteTimeUtc.Ticks}:{f.Length}" : null;
        }
        catch (Exception) { return null; }
    }

    // ── the tree (R-em3d43-6a) ───────────────────────────────────────────────────────────────

    private bool _syncingTree;

    [ObservableProperty] private C3dTreeItem? _selectedTreeItem;

    private static readonly (Type Type, string Header)[] Groups =
    [
        (typeof(C3dBox), "Boxes"), (typeof(C3dPrism), "Prisms"), (typeof(C3dCylinder), "Cylinders"),
        (typeof(C3dPolyhedron), "Polyhedra"), (typeof(C3dSheet), "Sheets"), (typeof(C3dPolyline), "Polylines"),
    ];

    /// <summary>Objects in construction order, grouped by kind; then instances, each expandable to its
    /// cell's objects (read-only).</summary>
    private void RebuildTree()
    {
        string? keep = SelectedTreeItem?.Name;
        _syncingTree = true;
        try
        {
            Tree.Clear();
            foreach (var (type, header) in Groups)
            {
                var items = Document.Objects.Select((o, i) => (o, i)).Where(t => t.o.GetType() == type)
                    .Select(t => new C3dTreeItem(this, t.o.Name, C3dObject.KindOf(t.o), t.o.Material, t.i, -1, !t.o.Hidden)).ToList();
                if (items.Count > 0) Tree.Add(new C3dTreeGroup(header, items));
            }
            var instances = Document.Instances.Select((inst, i) => new C3dTreeItem(this, inst.Name, "Instance", inst.CellRef, -1, i, true)).ToList();
            if (instances.Count > 0) Tree.Add(new C3dTreeGroup("Instances", instances));
            RebuildInstanceChildren();
            RebuildRecordsTree();
            SelectedTreeItem = keep is null ? null : AllTreeItems().FirstOrDefault(t => t.Name == keep);
        }
        finally { _syncingTree = false; }
    }

    private IEnumerable<C3dTreeItem> AllTreeItems()
        => Tree.SelectMany(g => g.Items).SelectMany(i => i.Children.Prepend(i));

    /// <summary>Each instance's children, from the elaboration: what the placed cell contributed.</summary>
    private void RebuildInstanceChildren()
    {
        if (Elaboration is not { } e) return;
        foreach (var inst in Tree.Where(g => g.Header == "Instances").SelectMany(g => g.Items))
        {
            inst.Children.Clear();
            foreach (var (name, p) in e.Provenance.Where(kv => kv.Value.InstancePath == inst.Name ||
                                                               kv.Value.InstancePath.StartsWith(inst.Name + "/", StringComparison.Ordinal) ||
                                                               kv.Value.InstancePath.StartsWith(inst.Name + "[", StringComparison.Ordinal)))
                inst.Children.Add(new C3dTreeItem(this, name, "Part", Path.GetFileName(p.DocumentPath), -1, -1,
                                                  SceneObject(name) is { } s && Viewer.View.IsVisible(s.Id)) { IsReadOnly = true });
        }
    }

    private void RefreshTreeVisibility()
    {
        foreach (var item in AllTreeItems())
            if (item.ObjectIndex >= 0 && item.ObjectIndex < Document.Objects.Count) item.Sync(!Document.Objects[item.ObjectIndex].Hidden);
            else if (SceneObject(item.Name) is { } s) item.Sync(Viewer.View.IsVisible(s.Id));
    }

    /// <summary>A tree checkbox: a document object's writes <c>Hidden</c> (undoable); an instance's contents
    /// are hidden in the view only.</summary>
    internal void TreeVisibilityChanged(C3dTreeItem item, bool visible)
    {
        if (_syncingTree) return;
        if (item.ObjectIndex >= 0)
        {
            ChangeObjects($"{(visible ? "Show" : "Hide")} {item.Name}", [item.ObjectIndex], o => o.Hidden = !visible);
            return;
        }
        var names = item.InstanceIndex >= 0 ? item.Children.Select(c => c.Name) : [item.Name];
        foreach (string n in names)
            if (SceneObject(n) is { } s) Viewer.SetVisibleEverywhere(s.Id, visible);
        foreach (var c in item.Children) c.Sync(visible);
    }

    partial void OnSelectedTreeItemChanged(C3dTreeItem? value)
    {
        if (_syncingTree || value is null) return;
        _syncingTree = true;
        try
        {
            var names = value.InstanceIndex >= 0 ? value.Children.Select(c => c.Name).ToList() : [value.Name];
            var ids = names.Select(SceneObject).OfType<Scene3DObject>().Select(s => Scene3DItem.OfObject(s.Id)).ToList();
            if (Viewer.SelectMode != Scene3DSelectMode.Object) Viewer.SelectMode = Scene3DSelectMode.Object;
            Viewer.SetSelection(ids);
        }
        finally { _syncingTree = false; }
    }

    private void OnViewerSelectionChanged()
    {
        Properties.Reload();
        if (_syncingTree) return;
        _syncingTree = true;
        try
        {
            var first = Viewer.SelectedObjects().FirstOrDefault();
            SelectedTreeItem = first is null ? null
                : AllTreeItems().FirstOrDefault(t => t.Name == first.Name)
                  ?? (InstanceOf(first) is { } inst ? AllTreeItems().FirstOrDefault(t => t.InstanceIndex >= 0 && t.Name == inst.Split('/', '[')[0]) : null);
        }
        finally { _syncingTree = false; }
    }

    /// <summary>A camera move changes the drawn grid spacing: the status line follows (cheap arithmetic, no geometry).</summary>
    private void OnViewerFrame()
    {
        if (ShowDrawingGrid) RefreshGridText();
    }

    public void Dispose() => Viewer.Dispose();
}

/// <summary>brief-em3d-44 R-em3d44-4 — a snapped point in the document: <paramref name="Dbu"/> (rounded when
/// not <paramref name="Exact"/>) and the metres it came from.</summary>
public readonly record struct C3dSnapPoint(C3dPoint3 Dbu, Point3 Metres, bool Exact);

/// <summary>A group of the editor's tree.</summary>
public sealed class C3dTreeGroup(string header, IEnumerable<C3dTreeItem> items)
{
    public string Header { get; } = header;
    public ObservableCollection<C3dTreeItem> Items { get; } = [.. items];
}

/// <summary>One node of the editor's tree: a document object, an instance, or (read-only) an instance's part.</summary>
public sealed partial class C3dTreeItem(C3dEditorViewModel owner, string name, string kind, string? detail,
                                        int objectIndex, int instanceIndex, bool visible) : ObservableObject
{
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string? Detail { get; } = detail;
    public int ObjectIndex { get; } = objectIndex;
    public int InstanceIndex { get; } = instanceIndex;

    /// <summary>brief-em3d-46 R-em3d46-4d — a document object's place in construction order (1-based), which decides
    /// which solid wins an overlap; empty for an instance and its parts.</summary>
    public string OrderText => ObjectIndex >= 0 ? $"#{ObjectIndex + 1}" : "";
    public bool IsReadOnly { get; init; }
    public ObservableCollection<C3dTreeItem> Children { get; } = [];

    [ObservableProperty] private bool _isVisible = visible;

    partial void OnIsVisibleChanged(bool value) => owner.TreeVisibilityChanged(this, value);

    /// <summary>Set without calling back — the document or the view changed it.</summary>
    internal void Sync(bool visible)
    {
#pragma warning disable MVVMTK0034
        if (_isVisible == visible) return;
        _isVisible = visible;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsVisible));
    }
}
