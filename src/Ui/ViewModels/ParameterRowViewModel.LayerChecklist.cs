using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One conductor in a layer checklist (brief-dfr27-1 R-dfr27-1-2).</summary>
public sealed class LayerCheckItem : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    internal LayerCheckItem(string name, bool missing, Action changed)
    {
        Name = name;
        IsMissing = missing;
        _changed = changed;
    }

    public string Name { get; }

    /// <summary>The row names it, but it is not a conductor of the technology: shown checked and flagged.</summary>
    public bool IsMissing { get; }

    public string Label => IsMissing ? $"{Name} — not a conductor" : Name;

    public bool IsChecked
    {
        get => _isChecked;
        set { if (SetProperty(ref _isChecked, value)) _changed(); }
    }

    /// <summary>Sets the box from the model without committing anything.</summary>
    internal void Show(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }
}

public sealed partial class ParameterRowViewModel
{
    /// <summary>
    /// A parameter naming several conductors, comma-separated — a test pad's <c>ClearLayers</c>
    /// (<see cref="ComponentTypeRegistry.IsLayerChecklistParameter"/>). The view shows a drop-down of the technology's
    /// conductors with a check box each instead of a text box; ticking writes the same text the row always held.
    /// </summary>
    public bool IsLayerChecklistParam { get; private set; }

    /// <summary>The check boxes: every conductor of the technology in stackup order, then any name the row holds that
    /// is not one. The list is replaced only when that set of names changes — a refresh runs on every row after
    /// every edit, and swapping the list under an open drop-down would close it.</summary>
    public IReadOnlyList<LayerCheckItem> LayerCheckItems { get; private set; } = [];

    /// <summary>What the drop-down's button reads: the names, or "(None)".</summary>
    public string LayerChecklistSummary => StagedExpression.Trim() is { Length: > 0 } t ? t : "(None)";

    /// <summary>The names that are not conductors of the technology, as the layer picker's own warning says it.</summary>
    public string LayerChecklistMissingWarning
    {
        get
        {
            var missing = LayerCheckItems.Where(i => i.IsMissing).Select(i => $"\"{i.Name}\"").ToList();
            if (missing.Count == 0) return "";
            return $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is not a conductor" : "are not conductors")} " +
                   "in the resolved technology — ignored. Untick to remove.";
        }
    }

    public bool HasLayerChecklistMissingWarning => LayerChecklistMissingWarning.Length > 0;

    /// <summary>The button's tooltip: why the row is greyed, else the warning, else nothing.</summary>
    public string? LayerChecklistTooltip => DisabledReason ?? (HasLayerChecklistMissingWarning ? LayerChecklistMissingWarning : null);

    private void InitLayerChecklist()
    {
        IsLayerChecklistParam = ComponentTypeRegistry.IsLayerChecklistParameter(_ownerSymbol, _param.Name);
        if (IsLayerChecklistParam) RecomputeLayerChecklist();
    }

    /// <summary>Re-reads the technology's conductors and ticks the ones the staged text names.</summary>
    private void RecomputeLayerChecklist()
    {
        var conductors = SchematicTechnology.Of(_schematicVm.EditModel)?.Stackup.Layers
                             .Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name).ToList() ?? [];
        var named = TestPadPCell.ParseClearLayers(StagedExpression);
        bool Named(string n) => named.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
        var missing = named.Where(n => !conductors.Any(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase))).ToList();

        var shape = conductors.Select(c => (c, false)).Concat(missing.Select(m => (m, true))).ToList();
        if (!shape.SequenceEqual(LayerCheckItems.Select(i => (i.Name, i.IsMissing))))
        {
            LayerCheckItems = [.. shape.Select(s => new LayerCheckItem(s.Item1, s.Item2, OnLayerCheckChanged))];
            OnPropertyChanged(nameof(LayerCheckItems));
        }
        foreach (var item in LayerCheckItems) item.Show(Named(item.Name));
        OnPropertyChanged(nameof(LayerChecklistSummary));
        OnPropertyChanged(nameof(LayerChecklistMissingWarning));
        OnPropertyChanged(nameof(HasLayerChecklistMissingWarning));
        OnPropertyChanged(nameof(LayerChecklistTooltip));
    }

    /// <summary>A box was ticked or unticked: write the ticked names, conductors in stackup order and then any
    /// unknown name still ticked, and commit. Every box unticked writes empty — no cut-outs.</summary>
    private void OnLayerCheckChanged()
    {
        if (_isRefreshing || !ExpressionEnabled) return;
        StagedExpression = string.Join(", ", LayerCheckItems.Where(i => i.IsChecked).Select(i => i.Name));
        CommitExpression();
        OnPropertyChanged(nameof(LayerChecklistSummary));
    }
}
