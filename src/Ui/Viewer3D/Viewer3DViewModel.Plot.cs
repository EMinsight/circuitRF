// brief-em3d-83 — the viewer draws ONE document field plot. The editor hands it the visible plot, resolved against the scene
// (FieldPlotRequest), and the viewer sets its own field state from it: which run it reads, which saved solution — picked BY
// VALUE, never by the combobox's index — which quantity, where, and the colour scale. The phase, the loop and play/pause stay
// the viewer's: an animation is not a design decision.
//
// A plot whose data is missing draws NOTHING and says why (FieldPlotProblem): no run of its setup, a run that no longer holds
// its solution, a step that no longer offers its quantity. It is never re-pointed to the nearest frequency (R-em3d83-5).
//
// With no plot (null) the viewer is what brief 29 made it: the ACTIVE setup's run, read and offered, nothing drawn.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>brief-em3d-83 — the field plot the viewer draws, as the editor resolved it against the current scene.</summary>
public sealed record FieldPlotRequest
{
    /// <summary>The plot's name: the legend's first line.</summary>
    public string Name { get; init; } = "";

    /// <summary>The setup it reads, as the user names it (the sentences say this).</summary>
    public string SetupName { get; init; } = "";

    /// <summary>The setup as its run is named (C3dSetups.ForRun) — where the fields are read from. Null: the view's own setup
    /// (a setup's 3D view, which has no other).</summary>
    public EmSetup? RunSetup { get; init; }

    /// <summary>Why there is no <see cref="RunSetup"/> (the setup was deleted or renamed), or null.</summary>
    public string? SetupProblem { get; init; }

    /// <summary>"Palace" or "openEMS" for a setup that runs both; null otherwise.</summary>
    public string? Solver { get; init; }

    public C3dFieldSolution? Solution { get; init; }
    public string Quantity { get; init; } = "E";
    public FieldMode? Mode { get; init; }
    public C3dFieldPlotOn On { get; init; }

    /// <summary>A ClipPlane plot's plane, scene-local metres; always enabled.</summary>
    public ClipPlane3D Plane { get; init; }

    /// <summary>A Faces plot's faces, resolved to the scene's face indices.</summary>
    public IReadOnlyList<PaintedFieldFace> Faces { get; init; } = [];

    public bool Db { get; init; }
    public double Percentile { get; init; } = 99;
    public bool FixRange { get; init; }

    public bool IsTemperature => Quantity == C3dFieldPlot.TemperatureQuantity;
}

/// <summary>brief-em3d-83 — what a setup's runs saved, read synchronously: the editor's Solution picker and the missing-data
/// check of a plot that is not drawn.</summary>
public sealed record FieldDiscovery(IReadOnlyList<FieldRun> Runs, IReadOnlyList<FieldSolutionItem> Items, ThermalResultTable? Table,
                                    IReadOnlyList<FieldGroup> Groups, string? Why, bool Ran, string? PalaceDir, string? OpenEmsDir);

public sealed partial class Viewer3DViewModel
{
    private FieldPlotRequest? _plot;
    private bool _applyingPlot;

    /// <summary>The plot drawn, or null.</summary>
    public FieldPlotRequest? Plot => _plot;

    /// <summary>R-em3d83-5 — why the plot draws nothing (no run, a solution or quantity the run no longer holds), or null.</summary>
    [ObservableProperty] private string? _fieldPlotProblem;

    /// <summary>The run's fields were read again (a new run, another setup): the editor re-checks its plots.</summary>
    public event Action? FieldsRead;

    /// <summary>
    /// Draws <paramref name="plot"/> (null draws nothing). A plot on another setup's run reads that run first; the same run
    /// re-picks its solution and quantity by value and rebuilds what is drawn.
    /// </summary>
    public void SetPlot(FieldPlotRequest? plot)
    {
        _plot = plot;
        if (plot is null)
        {
            FieldPlotProblem = null;
            _applyingPlot = true;
            try
            {
                _fieldFaces.Clear();
                _temperatureFaces.Clear();
                TemperatureAllFaces = false;
                TemperatureOnClip = false;
                ShowField = false;
            }
            finally { _applyingPlot = false; }
            ClearFieldGeometry();
            FieldText = "";
        }
        if (FieldRunDirectories() != _fieldDirs) { RefreshFields(); return; }   // ApplyPlot runs when the run has been read
        if (plot is not null) ApplyPlot();
    }

