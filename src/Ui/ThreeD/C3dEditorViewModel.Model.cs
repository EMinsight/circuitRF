// brief-em3d-93 R-em3d93-4 — the Model switch: keep an object in the drawing and out of the solve.
//
// ONE FUNCTION PER KIND OF RECORD, CALLED BY EVERY GESTURE. The Inspector's Model row, the canvas's and the tree's Model menu
// item all call SetModel (objects and instances — a group's every member at every depth, a multi-selection's every member),
// SetPortsModel or SetHeatSourceModel; each is ONE undo entry and marks the document dirty, exactly as Hidden does. A polyline
// (construction geometry, never modelled) and an operand inside an operation (its result's) are skipped, not refused.
//
// THE VIEW DRAWS IT AS IT IS (owner decision D4): no dashed silhouette, no tint. The tree's grey and the hover's
// "(not modelled)" are the cues; what is left out of a run is the run's notes' to say.

using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The Model row's and menu item's tooltip.</summary>
    public const string ModelTip = "Ticked: in every simulation run. Unticked: drawn and editable, left out of every run — " +
                                   "a quick way to try a run without it, and to put it back.";

    /// <summary>
    /// Writes <paramref name="model"/> onto the objects and instances named, as ONE undo entry. A polyline and an operand
    /// inside an operation are skipped: neither carries a Model of its own. Nothing changed, no entry.
    /// </summary>
    public void SetModel(IReadOnlyList<int> objects, IReadOnlyList<int> instances, bool model, string description)
    {
        var slots = new List<C3dEditSlot>();
        foreach (int i in objects.Where(i => !IsOperandIndex(i) && i >= 0 && i < Document.Objects.Count).Distinct().Order())
        {
            if (Document.Objects[i] is C3dPolyline) continue;
            string before = C3dPersistence.SerializeObject(Document.Objects[i]);
            var copy = C3dPersistence.DeserializeObject(before);
            copy.Model = model;
            string after = C3dPersistence.SerializeObject(copy);
            if (after != before) slots.Add(new C3dEditSlot(false, i, before, after));
        }
        foreach (int i in instances.Where(i => i >= 0 && i < Document.Instances.Count).Distinct().Order())
        {
            string before = C3dPersistence.SerializeInstance(Document.Instances[i]);
            var copy = C3dPersistence.DeserializeInstance(before);
            copy.Model = model;
            string after = C3dPersistence.SerializeInstance(copy);
            if (after != before) slots.Add(new C3dEditSlot(true, i, before, after));
        }
        if (slots.Count > 0) Push(new C3dEdit(description, slots, ApplySlots));
    }

    /// <summary>The members' objects and instances as <see cref="SetModel"/> takes them.</summary>
    private (List<int> Objects, List<int> Instances) ModelTargets(IEnumerable<C3dMemberRef> members)
    {
        var list = members.Distinct().ToList();
        return ([.. list.Where(m => !m.Instance && m.Index < Document.Objects.Count && Document.Objects[m.Index] is not C3dPolyline && !IsOperandIndex(m.Index))
                        .Select(m => m.Index)],
                [.. list.Where(m => m.Instance && m.Index < Document.Instances.Count).Select(m => m.Index)]);
    }

    /// <summary>The members' Model: true when every one is modelled, false when none is, null when they differ (or there is
    /// none to ask).</summary>
    public bool? ModelOf(IReadOnlyList<int> objects, IReadOnlyList<int> instances)
    {
        var values = objects.Select(i => Document.Objects[i].Model).Concat(instances.Select(i => Document.Instances[i].Model)).Distinct().ToList();
        return values is [var one] ? one : null;
    }

    /// <summary>Ports' Model, one undo entry.</summary>
    public void SetPortsModel(IReadOnlyList<C3dPort> ports, bool model)
    {
        var numbers = ports.Select(p => p.Number).ToHashSet();
        string what = ports.Count == 1 ? C3dPorts.Label(ports[0]) : $"{ports.Count} ports";
        ChangeRecords($"{(model ? "Model" : "Leave out")} {what}",
                      d => { foreach (var p in d.Ports.Where(p => numbers.Contains(p.Number))) p.Model = model; });
    }

    /// <summary>A heat source's Model (D1), one undo entry.</summary>
    public void SetHeatSourceModel(string name, bool model)
        => ChangeRecords($"{(model ? "Model" : "Leave out")} {name}",
                         d => { foreach (var h in d.HeatSources.Where(h => h.Name == name)) h.Model = model; });

    /// <summary>The Model item for <paramref name="members"/> — a ✓ when every one is modelled — or null when none carries one.
    /// The canvas's menu and the tree's call it, and it calls <see cref="SetModel"/>.</summary>
    private Viewer3DMenuItem? ModelItem(IEnumerable<C3dMemberRef> members, string label)
    {
        var (objects, instances) = ModelTargets(members);
        if (objects.Count + instances.Count == 0) return null;
        bool on = ModelOf(objects, instances) == true;
        return new Viewer3DMenuItem(on ? "✓ Model" : "Model", () => SetModel(objects, instances, !on, $"{(on ? "Leave out" : "Model")} {label}"),
                                    Tip: ModelTip);
    }

    /// <summary>The ports' Model item, as <see cref="ModelItem"/>.</summary>
    private Viewer3DMenuItem PortModelItem(IReadOnlyList<C3dPort> ports)
    {
        bool on = ports.All(p => p.Model);
        return new Viewer3DMenuItem(on ? "✓ Model" : "Model", () => SetPortsModel(ports, !on),
            Tip: "Unticked, the port is left out of every run: no excitation, no sheet, no lumped element — the gap is open, not " +
                 "terminated — and the result's ports are the others, renumbered from 1. Its placement, Z0 and path are kept.");
    }

    /// <summary>The hover's suffix for a scene object that is not modelled — an object, an instance's part, a wire, a port.</summary>
    private string? NotModelledSuffix(Scene3DObject o)
    {
        if (o.Kind == Scene3DKind.Port)
            return Document.Ports.FirstOrDefault(p => p.Number == o.PortNumber) is { Model: false } ? C3dModelled.Suffix : null;
        return Elaboration is { } e && C3dModelled.IsOff(e, o.Name) ? C3dModelled.Suffix : null;
    }
}
