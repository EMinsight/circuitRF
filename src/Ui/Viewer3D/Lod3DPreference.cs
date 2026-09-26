// brief-em3d-48 R-em3d48-3c — where the 3D view's triangle budget is STORED: per user, in AppPreferences, set from
// 3D ▸ Triangle Budget…. The shape of Snap3DPreference, test seam included, so a UI test never writes the developer's
// real preferences file.

using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Viewer3D;

public static class Lod3DPreference
{
    internal static long? TestOverrideStore;
    internal static bool TestOverrideActive;

    /// <summary>The smallest budget accepted: below it a view of one ordinary child would be boxes.</summary>
    public const long Minimum = 10_000;

    /// <summary>The budget, in triangles a frame; the default is <see cref="Scene3DFramePlan.DefaultTriangleBudget"/>.</summary>
    public static long Budget
    {
        get
        {
            if (TestOverrideActive) return TestOverrideStore ?? Scene3DFramePlan.DefaultTriangleBudget;
            return AppPreferencesIo.Load().View3DTriangleBudget is { } b && b >= Minimum ? b : Scene3DFramePlan.DefaultTriangleBudget;
        }
        set
        {
            long v = Math.Max(Minimum, value);
            if (TestOverrideActive) { TestOverrideStore = v; return; }
            AppPreferencesIo.Update(p => p.View3DTriangleBudget = v == Scene3DFramePlan.DefaultTriangleBudget ? null : v);
        }
    }
}
