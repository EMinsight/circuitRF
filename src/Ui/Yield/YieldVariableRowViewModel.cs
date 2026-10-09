// ================================================================
//  YieldVariableRowViewModel.cs  —  one statistical entry in the Yield
//  panel's list (brief-yield-10 R-ya10-3)
//
//  The row is the SAME entry Tuning and the Optimizer show (yield
//  overview D1): its check is the entry's stat flag, its distribution
//  and spread are the entry's. What the row adds is the nominal in the
//  parameter's unit and, once contributions have been computed, its
//  share of the variance as a bar.
// ================================================================

using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Ui.Yield;

public sealed partial class YieldVariableRowViewModel : ObservableObject
{
    private readonly YieldPanelViewModel _panel;
    private bool _syncing;

    internal YieldVariableRowViewModel(YieldPanelViewModel panel, string key)
    {
        _panel = panel;
        Key    = key;
    }

    public string Key { get; }

    public Tunable? Tunable { get; private set; }

    public string Location => Tunable is { Cell: not null } t ? t.Location : "";

    public bool HasLocation => Location.Length > 0;

    /// <summary>The schematic's value with its unit — <c>1 kOhm</c> as the design writes it.</summary>
    public string NominalText => Tunable is { } t ? (t.ValueText + (t.Unit.Length > 0 && !t.ValueText.Contains(' ') ? " " + t.Unit : "")) : "—";

    /// <summary>Why the key resolves to nothing; null when it does.</summary>
    public string? DisabledReason => Tunable is null ? "not in the design" : null;

    public System.Collections.Generic.IReadOnlyList<DistributionChoice> Distributions => DistributionChoice.All;

    /// <summary>The entry's stat flag.</summary>
    [ObservableProperty] private bool _isStat;

    partial void OnIsStatChanged(bool value)
    {
        if (!_syncing) _panel.SetStat(this, value);
    }

    [ObservableProperty] private DistributionChoice? _distribution;

    partial void OnDistributionChanged(DistributionChoice? value)
    {
        if (!_syncing && value is not null) _panel.SetDistribution(this, value.Value);
    }

    /// <summary>The spread as the compact editor shows it (<see cref="ToleranceText.Format"/>).</summary>
    [ObservableProperty] private string _spreadText = "";

    /// <summary>The spread typed into the editor — committed on Enter or when it loses focus.</summary>
    public void CommitSpread(string text)
    {
        if (text.Trim() == SpreadText) return;
        if (_panel.EditSpread(this, text) is not null) OnPropertyChanged(nameof(SpreadText));
    }

    // ---- Contribution (R-ya10-7) ------------------------------------------------

    /// <summary>The share of the explained variance as shown, clamped to 0..1 (D-a); null before contributions are
    /// computed.</summary>
    [ObservableProperty] private double? _share;

    public bool HasShare => Share is not null;

    public double ShareValue => Share ?? 0;

    public string ShareText => Share is { } s ? (100 * s).ToString("0", CultureInfo.InvariantCulture) + " %" : "";

    partial void OnShareChanged(double? value)
    {
        OnPropertyChanged(nameof(HasShare));
        OnPropertyChanged(nameof(ShareValue));
        OnPropertyChanged(nameof(ShareText));
    }

    internal void ShowShare(double? share) => Share = share;

    internal void Bind(TunableEntry entry, Tunable? tunable)
    {
        Tunable = tunable;
        _syncing = true;
        IsStat       = entry.IsStatistical;
        Distribution = DistributionChoice.All.FirstOrDefault(c => c.Value == entry.Distribution);
        SpreadText   = ToleranceText.Format(entry.Distribution, entry.Spread);
        _syncing = false;
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(NominalText));
        OnPropertyChanged(nameof(DisabledReason));
    }

    [RelayCommand] private void Remove() => _panel.RemoveTolerance(this);
}