    /// <summary>The plot's settings into the field state, its solution picked by value, then one rebuild.</summary>
    private void ApplyPlot()
    {
        if (_plot is not { } p) return;
        FieldSolutionItem? item;
        bool moved;
        _applyingPlot = true;
        try
        {
            FieldOnClipPlane = p.On == C3dFieldPlotOn.ClipPlane;
            FieldOnSurfaces = p.On == C3dFieldPlotOn.Surfaces;
            FieldDb = p.Db;
            FieldPercentile = p.Percentile;
            FixRangeAcrossSweep = p.FixRange;
            _fieldFaces.Clear();
            _temperatureFaces.Clear();
            if (p.On == C3dFieldPlotOn.Faces)
            {
                if (p.IsTemperature) _temperatureFaces.AddRange(p.Faces.Select(f => new TemperatureFace(f.Object, f.Face)));
                else _fieldFaces.AddRange(p.Faces);
            }
            TemperatureAllFaces = p.IsTemperature && p.On == C3dFieldPlotOn.Surfaces;
            TemperatureOnClip = p.IsTemperature && p.On == C3dFieldPlotOn.ClipPlane;
            item = PickSolution(p);
            moved = !ReferenceEquals(SelectedFieldSolution, item);
            if (moved) SelectedFieldSolution = item;
            ShowField = item is not null;
        }
        finally { _applyingPlot = false; }
        FieldPlotProblem = item is null ? PlotProblem(p, [.. FieldSolutions], FieldsRan, Scene.Problem) : null;
        if (item is null)
        {
            ClearFieldGeometry();
            FieldText = FieldPlotProblem ?? "";
            return;
        }
        // A sweep step on the same thermal run re-reads its temperature alone (brief 75 gate 7).
        if (moved && p.IsTemperature && TryRevalueTemperature(item)) return;
        EnsureFieldLoaded();
    }

    /// <summary>The plot's quantity among what the loaded step offers; false (and the reason) when it offers none.</summary>
    private bool PickPlotQuantity(FieldPlotRequest p)
    {
        var q = FieldQuantities.FirstOrDefault(x => x.Array.Name == p.Quantity && (p.Mode is null || x.Mode == p.Mode));
        _applyingPlot = true;
        try { SelectedFieldQuantity = q; }
        finally { _applyingPlot = false; }
        if (q is not null) return true;
        FieldPlotProblem = $"The run no longer offers {FieldNames.Friendly(p.Quantity)}" +
                           (p.Mode is { } m && FieldQuantities.Any(x => x.Array.Name == p.Quantity) ? $" read as {m}" : "") + ".";
        ClearFieldGeometry();
        FieldText = FieldPlotProblem;
        return false;
    }

    /// <summary>The saved solution a plot shows: its value among the run's (of its solver), or the first when it names none.</summary>
    private FieldSolutionItem? PickSolution(FieldPlotRequest p) => PickSolution(p.Solution, p.Solver, FieldSolutions, Scene.Problem);

    public static FieldSolutionItem? PickSolution(C3dFieldSolution? wanted, string? solver, IEnumerable<FieldSolutionItem> items, Em3dProblem? problem)
    {
        var mine = items.Where(i => solver is null || string.Equals(i.Run.Solver, solver, StringComparison.OrdinalIgnoreCase));
        return wanted is null ? mine.FirstOrDefault() : mine.FirstOrDefault(i => SolutionKey(i.Solution, problem).SameAs(wanted));
    }

    /// <summary>A saved step as a plot names it, by value.</summary>
    public static C3dFieldSolution SolutionKey(FieldSolution s, Em3dProblem? problem) => s.Kind switch
    {
        FieldProblemKind.Driven => new C3dFieldSolution { GHz = s.Timestep, Port = s.Excitation > 0 ? s.Excitation : null },
        FieldProblemKind.Eigenmode => new C3dFieldSolution { Mode = s.Index + 1 },
        FieldProblemKind.Thermal => new C3dFieldSolution { Point = s.Index + 1 },
        _ => new C3dFieldSolution
        {
            Terminal = problem?.Terminals is { } t && s.Index >= 0 && s.Index < t.Count ? t[s.Index].Name : (s.Index + 1).ToString(CultureInfo.InvariantCulture),
        },
    };

