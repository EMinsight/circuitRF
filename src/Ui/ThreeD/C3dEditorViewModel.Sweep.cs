// brief-em3d-86 R-em3d86-3 — the SWEEP SLIDER (R-em3d75-4c, never built until now). The viewer had the state — TemperatureStep,
// its label, a field solution per step — and the probe table and the line plots followed it, but no view bound it once rounds
// 6/7 removed the toolbar strip it would have lived on; the Inspector's Solution picker was the only way to another point.
//
// It sits on the viewport beside the field's legend, shown only when the drawn result has more than one step, one control per
// sweep axis (a two-axis sweep's point is the run's own order, last axis fastest), each labelled with its variable, value and
// unit ("Pdiss = 7 W"), never "step 2 of 3". It moves the drawn plot's step EXACTLY as the picker does — the plot's Solution,
// one undo entry, so a saved document reopens on the point last shown. A drag previews (the viewer's step moves, the document
// does not) and its release commits: one gesture, one entry. ◀ and ▶ commit per press.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One sweep axis of the slider: its variable, values and unit, and the index shown.</summary>
public sealed partial class C3dSweepAxis : ObservableObject
{
    private readonly Action _moved, _commit;
    internal bool Syncing;

    public C3dSweepAxis(string name, string unit, IReadOnlyList<double> values, Action moved, Action commit, Func<int, string>? label = null)
    {
        Name = name;
        Unit = unit;
        Values = values;
        _moved = moved;
        _commit = commit;
        _label = label;
    }

    private readonly Func<int, string>? _label;

    public string Name { get; }
    public string Unit { get; }
    public IReadOnlyList<double> Values { get; }
    public int Max => Values.Count - 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    private int _index;

    /// <summary><c>Pdiss = 7 W</c>: the variable, its value at the index shown, and its unit.</summary>
    public string Label => _label?.Invoke(Index)
                           ?? $"{Name} = {Values[Math.Clamp(Index, 0, Max)].ToString("G6", CultureInfo.InvariantCulture)}{(Unit.Length > 0 ? " " + Unit : "")}";

    partial void OnIndexChanged(int value)
    {
        if (!Syncing) _moved();
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous() { Index--; _commit(); }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() { Index++; _commit(); }

    private bool CanPrevious() => Index > 0;
    private bool CanNext() => Index < Max;
}

public sealed partial class C3dEditorViewModel
{
    /// <summary>The slider's controls, one per sweep axis.</summary>
    public ObservableCollection<C3dSweepAxis> SweepAxes { get; } = [];

    /// <summary>The slider is shown: a thermal result with more than one step is drawn.</summary>
    public bool SweepControlVisible => Viewer.ShowField && Viewer.HasTemperatureSweep && SweepAxes.Count > 0;

    /// <summary>Wires the slider to the viewer: rebuilt when the result is read, re-synced when the step moves elsewhere (the
    /// picker, an undo).</summary>
    private void WatchSweep()
    {
        Viewer.ThermalResultsChanged += RebuildSweepAxes;
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Viewer3DViewModel.TemperatureStep)) SyncSweepAxes();
            if (e.PropertyName is nameof(Viewer3DViewModel.ShowField) or nameof(Viewer3DViewModel.HasTemperatureSweep)
                                  or nameof(Viewer3DViewModel.TemperatureStepMax))
            {
                if (e.PropertyName == nameof(Viewer3DViewModel.TemperatureStepMax)) RebuildSweepAxes();
                OnPropertyChanged(nameof(SweepControlVisible));
            }
        };
    }

    /// <summary>One control per axis of the run's table, when its points are the steps; else one over the steps, labelled as
    /// the viewer labels them.</summary>
    private void RebuildSweepAxes()
    {
        SweepAxes.Clear();
        int steps = Viewer.FieldSolutions.Count;
        if (steps > 1)
        {
            if (Viewer.ThermalTable is { Axes.Count: > 0 } t && t.Points == steps)
                foreach (var a in t.Axes) SweepAxes.Add(new C3dSweepAxis(a.Name, a.Unit, a.Values, SweepMoved, () => CommitSweepStep()));
            else
                SweepAxes.Add(new C3dSweepAxis("point", "", [.. Enumerable.Range(1, steps).Select(k => (double)k)], SweepMoved, () => CommitSweepStep(),
                                               k => Viewer.FieldSolutions[Math.Clamp(k, 0, steps - 1)].Label));
        }
        SyncSweepAxes();
        OnPropertyChanged(nameof(SweepControlVisible));
    }

    /// <summary>The controls to the viewer's step.</summary>
    private void SyncSweepAxes()
    {
        if (SweepAxes.Count == 0) return;
        int step = Viewer.TemperatureStep;
        var idx = SweepAxes.Count == 1 ? [step] : (Viewer.ThermalTable?.Indices(step) ?? [step]);
        for (int k = 0; k < SweepAxes.Count && k < idx.Length; k++)
        {
            SweepAxes[k].Syncing = true;
            try { SweepAxes[k].Index = idx[k]; }
            finally { SweepAxes[k].Syncing = false; }
        }
    }

    /// <summary>A control moved: the step it names (last axis fastest), previewed.</summary>
    private void SweepMoved()
    {
        int step = 0;
        foreach (var a in SweepAxes) step = step * a.Values.Count + a.Index;
        PreviewSweepStep(step);
    }

    /// <summary>Shows step <paramref name="step"/> without writing the document: a drag's preview.</summary>
    public void PreviewSweepStep(int step)
    {
        if (step < 0 || step >= Viewer.FieldSolutions.Count) return;
        Viewer.TemperatureStep = step;
    }

    /// <summary>
    /// Writes the step shown into the drawn plot's Solution — the Inspector picker's own edit, one undo entry — unless it is
    /// already there. Null, or why nothing was written (no plot is drawn: a setup's 3D view keeps no document). brief-em3d-96 —
    /// the step is one control: every drawn temperature plot on the same run takes it, in the same entry.
    /// </summary>
    public string? CommitSweepStep()
    {
        int step = Viewer.TemperatureStep;
        if (step < 0 || step >= Viewer.FieldSolutions.Count) return null;
        if (VisibleFieldPlot is not { } plot) return "No field plot is drawn, so the step is shown but not kept.";
        var item = Viewer.FieldSolutions[step];
        var key = FieldPlotResolver.SolutionKey(item.Solution, Viewer.Scene.Problem);
        var names = VisibleFieldPlots.Where(p => ReferenceEquals(p, plot) ||
                                                 (plot.IsTemperature && p.IsTemperature && ReferenceEquals(Viewer.LayerNamed(p.Name)?.Item?.Run, item.Run)))
                                     .Where(p => p.Solution is not { } now || !now.SameAs(key)).Select(p => p.Name).ToList();
        if (names.Count == 0) return null;
        if (names.Count == 1) return SetFieldPlot(names[0], $"Plot {names[0]} at {Viewer.TemperatureStepLabel}", p => p.Solution = key);
        ChangePlots($"Plot {string.Join(", ", names)} at {Viewer.TemperatureStepLabel}", plots =>
        {
            foreach (var p in plots.Where(p => names.Contains(p.Name))) p.Solution = key;
        });
        return null;
    }

    /// <summary>Moves to step <paramref name="step"/> and keeps it: a press, one undo entry.</summary>
    public string? SetSweepStep(int step)
    {
        PreviewSweepStep(step);
        return CommitSweepStep();
    }
}
