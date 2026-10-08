using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices.Planar;

/// <summary>
/// The conductor-backed coplanar waveguide's closed forms (brief-artsch-1 R-as1-1;
/// docs/design/planar-line-models.md §2). Every capacitance below is in units of 2ε₀ per metre, so
/// Z₀ = (η₀/2)/√(C·C_air) and ε_eff = C/C_air.
///
/// <para><b>Coplanar branch — Ghione and Naldi.</b> The strip's two half-planes, quasi-static, with the
/// coplanar grounds taken as wide: the air above sees the coplanar map K(k)/K(k′), k = a/b; the
/// dielectric below sees the backing plane through k₃ = tanh(πa/2h)/tanh(πb/2h)
/// (G. Ghione and C. U. Naldi, "Coplanar waveguides for MMIC applications: effect of upper shielding,
/// conductor backing, finite-extent ground planes, and line-to-line coupling," IEEE Trans. MTT 35(3),
/// 260–267, 1987). a = W/2 and b = W/2 + G.</para>
///
/// <para><b>Thickness — the slot walls.</b> Each gap's two copper walls add an air capacitance, the
/// 0.7·t/G per slot of Gupta, Garg, Bahl and Bhartia's CPW thickness correction ("Microstrip Lines and
/// Slotlines," 2nd ed., 1996, eq. 7.100), added here to BOTH capacitances so Z₀ and ε_eff come out of one
/// pair. The same book's effective-width form (W + Δ, G − Δ) was implemented and measured against the field
/// solve first: it over-corrects two-fold, 2–7 % low on Z₀ on 18–35 µm copper, where this is within 0.6 %
/// (src/Core/RESOLVED.md, AS-1).</para>
///
/// <para><b>Microstrip branch.</b> As G grows the strip's upper-side field reaches the backing plane
/// around the slots, which the coplanar map drops, so the Ghione–Naldi air capacitance falls BELOW the
/// bare microstrip's — and adding grounded conductors never lowers a conductor's capacitance. So the
/// branch with the larger C_air is the model, C and C_air both taken from it: G-N while the coplanar
/// grounds are close, MLIN's own <see cref="HammerstadJensen.Compute"/> (plus the same slot walls, which
/// vanish as G grows) once they are far, and MLIN exactly in the limit. Taking the larger of C and of C_air
/// separately was measured and rejected: near the crossover it pairs one branch's C with the other's C_air
/// and ε_eff goes 5.8 % wrong.</para>
///
/// <para>Against the field solve (testdata/planar-lines): Z₀ within 2.3 % at every geometry measured,
/// W/H 0.19–2.5, G/H 0.3–6, εr 3–12.9, t/G to 0.18; ε_eff within 0.9 % on the coplanar branch and within
/// 4.1 % on the microstrip branch with thick copper, which is MLIN's own thickness correction (it is as far
/// off at G/H = 6, where the coplanar ground no longer matters).</para>
/// </summary>
public static class GroundedCoplanar
{
    public const string ModelName = "CPWG";

    /// <summary>The slot-wall term's own assumption is a thin wall in a wide slot. Measured to t/G = 0.18.</summary>
    public static readonly ValidityRange TOverGRange = new(0.0, 0.2);
    /// <summary>About twice past the span the field-solve comparison covered (W/H 0.19–2.5, G/H 0.3–6,
    /// testdata/planar-lines). G/H has no upper bound: a far ground is the microstrip branch, which is MLIN.</summary>
    public static readonly ValidityRange WOverHRange = new(0.1, 5.0);
    public static readonly ValidityRange GOverHRange = new(0.1, double.PositiveInfinity);
    /// <summary>Frankel's coefficients are quadratics in ln(W/H), fitted over a range the accessible
    /// sources do not state; a decade either side of W = G and W = H is where this one bounds them.</summary>
    public static readonly ValidityRange DispersionWOverGRange = new(0.1, 10.0);
    public static readonly ValidityRange DispersionWOverHRange = new(0.1, 10.0);

    /// <summary>The two capacitances of one geometry, and whether they are the microstrip branch's.</summary>
    public readonly record struct Capacitances(double C, double CAir, bool Microstrip)
    {
        public double Z0 => PlanarLineLoss.Eta0 / 2 / Math.Sqrt(C * CAir);
        public double Z0Air => PlanarLineLoss.Eta0 / 2 / CAir;
        public double Eeff => C / CAir;
    }

