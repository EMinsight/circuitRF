// brief-em3d-80 §6 gates 1–5 — the Rth matrix, Z_th(jω), the Foster fit and the pulse train against brief 72's externally
// generated references (testdata/thermal/fingers, zth-slab, pulse). Gates 6–7 (the refusal, the netlist) are design-side:
// tests/Ui.Tests/ThreeD/ThermalSmallSignalTests.cs.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Thermal.Frequency;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class SmallSignalGateTests(ITestOutputHelper output)
{
    // ── 1. S6: the 8 × 8 Rth matrix ─────────────────────────────────────────────────────────────────

    /// <summary>brief 72's S6 half model at its ladder's 4 µm / 100 µm rung, from the same .geo text make_fingers.py writes.</summary>
    private static string S6Geo(double hs, double hmax, double dist)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "SetFactory(\"OpenCASCADE\");",
            "Box(1) = {0, 500, -100, 1000, 500, 100};",
            "Box(2) = {0, 500, -600, 1000, 500, 500};",
        };
        for (int i = 0; i < 8; i++)
        {
            double x0 = 500 + (i - 3.5) * 40 - 5;
            lines.Add(string.Format(inv, "Rectangle({0}) = {{{1}, 500, 0, 10, 100}};", 100 + i, x0));
        }
        lines.Add("BooleanFragments{ Volume{1, 2}; Delete; }{ Surface{" + string.Join(", ", Enumerable.Range(100, 8)) + "}; Delete; }");
        const double e = 1e-3;
        for (int i = 0; i < 8; i++)
        {
            double x0 = 500 + (i - 3.5) * 40 - 5;
            lines.Add(string.Format(inv, "f{0}[] = Surface In BoundingBox{{{1}, {2}, {3}, {4}, {5}, {6}}};", i, x0 - e, 500 - e, -e, x0 + 10 + e, 600 + e, e));
            lines.Add($"Physical Surface(\"f{i}\") = {{f{i}[]}};");
        }
        lines.Add(string.Format(inv, "bot[] = Surface In BoundingBox{{{0}, {1}, {2}, {3}, {4}, {5}}};", -e, 500 - e, -600 - e, 1000 + e, 1000 + e, -600 + e));
        lines.Add(string.Format(inv, "v1[] = Volume In BoundingBox{{{0}, {1}, {2}, {3}, {4}, {5}}};", -e, 500 - e, -100 - e, 1000 + e, 1000 + e, e));
        lines.Add(string.Format(inv, "v2[] = Volume In BoundingBox{{{0}, {1}, {2}, {3}, {4}, {5}}};", -e, 500 - e, -600 - e, 1000 + e, 1000 + e, -100 + e));
        lines.Add("Physical Volume(\"layer1\") = {v1[]};");
        lines.Add("Physical Volume(\"layer2\") = {v2[]};");
        lines.Add("Physical Surface(\"bottom\") = {bot[]};");
        lines.Add(string.Format(inv, "Mesh.MeshSizeMax = {0};", hmax));
        lines.Add("Mesh.MeshSizeFromPoints = 0;");
        lines.Add("Mesh.MeshSizeExtendFromBoundary = 0;");
        lines.Add("Mesh.MeshSizeFromCurvature = 0;");
        lines.Add("Field[1] = Distance; Field[1].SurfacesList = {" + string.Join(", ", Enumerable.Range(0, 8).Select(i => $"f{i}[]")) + "}; Field[1].Sampling = 80;");
        lines.Add(string.Format(inv, "Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = {0}; Field[2].SizeMax = {1};", hs, hmax));
        lines.Add(string.Format(inv, "Field[2].DistMin = {0}; Field[2].DistMax = {1};", hs, dist));
        lines.Add("Background Field = 2;");
        return string.Join("\n", lines) + "\n";
    }

    [GmshFact]
    public void Gate1_S6Fingers_TheRthMatrixMeetsTheReference_AndAvgIsSymmetric()
    {
        var (mesh, raw) = TestMeshes.Mesh(S6Geo(4, 100, 220), ["layer1", "layer2"]);
        var problem = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(370), ThermalConductivity.Constant(200)],
            Fixed = [new FixedTemperature(TestMeshes.Tag(raw, "bottom"), 25)],
        };
        var sources = Enumerable.Range(0, 8).Select(i => new SmallSignalSource($"f{i}", [TestMeshes.Tag(raw, $"f{i}")], [])).ToList();
        var ss = new ThermalSmallSignal(problem, sources, null, kOfT: false);
        var rth = ss.Rth(RthStatistic.Avg, new ThermalSolveOptions());

        // the half model's sources carry the power of the modelled half: per watt in the WHOLE finger is half of it
        var reference = ReadMatrix(TestMeshes.Testdata("fingers", "rth-matrix.csv"));
        var rung = File.ReadAllLines(TestMeshes.Testdata("fingers", "ladder.csv")).Skip(1).Select(l => l.Split(',')).First(c => c[0] == "4");
        double worst = 0;
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++) worst = Math.Max(worst, Math.Abs(rth.R[i, j] / 2 - reference[i, j]) / reference[i, j]);
        double[] mine = [rth.R[0, 0] / 2, rth.R[0, 1] / 2, rth.R[0, 7] / 2, rth.R[3, 3] / 2];
        double[] theirs = [.. rung.Skip(3).Take(4).Select(v => double.Parse(v, CultureInfo.InvariantCulture))];
        double rungWorst = mine.Zip(theirs, (a, b) => Math.Abs(a - b) / b).Max();
        output.WriteLine($"{rth.Unknowns} unknowns, {rth.Solver}; R11 {mine[0]:F6} R12 {mine[1]:F6} R18 {mine[2]:F6} R44 {mine[3]:F6}");
        output.WriteLine($"against the finest rung: worst {100 * worst:F3} %; against brief 72's own FEM on this rung: worst {rungWorst:G3}; " +
                         $"asymmetry {rth.Asymmetry:G3}");
        Assert.True(worst <= 0.01, $"worst {100 * worst:F3} % from the finest rung (the 4/100 rung moved 1.25 % on refinement)");
        Assert.True(rungWorst <= 2e-4, $"worst {rungWorst:G3} against the reference's own 4/100 rung");
        Assert.True(rth.Asymmetry < 1e-9, $"asymmetry {rth.Asymmetry:G3}");
    }

    private static double[,] ReadMatrix(string path)
    {
        var rows = File.ReadAllLines(path).Where(l => !l.StartsWith('#')).Skip(1).Select(l => l.Split(',').Skip(1)
                           .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        var m = new double[rows.Length, rows.Length];
        for (int i = 0; i < rows.Length; i++) for (int j = 0; j < rows.Length; j++) m[i, j] = rows[i][j];
        return m;
    }

    // ── 2. superposition ────────────────────────────────────────────────────────────────────────────

    /// <summary>Three sources on a two-layer block, a hot bottom and a convection side: R·P is the direct solve with every
    /// power on, less the zero-power baseline.</summary>
    [Fact]
    public void Gate2_Superposition_RTimesP_IsTheDirectSolve()
    {
        var mesh = TestMeshes.Box(TestMeshes.Linspace(0, 400e-6, 8), TestMeshes.Linspace(0, 200e-6, 4), TestMeshes.Linspace(0, 150e-6, 5),
                                  (_, _, z) => z > 100e-6 ? 1 : 0).ToSecondOrder();
        const int s1 = 11, s2 = 12, s3 = 13;
        mesh = MeshEdits.Retag(mesh, TestMeshes.ZMax, s1, (x, _, _) => x < 100e-6);
        mesh = MeshEdits.Retag(mesh, TestMeshes.ZMax, s2, (x, _, _) => x > 150e-6 && x < 250e-6);
        mesh = MeshEdits.Retag(mesh, TestMeshes.ZMax, s3, (x, _, _) => x > 300e-6);
        var baseProblem = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(150), ThermalConductivity.Constant(40)],
            Fixed = [new FixedTemperature(TestMeshes.ZMin, 40)],
            Convection = [new ConvectionCondition(TestMeshes.XMax, 5e4, 30)],
        };
        int[] tags = [s1, s2, s3];
        var sources = tags.Select((t, i) => new SmallSignalSource($"s{i}", [t], [])).ToList();
        var ss = new ThermalSmallSignal(baseProblem, sources, null, kOfT: false);
        var rth = ss.Rth(RthStatistic.Avg, new ThermalSolveOptions { Solver = ThermalSolverKind.Direct });
        double[] p = [0.7, 1.9, 0.25];
        double[] area = [.. tags.Select(t => new ThermalField(mesh, new double[mesh.NodeCount]).Surface(new HashSet<int> { t })!.Value.Measure)];

        var on = new ThermalProblem
        {
            Mesh = mesh, Conductivity = baseProblem.Conductivity, Fixed = baseProblem.Fixed, Convection = baseProblem.Convection,
            SurfaceSources = [.. tags.Select((t, i) => new SurfaceSource(t, p[i] / area[i]))],
        };
        var hot = new ThermalField(mesh, ThermalSolver.Solve(on, new ThermalSolveOptions { Solver = ThermalSolverKind.Direct }).Temperature);
        var cold = new ThermalField(mesh, ThermalSolver.Solve(baseProblem, new ThermalSolveOptions { Solver = ThermalSolverKind.Direct }).Temperature);
        double worst = 0;
        for (int i = 0; i < 3; i++)
        {
            double direct = hot.Surface(new HashSet<int> { tags[i] })!.Value.AvgC - cold.Surface(new HashSet<int> { tags[i] })!.Value.AvgC;
            double rp = 0;
            for (int j = 0; j < 3; j++) rp += rth.R[i, j] * p[j];
            output.WriteLine($"source {i}: R·P {rp:R} K, direct {direct:R} K");
            worst = Math.Max(worst, Math.Abs(rp - direct) / direct);
        }
        Assert.True(worst <= 1e-9, $"R·P and the direct solve differ by {worst:G3}");
        Assert.True(rth.Asymmetry < 1e-9, $"asymmetry {rth.Asymmetry:G3}");
    }

    // ── 3 + 4. Z1: Z_th(jω) of a slab, both solver paths; the Foster fit ─────────────────────────────

    private sealed record Slab(string Name, double L, double K, double Rho, double C);

    private static Slab Z1(string material)
    {
        var m = JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("zth-slab", "zth-slab.json"))).RootElement.GetProperty("materials").GetProperty(material);
        return new Slab(material, m.GetProperty("thickness_m").GetDouble(), m.GetProperty("k_W_mK").GetDouble(),
                        m.GetProperty("density_kg_m3").GetDouble(), m.GetProperty("cp_J_kgK").GetDouble());
    }

    /// <summary>A one-cell-wide column through the slab's thickness (x), graded geometrically from the heated face so the
    /// penetration depth is resolved at 10 MHz: heated face XMin, fixed face XMax, the sides insulated — the 1D problem in 3D.</summary>
    private static (ThermalSmallSignal System, double Area) SlabSystem(Slab s)
    {
        double h0 = s.L * 5e-4, growth = 1.15;
        var xs = new List<double> { 0 };
        while (xs[^1] < s.L) xs.Add(xs[^1] + h0 * Math.Pow(growth, xs.Count - 1));
        double scale = s.L / xs[^1];
        double side = s.L / 20;
        var mesh = TestMeshes.Box([.. xs.Select(x => x * scale)], [0, side], [0, side]).ToSecondOrder();
        var problem = new ThermalProblem
        {
            Mesh = mesh, Conductivity = [ThermalConductivity.Constant(s.K)], Fixed = [new FixedTemperature(TestMeshes.XMax, 25)],
        };
        return (new ThermalSmallSignal(problem, [new SmallSignalSource("front", [TestMeshes.XMin], [])], null, kOfT: false), side * side);
    }

    private static List<(double F, Complex Z)> Reference(string material)
        => [.. File.ReadAllLines(TestMeshes.Testdata("zth-slab", $"zth-{material}.csv")).Where(l => !l.StartsWith('#')).Skip(1)
                .Select(l => l.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray())
                .Select(v => (v[0], new Complex(v[1], v[2])))];

    [Theory]
    [InlineData("silicon")]
    [InlineData("copper")]
    public void Gate3_Z1Slab_ZthMeetsTanhOverKGamma_ByBothSolverPaths(string material)
    {
        var s = Z1(material);
        var reference = Reference(material);
        var pick = Enumerable.Range(0, 30).Select(i => reference[i * (reference.Count - 1) / 29]).ToList();
        var (system, area) = SlabSystem(s);
        double[] freqs = [.. pick.Select(p => p.F)];
        foreach (var kind in new[] { ThermalSolverKind.Direct, ThermalSolverKind.Iterative })
        {
            var z = system.Zth([s.Rho * s.C], freqs, new ThermalSolveOptions { Solver = kind });
            double worst = 0;
            for (int k = 0; k < pick.Count; k++)
                worst = Math.Max(worst, (z.Z[k][0, 0] * area - pick[k].Z).Magnitude / pick[k].Z.Magnitude);
            output.WriteLine($"{material}, {kind} ({system.Unknowns} unknowns, {z.MaxIterations} iterations at most, residual " +
                             $"{z.MaxResidual:G3}): worst {worst:G3} over {pick.Count} frequencies");
            Assert.Equal(kind, z.Solver);
            Assert.True(worst <= 1e-4, $"{material} {kind}: worst {worst:G3}");
        }
    }

    [Fact]
    public void Gate4_Z1Fit_UnderOnePercent_NonNegative_SumIsTheRth()
    {
        var s = Z1("silicon");
        var (system, _) = SlabSystem(s);
        var freqs = new List<double> { 0 };
        for (int i = 0; i <= 80; i++) freqs.Add(0.01 * Math.Pow(10, i / 10.0));   // the brief's default band, 0.01 Hz – 1 MHz, 10 per decade
        var z = system.Zth([s.Rho * s.C], freqs, new ThermalSolveOptions { Solver = ThermalSolverKind.Direct });
        var values = z.Z.Select(m => m[0, 0]).ToList();
        var fit = FosterFit.Fit(freqs, values);
        double rth = values[0].Real;
        output.WriteLine($"{fit.Terms.Count} terms; fit error {100 * fit.FitError:F4} %; ΣR {fit.Rth:R} against Rth {rth:R}");
        foreach (var t in fit.Terms) output.WriteLine($"  R {t.R:G6} K/W, τ {t.Tau:G4} s");
        Assert.True(fit.FitError < 0.01, $"fit error {fit.FitError:G3}");
        Assert.All(fit.Terms, t => Assert.True(t.R >= 0));
        Assert.True(Math.Abs(fit.Rth - rth) <= 1e-6 * rth, $"ΣR {fit.Rth} against {rth}");
        Assert.True(Math.Abs(values[0].Imaginary) <= 1e-12 * rth);
    }

    // ── 5. Z2: the pulse train in closed form ───────────────────────────────────────────────────────

    private static (FosterNetwork Network, double P, double Period, double TOn, double Peak) Z2(string name)
    {
        var c = JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("pulse", "pulse.json"))).RootElement.GetProperty("cases").GetProperty(name);
        var terms = c.GetProperty("foster").EnumerateArray().Select(t => new FosterTerm(t.GetProperty("R_K_W").GetDouble(), t.GetProperty("tau_s").GetDouble())).ToList();
        return (new FosterNetwork { Terms = terms, FitError = 0 }, c.GetProperty("P_W").GetDouble(), c.GetProperty("period_s").GetDouble(),
                c.GetProperty("t_on_s").GetDouble(), c.GetProperty("dT_peak_closed_form_K").GetDouble());
    }

    [Theory]
    [InlineData("Z2a")]
    [InlineData("Z2b")]
    public void Gate5_Z2Pulse_TheClosedFormMeetsTheReference_AndItsLimits(string name)
    {
        var (net, p, period, tOn, peak) = Z2(name);
        PulseDrive[] drive = [new(p, net)];
        double mine = PulseTrain.Peak(drive, period, tOn);
        Assert.True(Math.Abs(mine - peak) <= 1e-9 * peak, $"peak {mine:R} against {peak:R}");

        var rows = File.ReadAllLines(TestMeshes.Testdata("pulse", $"pulse-{name}.csv")).Where(l => !l.StartsWith('#')).Skip(1)
                       .Select(l => l.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToList();
        var wave = PulseTrain.Waveform(drive, period, tOn, [.. rows.Select(r => r[0])]);
        double worst = rows.Select((r, k) => Math.Abs(wave[k] - r[1]) / peak).Max();
        output.WriteLine($"{name}: peak {mine:R} K (reference {peak:R}); waveform worst {worst:G3} of the peak over {rows.Count} points");
        Assert.True(worst <= 1e-9, $"waveform worst {worst:G3}");

        // duty → 1 is the steady DC rise; duty → 0 is nothing; the average is duty × P × Rth
        Assert.Equal(p * net.Rth, PulseTrain.Peak(drive, period, period), 1e-12 * p * net.Rth);
        Assert.Equal(p * net.Rth, PulseTrain.Peak(drive, period, period * (1 - 1e-12)), 1e-9 * p * net.Rth);
        Assert.Equal(0, PulseTrain.Peak(drive, period, 0));
        Assert.True(PulseTrain.Peak(drive, period, period * 1e-12) <= 1e-9 * p * net.Rth);
        Assert.Equal(tOn / period * p * net.Rth, PulseTrain.Average(drive, period, tOn), 1e-12 * p * net.Rth);
        Assert.True(PulseTrain.Single(drive, tOn) < mine);
    }
}
