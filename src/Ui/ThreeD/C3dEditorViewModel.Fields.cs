// brief-em3d-82 R-em3d82-1 / -2 — right-click a face ▸ Plot Field: the EM field shown (|E|, |B|, J_s …) on that face alone,
// beside Plot Temperature and on its pattern — the item is always listed on a face, greyed with its reason when there is no EM
// field to paint (PlotFieldRefusal, the one predicate), and it toggles. A sheet shown in a volume quantity asks WHICH SIDE:
// E's normal component jumps across a sheet that carries charge, so its two sides are two pictures, and neither is picked
// for the user. The painting itself is the viewer's (FieldFacePainter).
//
// brief-em3d-83 — the gesture now edits the DOCUMENT: the face joins the drawn Faces plot, or makes a new one
// (C3dEditorViewModel.FieldPlots.cs PaintFace). What is painted is saved with the file and undone like any record.

using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>Why Plot Field is greyed, or null when it can paint: the active setup is an EM one and its run left fields.
    /// A stale EM result still paints — the banner says so, and the picture is the solver's own geometry (R-em3d49-5b).</summary>
    public string? PlotFieldRefusal()
    {
        if (ActiveSetup is { IsThermal: true })
            return "Plot Field reads an EM run: make an EM setup active (a thermal run's field is Plot Temperature).";
        if (!Viewer.FieldsAvailable || Viewer.IsThermalRun)
            return $"There are no EM fields for '{ActiveSetupName}': run it with fields saved (Simulate ▸ Run; the setup's SaveFieldsGHz).";
        return null;
    }

    /// <summary>Plot Field on face <paramref name="face"/> of scene object <paramref name="objectId"/> (on side
    /// <paramref name="side"/> of a sheet: +1 top, −1 bottom, 0 no side): added to the drawn Faces plot, or taken off it when it
    /// already was — or a new Faces plot with it (brief-em3d-83). One undo entry.</summary>
    public string? PlotFieldOnFace(uint objectId, int face, int side = 0)
    {
        if (PlotFieldRefusal() is { } why) return why;
        if (Viewer.Scene.Object(objectId) is not { } o || face < 0) return "There is no face under the cursor.";
        PaintFace(o, face, side, temperature: false);
        return null;
    }

    private IEnumerable<Viewer3DMenuItem> FieldMenuItems()
    {
        var sel = Viewer.Selection;
        if (Viewer.SelectMode != Scene3DSelectMode.Face || sel.Count != 1 || sel[0].Face < 0 || Viewer.Scene.Object(sel[0].Object) is not { } o ||
            o.Kind is Scene3DKind.Port or Scene3DKind.Boundary || BoxFaceOf(o) is not null)
            yield break;
        uint id = o.Id;
        int fi = sel[0].Face;
        string? why = PlotFieldRefusal();
        int? painted = Viewer.FieldFaceSide(o.Name, fi);
        string check = painted is not null ? "✓ " : "";
        // A boundary quantity (J_s on a PEC sheet) is single-valued: no side to choose.
        if (o.Kind != Scene3DKind.Sheet || Viewer.SelectedFieldQuantity is { OnBoundary: true })
        {
            yield return new Viewer3DMenuItem(check + "Plot Field", () => Report(PlotFieldOnFace(id, fi)), Enabled: why is null,
                Tip: why ?? (painted is not null ? "Take the field off this face." : "The field shown, on this face; again to take it off. Several faces accumulate."));
            yield break;
        }
        Viewer3DMenuItem Side(string header, int side, string toward) => new(
            (painted == side ? "✓ " : "") + header, () => Report(PlotFieldOnFace(id, fi, side)), Enabled: why is null,
            Tip: why ?? (painted == side ? "Take the field off this sheet." : $"The field on the sheet's {toward} side — the tetrahedra there."));
        yield return new Viewer3DMenuItem(check + "Plot Field", Enabled: why is null,
            Tip: why ?? "A sheet's two sides are two pictures: E's normal component jumps across a sheet carrying charge.",
            Children: [Side("Top side", 1, "top (+n, along its own normal)"), Side("Bottom side", -1, "bottom (−n)")]);
    }
}
