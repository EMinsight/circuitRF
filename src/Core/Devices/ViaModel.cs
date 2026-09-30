using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The geometry and materials of one plated barrel, fully resolved (SI). What both via components
/// are built from; <see cref="ComponentModelFactory"/> reads it off the instance's parameters.
/// </summary>
/// <param name="Drill">Finished hole diameter, m. The barrel's radius is half of it.</param>
/// <param name="Pad">Pad diameter, m.</param>
/// <param name="Antipad">Clearance diameter in every plane the barrel passes, m.</param>
/// <param name="Plating">Barrel wall thickness, m (ignored when <paramref name="Solid"/>).</param>
/// <param name="Length">Barrel length between the two conductors it joins, m.</param>
/// <param name="Sigma">Conductivity of the barrel, S/m.</param>
/// <param name="Solid">A filled barrel: a rod rather than a tube.</param>
/// <param name="EpsRMax">εr of the densest dielectric the barrel crosses, for the validity bound.</param>
/// <param name="Planes">Each plane the barrel passes through a clearance in, as (T, εr): the
/// dielectric thickness that plane's capacitance is taken over, and its permittivity.</param>
/// <param name="StubLength">The barrel beyond the second conductor (B), to the end of the drill, m. Zero
/// when the second conductor is where the drill ends.</param>
/// <param name="StubPlanes">The planes that stub passes, as <paramref name="Planes"/>.</param>
/// <param name="StubALength">The barrel beyond the FIRST conductor (A), on the side away from B — a
/// through drill used between two inner layers has one at each end.</param>
/// <param name="StubAPlanes">The planes that stub passes.</param>
/// <param name="CapacitanceOverride">When set, the whole computed capacitance is replaced by this, F.</param>
/// <param name="IncludeCapacitance">False drops every capacitance term, leaving the barrel's R and L.</param>
public sealed record ViaGeometry(
    double Drill, double Pad, double Antipad, double Plating, double Length, double Sigma, bool Solid,
    double EpsRMax,
    IReadOnlyList<(double T, double EpsR)> Planes,
    double StubLength,
    IReadOnlyList<(double T, double EpsR)> StubPlanes,
    double? CapacitanceOverride,
    bool IncludeCapacitance,
    double StubALength = 0,
    IReadOnlyList<(double T, double EpsR)>? StubAPlanes = null)
{
    public double Radius => Drill / 2;

    /// <summary>The barrel's inductance, Goldfarb and Pucel.</summary>
    public double Inductance => ViaFormulas.GoldfarbPucelInductance(Length, Radius);

    /// <summary>The barrel's resistance at <paramref name="freqHz"/>.</summary>
    public double Resistance(double freqHz) => ViaFormulas.AcResistance(freqHz, Length, Radius, Plating, Sigma, Solid);

    /// <summary>A stub's inductance, from the same form over the stub's own length. Its mutual
    /// inductance with the barrel is not included.</summary>
    public double StubInductanceOf(double length) => ViaFormulas.GoldfarbPucelInductance(length, Radius);

    public double StubResistanceOf(double freqHz, double length) => ViaFormulas.AcResistance(freqHz, length, Radius, Plating, Sigma, Solid);

    /// <summary>ΣC over <see cref="Planes"/>, Johnson and Graham, or the override.</summary>
    public double PlaneCapacitance
        => !IncludeCapacitance ? 0.0
         : CapacitanceOverride ?? Planes.Sum(p => ViaFormulas.JohnsonGrahamCapacitance(p.EpsR, p.T, Pad, Antipad));

    /// <summary>ΣC over a stub's planes. There is no pad at a stub's end (the brief's D4: no
    /// non-functional pads), so the planes it passes are the whole of it; an override on the barrel does
    /// not reach a stub.</summary>
    public double StubCapacitanceOf(IReadOnlyList<(double T, double EpsR)>? planes)
        => !IncludeCapacitance || CapacitanceOverride is not null || planes is null ? 0.0
         : planes.Sum(p => ViaFormulas.JohnsonGrahamCapacitance(p.EpsR, p.T, Pad, Antipad));

    /// <summary>The whole drill: the barrel and both stubs.</summary>
    public double DrillLength => Length + StubLength + StubALength;

    /// <summary>The highest frequency the lumped model is claimed for: the whole drill a twentieth of a
    /// wavelength in the densest dielectric crossed.</summary>
    public double ValidityFrequency => ViaFormulas.LumpedValidityFrequency(DrillLength, EpsRMax);
}

