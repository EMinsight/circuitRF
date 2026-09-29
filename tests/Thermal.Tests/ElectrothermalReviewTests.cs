// The thermal series' second review: the coupled solve at an isothermal point, the well's k(T), the ring note, and a Foster fit
// handed a non-finite value.

using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal.Electrothermal;
using CircuitRF.Thermal.Frequency;
using CircuitRF.Thermal.Nonlinear;
using System.Numerics;

namespace CircuitRF.Thermal.Tests;

public sealed class ElectrothermalReviewTests
{
    /// <summary>No current and both ends at one temperature, warm-started from a hot state: the coupled Newton's update test
    /// is floored as the thermal solve's is. Floored at 1e-300 it never held at a flat answer, ran every step, failed, and a
    /// continuation read the bracket closing near zero as a runaway.</summary>
    [Fact]
    public void ConductiveBalance_WarmStartedToAnIsothermalAnswer_Converges()
    {
        // the row before: the same bench at 25 °C, solved; this row starts from it and asks for 85 °C
        var sigma = ElectricalConductivity.LinearResistivity(2.2e-8, 0.0034);
        var options = new ThermalSolveOptions { Solver = ThermalSolverKind.Direct };
        var cold = ElectrothermalGateTests.Bench(25e-6, 1e-3, 25, 25, 0, ThermalConductivity.Constant(300), sigma);
        var before = ConductiveBalance.Solve(cold, options);
        Assert.True(before.Converged, before.Failure);
        var p = ElectrothermalGateTests.Bench(25e-6, 1e-3, 85, 85, 0, ThermalConductivity.Constant(300), sigma);
        var s = ConductiveBalance.Solve(p, options, new ElectrothermalSystem(p), before.State);
        Assert.True(s.Converged, s.Failure + " | " + string.Join(" | ", s.Thermal.Notes) + $" | its {s.Iterations}");
        Assert.All(s.WireTemperature[0], t => Assert.Equal(85, t, 1e-6));
    }

    /// <summary>A Foster fit handed a NaN refuses it: the all-zero branch would otherwise call it an exact, empty network.</summary>
    [Fact]
    public void AFosterFit_OfANonFiniteValue_IsRefused()
        => Assert.Throws<ArgumentException>(() => FosterFit.Fit([0, 1, 10], [new Complex(1, 0), new Complex(double.NaN, 0), new Complex(0.5, -0.1)]));

    /// <summary>With k(T) on, the mould's k scales the well round the wire as it scales the mesh: a mould whose k(T) is twice
    /// its nominal everywhere is the constant-2k mould, wire and all.</summary>
    [GmshFact]
    public void TheWell_FollowsTheMouldsKOfT()
    {
        var (mesh, raw) = Mould();
        double km = 0.8;
        double Centre(ThermalConductivity mould, bool kOfT)
        {
            var s = Solve(mesh, raw, mould, (0, 0), kOfT);
            Assert.True(s.Converged, s.Failure);
            return s.WireTemperature[0][s.WireTemperature[0].Length / 2];
        }
        double varying = Centre(ThermalConductivity.Varying(km, _ => (2 * km, 0)), kOfT: true);
        double doubled = Centre(ThermalConductivity.Constant(2 * km), kOfT: false);
        Assert.Equal(doubled, varying, 1e-6 * (doubled - 25));
    }

    /// <summary>A wire so close to its mould's surface that the ring the well is read on leaves the solid is said, not silently
    /// weakened.</summary>
    [GmshFact]
    public void AWireAtTheMouldsSurface_SaysItsRingLeavesTheSolid()
    {
        var (mesh, raw) = Mould();
        var s = Solve(mesh, raw, ThermalConductivity.Constant(0.8), (480e-6, 0), kOfT: false);
        Assert.Contains(s.Thermal.Notes, n => n.Contains("Wire 'w'") && n.Contains("cuts the ring"));
    }

    private static readonly Lazy<(ThermalMesh, MshMesh)> MouldMesh = new(() => TestMeshes.Mesh("""
        SetFactory("OpenCASCADE");
        Cylinder(1) = {0, 0, 0, 0, 0, 1000, 500};
        Cylinder(2) = {0, 0, -50, 0, 0, 50, 500};
        Cylinder(3) = {0, 0, 1000, 0, 0, 50, 500};
        BooleanFragments{ Volume{1, 2, 3}; Delete; }{ }
        e = 1e-3;
        mould[] = Volume In BoundingBox{-501, -501, -e, 501, 501, 1000 + e};
        padA[] = Volume In BoundingBox{-501, -501, -50 - e, 501, 501, e};
        padB[] = Volume In BoundingBox{-501, -501, 1000 - e, 501, 501, 1050 + e};
        ifA[] = Surface In BoundingBox{-501, -501, -e, 501, 501, e};
        ifB[] = Surface In BoundingBox{-501, -501, 1000 - e, 501, 501, 1000 + e};
        botA[] = Surface In BoundingBox{-501, -501, -50 - e, 501, 501, -50 + e};
        topB[] = Surface In BoundingBox{-501, -501, 1050 - e, 501, 501, 1050 + e};
        Physical Volume("mould") = {mould[]};
        Physical Volume("padA") = {padA[]};
        Physical Volume("padB") = {padB[]};
        Physical Surface("ifA") = {ifA[]};
        Physical Surface("ifB") = {ifB[]};
        Physical Surface("botA") = {botA[]};
        Physical Surface("topB") = {topB[]};
        Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0; Mesh.MeshSizeMax = 80;
        """, ["mould", "padA", "padB"]));

    private static (ThermalMesh Mesh, MshMesh Raw) Mould() => MouldMesh.Value;

    /// <summary>A gold wire from pad A to pad B through the mould at (x, y), 1 A through it, the pads' outer faces at 25 °C.</summary>
    private static ElectrothermalSolution Solve(ThermalMesh mesh, MshMesh raw, ThermalConductivity mould, (double X, double Y) at, bool kOfT)
    {
        int T(string name) => TestMeshes.Tag(raw, name);
        var wire = ElectrothermalGateTests.Chain("w", [(at.X, at.Y, 0), (at.X, at.Y, 1e-3)], [20], 25.4e-6, ThermalConductivity.Constant(310),
                                                 ElectricalConductivity.LinearResistivity(2.2e-8, 0.0034), startFoot: false);
        int last = wire.NodeCount - 1;
        wire = ElectrothermalGateTests.With(wire, contacts: [new WireContact(T("ifA"), 0, 0, 1), new WireContact(T("ifB"), last, last, 2)]);
        var p = new ElectrothermalProblem
        {
            Thermal = new ThermalProblem
            {
                Mesh = mesh, Conductivity = [mould, ThermalConductivity.Constant(400), ThermalConductivity.Constant(400)],
                Fixed = [new FixedTemperature(T("botA"), 25), new FixedTemperature(T("topB"), 25)],
            },
            Wires = [wire], Sigma = [null, ElectricalConductivity.Constant(5.8e7), ElectricalConductivity.Constant(5.8e7)],
            Currents = [new CurrentTerminal(1, [T("botA")], [T("topB")], 1.0)],
        };
        return ConductiveBalance.Solve(p, new ThermalSolveOptions { KOfT = kOfT, Solver = ThermalSolverKind.Direct });
    }
}
