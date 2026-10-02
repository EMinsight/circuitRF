// brief-em3d-99 — the 3D pane's surface updates are drawn by the render that applies them only while Avalonia keeps the two
// internal members SurfaceUpdateFlush reaches. If an upgrade renames either, the flush is skipped without an error and a live
// window resize stutters again on macOS, so this holds them.

using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.Tests.Viewer3D;

public class SurfaceUpdateFlushTests
{
    [Fact]
    public void TheCompositorMembersTheFlushNeedsExist() => Assert.True(SurfaceUpdateFlush.Available);
}