/// <summary>
/// <c>VIA</c> — a signal via changing layers (brief-via-component.md R-viac-1/3). Terminal A is on the
/// first conductor, B on the second, and the reference is node 0, as for <c>MLIN</c>.
///
/// <para><b>Topology.</b> A symmetric T: half the barrel's <c>R + jωL</c>, then the capacitance of every
/// plane the barrel passes, to node 0, then the other half. When the drill runs on past the second
/// conductor, the stub hangs off B as its own open-ended section, <c>R + jωL</c> into the capacitance
/// of the planes the stub passes, from the same formulas — and a drill running on past A the other
/// way hangs its own stub off A. Every quantity is in
/// <see cref="ViaFormulas"/>, with its source.</para>
///
/// <para><b>Validity.</b> A lumped T holds while the barrel (with its stub) is under a twentieth of the
/// wavelength in the densest dielectric crossed. Past that frequency the instance posts one warning,
/// and the result is computed anyway: an extrapolation, said to be one.</para>
/// </summary>
public sealed class ViaModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    public ViaGeometry Geometry { get; }
    private readonly ViaValidityWarning _warning;

    public ViaModel(ViaGeometry geometry)
    {
        Geometry = geometry;
        _warning = new ViaValidityWarning(geometry);
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _warning.Drain();

    /// <summary>The 2×2 admittance matrix this component stamps, at <paramref name="omega"/>.</summary>
    public (Complex Y11, Complex Y12, Complex Y22) Admittance(double omega)
    {
        double f = omega / (2 * Math.PI);
        var g = Geometry;
        Complex half = SeriesFloor(new Complex(g.Resistance(f), omega * g.Inductance) / 2);
        Complex yc = new(0, omega * g.PlaneCapacitance);

        // T of (half, yc, half): D = Za + Zb + Za·Zb·Yc, which stays finite with no capacitance.
        Complex d = half + half + half * half * yc;
        Complex y11 = (1 + half * yc) / d;
        Complex y12 = -1 / d;
        Complex y22 = y11 + StubAdmittance(g, g.StubLength, g.StubPlanes, f, omega);
        y11 += StubAdmittance(g, g.StubALength, g.StubAPlanes, f, omega);
        return (y11, y12, y22);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        _warning.Check(c.InstancePath, omega / (2 * Math.PI));
        var (y11, y12, y22) = Admittance(omega);
        int a = c.Nodes[0], b = c.Nodes[1];
        mna.AddBlockAdmittance(a, a, y11);
        mna.AddBlockAdmittance(a, b, y12);
        mna.AddBlockAdmittance(b, a, y12);
        mna.AddBlockAdmittance(b, b, y22);
    }

    /// <summary>The open stub's input admittance: <c>R + jωL</c> into its capacitance. Zero when there
    /// is no stub, or no capacitance to load it (an unloaded series section into an open carries no
    /// current).</summary>
    internal static Complex StubAdmittance(ViaGeometry g, double length,
        IReadOnlyList<(double T, double EpsR)>? planes, double f, double omega)
    {
        double cs = g.StubCapacitanceOf(planes);
        if (length <= 0 || cs <= 0 || omega <= 0) return Complex.Zero;
        Complex zs = new Complex(g.StubResistanceOf(f, length), omega * g.StubInductanceOf(length)) + 1 / new Complex(0, omega * cs);
        return 1 / zs;
    }

    /// <summary>A barrel with neither resistance nor inductance (a zero-length one, at DC) would be an
    /// ideal short, which a nodal admittance cannot hold. A micro-ohm stands in for it.</summary>
    internal static Complex SeriesFloor(Complex z) => z.Magnitude < 1e-9 ? new Complex(1e-9, 0) : z;
}

