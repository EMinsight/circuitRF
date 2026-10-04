// brief-em3d-111 R-em3d111-3b — what File ▸ Export ▸ glTF… exports from this editor: the scene the view draws, what it shows now
// (hidden-by-session included), the elaboration it was built from, the camera, and the plots it draws on the model's surfaces. Every
// rule about what is IN the file is GltfExport's.

using CircuitRF.Render.Scene3D.Export;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The scene as the view draws it: its objects, which are shown, the elaboration and document behind them.</summary>
    public GltfExportSource GltfSource()
        => new(Viewer.Scene, [.. Viewer.View.Visible], Elaboration, Document, Path.GetFileNameWithoutExtension(FilePath));

    /// <summary>The view's camera at the view's aspect.</summary>
    public GltfCamera GltfCamera() => new(Viewer.View.Camera, Viewer.Aspect);

    /// <summary>R-em3d111-2a — each drawn Surfaces or Faces plot as the view colours it now: its triangles, its range, the phase (an
    /// animated quantity's), and the objects it stands in for. A ClipPlane plot is a section through the volume and is not offered.</summary>
    public IReadOnlyList<GltfField> GltfFields()
    {
        double phase = Viewer.FieldPhaseDegrees * Math.PI / 180;
        return [.. Viewer.FieldLayers
            .Where(l => l.Name is not null && !l.IsClipPlanePlot && l.Quantity is not null && l.Scale is not null && l.Geometry.Vertices.Length > 0)
            .Select(l => new GltfField(l.Name!, l.Geometry.Vertices, l.Quantity!, l.Scale!, l.Quantity!.Animated ? phase : 0, [.. l.Covered]))];
    }
}
