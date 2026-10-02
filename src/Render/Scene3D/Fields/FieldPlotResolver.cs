// brief-em3d-84 R-em3d84-1 — a document FIELD PLOT (brief-em3d-83) resolved against a run, with no window: which run it
// reads, which saved solution it shows (picked BY VALUE), which quantity, where, and — when it cannot be drawn — the sentence
// that says why (R-em3d83-5). The 3D view, the 3D editor and `circuitrf render --field` all resolve a plot HERE, so a plot the
// window draws is the plot the command line draws, and a plot one of them refuses the other refuses in the same words.
//
// Moved out of Viewer3DViewModel.Plot.cs and C3dEditorViewModel.FieldPlots.cs unchanged. What stayed behind is view state:
// the phase, the loop, play/pause, the selected solid, and the editor's active and external setups.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A solution in the picker: a saved frequency (driven), a mode (eigenmode), a terminal (static).</summary>
public sealed record FieldSolutionItem(FieldSolution Solution, string Label, FieldRun Run)
{
    public override string ToString() => Label;
}

/// <summary>brief-em3d-82 — a face painted with the EM field: the object's name, its face index in the scene, and — on a
/// sheet, for a volume quantity — the side shown (+1 along the sheet's own normal, −1 against it; 0 otherwise).</summary>
public readonly record struct PaintedFieldFace(string Object, int Face, int Side = 0);

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

    /// <summary>brief-em3d-100 — the plot's drive power, W (null: the solver's own), and what it is referred to.</summary>
    public double? DrivePowerW { get; init; }
    public C3dDriveReferredTo DriveReferredTo { get; init; }

    public bool IsTemperature => Quantity == C3dFieldPlot.TemperatureQuantity;

    /// <summary>brief-em3d-96 — what the requester says identifies this request (the plot, its plane, its faces, its run): the 3D
    /// view leaves a drawn plot whose key is unchanged exactly as it is. Empty: always applied.</summary>
    public string Key { get; init; } = "";
}

/// <summary>brief-em3d-83 — what a setup's runs saved, read synchronously: the editor's Solution picker and the missing-data
/// check of a plot that is not drawn.</summary>
public sealed record FieldDiscovery(IReadOnlyList<FieldRun> Runs, IReadOnlyList<FieldSolutionItem> Items, ThermalResultTable? Table,
                                    IReadOnlyList<FieldGroup> Groups, string? Why, bool Ran, string? PalaceDir, string? OpenEmsDir)
{
    /// <summary>The run directory <paramref name="item"/> was read from.</summary>
    public string? DirectoryOf(FieldSolutionItem item)
        => string.Equals(item.Run.Solver, "openEMS", StringComparison.OrdinalIgnoreCase) ? OpenEmsDir : PalaceDir;
}

public static class FieldPlotResolver
{
    /// <summary>A plot with no setup of its own, in a document with no active one (R-em3d83-5).</summary>
    public const string NoActiveSetup = "No setup is active: pick this plot's setup in the Properties Inspector.";

    /// <summary>A plot whose pinned setup the document no longer holds (R-em3d83-5).</summary>
    public static string MissingSetup(string name)
        => $"The setup '{name}' is no longer in this 3D view: pick this plot's setup in the Properties Inspector.";

    // ── the setup and the request ───────────────────────────────────────────────────────────

    /// <summary>The document's setup named <paramref name="name"/>, as its run is named (C3dSetups.ForRun), or the sentence
    /// saying it is gone.</summary>
    public static (EmSetup? Run, string? Problem) ResolveSetup(C3dDocument doc, string topPath, string name)
        => C3dSetups.Read(doc).FirstOrDefault(s => s.Name == name) is { Setup: { } found }
            ? (C3dSetups.ForRun(found, topPath), null)
            : (null, MissingSetup(name));

    /// <summary>
    /// <paramref name="p"/> as the viewer takes it, before the scene is consulted: its run's setup (<paramref name="run"/>, or
    /// why there is none), its solver when <paramref name="shape"/> runs both, and its reading.
    /// </summary>
    public static FieldPlotRequest Request(C3dFieldPlot p, string setupName, EmSetup? run, string? setupProblem, EmSetup? shape)
    {
        string? solver = shape is { Solver3D: Em3dSolver.Both } ? (p.Solver == Em3dSolver.OpenEms ? "openEMS" : "Palace") : null;
        return new FieldPlotRequest
        {
            Name = p.Name, SetupName = setupName, RunSetup = run, SetupProblem = setupProblem, Solver = solver,
            Solution = p.Solution, Quantity = p.Quantity, On = p.On, Db = p.Db, Percentile = p.Percentile, FixRange = p.FixRange,
            DrivePowerW = p.DrivePowerW, DriveReferredTo = p.DriveReferredTo,
            Mode = Enum.TryParse<FieldMode>(p.Mode, ignoreCase: true, out var m) ? m : null,
        };
    }

