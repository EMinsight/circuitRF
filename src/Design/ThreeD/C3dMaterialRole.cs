using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What a material, on its own, says an object made of it is (brief-em3d-53 §4).</summary>
public enum C3dImpliedRole
{
    /// <summary>States σ₂₀ and no εr.</summary>
    Conductor,
    /// <summary>States εr (or an εr tensor) and no σ₂₀.</summary>
    Dielectric,
    /// <summary>Named <c>Air</c>, whatever it states.</summary>
    Air,
    /// <summary>States both — a conductor or a lossy dielectric; the object's Role must say which.</summary>
    Ambiguous,
    /// <summary>States neither.</summary>
    Unstated,
}

/// <summary>
/// The material half of R-em3d42-3e's role rule, in one place (brief-em3d-53 §4): the elaborator asks
/// it when an object states no Role, and the Materials editor's Role column shows its answer — so the
/// column cannot say one thing while Simulate does another.
/// </summary>
public static class C3dMaterialRole
{
    public static C3dImpliedRole Implied(TechMaterial m)
    {
        if (string.Equals(m.Name, Em3dGenerator.AirMaterial, StringComparison.OrdinalIgnoreCase)) return C3dImpliedRole.Air;
        bool hasSigma = m.Sigma20 is > 0;
        bool hasEpsr = m.Epsr is not null || m.EpsrTensor is { Length: 3 };
        return (hasSigma, hasEpsr) switch
        {
            (true, true)  => C3dImpliedRole.Ambiguous,
            (true, false) => C3dImpliedRole.Conductor,
            (false, true) => C3dImpliedRole.Dielectric,
            _             => C3dImpliedRole.Unstated,
        };
    }

    /// <summary>
    /// The colour a material that states none is shown in, by what it implies: the Materials editor's swatch, and the 3D
    /// view's colour for a conductor that has no drawing layer (a flattened layout's copper, a drawn box) — one table, so
    /// the swatch in the list is the colour on the canvas.
    /// </summary>
    public static (byte R, byte G, byte B) ImpliedColour(C3dImpliedRole role) => role switch
    {
        C3dImpliedRole.Conductor  => (0xC8, 0x9A, 0x4E),
        C3dImpliedRole.Dielectric => (0x6E, 0x9E, 0x96),
        C3dImpliedRole.Air        => (0xA8, 0xC8, 0xE8),
        _                         => (0x90, 0x90, 0x90),
    };

    /// <summary>The Role column's reason, one sentence.</summary>
    public static string Reason(C3dImpliedRole role) => role switch
    {
        C3dImpliedRole.Conductor  => "conductor — it states σ₂₀ and no εr",
        C3dImpliedRole.Dielectric => "dielectric — it states εr and no σ₂₀",
        C3dImpliedRole.Air        => "air — a material named Air is air, whatever it states",
        C3dImpliedRole.Ambiguous  => "ambiguous — it states both σ₂₀ and εr, so an object made of it must state a Role",
        _                         => "states nothing — give it σ₂₀ or εr",
    };
}
