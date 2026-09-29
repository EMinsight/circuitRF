// brief-em3d-93 R-em3d93-4 — the Inspector's Model row: one check box for an object, an instance, a group selected whole
// (every member at every depth), a multi-selection, the selected ports, or a heat source. Members that differ show it
// indeterminate; a click writes every member through the editor's one function for that kind — one undo entry.

using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    [ObservableProperty] private bool _hasModel;

    /// <summary>True when every member is modelled, false when none is, null (indeterminate) when they differ.</summary>
    [ObservableProperty] private bool? _modelChecked;

    /// <summary>The row's tooltip: the editor's, or a port's or a heat source's own sentence.</summary>
    [ObservableProperty] private string _modelTip = C3dEditorViewModel.ModelTip;

    /// <summary>What a click writes.</summary>
    private Action<bool>? _modelWrite;

    private void ClearModel()
    {
        HasModel = false;
        ModelChecked = true;
        ModelTip = C3dEditorViewModel.ModelTip;
        _modelWrite = null;
    }

    /// <summary>The row for <paramref name="objects"/> and <paramref name="instances"/> (a polyline and an operand take none),
    /// named <paramref name="label"/> in the undo entry.</summary>
    private void LoadModel(IEnumerable<int> objects, IEnumerable<int> instances, string label)
    {
        var doc = editor.Document;
        var objs = objects.Where(i => i >= 0 && i < doc.Objects.Count && doc.Objects[i] is not C3dPolyline && !C3dEditorViewModel.IsOperandIndex(i))
                          .Distinct().ToList();
        var insts = instances.Where(i => i >= 0 && i < doc.Instances.Count).Distinct().ToList();
        if (objs.Count + insts.Count == 0) return;
        Show(editor.ModelOf(objs, insts), C3dEditorViewModel.ModelTip,
             model => editor.SetModel(objs, insts, model, $"{(model ? "Model" : "Leave out")} {label}"));
    }

    /// <summary>The row for the selected ports.</summary>
    private void LoadPortModel(IReadOnlyList<C3dPort> ports)
    {
        if (ports.Count == 0) return;
        var values = ports.Select(p => p.Model).Distinct().ToList();
        Show(values is [var one] ? one : null,
             "Unticked, the port is left out of every run — no excitation, no sheet, no lumped element: the gap is open, not " +
             "terminated — and the result's ports are the others, renumbered from 1. Its placement, Z0 and path are kept.",
             model => editor.SetPortsModel(ports, model));
    }

    /// <summary>brief-em3d-93 D1 — the row for a heat source.</summary>
    private void LoadHeatSourceModel(C3dHeatSource h)
        => Show(h.Model, "Unticked, the source is kept, drawn and editable, and puts no power in any thermal run.",
                model => editor.SetHeatSourceModel(h.Name, model));

    private void Show(bool? value, string tip, Action<bool> write)
    {
        HasModel = true;
        ModelChecked = value;
        ModelTip = tip;
        _modelWrite = write;
    }

    partial void OnModelCheckedChanged(bool? value)
    {
        if (_loading || !HasModel || _modelWrite is not { } write) return;
        // The box is two-state to a click (IsThreeState false): from indeterminate it moves to a value, and every member takes it.
        write(value ?? true);
    }
}