    /// <summary>The origin a 3D view's scene is placed at for elaboration <paramref name="e"/>: the centre of what it displays
    /// (a millimetre cube round the origin when it displays nothing). A ClipPlane plot's plane and its slice are in metres
    /// from here, so the headless picture cuts the field exactly where the window does.</summary>
    public static (double X, double Y, double Z) SceneOrigin((double X0, double Y0, double Z0, double X1, double Y1, double Z1)? extent)
    {
        var x = extent ?? EmptyExtent;
        return ((x.X0 + x.X1) / 2, (x.Y0 + x.Y1) / 2, (x.Z0 + x.Z1) / 2);
    }

    /// <summary>What an empty 3D view displays: a millimetre cube round the origin.</summary>
    public static readonly (double X0, double Y0, double Z0, double X1, double Y1, double Z1) EmptyExtent = (-5e-4, -5e-4, -5e-4, 5e-4, 5e-4, 5e-4);

    /// <summary>A ClipPlane plot's plane in scene-local metres: its axis, its DBU offset less the scene's origin — kept on the
    /// side <paramref name="flip"/> keeps.</summary>
    public static ClipPlane3D ScenePlane(C3dFieldPlot p, int dbuPerMicron, (double X, double Y, double Z) origin, bool flip = false)
    {
        int a = (int)(p.Axis ?? C3dAxis.Z);
        double world = PlaneMetres(p, dbuPerMicron);
        double o = a == 0 ? origin.X : a == 1 ? origin.Y : origin.Z;
        return new ClipPlane3D { Enabled = true, Axis = (ClipAxis3D)a, Offset = (float)(world - o), Flip = flip };
    }

    /// <summary>Where a ClipPlane plot's plane is along its axis, world metres.</summary>
    public static double PlaneMetres(C3dFieldPlot p, int dbuPerMicron) => C3dLowering.Metres(p.Offset ?? 0, dbuPerMicron);

    /// <summary>A Faces plot's faces by the scene's indices; a face <paramref name="objectNamed"/> no longer has is left out.</summary>
    public static List<PaintedFieldFace> SceneFaces(C3dFieldPlot p, Func<string, Scene3DObject?> objectNamed)
    {
        var list = new List<PaintedFieldFace>();
        foreach (var f in p.Faces)
            if (SceneFace(f, objectNamed) is { } i)
                list.Add(new PaintedFieldFace(i.Object, i.Face, f.Side switch { C3dFieldSide.Top => 1, C3dFieldSide.Bottom => -1, _ => 0 }));
        return list;
    }

    /// <summary><c>object/face</c> as the scene's object and face index, or null when the scene has no such face.</summary>
    public static (string Object, int Face)? SceneFace(C3dFieldPlotFace f, Func<string, Scene3DObject?> objectNamed)
    {
        var (obj, face) = f.Parts;
        if (objectNamed(obj) is not { } so) return null;
        for (int i = 0; i < Math.Max(so.FaceNames.Count, 1); i++)
            if (so.FaceName(i) == face) return (so.Name, i);
        return null;
    }

    /// <summary>What a plot reads, as the tree writes it: <c>|E|</c>, <c>Re{E}</c>, <c>V</c>, <c>T</c>.</summary>
    public static string QuantitySymbol(C3dFieldPlot p) => p.IsTemperature ? "T" : p.Mode switch
    {
        nameof(FieldMode.Instantaneous) => $"Re{{{p.Quantity}}}",
        nameof(FieldMode.Value) => p.Quantity,
        _ => $"|{p.Quantity}|",
    };

    // ── the solution, by value ──────────────────────────────────────────────────────────────

    /// <summary>The saved solution a plot shows: its value among the run's (of its solver), or the first when it names none.</summary>
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

    /// <summary>The plot's quantity among what the loaded step offers, or null and the sentence saying the step offers none.</summary>
    public static FieldQuantity? PickQuantity(IReadOnlyList<FieldQuantity> offered, FieldPlotRequest p, out string? problem)
    {
        var q = offered.FirstOrDefault(x => x.Array.Name == p.Quantity && (p.Mode is null || x.Mode == p.Mode));
        problem = q is not null ? null
            : $"The run no longer offers {FieldNames.Friendly(p.Quantity)}" +
              (p.Mode is { } m && offered.Any(x => x.Array.Name == p.Quantity) ? $" read as {m}" : "") + ".";
        return q;
    }

