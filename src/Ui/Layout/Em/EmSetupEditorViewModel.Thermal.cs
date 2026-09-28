// brief-em3d-75 R-em3d75-3 — the Setups dialog's THERMAL page: a 3D view's setup whose Problem is Thermal (brief 73 D1 — only a
// .c3d embeds one). Top to bottom: Sources (each heat source's default and this setup's override), Boundaries (the list the
// face menu writes, and All exposed faces), Sweep (up to two variables), Measures (parsed as typed, each error beside it),
// Mesh, Balance, and the size estimate `explain` gives (brief 74 §6).
//
// NOTHING LIVES ONLY HERE (R-em-11): every control writes the setup's Thermal section through the panel's one CommitEdit, so
// an edit is one undo entry of the .c3d and the dialog shows exactly what the file says. The document (its heat sources,
// probes and variables) is read through ThermalContext, which the 3D editor hands the panel; a .cem panel has none and is
// never thermal.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>What the thermal page reads of the document it is embedded in: the document, its resolution and elaboration.</summary>
public sealed record EmThermalContext(Func<C3dDocument> Document, Func<C3dResolution?> Resolution, Func<C3dElaboration?> Elaboration);

/// <summary>One heat source on the page: its document default, and this setup's override (empty: the default).</summary>
public sealed partial class ThermalSourceRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Default { get; init; }
    [ObservableProperty] private string _override = "";
}

/// <summary>One boundary row: a face, its kind, and its values as typed.</summary>
public sealed partial class ThermalBoundaryRow : ObservableObject
{
    [ObservableProperty] private string _face = "";
    [ObservableProperty] private ThermalBoundaryKind _kind;
    [ObservableProperty] private string _tempC = "";
    [ObservableProperty] private string _h = "";
    [ObservableProperty] private string _ambientC = "";

    public bool IsFixed => Kind == ThermalBoundaryKind.FixedT;
    public bool IsConvection => Kind == ThermalBoundaryKind.Convection;

    partial void OnKindChanged(ThermalBoundaryKind value)
    {
        OnPropertyChanged(nameof(IsFixed));
        OnPropertyChanged(nameof(IsConvection));
    }
}

/// <summary>One sweep axis as typed.</summary>
public sealed partial class ThermalSweepRow : ObservableObject
{
    [ObservableProperty] private string _var = "";
    [ObservableProperty] private string _start = "";
    [ObservableProperty] private string _stop = "";
    [ObservableProperty] private string _points = "1";
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>R-em3d75-3 — the problems this panel offers: an embedded setup may also be Thermal (D1: a .cem may not).</summary>
    public IReadOnlyList<Em3dProblemChoice> Problem3DChoiceList => IsEmbedded
        ? [.. Problem3DChoices, new Em3dProblemChoice(Em3dProblemType.Thermal, "Thermal (temperature, circuitRF's own solver)")]
        : Problem3DChoices;

    /// <summary>The 3D view's document changed (a heat source drawn, renamed or deleted): the page reads it again.</summary>
    public void RefreshThermal() { if (IsThermalSetup) SyncThermalFields(); }

    /// <summary>Set by the 3D editor: what the thermal page reads of the document. Setting it fills the page.</summary>
    public EmThermalContext? ThermalContext
    {
        get => _thermalContext;
        set { _thermalContext = value; SyncThermalFields(); }
    }

    private EmThermalContext? _thermalContext;

    /// <summary>The thermal page is shown: an embedded setup whose problem is Thermal.</summary>
    public bool IsThermalSetup => IsEmbedded && Working.Problem3D == Em3dProblemType.Thermal;

    /// <summary>What an EM setup reads and a thermal one does not (the solver, the frequency, the conductors, the options).</summary>
    public bool IsNotThermalSetup => !IsThermalSetup;

    public ObservableCollection<ThermalSourceRow> ThermalSources { get; } = [];
    public ObservableCollection<ThermalBoundaryRow> ThermalBoundaries { get; } = [];
    public ObservableCollection<ThermalSweepRow> ThermalSweeps { get; } = [];

