// File ▸ Export ▸ glTF… from any 3D view — a .c3d editor's or a setup's Show 3D (brief-em3d-111 R-em3d111-3b). What the view
// itself holds: the camera and the plots drawn on the model's surfaces. Every rule about what is IN the file is GltfExport's.

using CircuitRF.Render.Scene3D.Export;

namespace CircuitRF.Ui.Viewer3D;

public sealed partial class Viewer3DViewModel
{
    /// <summary>The scene as this view draws it, with no document behind it — a setup's Show 3D has none.</summary>
    public GltfExportSource GltfSource(string name) => new(Scene, [.. View.Visible], null, null, name);

    /// <summary>The view's camera at the view's aspect.</summary>
    public GltfCamera GltfCamera() => new(View.Camera, Aspect);

    /// <summary>R-em3d111-2a — each drawn Surfaces or Faces plot as the view colours it now: its triangles, its range, the phase (an
    /// animated quantity's), and the objects it stands in for. A ClipPlane plot is a section through the volume and is not offered.</summary>
    public IReadOnlyList<GltfField> GltfFields()
    {
        double phase = FieldPhaseDegrees * Math.PI / 180;
        return [.. FieldLayers
            .Where(l => l.Name is not null && !l.IsClipPlanePlot && l.Quantity is not null && l.Scale is not null && l.Geometry.Vertices.Length > 0)
            .Select(l => new GltfField(l.Name!, l.Geometry.Vertices, l.Quantity!, l.Scale!, l.Quantity!.Animated ? phase : 0, [.. l.Covered]))];
    }
}