    // ── discovery ───────────────────────────────────────────────────────────────────────────

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
            run = dir is null ? null : FieldRun.OpenPalace(dir, GmshGeoWriter.LengthUnitM);
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
                    : SolutionLabel(x, modes, problem, r.Solver)) + (runs.Length > 1 ? $" ({r.Solver})" : ""), r));
        bool ran = dir is not null && Directory.Exists(dir) || openEmsDir is not null && Directory.Exists(openEmsDir);
        return new FieldDiscovery(runs, items, table, groups, why, ran, dir, openEmsDir);
    }

    /// <summary>The thermal run's table, read when the fields are.</summary>
    public static ThermalResultTable? ReadThermalTable(EmSetup setup, string root)
    {
        string npy = ThermalRunService.NpyPath(root, setup);
        return File.Exists(npy) ? ThermalResultTable.Read(npy, out _) : null;
    }

    /// <summary>
    /// A saved step as the picker and the legend label it. brief-em3d-100 R-em3d100-4 — a driven step names the port it drives
    /// and no power: the drive is the PLOT's (FieldDrive.Read), stated on the legend's own line. (Round 11's "with 1 W incident"
    /// was Palace's own unit, which is 0.5 W time-averaged in circuitRF's peak convention — PalaceDrive.)
    /// </summary>
    public static string SolutionLabel(FieldSolution s, IReadOnlyList<PalaceMode>? modes, Em3dProblem? problem, string? solver = null)
    {
        string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        int port = s.Excitation > 0 ? s.Excitation : s.Drive?.Port ?? 0;
        return s.Kind switch
        {
            FieldProblemKind.Driven => $"{G(s.Timestep)} GHz" + (port > 0 ? $", port {port} driven" : ""),
            FieldProblemKind.Eigenmode when modes?.FirstOrDefault(m => m.Index == s.Index + 1) is { } m =>
                $"Mode {s.Index + 1}: {G(m.FrequencyHz / 1e9)} GHz, Q {m.Q.ToString("G3", CultureInfo.InvariantCulture)}",
            FieldProblemKind.Eigenmode => $"Mode {s.Index + 1}",
            FieldProblemKind.Electrostatic => $"Terminal {TerminalName(s.Index, problem)} at 1 V, the others at 0 V",
            FieldProblemKind.Thermal => $"Point {s.Index + 1}",
            _ => $"Terminal {TerminalName(s.Index, problem)} carrying 1 A",
        };
    }

    /// <summary>brief-em3d-100 — request <paramref name="p"/>'s drive at <paramref name="s"/> for its own quantity
    /// (FieldDrive.Read).</summary>
    public static FieldDriveReading Drive(FieldPlotRequest p, FieldSolution s) => FieldDrive.Read(s, p.Quantity, p.DrivePowerW, p.DriveReferredTo);

    private static string TerminalName(int index, Em3dProblem? problem)
        => problem?.Terminals is { } t && index >= 0 && index < t.Count ? $"'{t[index].Name}'" : (index + 1).ToString(CultureInfo.InvariantCulture);

    // ── the legend ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The legend's lines: the plot's name (brief-em3d-83), the quantity, the range, and what was solved (R-em3d29-3c/3d). An
    /// animated quantity states its phase; <paramref name="loopSeconds"/> adds the window's "one cycle every … on screen", which
    /// a still picture has no use for. A temperature carries its own lines (brief-em3d-75 R-em3d75-4b).
    /// <para>brief-em3d-100 — <paramref name="drive"/>'s line follows the solution's (the drive a driven field is shown at), and
    /// a field with no referral reads with no unit.</para>
    /// </summary>
    public static List<string> LegendLines(string? plotName, FieldQuantity q, FieldColorScale s, string? solutionLabel,
                                           double phaseDegrees, double? loopSeconds,
                                           bool fixedAcrossSweep = false, string stepLabel = "", string hotSpot = "",
                                           FieldDriveReading? drive = null)
    {
        List<string> lines;
        if (q.IsTemperature)
        {
            lines = ["Temperature (°C)", s.Describe() + (fixedAcrossSweep ? ", fixed across the sweep" : "")];
            if (stepLabel.Length > 0) lines.Add(stepLabel);
            if (hotSpot.Length > 0) lines.Add("Hot spot: " + hotSpot);
        }
        else
        {
            string unit = drive is { Relative: true } ? "" : FieldNames.Unit(q.Array.Name);
            lines =
            [
                $"{q.Symbol}{(s.Db ? $" ({FieldColorScale.DbUnit(unit)})" : unit.Length > 0 ? $" ({unit})" : "")}",
                s.Describe(),
            ];
            if (solutionLabel is not null) lines.Add(solutionLabel);
            if (drive?.Line is { } d) lines.Add(d);
            if (q.Animated)
                lines.Add($"φ = {phaseDegrees.ToString("0", CultureInfo.InvariantCulture)}°" +
                          (loopSeconds is { } l ? $", one cycle every {l.ToString("0.##", CultureInfo.InvariantCulture)} s on screen" : ""));
        }
        if (plotName is { Length: > 0 }) lines.Insert(0, plotName);
        return lines;
    }
}
