// brief-em3d-74 §7 gates 1–7 — the solver against brief 72's externally generated references (testdata/thermal/). Each is
// small; S5 and the AMG counter mesh brief 72's own geometry with Gmsh and skip, saying so, where there is none.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class SolverGateTests(ITestOutputHelper output)
{
    private static readonly ThermalSolveOptions Direct = new() { Solver = ThermalSolverKind.Direct };

    private static JsonElement Case(string folder, string file, string name)
        => JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata(folder, file))).RootElement.GetProperty("cases").GetProperty(name);

    // ── 1. S1 slab: exact for a polynomial field ────────────────────────────────────────────────────

    /// <summary>S1a's field is linear and S1b's quadratic: P2 reproduces both exactly on any mesh, P1 the linear one.</summary>
    [Theory]
    [InlineData("S1a", 2)]
    [InlineData("S1a", 1)]
    [InlineData("S1b", 2)]
    public void Gate1_S1Slab_IsExact(string name, int order)
    {
        var c = Case("slab", "slab.json", name);
        double l = c.GetProperty("thickness_L_m").GetDouble(), k = c.GetProperty("k_W_mK").GetDouble();
        double tb = c.GetProperty("T_bottom_degC").GetDouble(), q2 = c.GetProperty("flux_top_W_m2").GetDouble();
        double q3 = c.GetProperty("q_volume_W_m3").GetDouble();
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, 1e-3, 3), TestMeshes.Linspace(0, 1e-3, 2), TestMeshes.Linspace(0, l, 4));
        if (order == 2) mesh = mesh.ToSecondOrder();
        var p = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(k)],
            VolumeSources = q3 != 0 ? [new VolumeSource(0, q3)] : [],
            SurfaceSources = [new SurfaceSource(TestMeshes.ZMax, q2)],
            Fixed = [new FixedTemperature(TestMeshes.ZMin, tb)],
        };
        var s = ThermalSolver.Solve(p, Direct);
        double worst = 0;
        for (int i = 0; i < mesh.NodeCount; i++)
        {
            double z = mesh.Nodes[3 * i + 2];
            double exact = tb + (q2 + q3 * l) * z / k - q3 * z * z / (2 * k);
            worst = Math.Max(worst, Math.Abs(s.Temperature[i] - exact));
        }
        output.WriteLine($"{name} P{order}: max nodal error {worst:G3} K; T(L) reference {c.GetProperty("T_top_degC").GetDouble()}");
        Assert.True(worst <= 1e-10, $"max nodal error {worst:G3} K");
    }

    // ── 2. S3 Robin ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("S3a")]
    [InlineData("S3b")]
    public void Gate2_S3Robin_SurfaceTemperatures(string name)
    {
        var c = Case("robin", "robin.json", name);
        double l = c.GetProperty("thickness_L_m").GetDouble(), k = c.GetProperty("k_W_mK").GetDouble();
        var bottom = c.GetProperty("bottom");
        var conv = new List<ConvectionCondition> { new(TestMeshes.ZMin, bottom.GetProperty("h_W_m2K").GetDouble(), bottom.GetProperty("T_amb_degC").GetDouble()) };
        if (c.TryGetProperty("top", out var top))
            conv.Add(new(TestMeshes.ZMax, top.GetProperty("h_W_m2K").GetDouble(), top.GetProperty("T_amb_degC").GetDouble()));
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, 1e-3, 2), TestMeshes.Linspace(0, 1e-3, 2), TestMeshes.Linspace(0, l, 4)).ToSecondOrder();
        var p = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(k)], Convection = conv,
            SurfaceSources = c.TryGetProperty("flux_top_W_m2", out var f) ? [new SurfaceSource(TestMeshes.ZMax, f.GetDouble())] : [],
            VolumeSources = c.TryGetProperty("q_volume_W_m3", out var q) ? [new VolumeSource(0, q.GetDouble())] : [],
        };
        var s = ThermalSolver.Solve(p, Direct);
        var field = new ThermalField(mesh, s.Temperature);
        double tBot = field.Surface(new HashSet<int> { TestMeshes.ZMin })!.Value.AvgC;
        double tTop = field.Surface(new HashSet<int> { TestMeshes.ZMax })!.Value.AvgC;
        output.WriteLine($"{name}: bottom {tBot:R} (ref {c.GetProperty("T_bottom_degC").GetDouble():R}), top {tTop:R} (ref {c.GetProperty("T_top_degC").GetDouble():R})");
        Assert.Equal(c.GetProperty("T_bottom_degC").GetDouble(), tBot, 1e-6);
        Assert.Equal(c.GetProperty("T_top_degC").GetDouble(), tTop, 1e-6);
    }

    // ── 3 + 5. S5 spreading, both solvers; the energy balance ──────────────────────────────────────

    /// <summary>brief 72's S5 quarter model at its ladder's 5 µm / 130 µm rung (11.6k unknowns), meshed from the same text.</summary>
    private const string S5Geo = """
        SetFactory("OpenCASCADE");
        Box(1) = {1000, 1000, -100, 1000, 1000, 100};
        Box(2) = {1000, 1000, -1100, 1000, 1000, 1000};
        Rectangle(100) = {1000, 1000, 0, 100, 50};
        BooleanFragments{ Volume{1, 2}; Delete; }{ Surface{100}; Delete; }
        src[] = Surface In BoundingBox{999.999, 999.999, -0.001, 1100.001, 1050.001, 0.001};
        bot[] = Surface In BoundingBox{999.999, 999.999, -1100.001, 2000.001, 2000.001, -1099.999};
        v1[] = Volume In BoundingBox{999.999, 999.999, -100.001, 2000.001, 2000.001, 0.001};
        v2[] = Volume In BoundingBox{999.999, 999.999, -1100.001, 2000.001, 2000.001, -99.999};
        Physical Volume("layer1") = {v1[]};
        Physical Volume("layer2") = {v2[]};
        Physical Surface("source") = {src[]};
        Physical Surface("bottom") = {bot[]};
        Mesh.MeshSizeMax = 130;
        Mesh.MeshSizeFromPoints = 0;
        Mesh.MeshSizeExtendFromBoundary = 0;
        Mesh.MeshSizeFromCurvature = 0;
        Field[1] = Distance; Field[1].SurfacesList = {src[]}; Field[1].Sampling = 60;
        Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = 5; Field[2].SizeMax = 130;
        Field[2].DistMin = 5; Field[2].DistMax = 330;
        Background Field = 2;
        """;

    [GmshFact]
    public void Gate3And5_S5Spreading_BothSolversMeetTheReference_AgreeAndBalance()
    {
        var (mesh, raw) = TestMeshes.Mesh(S5Geo, ["layer1", "layer2"]);
        var reference = JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("spreading", "spreading.json"))).RootElement.GetProperty("reference");
        double centre = reference.GetProperty("T_centre_degC").GetDouble(), mean = reference.GetProperty("T_mean_source_degC").GetDouble();
        int src = TestMeshes.Tag(raw, "source"), bot = TestMeshes.Tag(raw, "bottom");
        // 1 W over the whole 200 × 100 µm source; the quarter model carries a quarter of it
        var p = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(150), ThermalConductivity.Constant(390)],
            SurfaceSources = [new SurfaceSource(src, 1.0 / (200e-6 * 100e-6))],
            Fixed = [new FixedTemperature(bot, 25)],
        };
        var direct = ThermalSolver.Solve(p, Direct);
        var iterative = ThermalSolver.Solve(p, new ThermalSolveOptions { Solver = ThermalSolverKind.Iterative });
        double diff = direct.Temperature.Zip(iterative.Temperature, (a, b) => Math.Abs(a - b)).Max();
        double rise = direct.Temperature.Max() - 25;
        foreach (var (s, label) in new[] { (direct, "direct"), (iterative, "iterative") })
        {
            var f = new ThermalField(mesh, s.Temperature);
            double c = f.At(1e-3, 1e-3, 0)!.Value, m = f.Surface(new HashSet<int> { src })!.Value.AvgC;
            output.WriteLine($"{label}: {s.Unknowns} unknowns, {s.LinearIterations} its; centre {c:F5} ({100 * (c - centre) / (centre - 25):+0.000;-0.000} %), " +
                             $"mean {m:F5} ({100 * (m - mean) / (mean - 25):+0.000;-0.000} %); balance {s.BalanceRelative:G3} " +
                             $"(in {s.SourcePowerW:R} W, out {s.FixedHeatOutW:R} W)");
            // brief 72's ladder: every rung from 2.5 µm down is within 0.1 % of the series, and this rung's own FEM was −0.024 % / −0.060 %
            Assert.True(Math.Abs(c - centre) <= 1e-3 * (centre - 25), $"{label} centre {c}");
            Assert.True(Math.Abs(m - mean) <= 1e-3 * (mean - 25), $"{label} mean {m}");
            Assert.True(s.BalanceRelative <= ThermalSolver.BalanceTolerance, $"{label} balance {s.BalanceRelative}");
            Assert.Equal(0.25, s.SourcePowerW, 1e-9);
        }
        output.WriteLine($"direct vs iterative: {diff:G3} K of a {rise:F3} K rise");
        Assert.Equal(ThermalSolverKind.Direct, direct.Solver);
        Assert.Equal(ThermalSolverKind.Iterative, iterative.Solver);
        Assert.True(diff <= 1e-8 * rise, $"the solvers differ by {diff:G3} K");
    }

    // ── 4. S4 k(T): Newton ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("S4a")]
    [InlineData("S4b")]
    public void Gate4_S4KOfT_NewtonReachesTheReference_InAtMostEightSteps(string name)
    {
        var c = Case("kslab", "kslab.json", name);
        double l = c.GetProperty("thickness_L_m").GetDouble(), tb = c.GetProperty("T_bottom_degC").GetDouble();
        double q2 = c.GetProperty("flux_top_W_m2").GetDouble(), want = c.GetProperty("T_top_degC").GetDouble();
        var kspec = c.GetProperty("k");
        ThermalConductivity k;
        if (kspec.GetProperty("form").GetString() == "table")
        {
            var rows = File.ReadLines(TestMeshes.Testdata("kslab", kspec.GetProperty("table").GetString()!))
                           .Where(x => x.Length > 0 && char.IsDigit(x[0]) || x.StartsWith('-'))
                           .Select(x => x.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToList();
            (double, double) At(double t)
            {
                if (t <= rows[0][0]) return (rows[0][1], 0);
                if (t >= rows[^1][0]) return (rows[^1][1], 0);
                int i = 0;
                while (rows[i + 1][0] <= t) i++;
                double s = (rows[i + 1][1] - rows[i][1]) / (rows[i + 1][0] - rows[i][0]);
                return (rows[i][1] + s * (t - rows[i][0]), s);
            }
            k = ThermalConductivity.Varying(At(tb).Item1, At);
        }
        else
        {
            double k0 = kspec.GetProperty("k0_W_mK").GetDouble(), t0 = kspec.GetProperty("T0_degC").GetDouble(), beta = kspec.GetProperty("beta_per_K").GetDouble();
            k = ThermalConductivity.Varying(k0, t => (k0 / (1 + beta * (t - t0)), -k0 * beta / Math.Pow(1 + beta * (t - t0), 2)));
        }
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, 1e-3, 1), TestMeshes.Linspace(0, 1e-3, 1), TestMeshes.Linspace(0, l, 60)).ToSecondOrder();
        var p = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [k],
            SurfaceSources = [new SurfaceSource(TestMeshes.ZMax, q2)],
            Fixed = [new FixedTemperature(TestMeshes.ZMin, tb)],
        };
        var s = ThermalSolver.Solve(p, Direct);
        double top = new ThermalField(mesh, s.Temperature).Surface(new HashSet<int> { TestMeshes.ZMax })!.Value.AvgC;
        output.WriteLine($"{name}: T(L) {top:R} vs {want:R} ({(top - want) / (want - tb):G3} of the rise); {s.NewtonIterations} Newton steps; " +
                         string.Join(" ", s.Notes));
        Assert.True(s.Converged);
        Assert.InRange(s.NewtonIterations, 1, 8);
        Assert.True(Math.Abs(top - want) <= 1e-6 * (want - tb), $"T(L) {top} vs {want}");
    }

    // ── 6. determinism ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The assembled matrix's bits are the same across two assemblies and across thread counts.</summary>
    [Fact]
    public void Gate6_TheAssembledMatrix_IsBitForBitTheSame_AcrossRunsAndThreadCounts()
    {
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, 1e-3, 12), TestMeshes.Linspace(0, 2e-3, 10), TestMeshes.Linspace(0, 5e-4, 6),
                                  (x, y, z) => z > 2.5e-4 ? 1 : 0).ToSecondOrder();
        var p = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(150), ThermalConductivity.Constant(0.3)],
            VolumeSources = [new VolumeSource(1, 1e8)], Convection = [new ConvectionCondition(TestMeshes.ZMin, 1e3, 25)],
        };
        string Hash(int? threads)
        {
            var a = new ThermalAssembly(mesh, threads).Assemble(p, null, kOfT: false, tangent: false);
            var bytes = new byte[8 * (a.Secant.Length + a.Load.Length)];
            Buffer.BlockCopy(a.Secant, 0, bytes, 0, 8 * a.Secant.Length);
            Buffer.BlockCopy(a.Load, 0, bytes, 8 * a.Secant.Length, 8 * a.Load.Length);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        string one = Hash(1), again = Hash(1), many = Hash(8), any = Hash(null);
        output.WriteLine($"{mesh.TetCount} tetrahedra: {one[..16]}…");
        Assert.Equal(one, again);
        Assert.Equal(one, many);
        Assert.Equal(one, any);
    }

    // ── 7. the AMG counter on Q6's contrast case ────────────────────────────────────────────────────

    /// <summary>brief 72 Q6's copper block on FR-4 under a mould cap, its coarsest rung (22,891 unknowns).</summary>
    private const string Q6Geo = """
        SetFactory("OpenCASCADE");
        Box(1) = {-5000, -5000, -1600, 10000, 10000, 1600};
        Box(2) = {-1500, -1500, 0, 3000, 3000, 1000};
        Box(3) = {-2500, -2500, 0, 5000, 5000, 2000};
        Rectangle(1001) = {-500, -500, 1000, 1000, 1000};
        BooleanFragments{ Volume{1, 2, 3}; Delete; }{ Surface{1001}; Delete; }
        e = 1e-3;
        board[] = Volume In BoundingBox{-5000 - e, -5000 - e, -1600 - e, 5000 + e, 5000 + e, e};
        cu[] = Volume In BoundingBox{-1500 - e, -1500 - e, -e, 1500 + e, 1500 + e, 1000 + e};
        all[] = Volume{:};
        mould[] = all[];
        mould[] -= board[];
        mould[] -= cu[];
        src[] = Surface In BoundingBox{-500 - e, -500 - e, 1000 - e, 500 + e, 500 + e, 1000 + e};
        bot[] = Surface In BoundingBox{-5000 - e, -5000 - e, -1600 - e, 5000 + e, 5000 + e, -1600 + e};
        Physical Volume("board") = {board[]};
        Physical Volume("copper") = {cu[]};
        Physical Volume("mould") = {mould[]};
        Physical Surface("source") = {src[]};
        Physical Surface("bottom") = {bot[]};
        Mesh.MeshSizeMax = 1200;
        Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0; Mesh.MeshSizeFromCurvature = 0;
        Field[1] = Box; Field[1].VIn = 300; Field[1].VOut = 1200;
        Field[1].XMin = -2600; Field[1].XMax = 2600; Field[1].YMin = -2600; Field[1].YMax = 2600;
        Field[1].ZMin = -500; Field[1].ZMax = 2100; Field[1].Thickness = 1500;
        Background Field = 1;
        """;

    /// <summary>
    /// brief 72 measured 27 CG iterations at θ = 0 to a relative residual of 1e-8 on this rung (28 by an independent
    /// implementation of the same method). The gate asserts the count stays under 32 at the same tolerance — a counter,
    /// not a time — so a change that quietly weakens the hierarchy (a smoother that is no longer symmetric, a prolongator
    /// left unsmoothed) fails here.
    /// </summary>
    [GmshFact]
    public void Gate7_Q6Contrast_PcgAmgConvergesUnderTheMeasuredCount()
    {
        var (mesh, raw) = TestMeshes.Mesh(Q6Geo, ["board", "copper", "mould"]);
        var p = new ThermalProblem
        {
            Mesh = mesh,
            Conductivity = [ThermalConductivity.Constant(0.3), ThermalConductivity.Constant(390), ThermalConductivity.Constant(0.65)],
            SurfaceSources = [new SurfaceSource(TestMeshes.Tag(raw, "source"), 1e6)],
            Fixed = [new FixedTemperature(TestMeshes.Tag(raw, "bottom"), 25)],
        };
        var s = ThermalSolver.Solve(p, new ThermalSolveOptions { Solver = ThermalSolverKind.Iterative, RelativeTolerance = 1e-8 });
        output.WriteLine($"{s.Unknowns} unknowns, {s.LinearIterations} PCG iterations, {s.AmgLevels} AMG levels");
        Assert.Equal(ThermalSolverKind.Iterative, s.Solver);
        Assert.InRange(s.LinearIterations, 1, 31);
    }
}
