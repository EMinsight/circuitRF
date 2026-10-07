// ================================================================
//  TuningScopeViewModel.cs  —  the ⚙ settings: which analyses a
//  session evaluates (brief-tuneopt-4 R-to4-5, TO-3 R-to3-4)
//
//  All enabled analyses, or a checked subset. Each row shows what one
//  slider move costs in points, and a parametric sweep past
//  SweepWarningPoints says so — every move re-runs all of it.
// ================================================================

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Tuning;

public sealed partial class TuningScopeViewModel(TuningPanelViewModel panel) : ObservableObject
{
    /// <summary>Evaluate every enabled analysis — the default.</summary>
    [ObservableProperty] private bool _useAll = true;

    public ObservableCollection<TuningScopeRow> Analyses { get; } = [];

    /// <summary>Re-reads the tuned schematic's enabled analyses, keeping the user's checks by name.</summary>
    public void Reload()
    {
        var checkedNames = Analyses.Where(a => a.IsChecked).Select(a => a.Name).ToHashSet();
        Analyses.Clear();
        if (panel.Tuned is not { } vm) return;
        foreach (var a in vm.EditModel.Analyses.Where(a => a.Enabled))
            Analyses.Add(new TuningScopeRow(a.Name, a is ParametricSweepAnalysis)
            {
                IsChecked = checkedNames.Contains(a.Name),
            });
    }

    /// <summary>Fills in each row's point count — asked when the ⚙ popup opens, not on every focus change.</summary>
    public void LoadPoints()
    {
        Reload();
        if (panel.Tuned is not { } vm || panel.PointsOf is not { } pointsOf) return;
        IReadOnlyDictionary<string, (long Points, long SweepPoints)> costs;
        try { costs = pointsOf(vm, [.. Analyses.Select(a => a.Name)]); }
        catch { return; }
        foreach (var row in Analyses)
            if (costs.TryGetValue(row.Name, out var c)) (row.Points, row.SweepPoints) = (c.Points, c.SweepPoints);
    }

    /// <summary>The names a session is narrowed to, or null for every enabled analysis.</summary>
    public IReadOnlyList<string>? SelectedAnalyses()
    {
        if (UseAll) return null;
        var names = Analyses.Where(a => a.IsChecked).Select(a => a.Name).ToList();
        return names.Count == 0 ? null : names;
    }
}

public sealed partial class TuningScopeRow(string name, bool isSweep) : ObservableObject
{
    public string Name    { get; } = name;
    public bool   IsSweep { get; } = isSweep;

    [ObservableProperty] private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PointsText))]
    private long? _points;

    /// <summary>Points across the parametric sweeps the analysis dispatches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warning), nameof(HasWarning))]
    private long _sweepPoints;

    public string PointsText => Points is { } p ? $"{p:N0} pts" : "";

    /// <summary>A sweep past <see cref="TuningPanelViewModel.SweepWarningPoints"/>: every slider move runs all of it.</summary>
    public string Warning => SweepPoints > TuningPanelViewModel.SweepWarningPoints
        ? $"sweep of {SweepPoints:N0} per move" : "";

    public bool HasWarning => Warning.Length > 0;
}
