// Whether a 3D design opens in the realistic view: per user, in AppPreferences, never in the .c3d. Whether a machine can
// afford the realistic view is a property of its GPU, not of the design, and a .c3d from someone with a faster machine must
// not switch a slower one into it. This supersedes overview D5's "off on every open" as the DEFAULT only — the toggle itself
// is still view state, still marks nothing dirty, and is still not written anywhere when it is flipped. The shape of
// Snap3DPreference, test seam included — without it a test that opens a .c3d reads the developer's real preferences file.

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.ThreeD;

public static class Realistic3DPreference
{
    internal static bool? TestOverrideStore;
    internal static bool TestOverrideActive;

    /// <summary>Settings ▸ General ▸ 3D Designs ▸ Open in realistic view. Off by default.</summary>
    public static bool OnOpen
    {
        get => TestOverrideActive ? TestOverrideStore ?? false : AppPreferencesIo.Load().View3DRealisticOnOpen ?? false;
        set
        {
            if (TestOverrideActive) { TestOverrideStore = value; return; }
            AppPreferencesIo.Update(p => p.View3DRealisticOnOpen = value);
        }
    }
}
