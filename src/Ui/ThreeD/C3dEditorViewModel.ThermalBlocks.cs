// brief-em3d-76 — the editor's side of effective blocks and symmetry planes. A block is a thermal place like a mesh region
// (drawn with the same box gesture, listed, edited in Properties, deleted) that is made DISABLED; its row says what enabling
// it would replace and the tensor it would carry, computed by the lowering a run does. A symmetry plane is declared on the
// face the modelled half was cut on (right-click it ▸ Symmetry Plane), which must lie on the model's own extent.

using System.Globalization;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>A symmetry plane's tree row is named this, then its axis.</summary>
    public const string SymmetryRowPrefix = "symmetry:";

    private static string SymmetryRowName(C3dAxis axis) => SymmetryRowPrefix + axis;

    /// <summary>R-em3d76-2 — what block <paramref name="b"/> replaces and carries when enabled, or why it cannot.</summary>
    public string EffectiveBlockSummary(C3dEffectiveBlock b)
    {
        if (Elaboration is not { Ok: true, Technology: { } tech } e) return "The model does not elaborate, so the block cannot be measured.";
        var low = ThermalEffectiveBlocks.Lower(Document, e, b, tech, int.MaxValue, out string? why);
        return low is null ? why! : $"k_xy {G(low.Mixture.KXy)}, k_z {G(low.Mixture.KZ)} W/(m·K) over {low.Vias} via(s), replacing {low.Removed.Count} solid(s)" +
                                    (b.Enabled ? "" : " — when enabled");
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// R-em3d76-4a — the symmetry plane face <paramref name="face"/> of scene object <paramref name="id"/> would declare: its
    /// normal axis and coordinate, when the face is flat, axis-aligned and on the model's extent; else null with why.
    /// </summary>
    public (C3dSymmetryPlane? Plane, string? Why) SymmetryOfFace(uint id, int face)
    {
        if (Viewer.Scene.Object(id) is not { } o || face < 0) return (null, "There is no face under the cursor.");
        var (_, normal) = Scene3DFaces.AreaAndNormal(Viewer.Scene, o.Id, face);
        if (normal is not { } n) return (null, "A symmetry plane is flat: this face is curved.");
        int axis = Math.Abs(n.X) > 0.999f ? 0 : Math.Abs(n.Y) > 0.999f ? 1 : Math.Abs(n.Z) > 0.999f ? 2 : -1;
        if (axis < 0) return (null, "A symmetry plane is normal to x, y or z: this face is oblique.");
        var tris = Scene3DFaces.Triangles(Viewer.Scene, o.Id, face);
        if (tris.Count == 0) return (null, "The face has no triangles.");
        var p = Viewer.Scene.ToWorld(tris[0].A);
        double at = axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
        if (Elaboration?.Extent() is not { } ext) return (null, "The model does not elaborate.");
        double lo = axis switch { 0 => ext.X0, 1 => ext.Y0, _ => ext.Z0 }, hi = axis switch { 0 => ext.X1, 1 => ext.Y1, _ => ext.Z1 };
        double per = C3dLowering.Metres(1, Document.DbuPerMicron), tol = Math.Max(per, 1e-6 * (hi - lo));
        if (Math.Abs(at - lo) > tol && Math.Abs(at - hi) > tol)
            return (null, "A symmetry plane is the face the modelled half was cut on, so it lies on the model's extent: this face is inside it.");
        long dbu = (long)Math.Round((Math.Abs(at - lo) <= tol ? lo : hi) / per, MidpointRounding.AwayFromZero);
        return (new C3dSymmetryPlane { Axis = (C3dAxis)axis, At = dbu }, null);
    }

    /// <summary>Declares the symmetry plane on a face, or removes it when that plane is already declared. One undo entry.</summary>
    public string? ToggleSymmetryPlane(uint id, int face)
    {
        var (plane, why) = SymmetryOfFace(id, face);
        if (plane is null) return why;
        if (Document.SymmetryPlanes.Any(p => p.Axis == plane.Axis && p.At == plane.At)) { ClearSymmetryPlane(plane.Axis); return null; }
        ChangeRecords($"Symmetry plane {plane.Axis}", d =>
        {
            d.SymmetryPlanes.RemoveAll(p => p.Axis == plane.Axis);
            d.SymmetryPlanes.Add(plane);
        });
        StatusMessage = $"Symmetry plane {plane.Axis}: the modelled part is 1/{1 << Document.SymmetryPlanes.Count} of the device. Its face stays " +
                        "insulated; give sources the modelled part's power, and multiply by SymmetryFactor in a measure for the whole device.";
        return null;
    }

    /// <summary>Removes the symmetry plane normal to <paramref name="axis"/>. One undo entry.</summary>
    public void ClearSymmetryPlane(C3dAxis axis)
        => ChangeRecords($"Remove the symmetry plane {axis}", d => d.SymmetryPlanes.RemoveAll(p => p.Axis == axis));
}
