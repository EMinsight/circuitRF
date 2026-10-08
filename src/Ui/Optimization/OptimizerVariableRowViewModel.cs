// ================================================================
//  OptimizerVariableRowViewModel.cs  —  one variable entry in the
//  Optimizer's list (brief-tuneopt-10 R-to10-2, R-to10-10)
//
//  The row is the SAME entry the Tuning panel shows (overview D4): its
//  check is the entry's opt flag, its range is the entry's range, and
//  a range edit here is a range edit there. What the row adds is the
//  run's view of it — the best value, marked on the range bar; its
//  sensitivity, once asked; and the railed mark (D17) with Widen.
//
//  A part of a complex value (D18) shows its view of the run's ONE
//  decoded complex value, so a part that only tunes still shows the
//  value the optimized parts gave it.
// ================================================================

using System;
using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Optimization;

public sealed partial class OptimizerVariableRowViewModel : ObservableObject
{
    private readonly OptimizerPanelViewModel _panel;
    private bool _syncing;

    internal OptimizerVariableRowViewModel(OptimizerPanelViewModel panel, string key)
    {
        _panel = panel;
        Key    = key;
    }

    public string Key { get; }

    public Tunable? Tunable { get; private set; }

    public string Location => Tunable is { Cell: not null } t ? t.Location : "";

    public bool HasLocation => Location.Length > 0;

    public string Unit => Tunable?.Unit ?? "";

    /// <summary>Why the optimizer cannot move it (a parametric sweep sweeps it, or the key names
    /// nothing); null when it can.</summary>
    public string? DisabledReason { get; private set; }

    public double Min { get; private set; }
    public double Max { get; private set; }
    public TuneScale Scale { get; private set; }

    public bool IsLog => TunableValue.Effective(Scale, Min, Max) == TuneScale.Log;

    public string MinText => TuningRowViewModel.FormatValue(Min, "");
    public string MaxText => TuningRowViewModel.FormatValue(Max, "");

    /// <summary>The entry's opt flag.</summary>
    [ObservableProperty] private bool _isOptimized;

    partial void OnIsOptimizedChanged(bool value)
    {
        if (!_syncing) _panel.SetOptimized(this, value);
    }

    // ---- The run's view -------------------------------------------------------

    private double _best;

    /// <summary>The best point's value, in <see cref="Unit"/> — the schematic's before any run.</summary>
    public double Best => _best;

    public string BestText => TuningRowViewModel.FormatValue(_best, "");

    /// <summary>Where <see cref="Best"/> sits on the range bar, 0..1.</summary>
    public double BestPosition => TuningSliderMapping.ToPosition(_best, Min, Max, Scale);

    /// <summary><c>s 0.41</c> — this variable's share of the cost's sensitivity; empty until asked.</summary>
    [ObservableProperty] private string _sensitivityText = "";

    /// <summary>The end of the range the best value has railed against (D17); null when it has not.</summary>
    [ObservableProperty] private RailEnd? _railed;

    /// <summary>Another part whose range holds this one at the edge (D18); null when it is its own.</summary>
    [ObservableProperty] private string? _railedAgainst;

    public bool IsRailed => Railed is not null;
    public bool IsRailedMin => Railed == RailEnd.Min;
    public bool IsRailedMax => Railed == RailEnd.Max;

    /// <summary><c>max</c>, or <c>max of mag(ZL)</c> when another part's range holds it.</summary>
    public string RailedText => Railed is not { } e ? "" :
        (e == RailEnd.Min ? "min" : "max") + (RailedAgainst is { } a ? $" of {a}" : "");

    partial void OnRailedChanged(RailEnd? value)
    {
        OnPropertyChanged(nameof(IsRailed));
        OnPropertyChanged(nameof(IsRailedMin));
        OnPropertyChanged(nameof(IsRailedMax));
        OnPropertyChanged(nameof(RailedText));
        WidenCommand.NotifyCanExecuteChanged();
    }

    partial void OnRailedAgainstChanged(string? value) => OnPropertyChanged(nameof(RailedText));

    internal void Bind(TunableEntry entry, Tunable? tunable)
    {
        Tunable = tunable;
        string u = tunable?.Unit ?? "";
        DisabledReason = tunable is null ? "not in the design" : tunable.DisabledReason;
        Min   = TunableValue.InUnit(entry.Min, u) ?? TunableValue.InUnit(tunable?.DefaultMin, u) ?? 0;
        Max   = TunableValue.InUnit(entry.Max, u) ?? TunableValue.InUnit(tunable?.DefaultMax, u) ?? 1;
        if (!(Max > Min)) Max = Min + 1;
        Scale = entry.Scale == TuneScale.Auto && tunable?.Part == ComplexPart.Phase ? TuneScale.Lin : entry.Scale;
        _syncing = true;
        IsOptimized = entry.Opt;
        _syncing = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Shows the value this row takes at <paramref name="values"/> (value key → text): a part
    /// reads its view of the whole complex value; a key the values do not name shows the schematic's.</summary>
    internal void ShowBest(System.Collections.Generic.IReadOnlyDictionary<string, string>? values)
    {
        if (Tunable is not { } t) return;
        double v = t.Value;
        if (values is not null && values.TryGetValue(t.ValueKey, out var text))
        {
            if (t.Part is { } part)
            {
                if (ComplexValue.TryParse(text, out Complex z, out _, out _))
                    v = ComplexValue.Get(z, part, phaseNear: (Min + Max) / 2);
            }
            else if (TunableValue.InUnit(text, t.Unit) is { } n)
            {
                v = n;
            }
        }
        _best = v;
        OnPropertyChanged(nameof(Best));
        OnPropertyChanged(nameof(BestText));
        OnPropertyChanged(nameof(BestPosition));
    }

    internal void ShowRailed(RailedVariable? railed)
    {
        Railed        = railed?.End;
        RailedAgainst = railed?.Against;
    }

    /// <summary>Doubles the span on the railed side — by ratio on a log range — as one undo step.</summary>
    [RelayCommand(CanExecute = nameof(IsRailed))]
    private void Widen() => _panel.Widen(this);

    [RelayCommand] private void Remove() => _panel.RemoveVariable(this);

    // ---- Range edits (the same entry the Tuning panel edits) -------------------

    public void CommitMin(string text)
    {
        if (TunableValue.InUnit(text, Unit) is { } v && v < Max)
            _panel.EditRange(this, e => e.Min = TuningRowViewModel.FormatValue(v, Unit), "Set tuning range");
        else OnPropertyChanged(nameof(MinText));
    }

    public void CommitMax(string text)
    {
        if (TunableValue.InUnit(text, Unit) is { } v && v > Min)
            _panel.EditRange(this, e => e.Max = TuningRowViewModel.FormatValue(v, Unit), "Set tuning range");
        else OnPropertyChanged(nameof(MaxText));
    }

    internal static string Share(double share) => "s " + share.ToString("0.00", CultureInfo.InvariantCulture);
}
