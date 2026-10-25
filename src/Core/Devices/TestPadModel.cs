using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>One plane a test pad's via passes through a clearance in (brief-dfr26-7 R-dfr26-7-4).</summary>
/// <param name="Position">Where along the via, measured from pin 1's end, m.</param>
/// <param name="Length">The length of barrel the plane owns, m (half the dielectric to each neighbour).</param>
/// <param name="EpsR">The dielectric round that length of barrel.</param>
/// <param name="Clearance">The clearance diameter in the plane, m.</param>
public sealed record TestPadPlane(double Position, double Length, double EpsR, double Clearance);

/// <summary>What is next to an unused pad on one side, which decides how much of that side's field it keeps
/// (brief-dfr26-8 R-dfr26-8-1).</summary>
public enum TestPadSide
{
    /// <summary>The via's own end — its land or the test pad, with no plane round it: no antipad edge to couple to,
    /// and that side adds nothing.</summary>
    ViaEnd = 0,
    /// <summary>A plane carrying a pad of the same via: the side is shared (<see cref="TestPadFormulas.InnerPadScreen"/>).</summary>
    Pad = 1,
    /// <summary>A plane with nothing of the via's but its barrel in the antipad: the whole side.</summary>
    Plane = 2,
}

/// <summary>An unused pad the via carries on a conductor it passes (brief-dfr26-8 R-dfr26-8-1).</summary>
/// <param name="Position">Where along the via, from pin 1's end, m.</param>
/// <param name="Thickness">The conductor's copper thickness, m.</param>
/// <param name="EpsR">The dielectric either side, thickness-weighted.</param>
/// <param name="Diameter">The pad's diameter, m.</param>
/// <param name="Antipad">The antipad round it, m.</param>
/// <param name="GapA">The dielectric to the next conductor towards pin 1, m.</param>
/// <param name="SideA">What that conductor is.</param>
/// <param name="GapB">The dielectric to the next conductor towards the pad, m.</param>
/// <param name="SideB">What that conductor is.</param>
public sealed record TestPadInnerPad(
    double Position, double Thickness, double EpsR, double Diameter, double Antipad,
    double GapA, TestPadSide SideA, double GapB, TestPadSide SideB)
{
    /// <summary>The pad's capacitance to ground less the bare barrel's it replaces, each side's half kept as far as
    /// <see cref="SideA"/>/<see cref="SideB"/> allow, F.</summary>
    public double Capacitance(double drill)
    {
        double Keep(TestPadSide side, double gap) => side switch
        {
            TestPadSide.ViaEnd => 0.0,
            TestPadSide.Pad => TestPadFormulas.InnerPadScreen(gap, Diameter, Antipad),
            _ => 1.0,
        };
        return TestPadFormulas.InnerPadCapacitance(Diameter, Antipad, drill, Thickness, EpsR)
               * (Keep(SideA, GapA) + Keep(SideB, GapB)) / 2;
    }
}

