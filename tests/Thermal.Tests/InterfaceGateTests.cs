// brief-em3d-76 §5 gates 1–3 — interface resistances (duplicated nodes joined by interface elements) and a diagonal
// conductivity tensor, against brief 72's closed-form S2 and exact 1D answers. Structured meshes only: no mesher.

using System.Text.Json;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class InterfaceGateTests(ITestOutputHelper output)
{
    private static readonly ThermalSolveOptions Direct = new() { Solver = ThermalSolverKind.Direct };
    // the default stopping point, 1e-10 of the residual, leaves ~1e-8 K here; the gate asks the physics for 1e-9
    private static readonly ThermalSolveOptions Iterative = new() { Solver = ThermalSolverKind.Iterative, RelativeTolerance = 1e-13 };

    // ── 1. S2: the jump ΔT = q″R″, both solvers; a zero resistance is perfect contact ────────────────────

    [Theory]
    [InlineData("S2a")]
    [InlineData("S2b")]
    public void Gate1_S2Composite_TheJumpIsQTimesR_BothSolvers(string name)
    {
        var c = JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("composite", "composite.json"))).RootElement
                            .GetProperty("cases").GetProperty(name);
        var layers = c.GetProperty("layers").EnumerateArray().ToList();
        double t0 = layers[0].GetProperty("thickness_m").GetDouble(), t1 = layers[1].GetProperty("thickness_m").GetDouble(),
               t2 = layers[2].GetProperty("thickness_m").GetDouble();
        double[] zs = [.. TestMeshes.Linspace(0, t0, 4), .. TestMeshes.Linspace(t0, t0 + t1, 2).Skip(1), .. TestMeshes.Linspace(t0 + t1, t0 + t1 + t2, 2).Skip(1)];
        double zi = t0 + t1;
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, 1e-3, 2), TestMeshes.Linspace(0, 1e-3, 2), zs,
                                  (_, _, z) => z < t0 ? 0 : z < zi ? 1 : 2).ToSecondOrder();
        double r = c.GetProperty("interface").GetProperty("resistance_m2K_W").GetDouble();
        var split = ThermalInterfaces.Split(mesh, [new InterfaceResistance(1, 2, r)]);
        var m = split.Mesh;
        var p = new ThermalProblem
        {
            Mesh = m,
            Conductivity = [.. layers.Select(l => ThermalConductivity.Constant(l.GetProperty("k_W_mK").GetDouble()))],
            SurfaceSources = [new SurfaceSource(TestMeshes.ZMax, c.GetProperty("flux_top_W_m2").GetDouble())],
            Fixed = [new FixedTemperature(TestMeshes.ZMin, c.GetProperty("T_bottom_degC").GetDouble())],
        };
        double below = c.GetProperty("interface_T_below_degC").GetDouble(), above = c.GetProperty("interface_T_above_degC").GetDouble();
        double top = c.GetProperty("T_top_degC").GetDouble();
        output.WriteLine($"{name}: {split.CopiedNodes} node copies, {split.Faces[0]} interface faces, {split.AreaM2[0]:G6} m²");
        if (r == 0) Assert.Equal(0, split.CopiedNodes);
        else Assert.Equal(1e-6, split.AreaM2[0], 1e-18);
        foreach (var (options, label) in new[] { (Direct, "direct"), (Iterative, "iterative") })
        {
            var s = ThermalSolver.Solve(p, options);
            Assert.Equal(options.Solver, s.Solver);
            var at = Enumerable.Range(0, m.NodeCount).Where(v => Math.Abs(m.Nodes[3 * v + 2] - zi) < 1e-12).Select(v => s.Temperature[v]).ToList();
            double tTop = new ThermalField(m, s.Temperature).Surface(new HashSet<int> { TestMeshes.ZMax })!.Value.AvgC;
            output.WriteLine($"  {label}: below {at.Min():R}, above {at.Max():R}, jump {at.Max() - at.Min():R}; top {tTop:R}; balance {s.BalanceRelative:G3}");
            Assert.Equal(below, at.Min(), 1e-9);
            Assert.Equal(above, at.Max(), 1e-9);
            Assert.Equal(c.GetProperty("interface_jump_K").GetDouble(), at.Max() - at.Min(), 1e-9);
            Assert.Equal(top, tTop, 1e-9);
            Assert.True(s.BalanceRelative <= 1e-9, $"{label} balance {s.BalanceRelative}");
        }
    }

    // ── 2. A triple junction: node copies counted, energy balanced ─────────────────────────────────────

    /// <summary>
    /// A (x 0..2, z 0..1) under B (x 0..1) and C (x 1..2), all three meeting on the edge x = 1, z = 1. A–B is resistive,
    /// A–C is stated as 0 (perfect) and B–C is not stated (perfect). Every node of the A–B face is copied ONCE — except
    /// those on the junction edge, where the perfect contacts join all three sides round the node and nothing is copied.
    /// </summary>
    [Fact]
    public void Gate2_TripleJunction_CopiesOnlyTheResistiveFace_AndBalances()
    {
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, 2e-3, 4), TestMeshes.Linspace(0, 1e-3, 2), TestMeshes.Linspace(0, 2e-3, 4),
                                  (x, _, z) => z < 1e-3 ? 0 : x < 1e-3 ? 1 : 2).ToSecondOrder();
        var split = ThermalInterfaces.Split(mesh, [new InterfaceResistance(0, 1, 2e-5), new InterfaceResistance(0, 2, 0)]);
        const double eps = 1e-12;
        int onFace = Enumerable.Range(0, mesh.NodeCount).Count(v => Math.Abs(mesh.Nodes[3 * v + 2] - 1e-3) < eps && mesh.Nodes[3 * v] < 1e-3 - eps);
        int onEdge = Enumerable.Range(0, mesh.NodeCount).Count(v => Math.Abs(mesh.Nodes[3 * v + 2] - 1e-3) < eps && Math.Abs(mesh.Nodes[3 * v] - 1e-3) < eps);
        output.WriteLine($"{split.CopiedNodes} copies; {onFace} nodes on the resistive face off the edge, {onEdge} on the junction edge; " +
                         $"{split.Faces[0]} resistive faces, {split.Faces[1]} on the perfect pair");
        Assert.Equal(onFace, split.CopiedNodes);
        Assert.True(onEdge > 0);
        Assert.True(split.Parent.Skip(mesh.NodeCount).All(v => mesh.Nodes[3 * v] < 1e-3 - eps), "a junction-edge node was copied");
        Assert.Equal(0, split.Faces[1]);

        var p = new ThermalProblem
        {
            Mesh = split.Mesh,
            Conductivity = [ThermalConductivity.Constant(390), ThermalConductivity.Constant(150), ThermalConductivity.Constant(57)],
            SurfaceSources = [new SurfaceSource(TestMeshes.ZMax, 2e5)],
            Fixed = [new FixedTemperature(TestMeshes.ZMin, 25)],
        };
        var s = ThermalSolver.Solve(p, Direct);
        output.WriteLine($"in {s.SourcePowerW:R} W, out {s.FixedHeatOutW:R} W, balance {s.BalanceRelative:G3}");
        Assert.True(s.BalanceRelative <= 1e-9, $"balance {s.BalanceRelative}");
        Assert.Equal(2e5 * 2e-6, s.SourcePowerW, 1e-15);
    }

    // ── 3. A diagonal tensor: each axis conducts at its own k, in either orientation ──────────────────────

    [Fact]
    public void Gate3_Anisotropy_EachAxisConductsAtItsOwnK()
    {
        const double l = 1e-3, dt = 1;
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, l, 2), TestMeshes.Linspace(0, l, 2), TestMeshes.Linspace(0, l, 2)).ToSecondOrder();
        double Heat(ThermalConductivity k, int lo, int hi)
        {
            var s = ThermalSolver.Solve(new ThermalProblem
            {
                Mesh = mesh, Conductivity = [k], Fixed = [new FixedTemperature(lo, 0), new FixedTemperature(hi, dt)],
            }, Direct);
            return s.FixedHeatOutByTag[lo];
        }
        // exact: Q = k·A·ΔT/L, A = L², so k·L·ΔT
        double z = Heat(ThermalConductivity.Diagonal(1, 1, 10), TestMeshes.ZMin, TestMeshes.ZMax);
        double x = Heat(ThermalConductivity.Diagonal(1, 1, 10), TestMeshes.XMin, TestMeshes.XMax);
        double rotated = Heat(ThermalConductivity.Diagonal(10, 1, 1), TestMeshes.XMin, TestMeshes.XMax);
        output.WriteLine($"z {z:R} W, x {x:R} W, the slab turned 90°: x {rotated:R} W");
        Assert.Equal(10 * l * dt, z, 1e-10 * 10 * l * dt);
        Assert.Equal(1 * l * dt, x, 1e-10 * l * dt);
        Assert.Equal(z, rotated, 1e-10 * z);
    }
}
