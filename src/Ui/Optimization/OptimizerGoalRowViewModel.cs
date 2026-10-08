// ================================================================
//  OptimizerGoalRowViewModel.cs  —  one goal in the Optimizer's list
//  (brief-tuneopt-10 R-to10-3)
//
//  Enabled check, name, a compact readable summary, and — once a run
//  has a best point — a margin bar (green when met, its length the
//  normalized margin), the worst value and ✓/✕.
// ================================================================

using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Optimization;

public sealed partial class OptimizerGoalRowViewModel : ObservableObject
{
    private readonly OptimizerPanelViewModel _panel;
    private bool _syncing;

    internal OptimizerGoalRowViewModel(OptimizerPanelViewModel panel, int index, OptimizationGoal goal)
    {
        _panel = panel;
        Bind(index, goal);
    }

    /// <summary>Where the goal sits in the setup's list.</summary>
    public int Index { get; private set; }

    public OptimizationGoal Goal { get; private set; } = new();

    public string Name => Goal.Name;

    /// <summary><c>dB(SP1.S(2, 1)) ≥ −0.5 · 1 GHz–2 GHz · SP1</c>.</summary>
    public string Summary => Summarize(Goal);

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing) _panel.SetGoalEnabled(this, value);
    }

    [ObservableProperty] private bool _isSelected;

    // ---- At the best point ---------------------------------------------------

    /// <summary>A run has scored this goal.</summary>
    [ObservableProperty] private bool _hasResult;

    [ObservableProperty] private bool _isMet;

    /// <summary>0..1: 1 when met; otherwise one less the worst violation over the goal's scale.</summary>
    [ObservableProperty] private double _margin;

    /// <summary>The expression's value at its worst point, <c>−0.62</c>.</summary>
    [ObservableProperty] private string _worstText = "";

    internal void Bind(int index, OptimizationGoal goal)
    {
        Index = index;
        Goal  = goal;
        _syncing = true;
        IsEnabled = goal.Enabled;
        _syncing = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Across corners, the binding corner — <c>@ hot</c> — where the goal's worst violation (met: its
    /// tightest margin) is (brief-yield-7 R-ya7-2); empty otherwise.</summary>
    [ObservableProperty] private string _cornerText = "";

    internal void ShowReport(GoalReport? report)
    {
        CornerText = report?.Corner is { } corner ? "@ " + corner : "";
        if (report is null)
        {
            HasResult = false;
            WorstText = "";
            return;
        }
        HasResult = true;
        IsMet     = report.Met;
        double scale = GoalResiduals.Scale(Goal) is > 0 and var s ? s : 1;
        Margin    = report.Met ? 1 : Math.Clamp(1 - report.WorstViolation / scale, 0.02, 1);
        WorstText = double.IsFinite(report.WorstValue) ? report.WorstValue.ToString("G4", CultureInfo.InvariantCulture) : "—";
    }

    /// <summary>The goal in one line: expression, limit, range and analysis, each only when it has one.</summary>
    public static string Summarize(OptimizationGoal g)
    {
        string limit = g.Type switch
        {
            GoalType.Le  => $"≤ {Slope(g)}",
            GoalType.Ge  => $"≥ {Slope(g)}",
            GoalType.Eq  => $"= {Slope(g)}",
            GoalType.In  => $"in [{g.Limit}, {g.UpperLimit}]",
            _            => $"out [{g.Limit}, {g.UpperLimit}]",
        };
        string text = $"{g.Expression} {limit}";
        if (g.Range is { } r) text += $" · {r.Lo}–{r.Hi}" + (r.Axis == "freq" ? "" : $" ({r.Axis})");
        if (g.Analysis is { } a) text += $" · {a}";
        if (g.Weight != 1) text += $" · ×{g.Weight.ToString("G4", CultureInfo.InvariantCulture)}";
        return text;

        static string Slope(OptimizationGoal g)
            => g.Limit.Length == 0 ? "?" : g.LimitAtHi is { } hi ? $"{g.Limit} → {hi}" : g.Limit;
    }
}