/// <summary>
/// Everything a test pad's model is built from, fully resolved (SI). The schematic's extraction resolves it from
/// the stackup (<c>TestPadInjection</c>, src/Design); a bare <c>.cnl</c> line types it.
/// </summary>
/// <param name="Diameter">The pad's diameter, m.</param>
/// <param name="Faces">The plane the pad faces on each side that has one, as the dielectric between them: its
/// summed thickness and its series permittivity. Empty for a pad facing no plane at all.</param>
/// <param name="Drill">The via's drill, m.</param>
/// <param name="ViaLength">The via's length from pin 1's conductor to the pad's, m; zero for a side-fed pad.</param>
/// <param name="ViaEpsR">The densest dielectric the via crosses, for the validity bound.</param>
/// <param name="Planes">The planes the via passes, with where along it each sits.</param>
/// <param name="InnerPads">The unused pads the via carries on the conductors it passes (brief-dfr26-8); none by
/// default, as <c>TestPadPCell</c> draws none unless <c>InnerPadD</c> is set.</param>
public sealed record TestPadGeometry(
    double Diameter,
    IReadOnlyList<(double H, double EpsR)> Faces,
    double Drill,
    double ViaLength,
    double ViaEpsR,
    IReadOnlyList<TestPadPlane> Planes,
    IReadOnlyList<TestPadInnerPad>? InnerPads = null)
{
    /// <summary>The unused pads, never null.</summary>
    public IReadOnlyList<TestPadInnerPad> Pads => InnerPads ?? [];

    /// <summary>One unused pad's capacitance, F.</summary>
    public double InnerPadCapacitanceOf(TestPadInnerPad p) => p.Capacitance(Drill);

    /// <summary>What the unused pads add, F; zero for a side-fed pad or none.</summary>
    public double InnerPadCapacitance => ViaLength > 0 ? Pads.Sum(InnerPadCapacitanceOf) : 0.0;

    /// <summary>The pad's capacitance to the planes it faces, F.</summary>
    public double PadCapacitance => Faces.Sum(f => TestPadFormulas.PadCapacitance(Diameter, f.H, f.EpsR));

    /// <summary>The via's inductance over its whole length, H; zero for a side-fed pad.</summary>
    public double ViaInductance => TestPadFormulas.ViaInductance(ViaLength, Drill);

    /// <summary>One plane's barrel capacitance, F.</summary>
    public double BarrelCapacitanceOf(TestPadPlane p) => TestPadFormulas.BarrelCapacitance(p.Length, p.EpsR, Drill, p.Clearance);

    /// <summary>The barrel's capacitance to every plane it passes, F.</summary>
    public double BarrelCapacitance => ViaLength > 0 ? Planes.Sum(BarrelCapacitanceOf) : 0.0;

    /// <summary>The highest frequency the via's lumped model is claimed for; infinity for a side-fed pad.</summary>
    public double ValidityHz => ViaLength > 0 ? TestPadFormulas.ValidityFrequency(ViaLength, ViaEpsR) : double.PositiveInfinity;

    /// <summary>The frequency at which the via's inductance and everything capacitive below it resonate — the
    /// notch the pad cuts in the line it hangs off.</summary>
    public double ResonanceHz => TestPadFormulas.SeriesResonanceHz(ViaInductance, StubCapacitance);

    /// <summary>Everything capacitive hanging off pin 1 at low frequency — pad, barrel and unused pads, F.</summary>
    public double StubCapacitance => PadCapacitance + BarrelCapacitance + InnerPadCapacitance;
}