    /// <summary>The Ghione–Naldi pair with the slot walls: (C, C_air) in units of 2ε₀.</summary>
    public static (double C, double CAir) Coplanar(double w, double g, double h, double t, double er)
    {
        double a = w / 2, b = w / 2 + g;
        double qc = EllipticRatio.Of(a / b, Math.Sqrt((b - a) * (b + a)) / b);
        var (k3, k3p) = BackedModuli(a, b, h);
        double qb = EllipticRatio.Of(k3, k3p);
        double walls = SlotWalls(g, t);
        return (qc + er * qb + walls, qc + qb + walls);
    }

    /// <summary>
    /// k₃ = tanh(πa/2h)/tanh(πb/2h) and k₃′. Written in u = e^(−2x) so neither tanh rounds to 1 on a wide
    /// line: tanh x = (1 − u)/(1 + u), and tanh x_b − tanh x_a = 2(u_a − u_b)/((1+u_a)(1+u_b)) exactly,
    /// from which k₃′² = (t_b − t_a)(t_b + t_a)/t_b² keeps its digits.
    /// </summary>
    public static (double K3, double K3p) BackedModuli(double a, double b, double h)
    {
        double xa = Math.PI * a / (2 * h), xb = Math.PI * b / (2 * h);
        double ua = Math.Exp(-2 * xa), ub = Math.Exp(-2 * xb);
        double ta = (1 - ua) / (1 + ua), tb = (1 - ub) / (1 + ub);
        // u_a − u_b = u_a·(1 − e^(−2(x_b − x_a))), with the bracket by expm1 so a narrow gap keeps it.
        double diff = 2 * ua * -ExpM1(-2 * (xb - xa)) / ((1 + ua) * (1 + ub));
        double k3 = ta / tb;
        double k3p = Math.Sqrt(diff * (tb + ta)) / tb;
        return (k3, k3p);
    }

    private static double ExpM1(double x) => Math.Abs(x) < 1e-5 ? x + 0.5 * x * x + x * x * x / 6 : Math.Exp(x) - 1;

    /// <summary>The model's two capacitances: the coplanar branch's, or the microstrip branch's where its
    /// air capacitance is the larger.</summary>
    public static Capacitances Static(double w, double g, double h, double t, double er, MicrostripValidityReporter reporter)
    {
        var (c, ca) = Coplanar(w, g, h, t, er);
        var (zMs, eMs) = HammerstadJensen.Compute(w, h, t, er, reporter);
        double walls = SlotWalls(g, t);
        double caMs = PlanarLineLoss.Eta0 / (2 * zMs * Math.Sqrt(eMs));
        return caMs + walls > ca
            ? new Capacitances(eMs * caMs + walls, caMs + walls, true)
            : new Capacitances(c, ca, false);
    }

    /// <summary>The two slots' wall capacitance, 2 × 0.7·t/G, in units of 2ε₀.</summary>
    public static double SlotWalls(double g, double t) => t > 0 ? 2 * 0.7 * t / g : 0.0;

    /// <summary>
    /// ε_eff at <paramref name="freqHz"/> on the coplanar branch: M. Y. Frankel, S. Gupta, J. A. Valdmanis and
    /// G. A. Mourou, "Terahertz attenuation and dispersion characteristics of coplanar transmission lines,"
    /// IEEE Trans. MTT 39(6), 910–916, 1991 — √ε(f) = √ε₀ + (√εr − √ε₀)/(1 + G·(f/f_TE)^−1.8), with
    /// f_TE = c/(4h√(εr − 1)), G = exp(u·ln(W/G) + v), u = 0.54 − 0.64p + 0.015p², v = 0.43 − 0.86p + 0.54p²,
    /// p = ln(W/h).
    /// </summary>
    public static double FrankelEeff(double freqHz, double w, double g, double h, double er, double eeff0)
    {
        if (freqHz <= 0 || er <= 1) return eeff0;
        double p = Math.Log(w / h);
        double u = 0.54 - 0.64 * p + 0.015 * p * p;
        double v = 0.43 - 0.86 * p + 0.54 * p * p;
        double gf = Math.Exp(u * Math.Log(w / g) + v);
        double fte = MicrostripLoss.SpeedOfLight / (4 * h * Math.Sqrt(er - 1));
        double s0 = Math.Sqrt(eeff0);
        double s = s0 + (Math.Sqrt(er) - s0) / (1 + gf * Math.Pow(freqHz / fte, -1.8));
        return s * s;
    }
}