    public static IReadOnlyList<ThermalBoundaryKind> ThermalBoundaryKinds { get; } = [ThermalBoundaryKind.FixedT, ThermalBoundaryKind.Convection];
    public static IReadOnlyList<int> ThermalOrders { get; } = [1, 2];
    public static IReadOnlyList<string> ThermalSolverChoices { get; } = ["Auto", "Direct", "Iterative"];

    /// <summary>All exposed faces: convection at h to T_amb from every face nothing else names.</summary>
    [ObservableProperty] private bool _thermalExposed;
    [ObservableProperty] private string _thermalExposedH = "";
    [ObservableProperty] private string _thermalExposedAmbient = "";

    /// <summary>The measures, one per line (<c>Rth = (Tmax(die) - Tavg(flange)) / Pdiss</c>).</summary>
    [ObservableProperty] private string _thermalMeasuresText = "";
    /// <summary>Each measure's error, as typed — parsed live, never written until the box loses focus.</summary>
    [ObservableProperty] private string _thermalMeasureErrors = "";

    [ObservableProperty] private int _thermalOrder = ThermalLowerings.DefaultOrder;
    [ObservableProperty] private string _thermalSizeFromSources = "";
    [ObservableProperty] private string _thermalMinThroughThickness = "";
    [ObservableProperty] private string _thermalGrading = "";
    [ObservableProperty] private string _thermalSolver = "Auto";
    [ObservableProperty] private bool _thermalCheck;
    [ObservableProperty] private bool _thermalKOfT = true;
    /// <summary>brief 77 adds σ(T); shown then.</summary>
    [ObservableProperty] private bool _thermalSigmaOfT = true;
    [ObservableProperty] private string _thermalTolerance = "";
    [ObservableProperty] private string _thermalMaxIterations = "";

    /// <summary>brief-em3d-76 R-em3d76-3a — a submodel: the whole-model thermal setup it is cut from ("" for none: this setup
    /// solves the whole model) and the mesh region it is cut to.</summary>
    [ObservableProperty] private string _thermalSubmodelFrom = "";
    [ObservableProperty] private string _thermalSubmodelRegion = "";

    /// <summary>The From picker's choices: "" (no submodel), then every OTHER thermal setup of the document that is not itself a
    /// submodel.</summary>
    public IReadOnlyList<string> ThermalSubmodelFromChoices
        => ["", .. (ThermalContext?.Document() is { } d ? C3dSetups.Read(d) : [])
                   .Where(s => s.Setup is { IsThermal: true } x && x.Name != Working.Name && x.Thermal?.Submodel is null).Select(s => s.Name)];

    /// <summary>The Region picker's choices: the document's mesh regions.</summary>
    public IReadOnlyList<string> ThermalSubmodelRegionChoices
        => [.. ThermalContext?.Document().MeshRegions.Select(r => r.Name) ?? []];

    public bool IsThermalSubmodel => ThermalSubmodelFrom.Length > 0;

    /// <summary>The page's refusal of what it was just given (a bad number), or null.</summary>
    [ObservableProperty] private string? _thermalError;

    /// <summary>Everything check says of this setup (C3dThermal's one validator), or empty.</summary>
    [ObservableProperty] private string _thermalFindings = "";

    /// <summary>R-em3d75-3 — the size estimate `explain` gives, updated as sizes change.</summary>
    [ObservableProperty] private string _thermalEstimate = "";

    public bool CanAddThermalSweep => ThermalSweeps.Count < C3dThermal.MaxSweepAxes;

    public bool HasThermalSources => ThermalSources.Count > 0;

    private bool _syncingThermal;

