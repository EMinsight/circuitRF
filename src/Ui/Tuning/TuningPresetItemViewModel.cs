// ================================================================
//  TuningPresetItemViewModel.cs  —  one row of the Presets drop-down
//  (brief-tuneopt-5 R-to5-4)
//
//  A view of preset INDEX of the tuned schematic's tuning block — the
//  panel rebuilds every item on each change, so an item never holds
//  state the document does not. Every action is the panel's.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Tuning;

public sealed partial class TuningPresetItemViewModel : ObservableObject
{
    private readonly TuningPanelViewModel _panel;

    internal TuningPresetItemViewModel(TuningPanelViewModel panel, int index, TuningPreset preset)
    {
        _panel  = panel;
        Index   = index;
        Preset  = preset;
        _renameText = preset.Name;
    }

    /// <summary>Its position in the tuning block's preset list.</summary>
    public int Index { get; }

    internal TuningPreset Preset { get; }

    public string Name => Preset.Name;

    public bool IsLastTuned => Preset.IsLastTuned;

    /// <summary>When it was locked in, in local time; empty when the file does not say.</summary>
    public string CreatedText => Preset.Created is { } c
        ? c.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture) : "";

    /// <summary>The hover: its values, one per line, and an optimizer preset's cost.</summary>
    public string ToolTipText
    {
        get
        {
            var lines = Preset.Values.Select(kv => $"{kv.Key} = {kv.Value}").ToList();
            if (lines.Count == 0) lines.Add("(no values)");
            if (Preset.Cost is { } cost) lines.Add($"cost {cost.ToString("G6", CultureInfo.InvariantCulture)}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    [ObservableProperty] private bool   _isRenaming;
    [ObservableProperty] private string _renameText;

    /// <summary>Ticked for Compare (R-to5-6).</summary>
    [ObservableProperty] private bool _isSelectedForCompare;

    partial void OnIsSelectedForCompareChanged(bool value) => _panel.OnCompareSelectionChanged();

    [RelayCommand] private void Recall()        => _panel.RecallPreset(this, push: false);
    [RelayCommand] private void RecallAndPush() => _panel.RecallPreset(this, push: true);
    [RelayCommand] private void Duplicate()     => _panel.DuplicatePreset(this);
    [RelayCommand] private void Delete()        => _panel.DeletePreset(this);
    [RelayCommand] private void CopyAsCnl()     => _panel.CopyPresetAsCnl(this);

    [RelayCommand]
    private void Rename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    /// <summary>Enter, or the box losing focus.</summary>
    public void CommitRename()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        _panel.RenamePreset(this, RenameText);
    }

    /// <summary>Esc.</summary>
    public void CancelRename()
    {
        IsRenaming = false;
        RenameText = Name;
    }
}