/// <summary>
/// <c>TESTPAD</c> — a round test-point pad hanging off a line, reached through a via or fed from a side
/// (brief-dfr26-7 R-dfr26-7-4). Pin 1 is the feed, pin 2 the pad centre; the reference is node 0, as for
/// <c>MLIN</c>.
///
/// <para><b>Topology.</b> The via's inductance from pin 1 to the pad node, split along its length at each plane
/// it passes, where that plane's barrel capacitance goes to node 0, and at each unused inner pad it carries
/// (<see cref="TestPadInnerPad"/>, brief-dfr26-8), where that pad's capacitance does; the pad's capacitance to the planes it faces
/// at the pad node, which is pin 2. A side-fed pad has no via, so pin 1 and pin 2 are one node (a micro-ohm, as
/// for a zero-length via). Lossless: neither the barrel's resistance nor the dielectric's loss is modelled.
/// Every closed form is in <see cref="TestPadFormulas"/>, with its source.</para>
///
/// <para><b>An estimate, said once per instance.</b> The first stamp posts one note naming the values and the
/// EM setup as the reference.</para>
/// </summary>
public sealed class TestPadModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    public TestPadGeometry Geometry { get; }

    private bool _noted, _validityReported;
    private readonly List<(string, string)> _pending = [];

    public TestPadModel(TestPadGeometry geometry) => Geometry = geometry;

    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
    {
        if (_pending.Count == 0) return [];
        var r = _pending.ToArray();
        _pending.Clear();
        return r;
    }

    /// <summary>The 2×2 admittance matrix this component stamps, at <paramref name="omega"/>.</summary>
    public (Complex Y11, Complex Y12, Complex Y22) Admittance(double omega)
    {
        var g = Geometry;
        // ABCD of the chain from pin 1 to pin 2: series L segments with each plane's shunt barrel C and each unused
        // pad's C between them, at their points along the via, then the pad's shunt C at pin 2.
        Complex a = 1, b = 0, c = 0, d = 1;
        void Series(Complex z) { b += a * z; d += c * z; }
        void Shunt(Complex y) { a += b * y; c += d * y; }

        double l = g.ViaInductance;
        if (g.ViaLength > 0)
        {
            double at = 0;
            var shunts = g.Planes.Select(p => (p.Position, C: g.BarrelCapacitanceOf(p)))
                          .Concat(g.Pads.Select(p => (p.Position, C: g.InnerPadCapacitanceOf(p))))
                          .Where(s => s.Position > 0 && s.Position < g.ViaLength)
                          .OrderBy(s => s.Position);
            foreach (var p in shunts)
            {
                Series(new Complex(0, omega * l * (p.Position - at) / g.ViaLength));
                Shunt(new Complex(0, omega * p.C));
                at = p.Position;
            }
            Series(new Complex(0, omega * l * (g.ViaLength - at) / g.ViaLength));
        }
        Shunt(new Complex(0, omega * g.PadCapacitance));

        b = ViaModel.SeriesFloor(b);
        // Reciprocal (AD − BC = 1): Y = [D/B, −1/B; −1/B, A/B].
        return (d / b, -1 / b, a / b);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        Note(c.InstancePath);
        CheckValidity(c.InstancePath, omega / (2 * Math.PI));
        var (y11, y12, y22) = Admittance(omega);
        int n1 = c.Nodes[0], n2 = c.Nodes[1];
        mna.AddBlockAdmittance(n1, n1, y11);
        mna.AddBlockAdmittance(n1, n2, y12);
        mna.AddBlockAdmittance(n2, n1, y12);
        mna.AddBlockAdmittance(n2, n2, y22);
    }

    /// <summary>One warning per instance, the first time the via is stamped above <see cref="TestPadGeometry.ValidityHz"/>,
    /// as a placed <c>VIA</c> reports it.</summary>
    private void CheckValidity(string instancePath, double freqHz)
    {
        if (_validityReported || !(freqHz > Geometry.ValidityHz)) return;
        _validityReported = true;
        string F(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        _pending.Add(($"TESTPAD-validity:{instancePath}",
            $"{instancePath}: the test pad's via is {F(Geometry.ViaLength * 1e3)} mm long, a twentieth of the wavelength in " +
            $"its densest dielectric (εr {F(Geometry.ViaEpsR)}) at {F(Geometry.ValidityHz / 1e9)} GHz. Above that its lumped " +
            $"model is an extrapolation (this run goes past it, from {F(freqHz / 1e9)} GHz); an EM setup of the pad is the reference there."));
    }

    private void Note(string instancePath)
    {
        if (_noted) return;
        _noted = true;
        var g = Geometry;
        string F(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
        string via = g.ViaLength > 0
            ? $", its via {F(g.ViaInductance * 1e9)} nH" +
              (g.BarrelCapacitance > 0 ? $" with {F(g.BarrelCapacitance * 1e12)} pF to the planes it passes" : "") +
              (g.Pads.Count > 0 ? $", its {g.Pads.Count} unused inner pad{(g.Pads.Count == 1 ? "" : "s")} {F(g.InnerPadCapacitance * 1e12)} pF" : "") +
              (double.IsFinite(g.ResonanceHz) ? $", resonating at {F(g.ResonanceHz / 1e9)} GHz" : "")
            : "";
        // A pad no narrower than its antipad would touch its plane, so it is counted as nothing — say so.
        if (g.Pads.FirstOrDefault(p => p.Diameter >= p.Antipad) is { } wide)
            _pending.Add(($"TESTPAD-innerpad:{instancePath}",
                $"{instancePath}: InnerPadD ({F(wide.Diameter * 1e3)} mm) is no smaller than AntipadD ({F(wide.Antipad * 1e3)} mm), " +
                "so each unused pad would touch its plane; they are left out of the estimate."));
        _pending.Add(($"TESTPAD-estimate:{instancePath}",
            $"{instancePath}: the test pad's values are a closed-form estimate — pad {F(g.PadCapacitance * 1e12)} pF{via}. " +
            "An EM setup of the pad, its via and the planes it passes is the reference."));
    }
}
