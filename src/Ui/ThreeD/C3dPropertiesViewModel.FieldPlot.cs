// brief-em3d-83 R-em3d83-4 — a field plot in the Properties Inspector: every field of the record, each commit one undo entry.
// The Solution picker lists what the plot's run ACTUALLY saved — the owner's frequency picker — by value, and ends with
// "Other frequency… (needs a re-run)", which asks the setup to save one more (R-em3d83-6). The Quantity picker lists only
// what the step's files hold (FieldQuantity.Offered). When the data is missing the reason is shown above the fields. The
// phase, Play and the loop period are view state: they sit at the bottom, for the plot being drawn, and are never saved.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One row of a plot's Solution picker: a saved step (by value), or the "Other frequency…" row.</summary>
public sealed record C3dPlotSolutionChoice(string Label, C3dFieldSolution? Solution, bool IsOther = false)
{
    public override string ToString() => Label;
}

/// <summary>One row of a plot's Quantity picker: the array and how it is read.</summary>
public sealed record C3dPlotQuantityChoice(string Label, string Quantity, string? Mode)
{
    public override string ToString() => Label;
}

public sealed partial class C3dPropertiesViewModel
{
    /// <summary>The Setup picker's first row: the setup active when the plot is drawn.</summary>
    public const string ActiveSetupChoice = "Active";

    public const string OtherFrequencyLabel = "Other frequency… (needs a re-run)";

    public static IReadOnlyList<C3dAxis> PlotAxes { get; } = [C3dAxis.X, C3dAxis.Y, C3dAxis.Z];
    public static IReadOnlyList<double> PlotPercentiles { get; } = [95, 99, 99.9, 100];
    public static IReadOnlyList<string> PlotSolvers { get; } = ["Palace", "openEMS"];

    /// <summary>A field plot's row is selected: its fields are shown.</summary>
    [ObservableProperty] private bool _isFieldPlot;
    private string _plotName = "";

    public ObservableCollection<string> PlotSetups { get; } = [];
    [ObservableProperty] private string? _plotSetup;
    [ObservableProperty] private bool _plotHasSolver;
    [ObservableProperty] private string? _plotSolver;
    public ObservableCollection<C3dPlotSolutionChoice> PlotSolutions { get; } = [];
    [ObservableProperty] private C3dPlotSolutionChoice? _plotSolution;
    [ObservableProperty] private bool _plotOtherOpen;
    [ObservableProperty] private string _plotOtherText = "";
    public ObservableCollection<C3dPlotQuantityChoice> PlotQuantities { get; } = [];
    [ObservableProperty] private C3dPlotQuantityChoice? _plotQuantity;
    public ObservableCollection<string> PlotTargets { get; } = [];
    [ObservableProperty] private string? _plotTarget;
    [ObservableProperty] private bool _plotIsClip;
    [ObservableProperty] private C3dAxis _plotAxis = C3dAxis.Z;
    [ObservableProperty] private string _plotOffsetText = "";
    [ObservableProperty] private bool _plotIsFaces;
    [ObservableProperty] private string _plotFacesText = "";
    [ObservableProperty] private bool _plotIsTemperature;
    [ObservableProperty] private bool _plotDb;
    [ObservableProperty] private double _plotPercentile = 99;
    [ObservableProperty] private bool _plotFixRange;
    [ObservableProperty] private bool _plotHidden;
    /// <summary>R-em3d83-5 — why the plot draws nothing, above its fields; null when it draws.</summary>
    [ObservableProperty] private string? _plotProblem;
    /// <summary>This plot is the one drawn: the phase controls apply to it.</summary>
    [ObservableProperty] private bool _plotIsDrawn;

    /// <summary>The view the phase, Play and loop period are the state of.</summary>
    public Viewer3DViewModel Viewer => Editor.Viewer;

    private const string OnClip = "Clip plane", OnFaces = "Faces";
    private string OnSurfaces => PlotIsTemperature ? "All faces" : "Surfaces";

    private void ClearFieldPlot()
    {
        IsFieldPlot = false;
        PlotOtherOpen = false;
        PlotProblem = null;
    }

