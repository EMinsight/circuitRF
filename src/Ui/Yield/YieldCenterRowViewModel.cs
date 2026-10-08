// ================================================================
//  YieldCenterRowViewModel.cs  —  one row of the Centering mode's
//  variable list (brief-yield-12 R-ya12-1): a designable nominal with
//  its range, or a toleranced value, or both — the tolerance beside
//  the range — and, once a run reports, start → best with its mark on
//  the range and a railed end.
// ================================================================

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Yield;

public sealed partial class YieldCenterRowViewModel : ObservableObject
{
    internal YieldCenterRowViewModel(string key) => Key = key;

    public string Key { get; }

    public Tunable? Tunable { get; private set; }

    /// <summary>The entry's opt flag — centering moves this one's nominal.</summary>
    public bool IsDesignable { get; private set; }

    public double Min { get; private set; }
    public double Max { get; private set; }
    public TuneScale Scale { get; private set; }

    /// <summary><c>500 … 2000 Ohm</c>; empty for a value centering does not move.</summary>
    public string RangeText { get; private set; } = "";

    /// <summary>The spread as the Yield mode's editor shows it; empty when the value is not toleranced.</summary>
    public string ToleranceText { get; private set; } = "";

    public bool HasTolerance => ToleranceText.Length > 0;

    /// <summary>The start's value; empty before a run.</summary>
    [ObservableProperty] private string _startText = "";

    /// <summary>The best point's value; empty before a run.</summary>
    [ObservableProperty] private string _bestText = "";

    /// <summary>Where the best value sits on the range, 0..1.</summary>
    [ObservableProperty] private double _bestPosition;

    [ObservableProperty] private bool _railedMin;
    [ObservableProperty] private bool _railedMax;

    public bool HasBest => BestText.Length > 0;

    partial void OnBestTextChanged(string value) => OnPropertyChanged(nameof(HasBest));

    internal void Bind(TunableEntry entry, Tunable? tunable)
    {
        Tunable = tunable;
        IsDesignable = entry.Opt;
        string u = tunable?.Unit ?? "";
        Min = TunableValue.InUnit(entry.Min, u) ?? TunableValue.InUnit(tunable?.DefaultMin, u) ?? 0;
        Max = TunableValue.InUnit(entry.Max, u) ?? TunableValue.InUnit(tunable?.DefaultMax, u) ?? 1;
        if (!(Max > Min)) Max = Min + 1;
        Scale = entry.Scale;
        RangeText = entry.Opt
            ? $"{TuningRowViewModel.FormatValue(Min, "")} … {TuningRowViewModel.FormatValue(Max, "")}{(u.Length > 0 ? " " + u : "")}"
            : "";
        ToleranceText = entry.IsStatistical ? CircuitRF.Design.Statistics.ToleranceText.Format(entry.Distribution, entry.Spread) : "";
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The run's start and best values (value key → text) and whether the best one railed.</summary>
    internal void Show(IReadOnlyDictionary<string, string>? start, IReadOnlyDictionary<string, string>? best, RailedVariable? railed)
    {
        StartText = Text(start);
        BestText  = Text(best);
        if (Tunable is { } t && best is not null && best.TryGetValue(t.ValueKey, out var b) && TunableValue.InUnit(b, t.Unit) is { } v)
            BestPosition = TuningSliderMapping.ToPosition(v, Min, Max, Scale);
        RailedMin = railed?.End == RailEnd.Min;
        RailedMax = railed?.End == RailEnd.Max;
    }

    private string Text(IReadOnlyDictionary<string, string>? values)
        => values is not null && Tunable is { } t && values.TryGetValue(t.ValueKey, out var text) ? text : "";
}
