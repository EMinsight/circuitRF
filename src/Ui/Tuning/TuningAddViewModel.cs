// ================================================================
//  TuningAddViewModel.cs  —  the Add… search popup (brief-tuneopt-4 R-to4-3.1)
//
//  Lists the catalog: key, location, current value, read-only mark.
//  Typing filters live; "Include sub-cells" widens the list past the
//  top level, as the Instances panel's check does. Enter adds the
//  selected rows — or the first one shown when none is selected — in
//  one undo step; a double-click adds that row with any selected.
//
//  The Optimizer panel's ＋ is the same popup over the same catalog;
//  only the flag it sets differs (ITunableAddHost).
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tuning;

/// <summary>What the Add… popup lists from and what adding sets: <c>tune</c> for the Tuning panel,
/// <c>opt</c> for the Optimizer's (brief-tuneopt-10 R-to10-2).</summary>
public interface ITunableAddHost
{
    TunableCatalog? Catalog { get; }

    /// <summary>The key already carries this panel's flag, so the popup does not offer it.</summary>
    bool IsActive(string key);

    /// <summary>Sets this panel's flag on every key, as one undo step.</summary>
    void Activate(IReadOnlyCollection<string> keys);
}

public sealed partial class TuningAddViewModel(ITunableAddHost panel) : ObservableObject
{
    [ObservableProperty] private bool   _isOpen;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool   _includeSubCells;

    /// <summary>The tunables shown, not yet tuned, matching the search.</summary>
    public ObservableCollection<TuningAddRow> Results { get; } = [];

    partial void OnSearchTextChanged(string value)       => Refresh();
    partial void OnIncludeSubCellsChanged(bool value)    => Refresh();
    partial void OnIsOpenChanged(bool value)             { if (value) Refresh(); }

    /// <summary>Rebuilds <see cref="Results"/>. Cheap: the catalog is already in hand.</summary>
    public void Refresh()
    {
        Results.Clear();
        if (panel.Catalog is not { } catalog) return;

        string q = SearchText.Trim();
        foreach (var t in catalog.Tunables)
        {
            if (t.Cell is not null && !IncludeSubCells) continue;
            if (panel.IsActive(t.Key)) continue;
            if (q.Length > 0 && !Matches(t, q)) continue;
            Results.Add(new TuningAddRow(t));
        }
    }

    /// <summary>Name, instance, parameter or cell — case-insensitive.</summary>
    private static bool Matches(Tunable t, string q)
        => new[] { t.Key, t.Owner, t.Parameter, t.Cell }
            .Any(s => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Adds the selected rows, or the first one shown when none is selected (Enter).</summary>
    [RelayCommand]
    private void AddSelected() => Add(null);

    /// <summary>Double-click: adds this row, along with any rows already selected.</summary>
    public void AddWith(TuningAddRow row) => Add(row);

    private void Add(TuningAddRow? also)
    {
        var keys = Results.Where(r => r.IsSelected || ReferenceEquals(r, also)).Select(r => r.Key).ToList();
        if (keys.Count == 0 && Results.Count > 0) keys.Add(Results[0].Key);
        if (keys.Count == 0) return;
        panel.Activate(keys);
        IsOpen = false;
        SearchText = "";
    }
}

/// <summary>One row of the Add… list.</summary>
public sealed partial class TuningAddRow(Tunable tunable) : ObservableObject
{
    public Tunable Tunable { get; } = tunable;
    public string  Key       => Tunable.Key;
    public string  Location  => Tunable.Location;
    public string  ValueText => Tunable.ValueText;
    public bool    IsReadOnly => Tunable.ReadOnlyReason is not null;
    public string? ReadOnlyReason => Tunable.ReadOnlyReason;

    [ObservableProperty] private bool _isSelected;
}
