// brief-em3d-29 — the 3D view's FIELDS: what a Palace run saved, drawn on the clip plane and on surfaces,
// coloured through a uniform, animated through a phase uniform, and read back under the cursor.
//
// The same three loops as the rest of the view (em-3d.md §8.4):
//   * reading a step and building its geometry (slice, surfaces, colour range) is KERNEL work, on the
//     thread pool, a newer request cancelling an older one — the view keeps drawing the last geometry;
//   * a colour-scale change or a PHASE STEP is INPUT work: it rewrites View.Field (a uniform block) and
//     asks for a frame. It builds and uploads nothing (gate 5);
//   * the FRAME loop draws Scene3DFieldGeometry, uploaded only when its version moves.
//
// Every value drawn or printed comes from the solver's own files (overview rule): a quantity is offered
// only when an array the step LISTS provides it (FieldQuantity.Offered), and the tooltip samples the
// field data, never the colour.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Thermal;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>The loop period the phase animation starts at, seconds — a display choice, not the frequency.</summary>
    public const double DefaultLoopSeconds = 2;

    private FieldRun? _fieldRun;
    private IReadOnlyList<FieldRun> _fieldRuns = [];
    private string? _fieldRunDir;
    private IReadOnlyList<FieldGroup> _fieldGroups = [];
    private FieldStep? _fieldVolume, _fieldBoundary;
    private FieldSolution? _fieldLoaded;
    private FieldSampler? _volumeSampler, _boundarySampler;
    private IReadOnlyList<FieldSurface> _fieldSurfaces = [];
    private long _fieldVersion;
    private CancellationTokenSource? _fieldLoadCts, _fieldCts;
    private System.Threading.Timer? _animation;
    private readonly Stopwatch _animationClock = new();
    private double _animationStartDegrees;
    private float _viewW = 800, _viewH = 500;

    /// <summary>The field's triangles, as the frame loop draws them.</summary>
    public Scene3DFieldGeometry FieldGeometry { get; private set; } = Scene3DFieldGeometry.None;

    /// <summary>The colour range of what is drawn (null while nothing is).</summary>
    public FieldColorScale? FieldScale { get; private set; }

    /// <summary>The map the drawn quantity is painted with (ColorMap3D.For — the rule `render --field` paints with too).</summary>
    public ColorMap3D FieldMap => ColorMap3D.For(SelectedFieldQuantity);

    public ObservableCollection<FieldSolutionItem> FieldSolutions { get; } = [];
    public ObservableCollection<FieldQuantity> FieldQuantities { get; } = [];
    public IReadOnlyList<double> FieldPercentiles { get; } = [95, 99, 99.9, 100];

    [ObservableProperty] private bool _fieldsAvailable;
    [ObservableProperty] private bool _showField;
    [ObservableProperty] private FieldSolutionItem? _selectedFieldSolution;
    [ObservableProperty] private FieldQuantity? _selectedFieldQuantity;
    /// <summary>A volume quantity on the clip plane (the slice).</summary>
    [ObservableProperty] private bool _fieldOnClipPlane = true;
    /// <summary>A volume quantity on the selected solid's faces; a boundary quantity (J_s) on the conductors.</summary>
    [ObservableProperty] private bool _fieldOnSurfaces = true;
    [ObservableProperty] private bool _fieldDb;
    [ObservableProperty] private double _fieldPercentile = 99;
    [ObservableProperty] private bool _fieldPlaying;
    [ObservableProperty] private double _fieldPhaseDegrees;
    [ObservableProperty] private double _fieldLoopSeconds = DefaultLoopSeconds;
    [ObservableProperty] private string _fieldText = "";

    public string FieldsTip => FieldsAvailable
        ? "Show the solved field, read from the solver's own files"
        : "Simulate with Palace to save fields (the setup's SaveFieldsGHz; the sweep's centre by default)";

    public bool FieldCanAnimate => SelectedFieldQuantity is { Animated: true };

    partial void OnFieldsAvailableChanged(bool value) => OnPropertyChanged(nameof(FieldsTip));

    partial void OnShowFieldChanged(bool value)
    {
        View.ShowField = value;
        if (value) EnsureFieldLoaded();
        if (!value) FieldPlaying = false;
        FrameRequested?.Invoke();
        OnPropertyChanged(nameof(FieldLegendVisible));
        OnPropertyChanged(nameof(ShowsTemperature));
    }

    partial void OnSelectedFieldSolutionChanged(FieldSolutionItem? value)
    {
        // brief-em3d-75 — the sweep slider follows the picker, and the picker the slider.
        if (value is not null && FieldSolutions.IndexOf(value) is var at and >= 0 && at != TemperatureStep) TemperatureStep = at;
        OnPropertyChanged(nameof(TemperatureStepLabel));
        if (_applyingPlot) return;                 // brief-em3d-83 — ApplyPlot loads it once, after every setting
        // Gate 7 — a thermal step on the same run re-reads its temperature alone.
        if (ShowField && value is not null && TryRevalueTemperature(value)) return;
        if (ShowField) EnsureFieldLoaded();
    }

    partial void OnSelectedFieldQuantityChanged(FieldQuantity? value)
    {
        OnPropertyChanged(nameof(FieldCanAnimate));
        OnPropertyChanged(nameof(FieldMap));
        OnPropertyChanged(nameof(ShowsTemperature));
        if (value is not { Animated: true }) FieldPlaying = false;
        ScheduleFieldGeometry();
    }

    partial void OnFieldOnClipPlaneChanged(bool value) => ScheduleFieldGeometry();
    partial void OnFieldOnSurfacesChanged(bool value) => ScheduleFieldGeometry();
    partial void OnFieldDbChanged(bool value) { if (!_applyingPlot) RescaleField(); }
    // brief-em3d-75 — the temperature's range is its own (D9): dB and the percentile are an EM field's.
    partial void OnFieldPercentileChanged(double value) { if (!_applyingPlot) RescaleField(); }

    partial void OnFieldPhaseDegreesChanged(double value)
    {
        WriteFieldUniforms();
        FrameRequested?.Invoke();
    }

    partial void OnFieldPlayingChanged(bool value)
    {
        _animation?.Dispose();
        _animation = null;
        if (!value) return;
        _animationStartDegrees = FieldPhaseDegrees;
        _animationClock.Restart();
        // The timer only moves a number; the frame it asks for draws what is already on the GPU.
        _animation = new System.Threading.Timer(_ => _post(AnimationTick), null, 0, 16);
    }

    private void AnimationTick()
    {
        if (!FieldPlaying || _disposed) return;
        double period = FieldLoopSeconds > 0.05 ? FieldLoopSeconds : DefaultLoopSeconds;
        FieldPhaseDegrees = (_animationStartDegrees + 360 * _animationClock.Elapsed.TotalSeconds / period) % 360;
    }

    /// <summary>The overlay's legend: shown with the field.</summary>
    public bool FieldLegendVisible => ShowField && FieldScale is not null && SelectedFieldQuantity is not null;

    /// <summary>The legend's lines: the plot's name (brief-em3d-83), the quantity, the range, and what was solved (R-em3d29-3c/3d) —
    /// FieldPlotResolver.LegendLines, the function `render --field`'s legend comes from.</summary>
    public IReadOnlyList<string> FieldLegendLines()
    {
        if (SelectedFieldQuantity is not { } q || FieldScale is not { } s) return [];
        return FieldPlotResolver.LegendLines(_plot?.Name, q, s, SelectedFieldSolution?.Label, FieldPhaseDegrees, FieldLoopSeconds,
                                             FixRangeAcrossSweep && FieldSolutions.Count > 1, TemperatureStepLabel, HotSpotLabel);
    }

    /// <summary>brief-em3d-75 — the thermal run's fields (and its table) were read again: the probe table and the menus follow.</summary>
    public event Action? ThermalResultsChanged;

    // ── discovery ───────────────────────────────────────────────────────────────────────────

    /// <summary>The run directories the fields are read from — the drawn plot's setup (brief-em3d-83), else this view's setup:
    /// Palace's for its current problem type, openEMS's — whichever the setup runs.</summary>
    private (string? Palace, string? OpenEms) FieldRunDirectories() => FieldPlotResolver.RunDirectories(FieldSetup, _resultsRoot());

    /// <summary>The setup whose run is read: the plot's, or — for a plot naming none, and with no plot — this view's own. A
    /// plot whose setup is gone reads nothing.</summary>
    private EmSetup? FieldSetup => _plot is { SetupProblem: not null } ? null : _plot?.RunSetup ?? _lastSetup;

    /// <summary>The directories the last read was asked for: a plot on the same run is applied without reading it again.</summary>
    private (string? Palace, string? OpenEms) _fieldDirs;
    private long _fieldReads;

    /// <summary>Re-reads which fields the setup's runs saved (a run may have made new ones).</summary>
    private void RefreshFields()
    {
        var dirs = _fieldDirs = FieldRunDirectories();
        string? dir = dirs.Palace;
        var scene = Scene;
        var setup = FieldSetup;
        string? root = _resultsRoot();
        long read = ++_fieldReads;
        Task.Run(() =>
        {
            var found = FieldPlotResolver.Discover(setup, root, scene.Problem);
            var (runs, table, groups, why) = (found.Runs, found.Table, found.Groups, found.Why);
            _post(() =>
            {
                if (_disposed || read != _fieldReads) return;
                FieldsRan = found.Ran;
                bool same = runs.Count > 0 && runs.Count == _fieldRuns.Count && dir == _fieldRunDir &&
                            runs.Zip(_fieldRuns).All(p => p.First.Solutions.Select(x => x.VolumePvtu).SequenceEqual(p.Second.Solutions.Select(x => x.VolumePvtu)) &&
                                                          StampOf(p.First) == StampOf(p.Second));
                if (same)
                {
                    _thermalTable = table ?? _thermalTable;
                    if (_plot is not null) ApplyPlot();
                    else if (ShowField) ScheduleFieldGeometry();
                    FieldsRead?.Invoke();
                    return;
                }
                _fieldRuns = runs;
                _fieldRun = runs.FirstOrDefault();
                _fieldFaces.Clear();                       // brief-em3d-82 — a new run: the faces were painted on another
                _fieldFacePaints = [];
                _thermalTable = table;
                _temperature = null;
                _fieldRunDir = dir;
                _fieldGroups = groups;
                _fieldVolume = _fieldBoundary = null;
                _fieldLoaded = null;
                _volumeSampler = _boundarySampler = null;
                _applyingPlot = true;
                try
                {
                    FieldSolutions.Clear();
                    foreach (var x in found.Items) FieldSolutions.Add(x);
                }
                finally { _applyingPlot = false; }
                FieldsAvailable = FieldSolutions.Count > 0;
                OnPropertyChanged(nameof(IsThermalRun));
                OnPropertyChanged(nameof(ThermalTable));
                OnPropertyChanged(nameof(TemperatureStepMax));
                OnPropertyChanged(nameof(HasTemperatureSweep));
                OnPropertyChanged(nameof(TemperatureStepLabel));
                ThermalResultsChanged?.Invoke();
                // brief-em3d-83 — a plot picks its own solution, by value; with none, the first is offered.
                if (_plot is not null)
                {
                    ApplyPlot();
                    if (why is not null && !FieldsAvailable) FieldText = "The fields could not be read: " + why;
                    FieldsRead?.Invoke();
                    return;
                }
                SelectedFieldSolution = FieldSolutions.FirstOrDefault();
                if (!FieldsAvailable)
                {
                    ShowField = false;
                    ClearFieldGeometry();
                    FieldText = why is null ? "" : "The fields could not be read: " + why;
                }
                else if (ShowField) EnsureFieldLoaded();
                FieldsRead?.Invoke();
            });
        });
    }

    /// <summary>When the run's collection was written: a re-run with the same steps is still new data.</summary>
    private static DateTime StampOf(FieldRun run)
    {
        try { return run.Solutions.Count > 0 ? File.GetLastWriteTimeUtc(run.Solutions[0].VolumePvtu!) : default; }
        catch (IOException) { return default; }
    }

    // ── loading a solution ──────────────────────────────────────────────────────────────────

    private void EnsureFieldLoaded()
    {
        if (SelectedFieldSolution is not { } item) return;
        var run = item.Run;
        if (_fieldLoaded == item.Solution && _fieldVolume is not null)
        {
            // brief-em3d-83 — the plot's quantity, by name, among what this step offers.
            if (_plot is { } p0 && !PickPlotQuantity(p0)) return;
            ScheduleFieldGeometry();
            return;
        }
        var sol = item.Solution;
        _fieldLoadCts?.Cancel();
        var cts = _fieldLoadCts = new CancellationTokenSource();
        FieldText = "Reading the field…";
        Task.Run(() =>
        {
            try
            {
                var vol = sol.VolumePvtu is { } v ? FieldStep.Open(v, run.ToMetres) : null;
                var bnd = sol.BoundaryPvtu is { } b ? FieldStep.Open(b, run.ToMetres) : null;
                cts.Token.ThrowIfCancellationRequested();
                var offered = FieldQuantity.Offered(vol?.Arrays ?? [], bnd?.Arrays ?? []);
                _post(() =>
                {
                    if (cts.IsCancellationRequested || _disposed) return;
                    // brief-em3d-82 — a solution on another mesh (the other solver's, a re-meshed run): the painted faces go.
                    // A plot's faces are the scene's, not the mesh's: they stay (brief-em3d-83).
                    if (_plot is null && _fieldVolume is { } was && vol is not null &&
                        (was.Mesh.NodeCount != vol.Mesh.NodeCount || was.Mesh.CellCount != vol.Mesh.CellCount)) _fieldFaces.Clear();
                    _fieldVolume = vol;
                    _fieldBoundary = bnd;
                    _fieldLoaded = sol;
                    _fieldRun = run;
                    _volumeSampler = _boundarySampler = null;
                    var keep = SelectedFieldQuantity;
                    _applyingPlot = true;
                    try
                    {
                        FieldQuantities.Clear();
                        foreach (var q in offered) FieldQuantities.Add(q);
                    }
                    finally { _applyingPlot = false; }
                    BuildSamplers(vol, bnd);
                    // brief-em3d-83 — a plot names its quantity; one this step does not offer draws nothing, and says so.
                    if (_plot is { } plot)
                    {
                        if (PickPlotQuantity(plot)) ScheduleFieldGeometry();
                        FieldsRead?.Invoke();
                        return;
                    }
                    // Keep the reading across solutions when the new one offers it; |E| otherwise.
                    SelectedFieldQuantity = FieldQuantities.FirstOrDefault(q => q == keep)
                        ?? FieldQuantities.FirstOrDefault(q => q.IsTemperature)
                        ?? FieldQuantities.FirstOrDefault(q => q.Array.Name == "E" && q.Mode == FieldMode.Peak)
                        ?? FieldQuantities.FirstOrDefault();
                    ScheduleFieldGeometry();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
            {
                _post(() => FieldText = "The field could not be read: " + e.Message);
            }
        });
    }

    /// <summary>The point samplers the tooltip reads with, indexed off the UI thread.</summary>
    private void BuildSamplers(FieldStep? vol, FieldStep? bnd)
    {
        Task.Run(() =>
        {
            var vs = vol is not null ? new FieldSampler(vol.Mesh) : null;
            var bs = bnd is not null ? new FieldSampler(bnd.Mesh) : null;
            _post(() =>
            {
                if (ReferenceEquals(_fieldVolume, vol)) _volumeSampler = vs;
                if (ReferenceEquals(_fieldBoundary, bnd)) _boundarySampler = bs;
            });
        });
    }

    // ── painted faces (brief-em3d-82) ───────────────────────────────────────────────────────

    private readonly List<PaintedFieldFace> _fieldFaces = [];
    private IReadOnlyList<(PaintedFieldFace Face, FieldFacePaint Paint)> _fieldFacePaints = [];

    /// <summary>The faces painted with the EM field one by one, in the order they were asked for.</summary>
    public IReadOnlyList<PaintedFieldFace> PaintedFieldFaces => _fieldFaces;

    /// <summary>
    /// R-em3d82-1 — right-click a face ▸ Plot Field: face <paramref name="face"/> of scene object <paramref name="obj"/> painted
    /// with the quantity shown (the field turned on when it was off), or taken away when it was already painted on that side.
    /// Asking for the OTHER side of a painted sheet moves it there. Several faces accumulate; they stand beside the clip plane
    /// and the selected region, never instead of them.
    /// </summary>
    public void ToggleFieldFace(string obj, int face, int side = 0)
    {
        int at = _fieldFaces.FindIndex(f => f.Object == obj && f.Face == face);
        if (at >= 0 && _fieldFaces[at].Side == side) _fieldFaces.RemoveAt(at);
        else if (at >= 0) _fieldFaces[at] = new PaintedFieldFace(obj, face, side);
        else _fieldFaces.Add(new PaintedFieldFace(obj, face, side));
        if (!ShowField && _fieldFaces.Count > 0) ShowField = true;
        else ScheduleFieldGeometry();
    }

    /// <summary>The side face <paramref name="face"/> of <paramref name="obj"/> is painted on (0 for a face with no side), or
    /// null when it is not painted.</summary>
    public int? FieldFaceSide(string obj, int face)
        => _fieldFaces.FindIndex(f => f.Object == obj && f.Face == face) is var i and >= 0 ? _fieldFaces[i].Side : null;

    /// <summary>Takes every painted face away (the clip plane and the selected region stay).</summary>
    public void ClearFieldFaces()
    {
        if (_fieldFaces.Count == 0) return;
        _fieldFaces.Clear();
        ScheduleFieldGeometry();
    }

    /// <summary>The painted faces as the painter takes them: each face's triangles and normal off the scene, its role from
    /// the object's kind, and the tolerance a mesh triangle is matched to it with (a sheet's is tight — its mesh faces lie
    /// exactly on it, and a thin tetrahedron just off it must not count).</summary>
    private List<(PaintedFieldFace Face, FieldFaceTarget Target)> FieldFaceTargets(Scene3DModel scene)
    {
        var list = new List<(PaintedFieldFace, FieldFaceTarget)>();
        foreach (var f in _fieldFaces)
        {
            if (scene.Objects.FirstOrDefault(o => o.Name == f.Object) is not { } so) continue;
            var tris = CircuitRF.Render.Scene3D.Edit.Scene3DFaces.Triangles(scene, so.Id, f.Face);
            if (tris.Count == 0) continue;
            var (_, normal) = CircuitRF.Render.Scene3D.Edit.Scene3DFaces.AreaAndNormal(scene, so.Id, f.Face);
            var role = so.Kind switch
            {
                Scene3DKind.Conductor or Scene3DKind.Via or Scene3DKind.Wire => FieldFaceRole.Conductor,
                Scene3DKind.Sheet => FieldFaceRole.Sheet,
                _ => FieldFaceRole.Solid,
            };
            double tol = role == FieldFaceRole.Sheet ? 1e-6 * (scene.BoundsMax - scene.BoundsMin).Length() : 0.01 * (so.Max - so.Min).Length();
            string label = $"'{so.Name}/{so.FaceName(f.Face)}'" + (f.Side switch { 1 => " (top side)", -1 => " (bottom side)", _ => "" });
            list.Add((f, new FieldFaceTarget(so.Name, label, role, tris, normal, tol, f.Side)));
        }
        return list;
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    private void ClearFieldGeometry()
    {
        FieldGeometry = new Scene3DFieldGeometry([], ++_fieldVersion);
        _fieldSurfaces = [];
        _temperature = null;
        HotSpot = null;
        HotSpotLabel = "";
        FieldScale = null;
        View.FieldCovered = [];
        OnPropertyChanged(nameof(FieldLegendVisible));
        FrameRequested?.Invoke();
    }

    /// <summary>Rebuilds the drawn slice and surfaces off the UI thread — on a new solution, quantity,
    /// surface choice, selection, or clip plane. A newer request cancels an older one.</summary>
    private void ScheduleFieldGeometry()
    {
        if (_applyingPlot || !ShowField || SelectedFieldQuantity is not { } q) return;
        var vol = _fieldVolume;
        var bnd = _fieldBoundary;
        var scene = Scene;
        var clip = FieldPlane;                     // brief-em3d-83 — a ClipPlane plot's own plane
        bool onPlane = FieldOnClipPlane, onSurfaces = FieldOnSurfaces, db = FieldDb;
        double pct = FieldPercentile;
        var groups = _fieldGroups;
        var selected = Scene.Object(View.Selected);
        _fieldCts?.Cancel();
        var cts = _fieldCts = new CancellationTokenSource();
        if (q.IsTemperature)
        {
            // brief-em3d-75 — the temperature's own targets, range and hot spot; the triangles carry recipes for a step.
            var faces = _temperatureFaces.ToList();
            bool all = TemperatureAllFaces, onClip = TemperatureOnClip, fix = FixRangeAcrossSweep;
            var mirrors = MirrorSymmetry ? SymmetryPlanes : [];
            var table = _thermalTable;
            int step = _fieldLoaded?.Index ?? 0;
            var steps = _fieldRun?.Solutions ?? [];
            long version = ++_geometryVersion;
            Task.Run(() =>
            {
                try
                {
                    if (vol?.Load(q.Array.Name) is not { } array) return;
                    var (parts, scale, note, covered) = BuildTemperature(q, vol, array, scene, clip, groups, faces, all, onClip, table, step,
                                                                         steps, fix, version, cts.Token, mirrors);
                    cts.Token.ThrowIfCancellationRequested();
                    _post(() =>
                    {
                        if (cts.IsCancellationRequested || _disposed) return;
                        // A newer scene arrived while this was built: build again on it (brief-em3d-83 — a plot is not
                        // re-applied on every scene, so a dropped build would leave it undrawn).
                        if (!ReferenceEquals(Scene, scene)) { ScheduleFieldGeometry(); return; }
                        FieldGeometryBuilds++;
                        AdoptTemperature(parts, q, scale, covered, note, scene);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _post(() => FieldText = "The temperature could not be drawn: " + e.Message);
                }
            });
            return;
        }
        // brief-em3d-82 — the painted faces, resolved against the scene here (cheap) and painted off the UI thread.
        var targets = FieldFaceTargets(scene);
        FieldSampler? sampler = _fieldLoaded?.Solver == "openEMS" ? _volumeSampler : null;
        bool sampled = _fieldLoaded?.Solver == "openEMS";
        Task.Run(() =>
        {
            try
            {
                var origin = scene.Origin;
                var surfaces = new List<FieldSurface>();
                var nudges = new List<Vector3>();
                var covered = new HashSet<string>(StringComparer.Ordinal);
                // A surface lies ON its face or plane; it moves a hair toward the eye's side so nothing it lies on hides it.
                float eps = 1e-4f * (scene.BoundsMax - scene.BoundsMin).Length();
                string? hint = null;
                if (!q.OnBoundary && vol?.Load(q.Array.Name) is { } array)
                {
                    if (onPlane && clip.Enabled)
                    {
                        var e = clip.Equation;
                        surfaces.Add(FieldSection.Slice(vol.Mesh, array, origin, clip, cts.Token));    // brief-em3d-84 — render --field's cut
                        // The slice lies ON the plane; the plane's own discard would eat half of it, so it
                        // moves a hair to the kept side (n·p + d ≤ 0).
                        nudges.Add(-eps * new Vector3(e.X, e.Y, e.Z));
                    }
                    if (onSurfaces && selected is { Kind: Scene3DKind.Dielectric or Scene3DKind.Air or Scene3DKind.Body } s &&
                        groups.Where(g => g.Name == s.Name && g.Dimension == 3).Select(g => g.Attribute).ToHashSet() is { Count: > 0 } region)
                    {
                        surfaces.Add(FieldSurfaces.RegionBoundary(vol.Mesh, array, region, origin, cts.Token));
                        nudges.Add(Vector3.Zero);
                        covered.Add(s.Name);
                    }
                    hint = "Turn the clip plane on, select a dielectric or the air in the tree, or right-click a face ▸ Plot Field, to show the field on it.";
                }
                else if (q.OnBoundary && bnd?.Load(q.Array.Name) is { } barray)
                {
                    var metal = groups.Where(g => g.Kind is "Conductor" or "Sheet").ToList();
                    if (onSurfaces && metal.Count > 0)
                    {
                        surfaces.Add(FieldSurfaces.Boundary(bnd.Mesh, barray, metal.Select(g => g.Attribute).ToHashSet(), origin));
                        nudges.Add(Vector3.Zero);
                        foreach (var g in metal) covered.Add(g.Name);
                    }
                    hint = metal.Count == 0 ? $"{FieldNames.Friendly(q.Array.Name)} is drawn on conductors, and this problem has none."
                                            : "Turn surfaces on to show the field on the conductors, or right-click one of their faces ▸ Plot Field.";
                }
                // brief-em3d-82 — the fourth source: each painted face, drawn a hair in front of its own face.
                var paints = new List<(PaintedFieldFace Face, FieldFacePaint Paint)>();
                var refused = new List<string>();
                if (targets.Count > 0)
                {
                    var painter = new FieldFacePainter(q, vol, bnd, groups, origin,
                        sampled && vol is not null ? sampler ?? new FieldSampler(vol.Mesh, cts.Token) : null, cts.Token);
                    foreach (var (face, target) in targets)
                    {
                        if (painter.Paint(target, out string? why) is { } paint)
                        {
                            surfaces.Add(paint.Surface);
                            nudges.Add(eps * paint.Toward);
                            paints.Add((face, paint));
                        }
                        else if (why is not null) refused.Add(why);
                    }
                }
                cts.Token.ThrowIfCancellationRequested();
                var scale = FieldColorScale.Auto(q, surfaces, db, pct);
                var packed = Scene3DFieldGeometry.Pack(q, surfaces, nudges);
                string text = surfaces.Count == 0 && hint is not null ? hint : $"{q.Label}: {surfaces.Sum(x => x.TriangleCount):N0} triangles.";
                if (refused.Count > 0) text += " " + string.Join(" ", refused);
                _post(() =>
                {
                    if (cts.IsCancellationRequested || _disposed) return;
                    if (!ReferenceEquals(Scene, scene)) { ScheduleFieldGeometry(); return; }
                    FieldGeometryBuilds++;
                    _fieldSurfaces = surfaces;
                    _fieldFacePaints = paints;
                    FieldScale = scale;
                    FieldGeometry = new Scene3DFieldGeometry(packed, ++_fieldVersion);
                    var cov = new bool[scene.Objects.Length];
                    for (int i = 0; i < cov.Length; i++) cov[i] = covered.Contains(scene.Objects[i].Name);
                    View.FieldCovered = cov;
                    FieldText = text;
                    WriteFieldUniforms();
                    OnPropertyChanged(nameof(FieldLegendVisible));
                    FrameRequested?.Invoke();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _post(() => FieldText = "The field could not be drawn: " + e.Message);
            }
        });
    }

    /// <summary>A new percentile or dB choice: a new range over the SAME triangles — uniforms only.</summary>
    private void RescaleField()
    {
        if (_applyingPlot || SelectedFieldQuantity is not { } q || _fieldSurfaces.Count == 0 || q.IsTemperature) return;
        FieldScale = FieldColorScale.Auto(q, _fieldSurfaces, FieldDb, FieldPercentile);
        WriteFieldUniforms();
        OnPropertyChanged(nameof(FieldLegendVisible));
        FrameRequested?.Invoke();
    }

    /// <summary>The field uniform block from the quantity, the range, the map and the phase.</summary>
    private void WriteFieldUniforms()
    {
        if (SelectedFieldQuantity is not { } q || FieldScale is not { } s) return;
        double phase = q.Animated ? FieldPhaseDegrees * Math.PI / 180 : 0;
        FieldUniforms.Write(View.Field, q, s, FieldMap, phase);
    }

    // ── Export picture… (R-em3d29-5) ────────────────────────────────────────────────────────

    public IReadOnlyList<int> ExportScales { get; } = [1, 2, 3, 4];
    [ObservableProperty] private int _exportScale = 2;
    [ObservableProperty] private bool _exportLegend = true;
    [ObservableProperty] private bool _exportCaption = true;
    /// <summary>What the last Copy or Export picture did, for the status line.</summary>
    [ObservableProperty] private string _pictureText = "";

    /// <summary>What Copy puts on the clipboard: the view at this multiple of the window (brief-em3d-29 follow-up).</summary>
    public const int CopyScale = 4;

    /// <summary>
    /// The view drawn by the GPU offscreen at <paramref name="scale"/> × the window's DEVICE-pixel size
    /// (1-4), reduced when needed so neither side passes <see cref="FieldPicture.MaxSide"/>, and read back;
    /// with the legend and the caption to paint over it as the export options say. The current camera; no
    /// pick pass, so no hover id reaches the picture. UI thread: the read-back holds the render lock.
    /// </summary>
    public FieldPictureShot? CapturePicture(int windowPixelsW, int windowPixelsH, int scale, out string? error)
    {
        error = null;
        float k = Math.Clamp(scale, 1, 4);
        k = Math.Min(k, FieldPicture.MaxSide / (float)Math.Max(1, Math.Max(windowPixelsW, windowPixelsH)));
        int w = Math.Clamp((int)Math.Round(windowPixelsW * k), 1, FieldPicture.MaxSide);
        int h = Math.Clamp((int)Math.Round(windowPixelsH * k), 1, FieldPicture.MaxSide);
        try
        {
            var backend = Session.EnsureBackend();
            var plan = new Scene3DFramePlan();
            float cx = View.CursorX, cy = View.CursorY;
            View.CursorX = View.CursorY = -1;
            try { plan.Plan(Scene, View, w, h, backend.FlipY, pick: false, MeshOverlay, SectionOverlay, GridOverlay, FieldGeometry); }
            finally { View.CursorX = cx; View.CursorY = cy; }
            var rgba = Session.RenderPixels(plan, Scene, MeshOverlay, SectionOverlay, GridOverlay, FieldGeometry);
            if (rgba is null) { error = "the 3D view has closed."; return null; }
            var legend = ExportLegend && FieldLegendVisible ? FieldLegendLines() : [];
            var caption = ExportCaption && ShowField && SelectedFieldSolution is { } sol ? sol.Label : null;
            return new FieldPictureShot(rgba, w, h, k, legend, legend.Count > 0 ? FieldMap : null, FieldScale, caption,
                                        ThemeServiceDark());
        }
        catch (Exception e) when (e is Viewer3DPresentFault or InvalidOperationException or OutOfMemoryException)
        {
            error = e.Message;
            return null;
        }
    }

    private static bool ThemeServiceDark() => CircuitRF.Render.ThemeService.CurrentVariant == CircuitRF.Render.ColorVariant.Dark;

    // ── the value under the cursor (R-em3d29-3e) ────────────────────────────────────────────

    /// <summary>
    /// The field's value under the cursor, from the FIELD DATA (FieldSampler: the element's own shape
    /// functions), or "" when the cursor is on no drawn field. The clip plane's slice is found by the
    /// cursor's ray, and counts when it is nearer than whatever the ID pass hit.
    /// </summary>
    internal string FieldValueUnderCursor(uint id, Vector3 point, bool hit)
    {
        if (!ShowField || SelectedFieldQuantity is not { } q || _fieldRun is not { } run || View.CursorX < 0) return "";
        if (q.IsTemperature) return TemperatureUnderCursor(id, point, hit, q, run);
        Span<double> ch = stackalloc double[6];
        double toUnits = 1 / run.ToMetres;
        bool Sample(FieldSampler? sampler, FieldStep? step, Vector3 local, double tol, Span<double> into)
        {
            if (sampler is null || step?.Load(q.Array.Name) is not { } a) return false;
            var (x, y, z) = Scene.ToWorld(local);
            return sampler.Sample(a, x * toUnits, y * toUnits, z * toUnits, into, tol);
        }
        bool found = false;
        if (!q.OnBoundary && FieldOnClipPlane && FieldPlane.Enabled)
        {
            var (o, d) = View.Camera.Ray(View.CursorX, View.CursorY, _viewW, _viewH);
            var e = FieldPlane.Equation;
            var n = new Vector3(e.X, e.Y, e.Z);
            float den = Vector3.Dot(n, d);
            if (Math.Abs(den) > 1e-12f)
            {
                float t = -(Vector3.Dot(n, o) + e.W) / den;
                float hitT = hit ? Vector3.Dot(point - o, d) : float.MaxValue;
                if (t > 0 && t <= hitT * 1.0001f) found = Sample(_volumeSampler, _fieldVolume, o + t * d, 0, ch);
            }
        }
        if (!found && hit && View.IsVisible(id) && id <= View.FieldCovered.Length && View.FieldCovered[id - 1])
        {
            double tol = 1e-3 * (Scene.BoundsMax - Scene.BoundsMin).Length() * toUnits;
            found = q.OnBoundary ? Sample(_boundarySampler, _fieldBoundary, point, tol, ch)
                                 : Sample(_volumeSampler, _fieldVolume, point, 0, ch);
        }
        // brief-em3d-82 — a painted face: read where its paint was read, on the side it shows.
        if (!found && hit && View.IsVisible(id) && LastPick.Object == id && Scene.Object(id) is { } po &&
            _fieldFacePaints.FirstOrDefault(p => p.Face.Object == po.Name && p.Face.Face == LastPick.Face).Paint is { } paint)
        {
            double diag = (Scene.BoundsMax - Scene.BoundsMin).Length();
            if (paint.OnBoundary) found = Sample(_boundarySampler, _fieldBoundary, point, 1e-3 * diag * toUnits, ch);
            else if (paint.Sampled)
                found = _volumeSampler is { } vs && _fieldVolume?.Load(q.Array.Name) is { } va &&
                        FieldFaces.SampleOffFace(vs, va, Vector3D.From(point), Vector3D.From(paint.ReadToward), Scene.Origin, ch);
            // Palace: a hair off the face into the tetrahedron on the side shown (a sheet's two sides differ).
            else found = Sample(_volumeSampler, _fieldVolume, point + (float)(1e-6 * diag) * paint.ReadToward, 0, ch);
        }
        if (!found) return "";
        double phase = q.Animated ? FieldPhaseDegrees * Math.PI / 180 : 0;
        double v = q.Evaluate(ch, phase);
        string unit = FieldNames.Unit(q.Array.Name);
        string text = $"{q.Symbol} = {v.ToString("G4", CultureInfo.InvariantCulture)}{(unit.Length > 0 ? " " + unit : "")}";
        if (q.Animated) text += $" at φ = {FieldPhaseDegrees.ToString("0", CultureInfo.InvariantCulture)}°";
        return text;
    }
}
