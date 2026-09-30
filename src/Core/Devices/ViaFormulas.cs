using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Every closed form the <c>VIA</c> and <c>VIAGND</c> components use, in one place, each with its
/// source (brief-via-component.md R-viac-3). SI throughout: metres, siemens per metre, hertz, henries,
/// ohms, farads.
///
/// <para><b>Where these are valid.</b> An isolated plated barrel whose return current is some distance
/// away, short against the wavelength in the densest dielectric it crosses. A return (stitching) via
/// close by lowers the loop inductance through a mutual term these forms do not have, so next to one
/// they overestimate L; that case is out of scope (the brief's D6).</para>
///
/// <para><b>railRF's barrel inductance is a different quantity, on purpose.</b>
/// <c>PdnInductance</c> in <c>src/Design/Layout/Pdn</c> uses Grover's uniform-current form, with its
/// <c>−3/4</c>, for a mounting loop whose mutual terms it also computes and cancels. That is a DC-to-low-
/// frequency loop figure. Goldfarb and Pucel's form below is the RF partial self-inductance of the
/// barrel alone, and the two are not expected to agree.</para>
/// </summary>
public static class ViaFormulas
{
    private const double Mu0 = MicrostripLoss.Mu0;

    /// <summary>
    /// Barrel inductance, M. E. Goldfarb and R. A. Pucel, "Modeling via hole grounds in microstrip",
    /// <i>IEEE Microwave and Guided Wave Letters</i> 1(6), pp. 135–137, 1991:
    /// <c>L = (µ₀/2π)·[h·ln((h + √(r² + h²))/r) + 1.5·(r − √(r² + h²))]</c>.
    ///
    /// <para>For <c>h ≫ r</c> it tends to <c>(µ₀/2π)·h·[ln(2h/r) − 3/2]</c>. That is Grover's
    /// surface-current partial inductance of a straight tube, <c>(µ₀/2π)·h·[ln(2h/r) − 1]</c>, less
    /// <c>(µ₀/2π)·h/2</c>: the paper's <c>1.5</c> on the second term, where the exact tube form has
    /// <c>1</c>, is where the two part. <see cref="GroverTubeInductance"/> is that exact tube form, kept
    /// here so the relationship is tested rather than asserted.</para>
    /// </summary>
    /// <param name="h">Barrel length, m.</param>
    /// <param name="r">Barrel (drill) radius, m.</param>
    public static double GoldfarbPucelInductance(double h, double r)
    {
        if (h <= 0 || r <= 0) return 0.0;
        double s = Math.Sqrt(r * r + h * h);
        return Mu0 / (2 * Math.PI) * (h * Math.Log((h + s) / r) + 1.5 * (r - s));
    }

    /// <summary>
    /// The exact partial self-inductance of a thin-walled tube carrying its current on its surface,
    /// F. W. Grover, <i>Inductance Calculations</i> (1946), the straight-conductor formula with the
    /// geometric mean distance of a circle: <c>(µ₀/2π)·[h·ln((h + √(r² + h²))/r) − √(r² + h²) + r]</c>,
    /// which tends to <c>(µ₀/2π)·h·[ln(2h/r) − 1]</c>. Not used by the model; the reference its test
    /// holds <see cref="GoldfarbPucelInductance"/> against.
    /// </summary>
    public static double GroverTubeInductance(double h, double r)
    {
        if (h <= 0 || r <= 0) return 0.0;
        double s = Math.Sqrt(r * r + h * h);
        return Mu0 / (2 * Math.PI) * (h * Math.Log((h + s) / r) - s + r);
    }

