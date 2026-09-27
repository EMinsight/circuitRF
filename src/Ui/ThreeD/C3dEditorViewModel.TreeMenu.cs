// 3D editor round 1 — the object tree's context menu. Right-clicking a node selects it (the scene and the Properties
// Inspector follow), then offers what the canvas offers for it, through the SAME functions the canvas menu, the keys and
// the 3D menu call — Duplicate, Delete, Rename, Material, Hide, Isolate, Show All, Properties — never a second copy.
// What a node cannot do is shown disabled with the reason, so the answer is readable (the air box, an instance's part).

using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The tree's menu for <paramref name="item"/>, which the caller has just made the selection.</summary>
    public IReadOnlyList<Viewer3DMenuItem> TreeMenuItems(C3dTreeItem item)
    {
        var items = new List<Viewer3DMenuItem> { new(item.Name, Enabled: false), Viewer3DMenuItem.Separator };
        var scene = SceneObjectsOfNode(item);

        if (item.IsAirBox)
        {
            const string why = "The air box is the active setup's, not the geometry's: it cannot be duplicated or deleted. " +
                               "Its boundaries and padding are in the Properties Inspector.";
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: why));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", Enabled: false, Tip: why));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem(Viewer.ShowBoundaryFaces ? "Hide" : "Show",
                                           () => Viewer.ShowBoundaryFaces = !Viewer.ShowBoundaryFaces));
        }
        else if (item.Kind == "Port")
        {
            var port = Document.Ports.FirstOrDefault(p => CircuitRF.Design.ThreeD.C3dPorts.ProblemName(p.Number) == item.Name);
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: "A port is drawn with the Port tool: each one joins its own pair of conductors."));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", port is null ? null : () => DeletePorts([port]), Enabled: port is not null));
        }
        else if (item.Kind == "Boundary")
        {
            var b = Document.FaceBoundaries.FirstOrDefault(f => Scene3DBuilder.FaceTintPrefix + f.Object + "/" + f.Face == item.Name);
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: "A boundary belongs to one named face."));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", b is null ? null : () => StatusMessage = SetFaceBoundary(b.Object, b.Face, null) ?? "",
                                           Enabled: b is not null));
        }
        else if (item.IsReadOnly)
        {
            const string why = "Part of an instance: it belongs to its own cell. Push into the cell to edit it.";
            items.Add(new Viewer3DMenuItem("Duplicate", Enabled: false, Tip: why));
            items.Add(Viewer3DMenuItem.Separator);
            items.Add(new Viewer3DMenuItem("Delete", Enabled: false, Tip: why));
        }
        else
        {
            bool drawn = scene.Count > 0;
            items.Add(new Viewer3DMenuItem("Duplicate  (Ctrl/Cmd+D)", StartDuplicate, Enabled: drawn,
                Tip: drawn ? "A copy in place, then a Move: click where it goes, or Esc to leave it where it is."
                           : "Not drawn (elaboration refused it — see Properties): there is nothing to place yet."));
            items.Add(Viewer3DMenuItem.Separator);
            if (item.ObjectIndex >= 0)
                items.Add(new Viewer3DMenuItem("Delete", () => DeleteObjects([item.ObjectIndex])));
            else if (item.InstanceIndex >= 0)
                items.Add(new Viewer3DMenuItem("Delete", () => DeleteInstance(item.InstanceIndex)));
            items.Add(Viewer3DMenuItem.Separator);
            if (item.ObjectIndex >= 0)
            {
                int index = item.ObjectIndex;
                items.Add(new Viewer3DMenuItem("Rename…", () => ShowProperties(rename: true)));
                var mats = Materials;
                items.Add(new Viewer3DMenuItem("Material", Enabled: mats.Count > 0,
                    Tip: mats.Count > 0 ? null : "The document's technology defines no materials.",
                    Children: [.. mats.Select(m => new Viewer3DMenuItem(m, () => ChangeObjects($"Material of {Document.Objects[index].Name}", [index], o => o.Material = m)))]));
                bool hidden = Document.Objects[index].Hidden;
                items.Add(new Viewer3DMenuItem(hidden ? "Show" : "Hide",
                    () => ChangeObjects($"{(hidden ? "Show" : "Hide")} {Document.Objects[index].Name}", [index], o => o.Hidden = !hidden)));
            }
            else if (drawn) items.Add(new Viewer3DMenuItem("Hide", () => Viewer.Hide(scene)));
            if (drawn) items.Add(new Viewer3DMenuItem("Isolate", () => Viewer.Isolate(scene)));
        }
        items.Add(new Viewer3DMenuItem("Show All", Viewer.ShowAll));
        items.Add(Viewer3DMenuItem.Separator);
        items.Add(new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false)));
        return items;
    }

    /// <summary>The scene objects a tree node stands for: an instance's parts, or the node's own object.</summary>
    private IReadOnlyList<Scene3DObject> SceneObjectsOfNode(C3dTreeItem item)
    {
        if (item.IsAirBox) return AirBoxFaceObjects();
        var names = item.InstanceIndex >= 0 ? item.Children.Select(c => c.Name) : [item.Name];
        return [.. names.Select(SceneObject).OfType<Scene3DObject>()];
    }

    /// <summary>Deletes the placed instance at <paramref name="index"/>: one undo entry. What it contributed goes with it;
    /// its cell is untouched.</summary>
    public void DeleteInstance(int index)
    {
        if (index < 0 || index >= Document.Instances.Count) return;
        var inst = Document.Instances[index];
        Viewer.SetSelection([]);
        Push(new C3dEdit($"Delete {inst.Name}", [new C3dEditSlot(true, index, CircuitRF.Design.ThreeD.C3dPersistence.SerializeInstance(inst), null)],
                         ApplySlots));
    }
}
