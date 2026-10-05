// brief-em3d-111 R-em3d111-3b — what File ▸ Export ▸ glTF… exports from this editor: the scene the view draws, what it shows now
// (hidden-by-session included), the elaboration it was built from, the camera, and the plots it draws on the model's surfaces. Every
// rule about what is IN the file is GltfExport's; the camera and the plots are the view's (Viewer3DViewModel.Gltf), shared with a
// setup's Show 3D.

using CircuitRF.Render.Scene3D.Export;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The scene as the view draws it: its objects, which are shown, the elaboration and document behind them.</summary>
    public GltfExportSource GltfSource()
        => new(Viewer.Scene, [.. Viewer.View.Visible], Elaboration, Document, Path.GetFileNameWithoutExtension(FilePath));

    /// <summary>The view's camera at the view's aspect.</summary>
    public GltfCamera GltfCamera() => Viewer.GltfCamera();

    /// <summary>The view's surface plots (Viewer3DViewModel.GltfFields).</summary>
    public IReadOnlyList<GltfField> GltfFields() => Viewer.GltfFields();
}