    /// <summary>
    /// DC resistance of the barrel. A plated barrel is a tube of wall <paramref name="plating"/>:
    /// <c>R_dc = h / (σ·π·(r² − (r − t)²))</c> (Goldfarb and Pucel, as above). A filled barrel is a
    /// solid rod, <c>h / (σ·π·r²)</c>. A wall thicker than the radius is a rod.
    /// </summary>
    public static double DcResistance(double h, double r, double plating, double sigma, bool solid)
    {
        if (h <= 0 || r <= 0 || sigma <= 0) return 0.0;
        double area = solid || plating >= r || plating <= 0
            ? Math.PI * r * r
            : Math.PI * (r * r - (r - plating) * (r - plating));
        return h / (sigma * area);
    }

    /// <summary>
    /// The frequency above which the skin effect starts to raise the barrel's resistance, Goldfarb and
    /// Pucel: <c>f_δ = 1/(π·µ₀·σ·t²)</c>, where the skin depth equals the wall <c>t</c>.
    ///
    /// <para>A solid rod takes the same form with <c>t = r/2</c>. That is the value for which
    /// <see cref="AcResistance"/>'s high-frequency limit is the rod's own surface resistance,
    /// <c>h/(σ·2πr·δ)</c>, while its low-frequency limit stays the rod's <c>R_dc</c>.</para>
    /// </summary>
    public static double SkinOnsetFrequency(double r, double plating, double sigma, bool solid)
    {
        double t = solid || plating >= r || plating <= 0 ? r / 2 : plating;
        return 1.0 / (Math.PI * Mu0 * sigma * t * t);
    }

    /// <summary>
    /// Barrel resistance at <paramref name="freqHz"/>, Goldfarb and Pucel:
    /// <c>R = R_dc·√(1 + f/f_δ)</c>. Exact at both ends — <c>R_dc</c> at DC, the wall's surface resistance
    /// once the skin depth is well inside it — and a smooth interpolation between.
    /// </summary>
    public static double AcResistance(double freqHz, double h, double r, double plating, double sigma, bool solid)
    {
        double rdc = DcResistance(h, r, plating, sigma, solid);
        if (rdc == 0.0 || freqHz <= 0) return rdc;
        return rdc * Math.Sqrt(1.0 + freqHz / SkinOnsetFrequency(r, plating, sigma, solid));
    }

    /// <summary>
    /// Pad-to-plane capacitance, H. Johnson and M. Graham, <i>High-Speed Digital Design: A Handbook of
    /// Black Magic</i> (Prentice Hall, 1993), §7.1:
    /// <c>C ≈ 1.41·εr·T·D₁/(D₂ − D₁)</c> pF with <c>T</c>, <c>D₁</c>, <c>D₂</c> in inches.
    ///
    /// <para><b>An estimate, and the source presents it as one.</b> <c>D₁</c> is the pad,
    /// <c>D₂</c> the clearance (antipad) in the plane, <c>T</c> the dielectric thickness the capacitance
    /// is taken over. A clearance no larger than the pad has no finite value here and returns zero, so a
    /// caller that has not stated one gets no capacitance rather than an infinite one. The component's
    /// help page records how far this lands from a 3D solve on one real stackup.</para>
    /// </summary>
    /// <returns>Farads.</returns>
    public static double JohnsonGrahamCapacitance(double epsR, double thickness, double pad, double antipad)
    {
        if (thickness <= 0 || pad <= 0 || antipad <= pad) return 0.0;
        const double metresPerInch = 0.0254;
        double tIn = thickness / metresPerInch, d1 = pad / metresPerInch, d2 = antipad / metresPerInch;
        return 1.41 * epsR * tIn * d1 / (d2 - d1) * 1e-12;
    }

    /// <summary>
    /// The highest frequency at which a lumped section of length <paramref name="length"/> is still a
    /// fair model: where it is a twentieth of the wavelength in a dielectric of
    /// <paramref name="epsRMax"/>, <c>f = c/(20·ℓ·√εr)</c>.
    /// </summary>
    public static double LumpedValidityFrequency(double length, double epsRMax)
    {
        if (length <= 0) return double.PositiveInfinity;
        return MicrostripLoss.SpeedOfLight / (20.0 * length * Math.Sqrt(Math.Max(epsRMax, 1.0)));
    }
}
