// brief-em3d-90 — the tree's RECORD rows in the view: a face boundary's tint (a thermal setup's, or an EM face boundary's) is
// what a click on it selects, and it selects its row; its menu and Delete act on the boundary, not on the face under it. The
// face under it is one B away (Viewer3DViewModel.Cycle), or its row's Select Face.
//
// A TINT IS A RECORD, NOT AN OBJECT: it has no name to rename, no material, no Hidden of its own in any file. What the canvas
// offers an object (Rename, Material, Isolate) is therefore not offered for it.

using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The tints the view has selected (Object or Face mode), each with its boundary's face as the document spells it
    /// (<c>die/zmin</c>) and whether it is a thermal setup's.</summary>
    private List<(Scene3DObject Tint, string Face, bool Thermal)> SelectedTints()
    {
        var list = new List<(Scene3DObject, string, bool)>();
        foreach (var o in Viewer.SelectedObjects())
        {
            if (!o.Tint || !o.Name.StartsWith(Scene3DBuilder.FaceTintPrefix, StringComparison.Ordinal)) return [];
            string rest = o.Name[Scene3DBuilder.FaceTintPrefix.Length..];
            bool thermal = rest.StartsWith(ThermalTintPrefix, StringComparison.Ordinal);
            list.Add((o, thermal ? rest[ThermalTintPrefix.Length..] : rest, thermal));
        }
        return list;
    }

    /// <summary>The tree row of a thermal boundary on <paramref name="face"/>, or null.</summary>
    private C3dTreeItem? ThermalBoundaryRow(string face)
        => AllTreeItems().FirstOrDefault(t => t.Kind == ThermalBoundaryKindName && t.Name == ThermalTintPrefix + face);

    /// <summary>
    /// The menu of a selected tint: its boundary's, never the face's — Select Face (what B also reaches), Hide (the row's own
    /// tick), Delete (the boundary, from its setup or the document), Properties. Empty when the selection is not all tints.
    /// </summary>
    private List<Viewer3DMenuItem> TintMenuItems()
    {
        var tints = SelectedTints();
        if (tints.Count == 0) return [];
        var items = new List<Viewer3DMenuItem>();
        if (tints.Count == 1)
        {
            string face = tints[0].Face;
            items.Add(new Viewer3DMenuItem("Select Face", () => Report(SelectBoundaryFace(face)),
                Tip: "The face this boundary lies on, in Face mode (B from the boundary reaches it too)."));
        }
        var rows = tints.Select(t => t.Thermal ? ThermalBoundaryRow(t.Face) : AllTreeItems().FirstOrDefault(r => r.Kind == EmBoundaryKind && r.Name == t.Tint.Name))
                        .OfType<C3dTreeItem>().ToList();
        if (rows.Count > 0)
            items.Add(new Viewer3DMenuItem("Hide", () => SetRowsVisible(rows, false, rows.Count == 1 ? $"Hide {rows[0].Name}" : $"Hide {rows.Count} boundaries"),
                Tip: "In this view only: nothing is saved. The boundary still applies to a run."));
        items.Add(Viewer3DMenuItem.Separator);
        items.Add(new Viewer3DMenuItem(tints.Count == 1 ? "Delete Boundary" : "Delete Boundaries", () => DeleteTints(tints)));
        items.Add(Viewer3DMenuItem.Separator);
        items.Add(new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false)));
        return items;
    }

    /// <summary>Deletes the selected tints' boundaries: a thermal one from the active thermal setup, an EM one from the document.</summary>
    private void DeleteTints(IReadOnlyList<(Scene3DObject Tint, string Face, bool Thermal)> tints)
    {
        Viewer.SetSelection([]);
        BeginGroup(tints.Count == 1 ? $"Delete the boundary on {tints[0].Face}" : $"Delete {tints.Count} boundaries");
        try
        {
            foreach (var (_, face, thermal) in tints)
            {
                if (thermal) { Report(SetThermalBoundary(face, null)); continue; }
                int slash = face.LastIndexOf('/');
                if (slash > 0) Report(SetFaceBoundary(face[..slash], face[(slash + 1)..], null));
            }
        }
        finally { EndGroup(); }
    }

    /// <summary>
    /// brief-em3d-90 R-em3d90-4 — the face a boundary conditions (<c>die/zmin</c>), selected in the view in Face mode: the
    /// Inspector's Select face link and the row's Select Face. Null, or why not.
    /// </summary>
    public string? SelectBoundaryFace(string spelled)
    {
        int slash = spelled.LastIndexOf('/');
        if (slash <= 0 || SceneObject(spelled[..slash]) is not { } o) return $"The face '{spelled}' is not in the model as it is drawn now.";
        string name = spelled[(slash + 1)..];
        int face = -1;
        for (int k = 0; k < o.FaceNames.Count; k++) if (o.FaceNames[k] == name) { face = k; break; }
        if (face < 0) return $"'{o.Name}' has no face named '{name}'.";
        Viewer.SelectMode = Scene3DSelectMode.Face;
        Viewer.SetSelection([Scene3DItem.OfFace(o.Id, face)]);
        return null;
    }
}
