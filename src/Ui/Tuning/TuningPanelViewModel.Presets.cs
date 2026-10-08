// ================================================================
//  TuningPanelViewModel.Presets.cs  —  lock in, recall, "Last tuned"
//  (brief-tuneopt-5, overview D5/D6/D18)
//
//  Presets live in the tuned schematic's tuning block, so every change
//  to one is a document edit with one undo step. Recall is the
//  session's: it loads values into the sliders (and the running
//  session) and never changes the schematic — Push does that. What a
//  recall could not apply is reported, never refused (PresetRecall).
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.Tuning;

public sealed partial class TuningPanelViewModel
{
    /// <summary>Puts text on the clipboard (Copy as <c>.cnl</c>); the view supplies it.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Posts a recall's report to the Messages panel: the summary, then one line per key.</summary>
    public Action<string, IReadOnlyList<string>>? ReportRecall { get; set; }

    /// <summary>The clock a preset's time is taken from; tests replace it.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>The drop-down: "Last tuned" first, then newest first (R-to5-4).</summary>
    public ObservableCollection<TuningPresetItemViewModel> Presets { get; } = [];

    public bool HasPresets => Presets.Count > 0;

    /// <summary>The full list behind <see cref="StatusText"/> — a recall's every key — for its tooltip.</summary>
    [ObservableProperty] private string? _statusDetail;

    // Any other status line has no list behind it.
    partial void OnStatusTextChanged(string value) => StatusDetail = null;

    /// <summary>A preset was just locked in and is open for its name: the view opens the drop-down.</summary>
    public event EventHandler? PresetNamingRequested;

    private TuningSetup? _presetsBuiltFrom;
    private int?         _renameIndex;

    /// <summary>Rebuilds the drop-down when the tuning block is a different one — every edit replaces it.</summary>
    private void RebuildPresets()
    {
        var setup = _tuned?.EditModel.Tuning;
        if (ReferenceEquals(setup, _presetsBuiltFrom) && (setup is not null || Presets.Count == 0)) return;
        _presetsBuiltFrom = setup;

        Presets.Clear();
        var list = setup?.Presets ?? [];
        var items = list.Select((p, i) => new TuningPresetItemViewModel(this, i, p))
            .OrderByDescending(x => x.IsLastTuned)
            .ThenByDescending(x => x.Preset.Created ?? DateTime.MinValue)
            .ThenByDescending(x => x.Index);
        foreach (var item in items)
        {
            if (item.Index == _renameIndex) item.IsRenaming = true;
            Presets.Add(item);
        }
        _renameIndex = null;
        CompareSchematic = false;
        OnPropertyChanged(nameof(HasPresets));
        LockInCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
    }

    // ---- Lock in (R-to5-1) ----------------------------------------------------