    private void LoadFieldPlot(string name)
    {
        var editor = Editor;
        if (editor.FieldPlot(name) is not { } p) { Heading = "Nothing selected"; return; }
        _plotName = name;
        IsFieldPlot = true;
        NameText = name;
        Heading = $"Field plot {name}";
        PlotIsTemperature = p.IsTemperature;
        PlotHidden = p.Hidden;
        PlotIsDrawn = !p.Hidden;

        PlotSetups.Clear();
        PlotSetups.Add(ActiveSetupChoice);
        foreach (string s in editor.PlotSetupNames()) PlotSetups.Add(s);
        if (p.Setup is { } named && !PlotSetups.Contains(named)) PlotSetups.Add(named);
        PlotSetup = p.Setup ?? ActiveSetupChoice;

        var request = editor.PlotRequest(p, resolveScene: false);
        PlotHasSolver = request.Solver is not null;
        PlotSolver = request.Solver;

        // The Solution picker: what the run saved, by value — and the plot's own value when the run lacks it, so the picker
        // never shows another step as if it were chosen.
        PlotSolutions.Clear();
        var found = editor.Discovered(request);
        var mine = found.Items.Where(i => request.Solver is null || string.Equals(i.Run.Solver, request.Solver, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var i in mine) PlotSolutions.Add(new C3dPlotSolutionChoice(i.Label, Viewer3DViewModel.SolutionKey(i.Solution, editor.Viewer.Scene.Problem)));
        C3dPlotSolutionChoice? chosen = p.Solution is null ? PlotSolutions.FirstOrDefault() : PlotSolutions.FirstOrDefault(c => c.Solution!.SameAs(p.Solution));
        if (chosen is null && p.Solution is { } missing)
        {
            chosen = new C3dPlotSolutionChoice($"{missing.Describe()} (not saved)", missing);
            PlotSolutions.Add(chosen);
        }
        if (editor.PlotCanAddFrequency(p)) PlotSolutions.Add(new C3dPlotSolutionChoice(OtherFrequencyLabel, null, IsOther: true));
        PlotSolution = chosen;

        // The Quantity picker: only what the step offers (the plot's own quantity kept, marked, when it does not).
        PlotQuantities.Clear();
        var item = Viewer3DViewModel.PickSolution(p.Solution, request.Solver, mine, editor.Viewer.Scene.Problem);
        var offered = item is null ? [] : !p.Hidden && editor.Viewer.Plot?.Name == p.Name && editor.Viewer.FieldQuantities.Count > 0
            ? [.. editor.Viewer.FieldQuantities] : editor.OfferedQuantities(item);
        foreach (var q in offered) PlotQuantities.Add(new C3dPlotQuantityChoice(q.Label, q.Array.Name, q.Mode.ToString()));
        var qc = PlotQuantities.FirstOrDefault(c => c.Quantity == p.Quantity && (p.Mode is null || string.Equals(c.Mode, p.Mode, StringComparison.OrdinalIgnoreCase)));
        if (qc is null)
        {
            qc = new C3dPlotQuantityChoice(FieldNames.Friendly(p.Quantity) + (p.Mode is { } md ? $" ({md})" : "") + (offered.Count > 0 ? " (not offered)" : ""),
                                           p.Quantity, p.Mode);
            PlotQuantities.Add(qc);
        }
        PlotQuantity = qc;

        PlotTargets.Clear();
        PlotTargets.Add(OnClip);
        PlotTargets.Add(OnSurfaces);
        PlotTargets.Add(OnFaces);
        PlotTarget = p.On switch { C3dFieldPlotOn.ClipPlane => OnClip, C3dFieldPlotOn.Surfaces => OnSurfaces, _ => OnFaces };
        PlotIsClip = p.On == C3dFieldPlotOn.ClipPlane;
        PlotIsFaces = p.On == C3dFieldPlotOn.Faces;
        PlotAxis = p.Axis ?? C3dAxis.Z;
        PlotOffsetText = p.Offset is { } o ? LayoutUnits.Format(o, editor.Document.DisplayUnit, editor.Document.DbuPerMicron) : "";
        PlotFacesText = p.Faces.Count == 0
            ? "None yet: right-click a face ▸ " + (p.IsTemperature ? "Plot Temperature" : "Plot Field") + " while this plot is drawn."
            : string.Join("\n", p.Faces.Select(f => f.Face + f.Side switch { C3dFieldSide.Top => " (top side)", C3dFieldSide.Bottom => " (bottom side)", _ => "" }));
        PlotDb = p.Db;
        PlotPercentile = p.Percentile;
        PlotFixRange = p.FixRange;
        PlotProblem = editor.FieldPlotProblem(p);
    }

    /// <summary>The drawn plot's verdict changed (the run was read, the step loaded): the sentence above the fields follows.</summary>
    internal void RefreshPlotProblem()
    {
        if (IsFieldPlot && Editor.FieldPlot(_plotName) is { } p) PlotProblem = Editor.FieldPlotProblem(p);
    }

    private void CommitPlot(string description, Action<C3dFieldPlot> mutate)
    {
        if (_loading || !IsFieldPlot) return;
        Error = Editor.SetFieldPlot(_plotName, description, mutate) ?? "";
    }

    partial void OnPlotSetupChanged(string? value)
    {
        if (value is null) return;
        string? setup = value == ActiveSetupChoice ? null : value;
        // Another setup's run saves other steps: the solution is left for the picker to name again.
        CommitPlot($"Plot {_plotName} from {value}", p => { if (p.Setup != setup) { p.Setup = setup; p.Solution = null; } });
    }

    partial void OnPlotSolverChanged(string? value)
    {
        if (value is null) return;
        var solver = value == "openEMS" ? Em3dSolver.OpenEms : Em3dSolver.Palace;
        CommitPlot($"Plot {_plotName} from {value}", p => p.Solver = solver);
    }

    partial void OnPlotSolutionChanged(C3dPlotSolutionChoice? value)
    {
        if (_loading || value is null) return;
        if (value.IsOther) { PlotOtherOpen = true; return; }
        PlotOtherOpen = false;
        CommitPlot($"Plot {_plotName} at {value.Solution!.Describe()}", p => p.Solution = value.Solution.Clone());
    }

    /// <summary>R-em3d83-6 — the Other frequency box's Enter: the setup saves it too, and the plot moves to it.</summary>
    public void CommitOtherFrequency()
    {
        if (!IsFieldPlot) return;
        if (!double.TryParse(PlotOtherText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ghz))
        {
            Error = "Enter a frequency in GHz, e.g. 6.";
            return;
        }
        Error = Editor.PlotOtherFrequency(_plotName, ghz) ?? "";
        if (Error.Length == 0) { PlotOtherOpen = false; PlotOtherText = ""; }
    }

    partial void OnPlotQuantityChanged(C3dPlotQuantityChoice? value)
    {
        if (value is null) return;
        CommitPlot($"Plot {_plotName}: {value.Label}", p => { p.Quantity = value.Quantity; p.Mode = value.Mode; });
    }

    partial void OnPlotTargetChanged(string? value)
    {
        if (value is null) return;
        var on = value == OnClip ? C3dFieldPlotOn.ClipPlane : value == OnFaces ? C3dFieldPlotOn.Faces : C3dFieldPlotOn.Surfaces;
        CommitPlot($"Plot {_plotName} on {value.ToLowerInvariant()}", p =>
        {
            p.On = on;
            if (on == C3dFieldPlotOn.ClipPlane && p.Offset is null) (p.Axis, p.Offset) = Editor.PlotClipDefault();
        });
    }

    partial void OnPlotAxisChanged(C3dAxis value) => CommitPlot($"Cut {_plotName} across {value}", p => p.Axis = value);

    /// <summary>The Offset box's Enter or lost focus: a length in the display unit (or with its own), one undo entry.</summary>
    public void CommitPlotOffset()
    {
        if (!IsFieldPlot) return;
        var doc = Editor.Document;
        if (!LayoutUnits.TryParse(PlotOffsetText, doc.DisplayUnit, doc.DbuPerMicron, out long dbu))
        {
            Error = $"Enter a position along {PlotAxis}, e.g. 120 ({LayoutUnits.Suffix(doc.DisplayUnit)}).";
            return;
        }
        CommitPlot($"Cut {_plotName} at {PlotAxis} = {PlotOffsetText.Trim()}", p => { p.Axis ??= PlotAxis; p.Offset = dbu; });
    }

    partial void OnPlotDbChanged(bool value) => CommitPlot($"{_plotName} in {(value ? "dB" : "linear units")}", p => p.Db = value);
    partial void OnPlotPercentileChanged(double value) => CommitPlot($"{_plotName}'s range to the {value} percentile", p => p.Percentile = value);
    partial void OnPlotFixRangeChanged(bool value) => CommitPlot($"{(value ? "Fix" : "Free")} the range of {_plotName}", p => p.FixRange = value);
}
