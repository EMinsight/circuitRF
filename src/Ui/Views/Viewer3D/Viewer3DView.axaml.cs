using Avalonia.Controls;

namespace CircuitRF.Ui.Views.Viewer3D;

/// <summary>
/// brief-em3d-28 — the 3D view's shell. 3D editor bugs round 5: it hosts <see cref="ThreeD.C3dEditorView"/> on the
/// document's view-only editor model; the theme, the picture export, the context menu and the tree's reveal are that
/// view's.
/// </summary>
public partial class Viewer3DView : UserControl
{
    public Viewer3DView() => InitializeComponent();
}