    /// <summary>
    /// The session's current value of every tune-enabled entry — the schematic's own text for one not
    /// moved, a complex value ONCE and whole, in the form the schematic writes it (overview D18).
    /// </summary>
    public IReadOnlyDictionary<string, string> LockInValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (row.Tunable is not { } t || row.IsDisabled) continue;
            if (t.WholeKey is null)
                values[row.Key] = row.DiffersFromSchematic ? row.ValueText : t.ValueText;
            else if (!values.ContainsKey(t.WholeKey))
                values[t.WholeKey] = _complex.TryGetValue(t.WholeKey, out var z)
                    ? ComplexValue.Format(z, t.WholeUnit, t.Form)
                    : ComplexValue.Format(t.Whole, t.WholeUnit, t.Form, "G15");
        }
        return values;
    }

    /// <summary>Stores the current values as a new preset, named <c>Preset n</c> and open for renaming.
    /// One undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanLockIn))]
    private void LockIn()
    {
        if (_tuned is null) return;
        var (setup, _) = TuningPresets.LockIn(_tuned.EditModel.Tuning, LockInValues(), UtcNow());
        _renameIndex = setup.Presets.Count - 1;
        _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel, setup, "Lock in preset"));
        RefreshNow();
        StatusText = $"Locked in {setup.Presets[^1].Name}";
        PresetNamingRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanLockIn() => _tuned is not null && Rows.Any(r => r.Tunable is not null && !r.IsDisabled);

    // ---- Recall (R-to5-2, R-to5-3) -------------------------------------------

    /// <summary>Loads a preset into the sliders and, when running, the session. The schematic is not
    /// changed — except that a range a value fell outside grows to include it, and a key not tuned yet
    /// is turned on, both as one undo step. <paramref name="push"/> then pushes, as its own step.</summary>
    internal void RecallPreset(TuningPresetItemViewModel item, bool push)
    {
        if (_tuned is null || Catalog is not { } catalog) return;
        var result = PresetRecall.Apply(item.Preset, catalog, _tuned.EditModel.Tuning);

        foreach (var (key, text) in result.Values)
        {
            if (catalog.Find(key) is { Part: null } t)
            {
                if (TunableValue.InUnit(text, t.Unit) is { } n) _values[key] = t.IsInteger ? Math.Round(n) : n;
            }
            else if (catalog.PartsOf(key) is { Count: > 0 } parts
                     && ComplexValue.TryParse(text, out var z, out _, out _))
            {
                _complex[key] = z;
            }
        }

        if (result.Setup is { } setup)
            _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel, setup, $"Recall preset {item.Name}"));
        RefreshNow();

        StatusText   = result.Summary;
        StatusDetail = string.Join(Environment.NewLine, result.Lines);
        ReportRecall?.Invoke($"Tuning: recalled '{item.Name}' — {result.Summary}", result.Lines);
        AfterValueChange(final: true);

        if (!push) return;
        Push();
        StatusText   = $"{result.Summary} · {StatusText}";
        StatusDetail = string.Join(Environment.NewLine, result.Lines);
    }

    // ---- Manage (R-to5-4) ------------------------------------------------------

    internal void RenamePreset(TuningPresetItemViewModel item, string name)
    {
        if (_tuned?.EditModel.Tuning is not { } setup) return;
        if (TuningPresets.NameProblem(setup, item.Index, name) is { } why)
        {
            StatusText = $"Not renamed: {why}.";
            item.CancelRename();
            return;
        }
        if (TuningPresets.Rename(setup, item.Index, name) is { } next)
            _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel, next, "Rename preset"));
        RefreshNow();
    }

    internal void DuplicatePreset(TuningPresetItemViewModel item)
    {
        if (_tuned?.EditModel.Tuning is not { } setup) return;
        _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel,
            TuningPresets.Duplicate(setup, item.Index, UtcNow()), $"Duplicate preset {item.Name}"));
        RefreshNow();
    }

    internal void DeletePreset(TuningPresetItemViewModel item)
    {
        if (_tuned?.EditModel.Tuning is not { } setup) return;
        _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel,
            TuningPresets.Delete(setup, item.Index), $"Delete preset {item.Name}"));
        RefreshNow();
    }

    internal void CopyPresetAsCnl(TuningPresetItemViewModel item)
    {
        CopyText?.Invoke(TuningPresets.CnlText(item.Preset));
        StatusText = $"Copied {item.Name} as .cnl";
    }

    // ---- Compare (R-to5-6) ------------------------------------------------------

    /// <summary>"Schematic" ticked as one side of a comparison.</summary>
    [ObservableProperty] private bool _compareSchematic;

    partial void OnCompareSchematicChanged(bool value) => OnCompareSelectionChanged();

    internal void OnCompareSelectionChanged() => CompareCommand.NotifyCanExecuteChanged();

    public ObservableCollection<PresetComparisonRow> ComparisonRows { get; } = [];

    [ObservableProperty] private string _comparisonTitle = "";

    public bool HasComparison => ComparisonRows.Count > 0 || ComparisonTitle.Length > 0;

    private List<TuningPresetItemViewModel> CompareSides()
        => [.. Presets.Where(p => p.IsSelectedForCompare).OrderBy(p => p.Index)];

    /// <summary>Two sides ticked: two presets, or one and "Schematic". The schematic, or the older preset,
    /// is the first side; the difference is second − first. No simulation.</summary>
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void Compare()
    {
        if (Catalog is not { } catalog) return;
        var sides = CompareSides();
        TuningPreset? a = CompareSchematic ? null : sides[0].Preset;
        TuningPreset? b = CompareSchematic ? sides[0].Preset : sides[1].Preset;

        ComparisonRows.Clear();
        foreach (var row in TuningPresets.Compare(a, b, catalog, _tuned?.EditModel.Tuning)) ComparisonRows.Add(row);
        ComparisonTitle = $"{a?.Name ?? "Schematic"} → {b!.Name}";
        OnPropertyChanged(nameof(HasComparison));
    }

    private bool CanCompare() => CompareSides().Count + (CompareSchematic ? 1 : 0) == 2;

    [RelayCommand]
    private void CloseComparison()
    {
        ComparisonRows.Clear();
        ComparisonTitle = "";
        OnPropertyChanged(nameof(HasComparison));
    }

    // ---- "Last tuned" (R-to5-5, overview D6) ----------------------------------

    /// <summary>
    /// The tuned schematic is being saved or closed: when its session values differ from the schematic,
    /// they are written into the single "Last tuned" preset — one undo step, and the document is dirty
    /// with it, so a close asks. Nothing when they are equal, or when Last tuned already holds them.
    /// True when it wrote.
    /// </summary>
    public bool StoreLastTuned()
    {
        if (_tuned is null || !Rows.Any(r => r.Tunable is not null && !r.IsDisabled && r.DiffersFromSchematic)) return false;
        if (TuningPresets.WithLastTuned(_tuned.EditModel.Tuning, LockInValues(), UtcNow()) is not { } next) return false;
        _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel, next, "Store last tuned values"));
        RefreshNow();
        return true;
    }
}
