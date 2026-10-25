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
    /// A round pad's capacitance to a plane, Kirchhoff's disc over a plane:
    /// <c>C = ε₀εᵣπr²/h + ε₀εᵣr·[ln(16πr/h) − 1]</c>, with <c>r</c> the pad's radius and <c>h</c> the dielectric
    /// between it and the plane (G. Kirchhoff, <i>Gesammelte Abhandlungen</i>, 1882).
    ///
    /// <para>The first term is the parallel-plate capacitance of the disc, exact as <c>r/h → ∞</c>; the second is
    /// the rim's fringe. A 1.5 mm pad 71 µm over εᵣ 4.4 is 1.12 pF here, of which 0.97 pF is the plates, and an
    /// axisymmetric field solve of that pad on the ten-layer board gives 1.12 pF; 140 µm over it, 0.63 pF against the
    /// solve's 0.57 (brief-dfr27-1, <c>src/Core/RESOLVED.md</c>). The rim term is held at zero where the asymptote
    /// would turn negative (<c>r/h</c> below 0.054, a pad far from its plane). Zero for a pad with no plane to face
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
        return Epsilon0 * epsR * (Math.PI * r * r / h + r * Math.Max(0.0, Math.Log(16 * Math.PI * r / h) - 1));
    }

    /// <summary>
    /// A round pad's capacitance on a side where the nearest plane has a round hole under it (brief-dfr27-1
    /// R-dfr27-1-1): the pad faces that plane over the annulus outside the hole and the next plane through it.
    /// <c>C = C₁ − s·(C₁ − C₂)</c>, with <c>C₁</c> the pad's <see cref="PadCapacitance"/> to the cleared plane and
    /// <c>C₂</c> to the plane beyond, and <c>s</c> the share of the pad the hole takes.
    ///
    /// <para><b>The share</b> is the area weight <c>(c/r)²</c> of a hole whose edge is pulled in by a strip
    /// <c>δ</c>: the pad under the hole's rim still couples to the rim, so a hole of radius <c>c</c> takes the area
    /// of one of <c>c − δ</c>. <c>δ = (h₁/π)·[(1+g)ln(1+g) − g·ln g] + t/2</c>, with <c>h₁</c> the dielectric to the
    /// cleared plane, <c>g</c> the dielectric beyond it over <c>h₁</c> (at most 10), and <c>t</c> the plane's
    /// copper. Where the parabola's decay length falls to <c>λ = 1.6δ</c>, <c>s</c> continues as
    /// <c>1 − q·exp(−x/λ)</c>, matching value and slope, so a hole wider than the pad still holds part of the rim's
    /// coupling and joins <c>C₂</c> smoothly — the uncut-pad value is reached only as the hole grows past the pad.</para>
    ///
    /// <para><b>Calibrated against a field solve</b>, as <see cref="InnerPadCapacitance"/> is: an axisymmetric
    /// finite-volume solve of a pad 0.8 to 2 mm across, cleared planes 69 to 300 µm away and the next plane 69 to
    /// 460 µm beyond, holes from 0 to 3 pad diameters (72 cases); with the solve's own <c>C₁</c> and <c>C₂</c> the
    /// share is within 3 %. Non-increasing in <paramref name="hole"/> by construction.</para>
    /// </summary>
    /// <param name="diameter">Pad diameter, m.</param>
    /// <param name="hole">The hole's diameter in the cleared plane, m.</param>
    /// <param name="hNear">Dielectric thickness to the cleared plane, m.</param>
    /// <param name="epsNear">Its permittivity.</param>
    /// <param name="tNear">The cleared plane's copper thickness, m.</param>
    /// <param name="hFar">Dielectric thickness to the plane beyond, m (zero: there is none).</param>
    /// <param name="epsFar">Its permittivity, in series.</param>
    public static double ClearedPadCapacitance(double diameter, double hole, double hNear, double epsNear, double tNear,
                                               double hFar, double epsFar)
    {
        double c1 = PadCapacitance(diameter, hNear, epsNear);
        if (!(hole > 0) || c1 <= 0) return c1;
        double c2 = Math.Min(c1, PadCapacitance(diameter, hFar, epsFar));
        return c1 - ClearedShare(diameter / 2, hole / 2, hNear, tNear, hFar) * (c1 - c2);
    }

    /// <summary>The share <c>s</c> of <see cref="ClearedPadCapacitance"/>: 0 with no hole, rising to 1 as the hole
    /// outgrows the pad.</summary>
    public static double ClearedShare(double r, double c, double hNear, double tNear, double hFar)
    {
        if (!(c > 0) || !(r > 0)) return 0.0;
        double g = hNear > 0 && hFar > hNear ? Math.Min((hFar - hNear) / hNear, 10.0) : 10.0;
        double delta = Math.Max(hNear, 0) / Math.PI * ((1 + g) * Math.Log(1 + g) - g * Math.Log(g)) + Math.Max(tNear, 0) / 2;
        double lambda = 1.6 * delta, ce = Math.Max(0.0, c - delta);
        if (!(lambda > 0)) return Math.Min(1.0, ce * ce / (r * r));
        double cs = Math.Sqrt(lambda * lambda + r * r) - lambda;   // where (r² − c²)/2c, the parabola's decay length, is λ
        if (ce <= cs) return ce * ce / (r * r);
        return 1 - (1 - cs * cs / (r * r)) * Math.Exp(-(ce - cs) / lambda);
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

    /// <summary>
    /// What an unused pad on the via adds where the via passes a plane through its antipad (brief-dfr26-8
    /// R-dfr26-8-1): the pad's rim term less the bare barrel's, which the pad replaces. A conductor of radius
    /// <c>r</c> in a clearance of radius <c>b</c> in a plane <c>t</c> thick couples to it as a coaxial line
    /// <c>t + λ</c> long, <c>C = 2πε₀εᵣ·(t + λ)/ln(b/r)</c>, where the fringe length <c>λ = k_r·r + k_g·(b − r)</c>
    /// carries the field that leaves the conductor's faces for the plane's edge and the antipad edges of the planes
    /// above and below. Nothing is parallel plate: the planes either side are cleared to the same antipad.
    ///
    /// <para><b>Calibrated, not derived.</b> The four constants are a fit to an axisymmetric finite-difference
    /// solve of a via through planes 69 to 460 µm apart, pads 0.45 to 0.75 mm in antipads 0.8 to 1.2 mm, drills 0.2
    /// to 0.4 mm (60 cases, 7 % rms, 15 % worst). The fringe is set by the antipad, not the dielectric: from 69 to
    /// 460 µm a neighbouring plane moved the unscreened term by 15 %, which the fit leaves out. On the ten-layer
    /// board of round 26 (0.6 mm pads in 0.94 mm antipads on eight conductors) the solve gives 0.19 pF for the
    /// eight; <see cref="InnerPadScreen"/> holds the part of that a neighbouring pad takes. Zero when the pad is no
    /// wider than the drill or no narrower than its antipad (it would touch its plane).</para>
    /// </summary>
    /// <param name="padDiameter">The unused pad's diameter, m.</param>
    /// <param name="antipadDiameter">The antipad in its plane, m.</param>
    /// <param name="drill">The barrel's diameter, m.</param>
    /// <param name="thickness">The plane's (and pad's) copper thickness, m.</param>
    /// <param name="epsR">The dielectric either side of it, thickness-weighted.</param>
    public static double InnerPadCapacitance(double padDiameter, double antipadDiameter, double drill, double thickness, double epsR)
    {
        if (!(drill > 0) || !(padDiameter > drill) || !(antipadDiameter > padDiameter)) return 0.0;
        double b = antipadDiameter / 2, eps = Epsilon0 * Math.Max(epsR, 1.0), t = Math.Max(thickness, 0.0);
        double Rim(double r, double kr, double kg) => 2 * Math.PI * eps * (t + kr * r + kg * (b - r)) / Math.Log(b / r);
        return Math.Max(0.0, Rim(padDiameter / 2, 0.2, 1.0) - Rim(drill / 2, 0.19, 1.2));
    }

    /// <summary>
    /// The fraction of one side's half of <see cref="InnerPadCapacitance"/> a pad keeps when the conductor next to
    /// it on that side carries a pad of the same via, <paramref name="gap"/> away through the dielectric:
    /// <c>1 − exp(−z / (0.72·(b − r)))</c>. The two pads share the field round the antipad edges between them, so
    /// the nearer the less each adds — at 69 µm a pad of 0.6 mm in 0.94 mm keeps 46 % of that side, at 460 µm 97 %.
    /// From the same solve as <see cref="InnerPadCapacitance"/>.
    /// </summary>
    public static double InnerPadScreen(double gap, double padDiameter, double antipadDiameter)
    {
        double ring = (antipadDiameter - padDiameter) / 2;
        return gap > 0 && ring > 0 ? 1 - Math.Exp(-gap / (0.72 * ring)) : 0.0;
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