    /// <summary>Reads the Thermal section into the page's rows (on load, an undo, a redo).</summary>
    private void SyncThermalFields()
    {
        _syncingThermal = true;
        try
        {
            var t = Working.Thermal ?? new CemThermal();
            ThermalSources.Clear();
            var doc = ThermalContext?.Document();
            foreach (var h in doc?.HeatSources ?? [])
                ThermalSources.Add(new ThermalSourceRow
                {
                    Name = h.Name, Default = h.Power ?? "(none)",
                    Override = t.Sources?.FirstOrDefault(s => s.Name == h.Name)?.Power ?? "",
                });
            ThermalBoundaries.Clear();
            ThermalExposed = false;
            ThermalExposedH = ThermalExposedAmbient = "";
            foreach (var b in t.Boundaries ?? [])
            {
                if (b.Face == C3dThermal.ExposedFaces)
                {
                    ThermalExposed = true;
                    ThermalExposedH = b.H ?? "";
                    ThermalExposedAmbient = b.AmbientC ?? "";
                    continue;
                }
                ThermalBoundaries.Add(new ThermalBoundaryRow { Face = b.Face, Kind = b.Kind, TempC = b.TempC ?? "", H = b.H ?? "", AmbientC = b.AmbientC ?? "" });
            }
            ThermalSweeps.Clear();
            foreach (var s in t.Sweep ?? [])
                ThermalSweeps.Add(new ThermalSweepRow { Var = s.Var, Start = s.Start, Stop = s.Stop, Points = s.Points.ToString(CultureInfo.InvariantCulture) });
            ThermalMeasuresText = string.Join("\n", t.Measures ?? []);
            var m = t.Mesh;
            ThermalOrder = m?.Order ?? ThermalLowerings.DefaultOrder;
            ThermalSizeFromSources = G(m?.SizeFromSources);
            ThermalMinThroughThickness = m?.MinThroughThickness?.ToString(CultureInfo.InvariantCulture) ?? "";
            ThermalGrading = G(m?.Grading);
            ThermalSolver = m?.Solver?.ToString() ?? "Auto";
            ThermalCheck = m?.Check ?? false;
            ThermalKOfT = t.Balance?.KOfT ?? true;
            ThermalSigmaOfT = t.Balance?.SigmaOfT ?? true;
            ThermalTolerance = G(t.Balance?.Tolerance);
            ThermalMaxIterations = t.Balance?.MaxIterations?.ToString(CultureInfo.InvariantCulture) ?? "";
            ThermalSubmodelFrom = t.Submodel?.From ?? "";
            ThermalSubmodelRegion = t.Submodel?.Region ?? "";
            ThermalError = null;
        }
        finally { _syncingThermal = false; }
        OnPropertyChanged(nameof(CanAddThermalSweep));
        OnPropertyChanged(nameof(HasThermalSources));
        OnPropertyChanged(nameof(ThermalSubmodelFromChoices));
        OnPropertyChanged(nameof(ThermalSubmodelRegionChoices));
        OnPropertyChanged(nameof(IsThermalSubmodel));
        RefreshThermalChecks();
    }

    private void RaiseThermalVisibility()
    {
        OnPropertyChanged(nameof(IsThermalSetup));
        OnPropertyChanged(nameof(IsNotThermalSetup));
        OnPropertyChanged(nameof(Is3DSetup));
        RaiseSolverKindVisibility();
    }

