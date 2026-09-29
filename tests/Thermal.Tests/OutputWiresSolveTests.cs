// brief-em3d-85 — the output wires' solve: a perfect bond is a tie, so the coupled system an iterative solve meets is the
// conduction problem's own (gate 1, as a counter, never a time); and a balanced pair of ground-referenced ports (gate 2): each
// port runs from a conductor to the flange across an insulator, so neither alone has a path.

using CircuitRF.Thermal.Electrothermal;
using CircuitRF.Thermal.Nonlinear;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class OutputWiresSolveTests(ITestOutputHelper output)
{
    private const double Um = 1e-6;

    /// <summary>The most BiCGStab iterations one Newton step may take on <see cref="OutputStage"/>. Tied, its steps take 24–26;
    /// with the same bonds assembled as penalties they took 64 and 57 on the first two. (On the full-size model brief 81 could not
    /// run — six wires, the run service's own mesh, 12.9k unknowns — a penalty step ran BiCGStab to its 2,000 and fell back to LU,
    /// ~50 s a step; tied, 27–34 iterations and under a second. src/Thermal/RESOLVED.md.)</summary>
    private const int MostIterations = 40;

    // ── gate 1: the coupled solve converges as the conduction problem does ────────────────────────────────

    /// <summary>
    /// The output stage in miniature: a 10 µm gold pad on a die on a flange, a copper lead on an alumina standoff, a mould over
    /// all of it, and six 1 mil gold wires from the pad to the lead with a wedge foot at each end (perfect bonds), 6 A through
    /// them, k(T) on, solved at order 1 on the iterative path. Every Newton step's BiCGStab converges within
    /// <see cref="MostIterations"/> with no fallback to LU, and all twelve bonds are tied. The penalty's cost needs several patches
    /// on one well-conducting stack: one wire alone, or six on pads with no die under them, converged either way.
    /// </summary>
    [GmshFact]
    public void PerfectBonds_AreTied_AndEveryNewtonStepConvergesIteratively()
    {
        var p = OutputStage();
        var sys = new ElectrothermalSystem(p);
        var s = ConductiveBalance.Solve(p, new ThermalSolveOptions { Solver = ThermalSolverKind.Iterative, MaxIterations = 200, KOfT = true }, sys);
        output.WriteLine($"{p.Thermal.Mesh.NodeCount:N0} nodes; {s.Iterations} Newton step(s), BiCGStab iterations " +
                         $"{string.Join(", ", s.LinearSteps.Select(l => $"{l.Iterations} ({l.Solver})"))}; hottest {s.WireTemperature.Max(t => t.Max()):G6} °C");
        Assert.True(s.Converged, s.Failure);
        Assert.NotEmpty(s.LinearSteps);
        Assert.All(s.LinearSteps, l =>
        {
            Assert.Equal(ThermalSolverKind.Iterative, l.Solver);
            Assert.InRange(l.Iterations, 1, MostIterations);
        });
        Assert.Equal((12, 12), sys.TiedContacts);
        Assert.DoesNotContain(s.Thermal.Notes, n => n.Contains("the direct solver was used instead"));
        Assert.Contains(s.Thermal.Notes, n => n.Contains("BiCGStab iterations per step"));
        Assert.True(s.Thermal.BalanceRelative <= 1e-6, $"balance {s.Thermal.BalanceRelative:G3}");
    }

    /// <summary>
    /// The Newton Jacobian against a central difference of the residual, block by block (T on the mesh, T on the wires, φ), at a
    /// solved state of <see cref="OutputStage"/> with σ(T) and k(T) on. Every earlier bench gave its pads a constant σ, so the 3D
    /// conductors' σ(T) terms (dσ/dT·∇φ in the φ rows, σ′|∇φ|² in the T rows) had never been checked this way. Measured: every
    /// block to 5.4e-6 of the difference or better.
    /// </summary>
    [GmshFact]
    public void TheJacobian_WithSigmaOfTInTheConductors_IsTheResidualsDerivative()
    {
        var p = OutputStage();
        var sys = new ElectrothermalSystem(p);
        var solved = ConductiveBalance.Solve(p, new ThermalSolveOptions { Solver = ThermalSolverKind.Iterative }, sys);
        Assert.True(solved.Converged, solved.Failure);
        var x = solved.State;
        var rnd = new Random(1);
        int n = sys.Size, nT = sys.TUnknowns;
        double[] Residual(double[] at)
        {
            var b = sys.Assemble(p, at, kOfT: true, sigmaOfT: true);
            var r = new double[n];
            sys.Matrix(b.Secant).Multiply(at, r);
            for (int i = 0; i < n; i++) r[i] -= b.Load[i];
            return r;
        }
        foreach (var (name, lo, hi, scale) in new[] { ("T mesh", 0, sys.HostNodes, 1.0), ("T wire", sys.HostNodes, nT, 1.0), ("phi", nT, n, 1e-3) })
        {
            var v = new double[n];
            for (int i = lo; i < hi; i++) v[i] = scale * (rnd.NextDouble() - 0.5);
            var jv = new double[n];
            sys.Matrix(sys.Assemble(p, x, kOfT: true, sigmaOfT: true).Tangent).Multiply(v, jv);
            const double eps = 1e-4;
            var plus = Residual([.. x.Select((xi, i) => xi + eps * v[i])]);
            var minus = Residual([.. x.Select((xi, i) => xi - eps * v[i])]);
            foreach (var (rows, rlo, rhi) in new[] { ("T mesh", 0, sys.HostNodes), ("T wire", sys.HostNodes, nT), ("phi", nT, n) })
            {
                double num = 0, den = 0;
                for (int i = rlo; i < rhi; i++)
                {
                    double fd = (plus[i] - minus[i]) / (2 * eps);
                    num += (jv[i] - fd) * (jv[i] - fd);
                    den += fd * fd;
                }
                output.WriteLine($"columns {name}, rows {rows}: |J·v − difference| {Math.Sqrt(num):G3} of {Math.Sqrt(den):G3}");
                Assert.True(Math.Sqrt(num) <= 1e-4 * Math.Sqrt(den) + 1e-12, $"columns {name}, rows {rows}: {Math.Sqrt(num):G3} of {Math.Sqrt(den):G3}");
            }
        }
    }

    // ── gate 2: ground-referenced ports, a balanced superposition ─────────────────────────────────────

    private static readonly ThermalSolveOptions Iterative = new() { Solver = ThermalSolverKind.Iterative };

    /// <summary>Port 1 from the pad to the flange under the die, port 2 from the lead to the flange under the standoff, +I and −I:
    /// the wires carry I from the pad to the lead between them, the electrical power in is the Joule heat, and the run says the
    /// ports are a balanced superposition.</summary>
    [GmshFact]
    public void TwoGroundReferencedPorts_Balanced_CarryTheCurrentThroughTheWires()
    {
        const double i = 3.0;
        var p = OutputStage(t => [new(1, [t("padEnd")], [t("flangeUnderDie")], i), new(2, [t("leadEnd")], [t("flangeUnderLead")], -i)]);
        var s = ConductiveBalance.Solve(p, Iterative);
        output.WriteLine($"wires {string.Join(", ", s.WireCurrentA.Select(a => a.ToString("G6")))} A (sum {s.WireCurrentA.Sum():G8}); " +
                         $"Σ V·I {s.ElectricalPowerW:G8} W, Joule {s.JouleW:G8} W");
        Assert.True(s.Converged, s.Failure);
        Assert.Equal(i, s.WireCurrentA.Sum(), 1e-6 * i);
        Assert.All(s.WireCurrentA, a => Assert.True(a > 0));
        Assert.Equal(s.JouleW, s.ElectricalPowerW, 1e-6 * s.JouleW);
        Assert.Contains(s.Thermal.Notes, n => n.Contains("balanced superposition"));
    }

    /// <summary>−I/2 against +I leaves I/2 with nowhere to go: refused, naming both ports.</summary>
    [GmshFact]
    public void TwoGroundReferencedPorts_Unbalanced_AreRefusedNamingBoth()
    {
        var p = OutputStage(t => [new(1, [t("padEnd")], [t("flangeUnderDie")], 2.0), new(2, [t("leadEnd")], [t("flangeUnderLead")], -1.0)]);
        var x = Assert.Throws<ElectrothermalException>(() => ConductiveBalance.Solve(p, Iterative));
        output.WriteLine(x.Message);
        Assert.StartsWith("Ports 1 and 2 share a conductor with no other path", x.Message);
    }

    /// <summary>A ground-referenced port with no other port to return through keeps the refusal it always had, word for word.</summary>
    [GmshFact]
    public void AGroundReferencedPortAlone_KeepsItsRefusal()
    {
        var p = OutputStage(t => [new(1, [t("padEnd")], [t("flangeUnderDie")], 1.0)]);
        var x = Assert.Throws<ElectrothermalException>(() => new ElectrothermalSystem(p));
        Assert.Equal("Port 1's positive and negative contacts are not joined by any conductor or wire, so its current has no path: connect them, " +
                     "or remove the port's current.", x.Message);
    }

    private const double R = 12.7;
    private static readonly double[] WireY = [-250, -150, -50, 50, 150, 250];

    /// <summary>The miniature output stage (micrometres in the .geo): regions mould, flange, die, pad, standoff, lead. Its
    /// currents are <paramref name="currents"/>'s, given the face tags by name; omitted, 6 A from the lead's end to the pad's.</summary>
    private static ElectrothermalProblem OutputStage(Func<Func<string, int>, IReadOnlyList<CurrentTerminal>>? currents = null)
    {
        var (mesh, raw) = StageMesh.Value;
        int T(string name) => TestMeshes.Tag(raw, name);
        double z = (110 + R) * Um;
        var gold = ElectricalConductivity.LinearResistivity(2.44e-8, 0.0034);
        var copper = ElectricalConductivity.LinearResistivity(1.72e-8, 0.0039);
        var wires = WireY.Select((y, i) =>
        {
            double yy = y * Um;
            var w = ElectrothermalGateTests.Chain($"w[{i}]", [(450 * Um, yy, z), (500 * Um, yy, z), (550 * Um, yy, 330 * Um), (1250 * Um, yy, 330 * Um),
                                                              (1300 * Um, yy, z), (1350 * Um, yy, z)], [1, 4, 12, 4, 1], 2 * R * Um,
                                                  ThermalConductivity.Constant(318), gold);
            int last = w.NodeCount - 1;
            var feet = new bool[w.Elements];
            feet[0] = feet[^1] = true;
            return ElectrothermalGateTests.With(w, [new WireContact(T($"pA{i}"), 2, 2, 3), new WireContact(T($"pB{i}"), last - 2, last - 2, 5)], feet);
        }).ToList();
        return new ElectrothermalProblem
        {
            Thermal = new ThermalProblem
            {
                Mesh = mesh,
                Conductivity = [.. new[] { 0.9, 180, 383, 318, 24, 400 }.Select(k => ThermalConductivity.Varying(k, t => (k * (1 - 1e-3 * (t - 25)), -1e-3 * k)))],
                Fixed = [new FixedTemperature(T("flangeBottom"), 25)],
            },
            Wires = wires, Sigma = [null, ElectricalConductivity.Constant(2.3e7), null, gold, null, copper],
            Currents = currents?.Invoke(T) ?? [new CurrentTerminal(1, [T("leadEnd")], [T("padEnd")], 6.0)],
        };
    }

    private static readonly Lazy<(ThermalMesh, CircuitRF.Engine.Em3d.MshMesh)> StageMesh = new(() =>
    {
        string patches = string.Join("\n", WireY.SelectMany((y, i) => new[]
        {
            $"Rectangle({200 + i}) = {{450, {y - R}, 110, 50, {2 * R}}};",
            $"Rectangle({300 + i}) = {{1300, {y - R}, 110, 50, {2 * R}}};",
        }));
        string groups = string.Join("\n", WireY.SelectMany((y, i) => new[]
        {
            $"Physical Surface(\"pA{i}\") = Surface In BoundingBox{{450 - e, {y - R} - e, 110 - e, 500 + e, {y + R} + e, 110 + e}};",
            $"Physical Surface(\"pB{i}\") = Surface In BoundingBox{{1300 - e, {y - R} - e, 110 - e, 1350 + e, {y + R} + e, 110 + e}};",
        }));
        return TestMeshes.Mesh($$"""
            SetFactory("OpenCASCADE");
            Box(1) = {-200, -400, -300, 2400, 800, 300};
            Box(2) = {0, -350, 0, 600, 700, 100};
            Box(3) = {350, -300, 100, 200, 600, 10};
            Box(4) = {1100, -350, 0, 900, 700, 60};
            Box(5) = {1100, -350, 60, 900, 700, 50};
            Box(6) = {-200, -400, 0, 2400, 800, 500};
            {{patches}}
            BooleanFragments{ Volume{1:6}; Surface{200:205, 300:305}; Delete; }{ }
            e = 1e-3;
            flange[] = Volume In BoundingBox{-200 - e, -400 - e, -300 - e, 2200 + e, 400 + e, e};
            die[] = Volume In BoundingBox{-e, -350 - e, -e, 600 + e, 350 + e, 100 + e};
            pad[] = Volume In BoundingBox{350 - e, -300 - e, 100 - e, 550 + e, 300 + e, 110 + e};
            standoff[] = Volume In BoundingBox{1100 - e, -350 - e, -e, 2000 + e, 350 + e, 60 + e};
            lead[] = Volume In BoundingBox{1100 - e, -350 - e, 60 - e, 2000 + e, 350 + e, 110 + e};
            mould[] = Volume In BoundingBox{-200 - e, -400 - e, -e, 2200 + e, 400 + e, 500 + e};
            mould[] -= die[];
            mould[] -= pad[];
            mould[] -= standoff[];
            mould[] -= lead[];
            Physical Volume("mould") = {mould[]};
            Physical Volume("flange") = {flange[]};
            Physical Volume("die") = {die[]};
            Physical Volume("pad") = {pad[]};
            Physical Volume("standoff") = {standoff[]};
            Physical Volume("lead") = {lead[]};
            Physical Surface("flangeBottom") = Surface In BoundingBox{-200 - e, -400 - e, -300 - e, 2200 + e, 400 + e, -300 + e};
            Physical Surface("padEnd") = Surface In BoundingBox{350 - e, -300 - e, 100 - e, 350 + e, 300 + e, 110 + e};
            Physical Surface("leadEnd") = Surface In BoundingBox{2000 - e, -350 - e, 60 - e, 2000 + e, 350 + e, 110 + e};
            Physical Surface("flangeUnderDie") = Surface In BoundingBox{-e, -350 - e, -e, 600 + e, 350 + e, e};
            Physical Surface("flangeUnderLead") = Surface In BoundingBox{1100 - e, -350 - e, -e, 2000 + e, 350 + e, e};
            {{groups}}
            Mesh.MeshSizeMax = 60;
            """, ["mould", "flange", "die", "pad", "standoff", "lead"], order: 1);
    });
}
