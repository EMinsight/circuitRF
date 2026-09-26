// brief-em3d-44 R-em3d44-1 — ONE snap distance for every editor: how near, in screen pixels, a feature must be
// to attract the cursor. In SCREEN pixels, not model units, so a snap feels the same at every zoom
// (R-snp-15). The layout canvas and the 3D pane both read it; changing it changes both.

namespace CircuitRF.Render;

public static class GeometrySnap
{
    /// <summary>The snap radius, device-independent pixels.</summary>
    public const float RadiusPixels = 8;
}
