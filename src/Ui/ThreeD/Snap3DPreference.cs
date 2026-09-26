// brief-em3d-44 R-em3d44-5 — where the 3D snap's switches are STORED: per user, in AppPreferences, never in
// the .c3d. The shape of EmSolveCorePreference, test seam included — without it a UI test that flips a
// toggle writes the developer's real preferences file.

using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.ThreeD;

public static class Snap3DPreference
{
    internal static (bool Enabled, Snap3DKinds Kinds)? TestOverrideStore;
    internal static bool TestOverrideActive;

    /// <summary>The master switch and the kinds; the default is on, every kind.</summary>
    public static (bool Enabled, Snap3DKinds Kinds) Preferred
    {
        get
        {
            if (TestOverrideActive) return TestOverrideStore ?? (true, Snap3DKinds.All);
            var p = AppPreferencesIo.Load();
            return (p.Snap3DEnabled ?? true, p.Snap3DKinds is { } k ? (Snap3DKinds)k & Snap3DKinds.All : Snap3DKinds.All);
        }
        set
        {
            if (TestOverrideActive) { TestOverrideStore = value; return; }
            AppPreferencesIo.Update(p => { p.Snap3DEnabled = value.Enabled; p.Snap3DKinds = (int)value.Kinds; });
        }
    }
}
