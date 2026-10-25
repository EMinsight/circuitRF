namespace CircuitRF.Core.Devices;

/// <summary>
/// Every closed form the <c>TESTPAD</c> component uses, in one place, each with its source
/// (brief-dfr26-7 R-dfr26-7-4). SI throughout: metres, farads, henries, hertz.
///
/// <para><b>One implementation.</b> <see cref="TestPadModel"/> is built from these, and the layout's trace
/// impedance tool calls the same functions for a round pad it finds hanging off a line (R-dfr26-7-5) — it
/// does not carry a copy. A value the two disagree on is therefore a difference in the GEOMETRY each was
/// handed, never in the arithmetic.</para>
///
/// <para><b>Where these are valid.</b> A round pad facing a plane much wider than itself, through a
/// dielectric that is thin against the wavelength, and a via barrel short against it. They are an
/// estimate; an EM setup of the pad, its via and the planes it passes is the reference.</para>
/// </summary>
public static class TestPadFormulas
{
    /// <summary>The permittivity of free space, F/m.</summary>
    public const double Epsilon0 = 8.8541878128e-12;

    /// <summary>
    /// A round pad's capacitance to a plane, as parallel plates plus a rim fringe:
    /// <c>C = ε₀εᵣπr²/h + ε₀εᵣ·2πr·ln(1 + 2r/h)</c>, with <c>r</c> the pad's radius and <c>h</c> the
    /// dielectric between it and the plane.
    ///
    /// <para>The first term is the parallel-plate capacitance of the disc, exact as <c>r/h → ∞</c>. The
    /// second charges the rim with a fringe per unit length that grows logarithmically with <c>r/h</c>, as
    /// Kirchhoff's asymptotic result for a disc over a plane does (G. Kirchhoff, <i>Gesammelte
    /// Abhandlungen</i>, 1882: <c>C ≈ ε₀εᵣπr²/h + ε₀εᵣr·[ln(16πr/h) − 1]</c>). This form is the board
    /// designer's hand estimate the component was asked for, and its rim term is the larger of the two: a
    /// 1.5 mm pad 71 µm over εᵣ 4.4 is 1.54 pF here and 1.12 pF by Kirchhoff, of which 0.97 pF is the
    /// plates. The EM setup decides between them. Zero for a pad with no plane to face
    /// (<paramref name="h"/> ≤ 0).</para>
    /// </summary>
    /// <param name="diameter">Pad diameter, m.</param>
    /// <param name="h">Dielectric thickness to the plane, m.</param>
    /// <param name="epsR">Its relative permittivity (the series combination of a layered dielectric —
    /// <see cref="SeriesDielectric"/>).</param>
    public static double PadCapacitance(double diameter, double h, double epsR)
    {
        if (diameter <= 0 || h <= 0) return 0.0;
        double r = diameter / 2;
        return Epsilon0 * epsR * (Math.PI * r * r / h + 2 * Math.PI * r * Math.Log(1 + 2 * r / h));
    }

    /// <summary>
    /// A stack of dielectric layers between two plates as one: <c>h = Σtᵢ</c> and the permittivity for which a
    /// single layer of that thickness has the same parallel-plate capacitance — the layers in series,
    /// <c>εᵣ = h / Σ(tᵢ/εᵣᵢ)</c>. A layer with no thickness contributes nothing.
    /// </summary>
    public static (double H, double EpsR) SeriesDielectric(IEnumerable<(double T, double EpsR)> layers)
    {
        double h = 0, s = 0;
        foreach (var (t, er) in layers)
        {
            if (!(t > 0)) continue;
            h += t;
            s += t / Math.Max(er, 1.0);
        }
        return h > 0 ? (h, h / s) : (0.0, 1.0);
    }

    /// <summary>
    /// A via barrel's capacitance to a plane it passes through a round clearance, as a coaxial line of the
    /// length of barrel the plane owns: <c>C = 2πε₀εᵣ·t / ln(b/a)</c>, with <c>a</c> the barrel's radius and
    /// <c>b</c> the clearance's (D. M. Pozar, <i>Microwave Engineering</i>, 4th ed., Wiley 2012, Table 2.1).
    /// Zero when the clearance is no wider than the barrel, which no fabricated via has.
    /// </summary>
    /// <param name="length">The barrel length the plane owns, m.</param>
    /// <param name="epsR">The dielectric round it.</param>
    /// <param name="drill">Barrel (drill) diameter, m.</param>
    /// <param name="clearance">Clearance diameter in the plane, m.</param>
    public static double BarrelCapacitance(double length, double epsR, double drill, double clearance)
    {
        if (length <= 0 || drill <= 0 || clearance <= drill) return 0.0;
        return 2 * Math.PI * Epsilon0 * Math.Max(epsR, 1.0) * length / Math.Log(clearance / drill);
    }

    /// <summary>The via's inductance over its length, Goldfarb and Pucel — the same form a placed <c>VIA</c>
    /// uses (<see cref="ViaFormulas.GoldfarbPucelInductance"/>). Zero for a side-fed pad, which has no via.</summary>
    public static double ViaInductance(double length, double drill)
        => ViaFormulas.GoldfarbPucelInductance(length, drill / 2);

    /// <summary>Where an inductance and a capacitance in series resonate, <c>1/(2π√(LC))</c>; infinity when
    /// either is zero.</summary>
    public static double SeriesResonanceHz(double inductance, double capacitance)
        => inductance > 0 && capacitance > 0
            ? 1.0 / (2 * Math.PI * Math.Sqrt(inductance * capacitance))
            : double.PositiveInfinity;

    /// <summary>The highest frequency the lumped model is claimed for: the via a twentieth of a wavelength in
    /// <paramref name="epsR"/> (<see cref="ViaFormulas.LumpedValidityFrequency"/>).</summary>
    public static double ValidityFrequency(double viaLength, double epsR)
        => ViaFormulas.LumpedValidityFrequency(viaLength, epsR);
}