/// <summary>
/// <c>VIAGND</c> — a via from a pad down to a ground plane: the return of a shunt part (brief-via-
/// component.md R-viac-1/3). Terminal A is on the first conductor; the far end is the reference.
///
/// <para><b>Nets.</b> The instance line binds two, A and the reference, the way <c>Term</c> and
/// <c>NonlinearC</c> do: the schematic draws one pin and the extractor appends <c>0</c>.</para>
///
/// <para><b>Topology.</b> The barrel's <c>R + jωL</c> from A to the reference, with the pad's
/// capacitance to the plane from A in parallel — the value a designer otherwise adds by hand beside
/// every grounded part. A plane the barrel passes on its way down contributes its own capacitance at
/// the barrel's midpoint. A stub past the ground plane hangs off a grounded node and is ignored.</para>
/// </summary>
public sealed class ViaGroundModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 1;
    public override ModelKind Kind => ModelKind.Linear;

    public ViaGeometry Geometry { get; }

    /// <summary>The pad's own capacitance to the ground plane, F — Johnson and Graham over the dielectric
    /// between the pad and the plane, or zero when capacitance is excluded or overridden.</summary>
    public double PadCapacitance { get; }

    private readonly ViaValidityWarning _warning;

    /// <param name="padThickness">The dielectric between the pad and the ground plane, m.</param>
    /// <param name="padEpsR">Its permittivity.</param>
    public ViaGroundModel(ViaGeometry geometry, double padThickness, double padEpsR)
    {
        Geometry = geometry;
        PadCapacitance = geometry.IncludeCapacitance && geometry.CapacitanceOverride is null
            ? ViaFormulas.JohnsonGrahamCapacitance(padEpsR, padThickness, geometry.Pad, geometry.Antipad)
            : 0.0;
        _warning = new ViaValidityWarning(geometry with { StubLength = 0, StubALength = 0 });
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _warning.Drain();

    /// <summary>The total capacitance this component places at A (pad plus planes, or the override).</summary>
    public double TotalCapacitance => Geometry.CapacitanceOverride is { } c && Geometry.IncludeCapacitance
        ? c : PadCapacitance + Geometry.PlaneCapacitance;

    /// <summary>The admittance from A to the reference, at <paramref name="omega"/>.</summary>
    public Complex Admittance(double omega)
    {
        double f = omega / (2 * Math.PI);
        var g = Geometry;
        Complex half = ViaModel.SeriesFloor(new Complex(g.Resistance(f), omega * g.Inductance) / 2);

        // An override is the whole capacitance, placed at A; otherwise the pad sits at A and the planes
        // the barrel passes sit at its midpoint.
        double atA = g.CapacitanceOverride is not null ? TotalCapacitance : PadCapacitance;
        double atMid = g.CapacitanceOverride is not null ? 0.0 : g.PlaneCapacitance;

        Complex lower = 1 / (new Complex(0, omega * atMid) + 1 / half);   // mid-node to reference
        return new Complex(0, omega * atA) + 1 / (half + lower);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        _warning.Check(c.InstancePath, omega / (2 * Math.PI));
        Complex y = Admittance(omega);
        int a = c.Nodes[0], r = c.Nodes.Length > 1 ? c.Nodes[1] : 0;
        mna.AddBlockAdmittance(a, a, y);
        mna.AddBlockAdmittance(r, r, y);
        mna.AddBlockAdmittance(a, r, -y);
        mna.AddBlockAdmittance(r, a, -y);
    }
}

/// <summary>One warning per instance, the first time a via is stamped above the frequency its lumped
/// model is valid to.</summary>
internal sealed class ViaValidityWarning(ViaGeometry geometry)
{
    private bool _reported;
    private readonly List<(string, string)> _pending = [];

    public void Check(string instancePath, double freqHz)
    {
        if (_reported) return;
        double limit = geometry.ValidityFrequency;
        if (!(freqHz > limit)) return;
        _reported = true;
        double lengthMm = geometry.DrillLength * 1e3;
        _pending.Add(($"VIA-validity:{instancePath}",
            $"{instancePath}: this via's barrel is {lengthMm:G4} mm long, a twentieth of the wavelength in its " +
            $"densest dielectric (εr {geometry.EpsRMax:G4}) at {limit / 1e9:G4} GHz. Above that its lumped model is " +
            $"an extrapolation (this run reaches {freqHz / 1e9:G4} GHz); an EM solve of the via is the reference there."));
    }

    public IReadOnlyList<(string Key, string Message)> Drain()
    {
        if (_pending.Count == 0) return [];
        var r = _pending.ToArray();
        _pending.Clear();
        return r;
    }
}
