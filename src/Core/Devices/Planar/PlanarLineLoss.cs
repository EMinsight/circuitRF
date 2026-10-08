using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices.Planar;

/// <summary>
/// The conductor loss shared by CPWG and SLIN (docs/design/planar-line-models.md §4): H. A. Wheeler's
/// incremental-inductance rule ("Formulas for the Skin Effect," Proc. IRE 30(9), 412–424, 1942), applied to
/// each model's own air-filled impedance — the route by which Owyang and Wu derived the coplanar line's
/// conductor loss from its conformal map (IRE Trans. MTT 6, 1958) — with the finite-thickness surface
/// resistance in place of Rs and MLIN's Hammerstad–Bekkadal roughness factor on top.
///
/// <para>α_c = R / (2 Z₀), R = (Rs/µ₀)·∂L/∂n and L = Z₀,air / c, so α_c = Rs·∂Z₀,air/∂n / (2 Z₀ η₀): every
/// conductor surface recedes by dn and the change in the air line's impedance is taken by a central
/// difference of the closed form itself. The caller supplies the receded geometry because only it knows
/// which way each of its dimensions moves when the copper's surface does.</para>
/// </summary>
public static class PlanarLineLoss
{
    /// <summary>The impedance of free space, µ₀·c, with µ₀ = 4π·10⁻⁷ as <see cref="MicrostripLoss.Mu0"/>.
    /// The new models write every "30π" and "60π" of their sources as η₀/4 and η₀/2: the round numbers are
    /// η₀ ≈ 120π, 0.07 % from the constant, which the field-solve references would see.</summary>
    public const double Eta0 = MicrostripLoss.Mu0 * MicrostripLoss.SpeedOfLight;

    /// <summary>The surface recession step, as a fraction of the smallest dimension it moves.</summary>
    public const double RecessionFraction = 1e-4;

    /// <summary>
    /// The surface resistance of a strip <paramref name="tMeters"/> thick carrying current on both faces:
    /// each face sees a slab t/2 thick, Re{Zs·coth(γ t/2)} with Zs = (1+j)/(σδ) and γ = (1+j)/δ
    /// (the surface impedance of a finite plane conductor). It is Rs when t ≫ δ and 2/(σt) — the two
    /// faces in parallel being the DC sheet resistance 1/(σt) — when t ≪ δ. t = 0 is Rs.
    /// </summary>
    public static double SurfaceResistance(double freqHz, double sigmaSPerM, double tMeters)
    {
        double rs = MicrostripLoss.SurfaceResistance(freqHz, sigmaSPerM);
        if (tMeters <= 0) return rs;
        double delta = MicrostripLoss.SkinDepth(freqHz, sigmaSPerM);
        var z = new Complex(1, 1) * (tMeters / (2 * delta));
        return rs * (new Complex(1, 1) / Complex.Tanh(z)).Real;
    }

    /// <summary>
    /// α_c in Np/m from Wheeler's rule. <paramref name="z0AirReceded"/>(dn) is the air line's impedance
    /// with every conductor surface receded by dn (dn may be negative); <paramref name="step"/> is dn.
    /// </summary>
    public static double ConductorLossNpPerM(double freqHz, double sigmaSPerM, double tMeters,
        double roughnessMeters, double z0Ohms, Func<double, double> z0AirReceded, double step)
    {
        double dZdn = (z0AirReceded(step) - z0AirReceded(-step)) / (2 * step);
        double rs = SurfaceResistance(freqHz, sigmaSPerM, tMeters);
        double kr = MicrostripLoss.RoughnessFactor(roughnessMeters, MicrostripLoss.SkinDepth(freqHz, sigmaSPerM));
        return rs * kr * dZdn / (2 * z0Ohms * Eta0);
    }
}
