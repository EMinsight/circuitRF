// brief-em3d-83 — FIELD PLOTS are records of the document: a Field Plots group in the tree (after Probes, in both groupings),
// each row ticked, renamed, duplicated and deleted like a probe, each edit one undo entry, saved with the .c3d.
//
// ONE PLOT IS DRAWN AT A TIME (owner decision Q2): ticking one unticks the others, and the viewer draws the one left ticked
// (Viewer3DViewModel.SetPlot). The others stay in the document and in the tree. A new plot PINS the setup active when it was
// made (Q3) — switching setups never changes what an existing plot shows — and is named Field1, Field2 … (Q1).
//
// A PLOT'S EDIT IS NOT A MODEL EDIT: it rebuilds the tree and redraws the field, never the scene, and the stale banner never
// moves for it (C3dPersistence.SerializeForRun). A plot whose data is missing stays, draws nothing, and its row says why.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The kind of a field plot's tree row.</summary>
    public const string FieldPlotKind = "Field plot";

    /// <summary>The Field Plots group's header tooltip in a setup's 3D view, which has no document to keep a plot in.</summary>
    public const string SessionPlotsTip = "Field plots of a setup's 3D view last for this session: the view has no document to save them in. " +
                                          "Open the 3D view the setup belongs to, to keep one.";

    /// <summary>The plot named <paramref name="name"/>, or null.</summary>
    public C3dFieldPlot? FieldPlot(string name) => Document.FieldPlots.FirstOrDefault(p => p.Name == name);

    /// <summary>The plot drawn: the one ticked.</summary>
    public C3dFieldPlot? VisibleFieldPlot => Document.FieldPlots.FirstOrDefault(p => !p.Hidden);

    /// <summary>The selected row's plot, or null.</summary>
    public C3dFieldPlot? SelectedFieldPlot => SelectedTreeItem is { Kind: FieldPlotKind } r ? FieldPlot(r.Name) : null;

    /// <summary>The active setup's name as the tree and a plot's Setup spell it (the external <c>.cem</c>'s own).</summary>
    private string? ActiveSetupDisplayName => IsViewOnly ? null : IsExternalActive ? ExternalItemName : ActiveSetupName;

    // ── edits: one undo entry each ───────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d83-1 — every plot edit: <paramref name="mutate"/> changes the plot list, and the change is ONE undo entry and a
    /// dirty mark. The plots alone are kept before and after: a plot edit touches nothing else. False when nothing changed.
    /// </summary>
    public bool ChangePlots(string description, Action<List<C3dFieldPlot>> mutate)
    {
        string before = C3dPersistence.SerializeFieldPlots(Document.FieldPlots);
        mutate(Document.FieldPlots);
        string after = C3dPersistence.SerializeFieldPlots(Document.FieldPlots);
        if (after == before) return false;
        Push(new C3dRecordsEdit(description, before, after, ApplyPlotRecords, alreadyApplied: true));
        PlotsChanged();
        return true;
    }

    private void ApplyPlotRecords(string text)
    {
        Document.FieldPlots = C3dPersistence.DeserializeFieldPlots(text);
        PlotsChanged();
    }

    /// <summary>The plots changed: the tree, the drawn plot, the inspector, the dirty mark — never the scene.</summary>
    private void PlotsChanged()
    {
        RebuildTree();
        ApplyVisiblePlot();
        Properties.Reload();
        OnPropertyChanged(nameof(IsDirty));
        RaiseMenuStateChanged();
    }

    /// <summary>Shows <paramref name="plot"/> and hides every other (one drawn at a time).</summary>
    private static void ShowOnly(List<C3dFieldPlot> plots, C3dFieldPlot plot)
    {
        foreach (var p in plots) p.Hidden = !ReferenceEquals(p, plot);
    }

    /// <summary>The tick: <paramref name="shown"/> draws the plot (and hides the others), or hides it.</summary>
    public void SetPlotShown(string name, bool shown)
    {
        if (FieldPlot(name) is null) return;
        ChangePlots($"{(shown ? "Show" : "Hide")} {name}", plots =>
        {
            var p = plots.First(x => x.Name == name);
            if (shown) ShowOnly(plots, p); else p.Hidden = true;
        });
    }

    /// <summary>Changes one plot's fields: one undo entry. Null, or why not.</summary>
    public string? SetFieldPlot(string name, string description, Action<C3dFieldPlot> mutate)
    {
        if (FieldPlot(name) is null) return $"There is no field plot named '{name}'.";
        ChangePlots(description, plots => mutate(plots.First(x => x.Name == name)));
        return null;
    }

    /// <summary>Renames a plot: unique among the plots, and not empty.</summary>
    public string? RenameFieldPlot(string name, string newName)
    {
        newName = newName.Trim();
        if (newName == name) return null;
        if (newName.Length == 0) return "A field plot needs a name.";
        if (FieldPlot(newName) is not null) return $"There is already a field plot named '{newName}'.";
        return SetFieldPlot(name, $"Rename {name} to {newName}", p => p.Name = newName);
    }

    /// <summary>A copy beside the original, named as a new plot is, and drawn.</summary>
    public void DuplicateFieldPlot(string name)
    {
        if (FieldPlot(name) is not { } source) return;
        var copy = C3dPersistence.DeserializeFieldPlots(C3dPersistence.SerializeFieldPlots([source]))[0];
        copy.Name = C3dFieldPlot.NextName(Document.FieldPlots);
        ChangePlots($"Duplicate {name}", plots =>
        {
            plots.Insert(plots.FindIndex(p => p.Name == name) + 1, copy);
            ShowOnly(plots, copy);
        });
        SelectPlotRow(copy.Name);
    }

    public void DeleteFieldPlot(string name) => ChangePlots($"Delete {name}", plots => plots.RemoveAll(p => p.Name == name));

    /// <summary>R-em3d83-3 — New Field Plot…: a plot of the active setup, drawn, selected, and the inspector brought forward to
    /// define it. <paramref name="temperature"/> null takes the active setup's kind.</summary>
    [RelayCommand]
    public void NewFieldPlot() => AddFieldPlot(null, null);

    private C3dFieldPlot AddFieldPlot(bool? temperature, Action<C3dFieldPlot>? shape, bool select = true)
    {
        var plot = DefaultPlot(temperature);
        shape?.Invoke(plot);
        ChangePlots($"New field plot {plot.Name}", plots =>
        {
            plots.Add(plot);
            ShowOnly(plots, plot);
        });
        if (select)
        {
            SelectPlotRow(plot.Name);
            PropertiesRequested?.Invoke(false);
        }
        return FieldPlot(plot.Name) ?? plot;
    }

    /// <summary>
    /// A new plot's defaults: the active setup (pinned, Q3), the first solution its run saved (by value), |E| — or the
    /// temperature on every exposed face for a thermal setup — cut where the view's clip plane cuts, or on z through the middle
    /// of the model.
    /// </summary>
    private C3dFieldPlot DefaultPlot(bool? temperature)
    {
        var setup = ActiveSetup;
        bool thermal = temperature ?? setup is { IsThermal: true };
        var plot = new C3dFieldPlot
        {
            Name = C3dFieldPlot.NextName(Document.FieldPlots),
            Setup = ActiveSetupDisplayName,
            Solver = setup is { Solver3D: Em3dSolver.Both } ? Em3dSolver.Palace : null,
            Quantity = thermal ? C3dFieldPlot.TemperatureQuantity : "E",
            Mode = thermal ? null : nameof(FieldMode.Peak),
            On = thermal ? C3dFieldPlotOn.Surfaces : C3dFieldPlotOn.ClipPlane,
        };
        var request = PlotRequest(plot, resolveScene: false);
        if (FieldPlotResolver.PickSolution(null, request.Solver, Discovered(request).Items, Viewer.Scene.Problem) is { } first)
            plot.Solution = FieldPlotResolver.SolutionKey(first.Solution, Viewer.Scene.Problem);
        (plot.Axis, plot.Offset) = ClipOfView();
        return plot;
    }

    /// <summary>The view's clip plane as a plot's axis and DBU offset, or z through the middle of the model when it is off (or
    /// faces the view).</summary>
    private (C3dAxis Axis, long Offset) ClipOfView()
    {
        var scene = Viewer.Scene;
        double perDbu = C3dLowering.Metres(1, Document.DbuPerMicron);
        var clip = Viewer.View.Clip;
        if (clip.Enabled && clip.Axis != ClipAxis3D.View)
        {
            int a = (int)clip.Axis;
            double world = (a == 0 ? scene.Origin.X : a == 1 ? scene.Origin.Y : scene.Origin.Z) + clip.Offset;
            return ((C3dAxis)a, (long)Math.Round(world / perDbu));
        }
        return (C3dAxis.Z, (long)Math.Round(scene.Origin.Z / perDbu));
    }

    private void SelectPlotRow(string name)
    {
        if (AllTreeItems().FirstOrDefault(t => t.Kind == FieldPlotKind && t.Name == name) is { } row) SelectedTreeItem = row;
    }

    // ── what the viewer draws ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The plot as the viewer takes it: its setup's run, its solver, its reading, and — when <paramref name="resolveScene"/> —
    /// its plane in scene-local metres and its faces by the scene's face indices.
    /// </summary>
    internal FieldPlotRequest PlotRequest(C3dFieldPlot p, bool resolveScene = true)
    {
        EmSetup? run = null;
        string? problem = null;
        string setupName = p.Setup ?? ActiveSetupDisplayName ?? "";
        if (IsViewOnly) setupName = Path.GetFileNameWithoutExtension(Viewer.CemPath);
        else if (p.Setup is null)
        {
            run = ActiveRunSetup;
            if (run is null) problem = FieldPlotResolver.NoActiveSetup;
        }
        else if (ExternalSetup is { } x && p.Setup == ExternalItemName) run = x.Setup;
        else (run, problem) = FieldPlotResolver.ResolveSetup(Document, TopFilePath, p.Setup);
        var request = FieldPlotResolver.Request(p, setupName, run, problem, IsViewOnly ? null : run);
        if (!resolveScene) return request;
        return request with { Plane = ScenePlane(p), Faces = FieldPlotResolver.SceneFaces(p, SceneObject) };
    }

    /// <summary>A ClipPlane plot's plane in scene-local metres: its axis, its DBU offset less the scene's origin — kept on the
    /// side the view's clip plane keeps.</summary>
    private ClipPlane3D ScenePlane(C3dFieldPlot p)
        => FieldPlotResolver.ScenePlane(p, Document.DbuPerMicron, Viewer.Scene.Origin, Viewer.ClipFlip);

    private (string Object, int Face)? SceneFace(C3dFieldPlotFace f) => FieldPlotResolver.SceneFace(f, SceneObject);

    /// <summary>The face a gesture picked, as a plot spells it: <c>object/face</c>.</summary>
    private static string FaceKey(Scene3DObject o, int face) => $"{o.Name}/{o.FaceName(face)}";

    /// <summary>The viewer's events a plot's rows follow: the run read again, and the drawn plot's own verdict.</summary>
    private void WatchFieldPlots()
    {
        Viewer.FieldsRead += RefreshPlotFlags;
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Viewer3DViewModel.FieldPlotProblem)) RefreshPlotFlags();
        };
    }

    /// <summary>
    /// The ticked plot to the viewer (or none). Called when the plots, the scene or the active setup change — and a request the
    /// viewer already has (the same plot, plane, faces, run and origin) is not sent again, so a model edit's new scene does not
    /// rebuild the field it draws on the solved geometry.
    /// </summary>
    private void ApplyVisiblePlot()
    {
        var plot = VisibleFieldPlot;
        var request = plot is null ? null : PlotRequest(plot);
        // The section follows a ClipPlane plot's plane when the plot, or its plane, changes — so the cut is seen; moving the
        // view's clip plane afterwards leaves the plot where it is.
        if (request is { On: C3dFieldPlotOn.ClipPlane } && (plot!.Name, plot.Axis, plot.Offset) != _sectionSyncedTo)
        {
            // brief-em3d-90 — remember whether the plot turned the section on, so hiding the plot can turn it off again; and
            // record the sync only once it happened (a document opening with a plot drawn asks before there is a scene to cut)
            bool wasOpen = Viewer.ClipEnabled;
            if (SyncSectionTo(request.Plane))
            {
                _sectionSyncedTo = (plot.Name, plot.Axis, plot.Offset);
                if (!wasOpen) _sectionOpenedByPlot = true;
            }
            request = request with { Plane = ScenePlane(plot) };
        }
        // brief-em3d-90 R-em3d90-1 (the owner's "Hide all left a field plot showing") — with no ClipPlane plot drawn, a section a
        // plot opened is closed again. The field itself was gone (SetPlot(null)), but the model stayed cut open on the plot's
        // plane, which still reads as a plot. A section the user had open before the plot is left open.
        if (request is not { On: C3dFieldPlotOn.ClipPlane } && _sectionSyncedTo is not null)
        {
            _sectionSyncedTo = null;
            if (_sectionOpenedByPlot && Viewer.ClipEnabled) Viewer.ClipEnabled = false;
            _sectionOpenedByPlot = false;
        }
        string key = request is null ? "" : string.Join("|", C3dPersistence.SerializeFieldPlots([plot!]), request.Plane.Offset, request.Plane.Flip,
            string.Join(",", request.Faces), FieldPlotResolver.RunDirectories(request.RunSetup, ResultsRootProvider?.Invoke()),
            request.Solver, request.SetupProblem, Viewer.Scene.Origin);
        if (key != _appliedPlotKey)
        {
            _appliedPlotKey = key;
            Viewer.SetPlot(request);
        }
        RefreshPlotFlags();
    }

    private string? _appliedPlotKey;

    private (string, C3dAxis?, long?)? _sectionSyncedTo;
    private bool _sectionOpenedByPlot;

    private bool SyncSectionTo(ClipPlane3D plane)
    {
        var scene = Viewer.Scene;
        if (scene.Objects.Length == 0) return false;
        var (lo, hi) = plane.Range(scene.BoundsMin, scene.BoundsMax);
        Viewer.ClipAxis = plane.Axis;
        Viewer.ClipPosition = hi > lo ? Math.Clamp((plane.Offset - lo) / (hi - lo), 0, 1) : 0.5;
        Viewer.ClipEnabled = true;
        return true;
    }

    // ── missing data (R-em3d83-5) ────────────────────────────────────────────────────────────

    private readonly Dictionary<(string?, string?), FieldDiscovery> _discoveries = [];
    private readonly Dictionary<string, IReadOnlyList<FieldQuantity>> _offered = new(StringComparer.Ordinal);

    /// <summary>What the plot's setup's runs saved, read once per run (again after a run finishes).</summary>
    internal FieldDiscovery Discovered(FieldPlotRequest request)
    {
        if (IsViewOnly || request.SetupProblem is not null)
            return new FieldDiscovery([], IsViewOnly ? [.. Viewer.FieldSolutions] : [], null, [], null, IsViewOnly && Viewer.FieldsAvailable, null, null);
        var root = ResultsRootProvider?.Invoke();
        var dirs = FieldPlotResolver.RunDirectories(request.RunSetup, root);
        if (_discoveries.TryGetValue(dirs, out var d)) return d;
        return _discoveries[dirs] = FieldPlotResolver.Discover(request.RunSetup, root, Viewer.Scene.Problem);
    }

    /// <summary>A run finished, or the viewer read one: every plot's check reads the runs again.</summary>
    private void ForgetDiscoveries()
    {
        _discoveries.Clear();
        _offered.Clear();
    }

    /// <summary>The quantities <paramref name="item"/>'s files offer (FieldQuantity.Offered), read from the step's headers once.</summary>
    internal IReadOnlyList<FieldQuantity> OfferedQuantities(FieldSolutionItem item)
    {
        string key = (item.Solution.VolumePvtu ?? "") + "|" + (item.Solution.BoundaryPvtu ?? "");
        if (_offered.TryGetValue(key, out var q)) return q;
        try
        {
            var vol = item.Solution.VolumePvtu is { } v ? FieldStep.Open(v, item.Run.ToMetres) : null;
            var bnd = item.Solution.BoundaryPvtu is { } b ? FieldStep.Open(b, item.Run.ToMetres) : null;
            q = FieldQuantity.Offered(vol?.Arrays ?? [], bnd?.Arrays ?? []);
        }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException) { q = []; }
        return _offered[key] = q;
    }

    /// <summary>
    /// Why <paramref name="p"/> draws nothing, or null. The drawn plot's is the viewer's (it has read the step, so it knows the
    /// quantity too); another's is checked against what its run saved.
    /// </summary>
    public string? FieldPlotProblem(C3dFieldPlot p)
    {
        var request = PlotRequest(p, resolveScene: false);
        if (!p.Hidden && Viewer.Plot is { } drawn && drawn.Name == p.Name && Viewer.FieldPlotProblem is { } now) return now;
        var found = Discovered(request);
        if (FieldPlotResolver.PlotProblem(request, found.Items, found.Ran, Viewer.Scene.Problem) is { } why) return why;
        if (p.Faces.Count > 0 && p.On == C3dFieldPlotOn.Faces && p.Faces.FirstOrDefault(f => SceneFace(f) is null) is { } gone && Viewer.Scene.Objects.Length > 0)
            return $"The face '{gone.Face}' is no longer in the model.";
        return null;
    }

    /// <summary>Each plot row's warning glyph and tooltip, without rebuilding the tree.</summary>
    private void RefreshPlotFlags()
    {
        foreach (var row in AllTreeItems().Where(t => t.Kind == FieldPlotKind))
            if (FieldPlot(row.Name) is { } p) row.Refusal = FieldPlotProblem(p);
        Properties?.RefreshPlotProblem();
    }

    // ── the tree (R-em3d83-3) ────────────────────────────────────────────────────────────────

    /// <summary>What the row says after the name: <c>|E| · 10 GHz · clip Z</c>.</summary>
    public string DescribePlot(C3dFieldPlot p)
    {
        string q = FieldPlotResolver.QuantitySymbol(p);
        string on = p.On switch
        {
            C3dFieldPlotOn.ClipPlane => $"clip {p.Axis ?? C3dAxis.Z}",
            C3dFieldPlotOn.Surfaces => p.IsTemperature ? "all faces" : "surfaces",
            _ => $"{p.Faces.Count} face{(p.Faces.Count == 1 ? "" : "s")}",
        };
        string text = $"{q} · {p.Solution?.Describe() ?? "first saved"} · {on}";
        if (!IsViewOnly && p.Setup is { } s && s != ActiveSetupDisplayName) text += $" · {s}";
        return text;
    }

    /// <summary>The Field Plots group, after Probes (where Probes would be), with its <c>+</c>: listed whenever the document has a
    /// plot or a setup to plot, and in a setup's 3D view.</summary>
    private void RebuildFieldPlotTree()
    {
        foreach (var g in Tree.Where(g => g.Role == C3dTreeGroupRole.FieldPlots).ToList())
        {
            DetachExpansion([g]);
            Tree.Remove(g);
        }
        if (Document.FieldPlots.Count == 0 && !HasSetups && !IsViewOnly) return;
        var rows = Document.FieldPlots.Select(p => new C3dTreeItem(this, p.Name, FieldPlotKind, DescribePlot(p), -1, -1, !p.Hidden)
        {
            Refusal = FieldPlotProblem(p), Icon = Material.Icons.MaterialIconKind.Waves,
        }).ToList();
        var group = new C3dTreeGroup("Field Plots", rows, C3dTreeGroupRole.FieldPlots)
        {
            AddCommand = NewFieldPlotCommand,
            AddTip = "New Field Plot…: a field of the active setup's run, drawn, and defined in the Properties Inspector.",
            HeaderTip = IsViewOnly ? SessionPlotsTip : "What the 3D view draws of a run's fields, saved with the document. One is drawn at a time.",
        };
        int at = Tree.ToList().FindIndex(g => g.Role is C3dTreeGroupRole.MeshRegions or C3dTreeGroupRole.EffectiveBlocks or
                                                  C3dTreeGroupRole.SymmetryPlanes or C3dTreeGroupRole.ThermalBoundaries);
        if (at < 0) Tree.Add(group); else Tree.Insert(at, group);
        RestoreExpansion();
    }

    /// <summary>A plot row's menu: Show / Hide, Rename…, Duplicate, Delete, New Field Plot…, Properties.</summary>
    private List<Viewer3DMenuItem> FieldPlotMenuItems(C3dTreeItem item)
    {
        string name = item.Name;
        var plot = FieldPlot(name);
        bool shown = plot is { Hidden: false };
        return
        [
            new(name, Enabled: false), Viewer3DMenuItem.Separator,
            new(shown ? "Hide" : "Show", () => SetRowsVisible([item], !shown, $"{(shown ? "Hide" : "Show")} {name}"),
                Tip: shown ? null : "Draw this plot (the one drawn now is hidden: one at a time)."),
            new("Rename…", () => TextRequested?.Invoke($"Rename {name}", "Name:", name, text => RenameFieldPlot(name, text))),
            new("Duplicate", () => DuplicateFieldPlot(name)),
            Viewer3DMenuItem.Separator,
            new("Delete", () => DeleteFieldPlot(name)),
            Viewer3DMenuItem.Separator,
            new("New Field Plot…", NewFieldPlot),
            new("Properties", () => ShowProperties(rename: false)),
        ];
    }

    /// <summary>A group header's menu: the Field Plots group adds a plot; a material's header (by material) edits the material
    /// (brief-em3d-94).</summary>
    public IReadOnlyList<Viewer3DMenuItem> TreeGroupMenuItems(C3dTreeGroup group)
        => group.Role == C3dTreeGroupRole.FieldPlots ? [new Viewer3DMenuItem("New Field Plot…", NewFieldPlot)]
         : !IsViewOnly && EditMaterialItem(group) is { } edit ? [edit] : [];

    /// <summary>
    /// brief-em3d-90 — the plots' side of SetRowsVisible: hiding hides each named plot; showing keeps the one-at-a-time rule —
    /// the selected plot among them, else the one drawn now, else the first — so Show all draws one plot, not the last ticked.
    /// One entry (inside the gesture's group).
    /// </summary>
    private void SetPlotsVisible(IReadOnlyList<string> names, bool visible, string description)
    {
        var listed = names.Where(n => FieldPlot(n) is not null).ToList();
        if (listed.Count == 0) return;
        string pick = SelectedFieldPlot?.Name is { } sel && listed.Contains(sel) ? sel
                    : VisibleFieldPlot?.Name is { } now && listed.Contains(now) ? now : listed[0];
        ChangePlots(listed.Count == 1 ? $"{(visible ? "Show" : "Hide")} {listed[0]}" : description, plots =>
        {
            if (visible) ShowOnly(plots, plots.First(p => p.Name == pick));
            else foreach (var p in plots.Where(p => listed.Contains(p.Name))) p.Hidden = true;
        });
    }

    // ── gestures that make or extend a plot ──────────────────────────────────────────────────

    /// <summary>
    /// brief-em3d-82's right-click a face ▸ Plot Field, and Plot Temperature: the face is added to the DRAWN Faces plot of that
    /// kind — or taken off it, or moved to the other side of a sheet — or, with none drawn, a new Faces plot is made with it.
    /// </summary>
    private void PaintFace(Scene3DObject o, int face, int side, bool temperature)
    {
        var entry = new C3dFieldPlotFace
        {
            Face = FaceKey(o, face), Side = side switch { 1 => C3dFieldSide.Top, -1 => C3dFieldSide.Bottom, _ => C3dFieldSide.None },
        };
        // a temperature face joins the drawn plot only when that plot reads the active setup's run: Plot Temperature was
        // offered for the ACTIVE setup, and a face added to a plot pinned to another setup would paint that setup's result
        if (VisibleFieldPlot is { On: C3dFieldPlotOn.Faces } target && target.IsTemperature == temperature &&
            (!temperature || target.Setup is null || target.Setup == ActiveSetupDisplayName))
        {
            string name = target.Name;
            SetFieldPlot(name, $"Plot on {entry.Face}", p =>
            {
                int at = p.Faces.FindIndex(f => f.Face == entry.Face);
                if (at >= 0 && p.Faces[at].Side == entry.Side) p.Faces.RemoveAt(at);
                else if (at >= 0) p.Faces[at] = entry;
                else p.Faces.Add(entry);
            });
            return;
        }
        AddFieldPlot(temperature, p =>
        {
            p.On = C3dFieldPlotOn.Faces;
            p.Faces = [entry];
        }, select: false);
    }

    /// <summary>A thermal plot drawn on <paramref name="on"/> (every exposed face, the clip plane): the first such plot shown,
    /// or a new one; <paramref name="show"/> false hides every such plot.</summary>
    private void ShowTemperaturePlot(C3dFieldPlotOn on, bool show)
    {
        var existing = Document.FieldPlots.FirstOrDefault(p => p.IsTemperature && p.On == on);
        if (!show)
        {
            ChangePlots("Hide the temperature", plots => { foreach (var p in plots.Where(p => p.IsTemperature && p.On == on)) p.Hidden = true; });
            return;
        }
        if (existing is null) AddFieldPlot(true, p => p.On = on, select: false);
        else SetPlotShown(existing.Name, true);
    }

    /// <summary>R-em3d83-6 — "Other frequency… (needs a re-run)": <paramref name="ghz"/> added to the plot's setup's saved
    /// frequencies and the plot moved to it — ONE undo entry. The plot says it is missing until a run saves it.</summary>
    public string? PlotOtherFrequency(string name, double ghz)
    {
        if (FieldPlot(name) is not { } plot) return $"There is no field plot named '{name}'.";
        if (!(ghz > 0) || double.IsInfinity(ghz)) return "Enter a frequency in GHz, e.g. 6.";
        string? setupName = plot.Setup ?? ActiveSetupName;
        var read = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == setupName);
        if (read is not { Setup: { } setup }) return "Only a setup of this 3D view can be told to save another frequency.";
        if (!setup.Is3D || setup.IsThermal) return "Only a 3D EM setup saves fields by frequency.";
        var solver = plot.Solver ?? (setup.Solver3D == Em3dSolver.OpenEms ? Em3dSolver.OpenEms : Em3dSolver.Palace);
        var saved = SavedFrequencies(setup, solver);
        if (!saved.Any(f => Math.Abs(f - ghz) <= 1e-9 * Math.Max(f, ghz))) saved.Add(ghz);
        saved.Sort();
        if (solver == Em3dSolver.OpenEms)
        {
            var section = setup.OpenEms?.Clone() ?? new CemOpenEms();
            section.SaveFieldsGHz = saved;
            setup.OpenEms = section;
        }
        else
        {
            var section = setup.Palace?.Clone() ?? new CemPalace();
            section.SaveFieldsGHz = saved;
            setup.Palace = section;
        }
        var plotText = C3dPersistence.SerializeFieldPlots(Document.FieldPlots);
        ChangeRecords($"Save the field at {ghz.ToString("G6", CultureInfo.InvariantCulture)} GHz", d =>
        {
            d.Setups[read.Index] = EmSetupPersistence.ToEmbedded(setup);
            var p = d.FieldPlots.First(x => x.Name == name);
            p.Solution = new C3dFieldSolution { GHz = ghz, Port = p.Solution?.Port };
        });
        if (C3dPersistence.SerializeFieldPlots(Document.FieldPlots) != plotText) ApplyVisiblePlot();
        return null;
    }

    /// <summary>The Setup picker's names: this document's setups, and the external <c>.cem</c> shown with it.</summary>
    public IReadOnlyList<string> PlotSetupNames()
        => IsViewOnly ? [] : [.. C3dSetups.Read(Document).Select(s => s.Name), .. ExternalSetup is not null ? [ExternalItemName] : Array.Empty<string>()];

    /// <summary>Whether the plot's Solution picker offers "Other frequency…": its setup is one of this document's driven 3D
    /// setups, whose saved frequencies can be asked for.</summary>
    public bool PlotCanAddFrequency(C3dFieldPlot p)
        => !IsViewOnly && C3dSetups.Read(Document).FirstOrDefault(s => s.Name == (p.Setup ?? ActiveSetupName))?.Setup is
               { Is3D: true, IsThermal: false, Problem3D: CircuitRF.Engine.Em3d.Em3dProblemType.Driven };

    /// <summary>Where a plot switched to the clip plane cuts: the view's clip plane, or z through the middle.</summary>
    public (C3dAxis Axis, long Offset) PlotClipDefault() => ClipOfView();

    /// <summary>The frequencies a setup's solver saves fields at now, GHz: its list, or the sweep's centre it defaults to.</summary>
    public static List<double> SavedFrequencies(EmSetup setup, Em3dSolver solver)
    {
        var list = solver == Em3dSolver.OpenEms ? setup.OpenEms?.SaveFieldsGHz : setup.Palace?.SaveFieldsGHz;
        if (list is not null) return [.. list];
        return CircuitRF.Design.Em3d.PalaceConfigWriter.SweepCentreGHz(setup) is { } c ? [c] : [];
    }
}