    /// <summary>
    /// Writes the page as it now reads into the Thermal section: one undoable edit, or none when nothing changed. A number
    /// that does not parse stays in its box beside the message, and nothing is written.
    /// </summary>
    public void CommitThermal()
    {
        if (_syncingThermal || !IsThermalSetup) return;
        ThermalError = null;
        var before = SnapshotJson();
        var t = Working.Thermal ?? new CemThermal();

        t.Sources = [.. ThermalSources.Where(r => r.Override.Trim().Length > 0).Select(r => new CemThermalSource { Name = r.Name, Power = r.Override.Trim() })];
        if (t.Sources.Count == 0) t.Sources = null;

        var boundaries = ThermalBoundaries.Where(r => r.Face.Trim().Length > 0).Select(r => new CemThermalBoundary
        {
            Face = r.Face.Trim(), Kind = r.Kind,
            TempC = r.Kind == ThermalBoundaryKind.FixedT ? Blank(r.TempC) : null,
            H = r.Kind == ThermalBoundaryKind.Convection ? Blank(r.H) : null,
            AmbientC = r.Kind == ThermalBoundaryKind.Convection ? Blank(r.AmbientC) : null,
        }).ToList();
        if (ThermalExposed)
            boundaries.Add(new CemThermalBoundary
            {
                Face = C3dThermal.ExposedFaces, Kind = ThermalBoundaryKind.Convection, H = Blank(ThermalExposedH), AmbientC = Blank(ThermalExposedAmbient),
            });
        t.Boundaries = boundaries.Count > 0 ? boundaries : null;

        var sweeps = new List<CemThermalSweep>();
        foreach (var r in ThermalSweeps)
        {
            if (r.Var.Trim().Length == 0) continue;
            if (!int.TryParse(r.Points.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                ThermalError = $"The sweep of '{r.Var}' needs a whole number of points, at least 1.";
                return;
            }
            sweeps.Add(new CemThermalSweep { Var = r.Var.Trim(), Start = r.Start.Trim(), Stop = r.Stop.Trim(), Points = n });
        }
        t.Sweep = sweeps.Count > 0 ? sweeps : null;

        var measures = ThermalMeasuresText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        t.Measures = measures.Count > 0 ? measures : null;

        double? D(string text, string what, bool positive)
        {
            text = text.Trim();
            if (text.Length == 0 || ThermalError is not null) return null;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && (!positive || v > 0)) return v;
            ThermalError = $"{what} is {(positive ? "a positive number" : "a number")}; '{text}' is not.";
            return null;
        }
        int? I(string text, string what)
        {
            text = text.Trim();
            if (text.Length == 0 || ThermalError is not null) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 1) return v;
            ThermalError = $"{what} is a whole number, at least 1; '{text}' is not.";
            return null;
        }
        var mesh = new CemThermalMesh
        {
            Order = ThermalOrder == ThermalLowerings.DefaultOrder ? null : ThermalOrder,
            SizeFromSources = D(ThermalSizeFromSources, "Elements across a source", true),
            MinThroughThickness = I(ThermalMinThroughThickness, "Elements through the thinnest solid"),
            Grading = D(ThermalGrading, "The grading", true),
            Solver = ThermalSolver switch { "Direct" => ThermalMeshSolver.Direct, "Iterative" => ThermalMeshSolver.Iterative, _ => null },
            Check = ThermalCheck ? true : null,
        };
        var balance = new CemThermalBalance
        {
            KOfT = ThermalKOfT ? null : false,
            SigmaOfT = ThermalSigmaOfT ? null : false,
            Tolerance = D(ThermalTolerance, "The balance's tolerance", true),
            MaxIterations = I(ThermalMaxIterations, "The most Newton steps"),
        };
        if (ThermalError is not null) return;
        t.Mesh = mesh is { Order: null, SizeFromSources: null, MinThroughThickness: null, Grading: null, Solver: null, Check: null } ? null : mesh;
        t.Balance = balance is { KOfT: null, SigmaOfT: null, Tolerance: null, MaxIterations: null } ? null : balance;
        t.Submodel = ThermalSubmodelFrom.Trim().Length > 0
            ? new CemThermalSubmodel { From = ThermalSubmodelFrom.Trim(), Region = ThermalSubmodelRegion.Trim() }
            : null;
        Working.Thermal = t;
        CommitEdit(before, "Change the thermal setup");
        RefreshThermalChecks();
    }

    private static string? Blank(string s) => s.Trim() is { Length: > 0 } t ? t : null;

    [RelayCommand]
    private void AddThermalBoundary()
    {
        ThermalBoundaries.Add(new ThermalBoundaryRow { Kind = ThermalBoundaryKind.FixedT, TempC = "25" });
    }

    [RelayCommand]
    private void RemoveThermalBoundary(ThermalBoundaryRow? row)
    {
        if (row is null || !ThermalBoundaries.Remove(row)) return;
        CommitThermal();
    }

    [RelayCommand]
    private void AddThermalSweep()
    {
        if (!CanAddThermalSweep) return;
        ThermalSweeps.Add(new ThermalSweepRow());
        OnPropertyChanged(nameof(CanAddThermalSweep));
    }

    [RelayCommand]
    private void RemoveThermalSweep(ThermalSweepRow? row)
    {
        if (row is null || !ThermalSweeps.Remove(row)) return;
        OnPropertyChanged(nameof(CanAddThermalSweep));
        CommitThermal();
    }

    // Switches and pickers commit at once; text commits on Enter or lost focus (the view calls CommitThermal).
    partial void OnThermalExposedChanged(bool value) => CommitThermal();
    partial void OnThermalOrderChanged(int value) => CommitThermal();
    partial void OnThermalSolverChanged(string value) => CommitThermal();
    partial void OnThermalCheckChanged(bool value) => CommitThermal();
    partial void OnThermalKOfTChanged(bool value) => CommitThermal();
    partial void OnThermalSigmaOfTChanged(bool value) => CommitThermal();
    partial void OnThermalSubmodelFromChanged(string value) { OnPropertyChanged(nameof(IsThermalSubmodel)); CommitThermal(); }
    partial void OnThermalSubmodelRegionChanged(string value) => CommitThermal();

    /// <summary>R-em3d75-3 — the measures, parsed as they are typed: each error beside the list, nothing written.</summary>
    partial void OnThermalMeasuresTextChanged(string value)
    {
        if (ThermalContext is not { } ctx || ctx.Resolution() is not { } res) { ThermalMeasureErrors = ""; return; }
        var doc = ctx.Document();
        var errors = new List<string>();
        foreach (string line in value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            if (C3dThermal.MeasureProblem(line, doc, res) is { } why) errors.Add($"{line}: {why}");
        ThermalMeasureErrors = string.Join("\n", errors);
    }

    /// <summary>What check says of this setup, and the size estimate `explain` gives — recomputed after each edit.</summary>
    private void RefreshThermalChecks()
    {
        if (!IsThermalSetup || ThermalContext is not { } ctx) { ThermalFindings = ""; ThermalEstimate = ""; return; }
        var doc = ctx.Document();
        var e = ctx.Elaboration();
        var res = ctx.Resolution();
        if (res is not null)
            ThermalFindings = string.Join("\n", C3dThermal.Setup(Working.Name, Working, doc, e, res).Select(d => d.Render()));
        ThermalEstimate = Estimate(doc, e, Working);
        OnThermalMeasuresTextChanged(ThermalMeasuresText);
    }

    /// <summary>R-em3d75-3 — brief 74 §6's estimate, as `explain` states it; or why there is none yet.</summary>
    public static string Estimate(C3dDocument doc, C3dElaboration? e, EmSetup setup)
    {
        if (e is not { Ok: true }) return "No estimate: the 3D view does not elaborate yet.";
        var t = setup.Thermal ?? new CemThermal();
        try
        {
            if (ThermalLowerings.Build(doc, e, t, 1, out string? why) is not { } lowering) return "No estimate: " + why;
            var solver = t.Mesh?.Solver switch { ThermalMeshSolver.Direct => ThermalSolverKind.Direct, ThermalMeshSolver.Iterative => ThermalSolverKind.Iterative, _ => ThermalSolverKind.Auto };
            var est = ThermalSizeEstimate.Of(lowering.Input, new ThermalSolveOptions { Solver = solver });
            return $"Estimate: ≈ {est.Elements:N0} elements, ≈ {est.Unknowns:N0} unknowns, the {est.Solver.ToString().ToLowerInvariant()} solver, " +
                   $"≈ {est.MemoryBytes / 1e6:N0} MB. An estimate before meshing: each solid at its own element size.";
        }
        catch (Exception x) when (x is InvalidOperationException or ArgumentException) { return "No estimate: " + x.Message; }
    }
}