    /// <summary>
    /// R-em3d83-5 — the sentence a plot whose run lacks its solution carries: no run yet, or what the run DID save beside what
    /// the plot asks for. Null when <paramref name="saved"/> holds it.
    /// </summary>
    public static string? PlotProblem(FieldPlotRequest p, IReadOnlyList<FieldSolutionItem> saved, bool ran, Em3dProblem? problem)
    {
        if (p.SetupProblem is { } why) return why;
        var mine = saved.Where(i => p.Solver is null || string.Equals(i.Run.Solver, p.Solver, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0)
            return ran ? $"The run of setup {p.SetupName} saved no fields — set Save fields at in its setup and Simulate again."
                       : $"No run of setup {p.SetupName} yet — Simulate to draw this plot.";
        if (PickSolution(p.Solution, p.Solver, mine, problem) is not null) return null;
        var w = p.Solution!;
        string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        var keys = mine.Select(i => SolutionKey(i.Solution, problem)).ToList();
        if (w.GHz is { } f)
        {
            var at = keys.Where(k => k.GHz is not null).Select(k => k.GHz!.Value).Distinct().OrderBy(x => x).Select(G);
            if (keys.Any(k => k.GHz is { } g && Math.Abs(g - f) <= 1e-9 * Math.Max(g, f)))
                return $"The run saved {G(f)} GHz, but not for port {w.Port}. Pick a saved port.";
            return $"The run saved {string.Join(", ", at)} GHz; this plot shows {G(f)} GHz. Pick a saved frequency, or add {G(f)} to " +
                   "Save fields at and run again.";
        }
        if (w.Mode is { } m)
            return $"The run saved modes {string.Join(", ", keys.Select(k => k.Mode).OfType<int>())}; this plot shows mode {m}. " +
                   "Pick a saved mode, or ask for more modes and run again.";
        if (w.Terminal is { } t)
            return $"The run saved terminals {string.Join(", ", keys.Select(k => $"'{k.Terminal}'"))}; this plot shows terminal '{t}'. Pick a saved terminal.";
        if (w.Point is { } k0)
            return $"The run has {keys.Count} sweep point{(keys.Count == 1 ? "" : "s")}; this plot shows point {k0}. Pick a saved point.";
        return null;
    }

    /// <summary>The plane a ClipPlane plot cuts on, or the view's own clip plane with no plot.</summary>
    private ClipPlane3D FieldPlane => _plot is { On: C3dFieldPlotOn.ClipPlane } p ? p.Plane : View.Clip;

    /// <summary>A run directory of the setup read exists: it ran, whether or not it saved fields.</summary>
    private bool FieldsRan { get; set; }

    // ── discovery, shared by the viewer and the editor ──────────────────────────────────────

    /// <summary>The run directories of <paramref name="setup"/>'s 3D solvers under <paramref name="root"/>: Palace's (or a
    /// thermal run's own), openEMS's — whichever it runs.</summary>
    public static (string? Palace, string? OpenEms) RunDirectories(EmSetup? setup, string? root)
    {
        if (setup is null || root is null) return (null, null);
        if (setup.IsThermal) return (ThermalRunService.RunDirectory(root, setup), null);
        return (setup.Solver3D is Em3dSolver.Palace or Em3dSolver.Both ? Em3dRunService.RunDirectory(root, setup, Em3dSolver.Palace) : null,
                setup.Solver3D is Em3dSolver.OpenEms or Em3dSolver.Both ? Em3dRunService.RunDirectory(root, setup, Em3dSolver.OpenEms) : null);
    }

    /// <summary>What <paramref name="setup"/>'s runs saved, each step labelled as the picker shows it. File reads: call it off the
    /// UI thread when it can be large, or cache it.</summary>
    public static FieldDiscovery Discover(EmSetup? setup, string? root, Em3dProblem? problem)
    {
        var (dir, openEmsDir) = RunDirectories(setup, root);
        FieldRun? run = null, openEms = null;
        string? why = null;
        var table = setup is { IsThermal: true } && root is not null ? ReadThermalTable(setup, root) : null;
        try
        {
            run = dir is null ? null : FieldRun.OpenPalace(dir, MeshToMetres);
            openEms = openEmsDir is null ? null : FieldRun.OpenOpenEms(openEmsDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { why = e.Message; }
        var groups = dir is null ? [] : FieldGroups.Read(dir);
        var modes = run?.Kind == FieldProblemKind.Eigenmode
            ? PalaceRun.ReadModes(Path.Combine(dir!, PalaceConfigWriter.OutputDirectory, PalaceRun.EigFile), out _) : null;
        FieldRun[] runs = [.. new[] { run, openEms }.OfType<FieldRun>()];
        var items = new List<FieldSolutionItem>();
        foreach (var r in runs)
            foreach (var x in r.Solutions)
                items.Add(new FieldSolutionItem(x, (x.Kind == FieldProblemKind.Thermal
                    ? table is { } tt && x.Index < tt.Points ? tt.PointLabel(x.Index) : $"Point {x.Index + 1}"
                    : SolutionLabel(x, modes, problem)) + (runs.Length > 1 ? $" ({r.Solver})" : ""), r));
        bool ran = dir is not null && Directory.Exists(dir) || openEmsDir is not null && Directory.Exists(openEmsDir);
        return new FieldDiscovery(runs, items, table, groups, why, ran, dir, openEmsDir);
    }
}
