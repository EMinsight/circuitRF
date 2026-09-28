// brief-em3d-77 R-em3d77-1..-4 — what conductive balance is given, beside a ThermalProblem: the bond wires as chains of
// one-dimensional elements, each conductor region's σ(T), and the DC currents at the ports' contacts. Like ThermalProblem it
// names no material and no file: the lowering in src/Design/Thermal builds it from the mesh's tags and the resolved wires.
//
// A WIRE (R-em3d77-3) is a chain of second-order line elements along its resolved centreline: node 2e and 2e + 2 are element
// e's ends and 2e + 1 its middle, and no element spans a vertex of the centreline, so the chain's length IS the centreline's
// 3D arc length. Its area is πd²/4 whatever its EM section. Its feet and balls touch their pads through CONTACT PATCHES —
// surface tags the mesher embedded in the pads' top faces — with a contact conductance per unit area; everywhere else a
// wire element is coupled to the solid it runs through (a mould compound) by the line-source ("well") conductance, or to an
// ambient by convection, or to nothing (an air cavity).
//
// A CURRENT TERMINAL (R-em3d77-1) is a port: its current enters the conductor behind its positive contact faces and leaves
// through its negative ones, each contact a floating equipotential carrying the prescribed total — never a uniform density.

namespace CircuitRF.Thermal.Electrothermal;

/// <summary>A region's (or a wire's) electrical conductivity, S/m: a constant, or a function of temperature (°C) with its
/// slope. <see cref="At20"/> is what the whole run uses when σ(T) is switched off.</summary>
public sealed class ElectricalConductivity
{
    private ElectricalConductivity(double at20, Func<double, (double Sigma, double Slope)>? ofT)
    {
        if (!(at20 > 0) || !double.IsFinite(at20)) throw new ArgumentOutOfRangeException(nameof(at20));
        At20 = at20;
        OfT = ofT;
    }

    /// <summary>σ at 20 °C.</summary>
    public double At20 { get; }

    /// <summary>σ and dσ/dT at a temperature (°C); null for a constant.</summary>
    public Func<double, (double Sigma, double Slope)>? OfT { get; }

    public bool IsConstant => OfT is null;

    public static ElectricalConductivity Constant(double sigma) => new(sigma, null);

    public static ElectricalConductivity Varying(double at20, Func<double, (double Sigma, double Slope)> ofT)
    {
        ArgumentNullException.ThrowIfNull(ofT);
        return new(at20, ofT);
    }

    /// <summary>A resistivity linear in temperature: ρ = ρ₀[1 + α(T − T₀)], σ = 1/ρ.</summary>
    public static ElectricalConductivity LinearResistivity(double rho0, double alpha, double t0 = 20)
        => new(1 / (rho0 * (1 + alpha * (20 - t0))), T =>
        {
            double d = rho0 * (1 + alpha * (T - t0));
            return (1 / d, -rho0 * alpha / (d * d));
        });

    /// <summary>σ and its slope at <paramref name="tempC"/>; the 20 °C value, slope 0, when <paramref name="ofT"/> is off.</summary>
    public (double Sigma, double Slope) At(double tempC, bool ofT) => ofT && OfT is { } f ? f(tempC) : (At20, 0);
}

/// <summary>
/// R-em3d77-3d — where a wire touches a pad: the triangles of surface tag <paramref name="Tag"/> (a patch the mesher embedded in
/// the pad's top face) against wire nodes <paramref name="FirstNode"/>..<paramref name="LastNode"/>. A patch point takes the
/// wire's value at its projection onto the line through those two nodes (a foot with a stated bond resistance, coupled along
/// its length); with FirstNode == LastNode the whole patch is tied to that one node — a ball, and a PERFECT bond of either
/// kind, whose patch is tied to the heel: a perfect bond makes the heel the pad, and a foot then carries nothing along itself.
/// </summary>
/// <param name="HWm2K">The thermal contact conductance per unit area; null is a perfect bond.</param>
/// <param name="GSm2">The electrical contact conductance per unit area; null is a perfect bond.</param>
/// <param name="HostRegion">The pad's region: what a perfect bond is scaled from.</param>
public sealed record WireContact(int Tag, int FirstNode, int LastNode, int HostRegion, double? HWm2K = null, double? GSm2 = null)
{
    /// <summary>A lumped series resistance per unit area added to both conductances (a ball's own height, say): m²·K/W and Ω·m².</summary>
    public double SeriesThermalM2KW { get; init; }
    public double SeriesElectricalOhmM2 { get; init; }
}

/// <summary>brief-em3d-78 R-em3d78-4a — one harmonic this wire carries: its frequency and its PEAK phasor magnitude in the wire.</summary>
/// <param name="Label">What the result names it by (<c>h2</c>).</param>
public sealed record WireHarmonic(string Label, double FrequencyHz, double PeakA);

/// <summary>R-em3d78-4a — a wire's AC resistance per unit length at a frequency (Hz) and a conductivity (S/m), with its derivative
/// in the conductivity: Ω/m and Ω·m/S. The lowering supplies the exact Bessel solution; this project names no wire physics.</summary>
public delegate (double R, double SigmaSlope) AcResistance(double frequencyHz, double sigma);

/// <summary>R-em3d77-3 — one bond wire, as the solver takes it.</summary>
public sealed class ThermalWire
{
    /// <summary>The wire's elaborated name (<c>w1[3]</c>).</summary>
    public required string Name { get; init; }

    /// <summary>x, y, z per chain node, metres: 2·Elements + 1 nodes, element e from node 2e to 2e + 2 through 2e + 1.</summary>
    public required double[] Points { get; init; }

    /// <summary>The 3D arc length at every node, metres — 0 at the start heel, negative along a start foot.</summary>
    public required double[] S { get; init; }

    /// <summary>πd²/4, m².</summary>
    public required double Area { get; init; }

    /// <summary>The diameter, m: the radius the well conductance and the perimeter the convection are read from.</summary>
    public required double Diameter { get; init; }

    public required ThermalConductivity K { get; init; }
    public required ElectricalConductivity Sigma { get; init; }

    /// <summary>Its feet and balls.</summary>
    public IReadOnlyList<WireContact> Contacts { get; init; } = [];

    /// <summary>Per element, true where it lies on a pad (a foot): such an element touches its pad through its patch only.</summary>
    public bool[]? OnPad { get; init; }

    /// <summary>R-em3d77-3e — heat lost by a span element in AIR: h·πd·(T − <see cref="AmbientC"/>) per unit length; null: none.</summary>
    public double? ConvectionH { get; init; }
    public double AmbientC { get; init; }

    /// <summary>brief-em3d-78 R-em3d78-4 — the RF currents this wire carries, uniform along its span (heel to heel); a foot lying
    /// on its pad takes no RF heat. Empty: none.</summary>
    public IReadOnlyList<WireHarmonic> Harmonics { get; init; } = [];

    /// <summary>R′_ac(f, σ); required when <see cref="Harmonics"/> is not empty.</summary>
    public AcResistance? AcResistance { get; init; }

    /// <summary>
    /// R-em3d78-4a/-4b — the RF heat per unit length at <paramref name="tempC"/>, q′ = Σₙ ½|Iₙ|²R′_ac(fₙ, σ(T)), and dq′/dT through
    /// dσ/dT (zero with σ(T) off). <paramref name="perHarmonic"/>, when given, receives each harmonic's part of q′.
    /// </summary>
    public (double Q, double Slope) RfHeat(double tempC, bool sigmaOfT, Span<double> perHarmonic = default)
    {
        if (Harmonics.Count == 0) return (0, 0);
        var r = AcResistance ?? throw new InvalidOperationException($"Wire '{Name}' carries harmonics and states no AC resistance.");
        var (sg, ds) = Sigma.At(tempC, sigmaOfT);
        double q = 0, dq = 0;
        for (int n = 0; n < Harmonics.Count; n++)
        {
            var h = Harmonics[n];
            var (rac, slope) = r(h.FrequencyHz, sg);
            double half = 0.5 * h.PeakA * h.PeakA;
            q += half * rac;
            dq += half * slope * ds;
            if (perHarmonic.Length > n) perHarmonic[n] = half * rac;
        }
        return (q, dq);
    }

    public int NodeCount => Points.Length / 3;
    public int Elements => (NodeCount - 1) / 2;
    public double Radius => Diameter / 2;
}

/// <summary>R-em3d77-1 — a port's DC current: it enters through the positive contacts' faces and leaves through the negative
/// ones'. Each side's faces together are one equipotential contact.</summary>
public sealed record CurrentTerminal(int Port, IReadOnlyList<int> PositiveTags, IReadOnlyList<int> NegativeTags, double CurrentA);

/// <summary>R-em3d77-4a — a conductive-balance problem: a thermal problem, its wires, its conductors' σ(T), its currents.</summary>
public sealed class ElectrothermalProblem
{
    public required ThermalProblem Thermal { get; init; }

    public IReadOnlyList<ThermalWire> Wires { get; init; } = [];

    /// <summary>Per region (as <see cref="ThermalProblem.Conductivity"/>): σ(T) of a conductor, null for a region that carries no
    /// current. Empty: no region conducts.</summary>
    public IReadOnlyList<ElectricalConductivity?> Sigma { get; init; } = [];

    public IReadOnlyList<CurrentTerminal> Currents { get; init; } = [];

    public ElectricalConductivity? SigmaOf(int region) => region < Sigma.Count ? Sigma[region] : null;
}
