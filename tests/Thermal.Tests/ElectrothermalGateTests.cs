// brief-em3d-77 §7 — conductive balance against brief 72's externally generated wire references (testdata/thermal/wire-*),
// at the solver: gates 1 (W1), 2 (runaway), 3 (W2), 4 (Newton against Picard), 7 (W4, meshed by Gmsh), 9 (the current share)
// and 10 (a contact is one equipotential). Each wire runs between two copper pads whose top faces are held at the ends'
// temperature: the feet lie on them through their contact patches, so the heels are the references' fixed ends.

using System.Globalization;
using System.Text.Json;
using CircuitRF.Thermal.Electrothermal;
using CircuitRF.Thermal.Nonlinear;
using Xunit.Abstractions;

namespace CircuitRF.Thermal.Tests;

public sealed class ElectrothermalGateTests(ITestOutputHelper output)
{
    private static readonly ThermalSolveOptions Direct = new() { Solver = ThermalSolverKind.Direct };

    private const double Um = 1e-6;
    private const int PatchA = 10, PatchB = 30, BottomA = TestMeshes.ZMin, BottomB = TestMeshes.ZMin + 20, TopA = TestMeshes.ZMax,
                      TopB = TestMeshes.ZMax + 20;

    // ── the bench ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A straight wire of diameter <paramref name="d"/> between heels L apart, a 50 µm foot on each copper pad, the pads'
    /// tops held at the ends' temperatures, the current in through pad A's bottom and out through pad B's.</summary>
    internal static ElectrothermalProblem Bench(double d, double l, double ta, double tb, double current, ThermalConductivity k,
                                               ElectricalConductivity sigma, int span = 50, double footLen = 50 * Um)
    {
        double r = d / 2;
        var a = TestMeshes.Box([-100 * Um, -50 * Um, -25 * Um, 0], [-50 * Um, -r, r, 50 * Um], [-50 * Um, -25 * Um, 0]);
        var b = TestMeshes.Box([l, l + 25 * Um, l + 50 * Um, l + 100 * Um], [-50 * Um, -r, r, 50 * Um], [-50 * Um, -25 * Um, 0]);
        var m = MeshEdits.Merge(a, b, 20);
        m = MeshEdits.Retag(m, TopA, PatchA, (x, y, _) => x > -footLen && x < 0 && Math.Abs(y) < r);
        m = MeshEdits.Retag(m, TopB, PatchB, (x, y, _) => x > l && x < l + footLen && Math.Abs(y) < r);
        m = m.ToSecondOrder();
        var wire = Chain("w1", [(-footLen, 0, r), (0, 0, r), (l, 0, r), (l + footLen, 0, r)], [1, span, 1], d, k, sigma);
        int last = wire.NodeCount - 1;
        wire = With(wire, contacts: [new WireContact(PatchA, 2, 2, 0), new WireContact(PatchB, last - 2, last - 2, 0)], onPad: Feet(wire.Elements));
        return new ElectrothermalProblem
        {
            Thermal = new ThermalProblem
            {
                Mesh = m, Conductivity = [ThermalConductivity.Constant(400)],
                Fixed = [new FixedTemperature(TopA, ta), new FixedTemperature(PatchA, ta), new FixedTemperature(TopB, tb), new FixedTemperature(PatchB, tb)],
            },
            Wires = [wire], Sigma = [ElectricalConductivity.Constant(5.8e7)],
            Currents = [new CurrentTerminal(1, [BottomA], [BottomB], current)],
        };
    }

    private static bool[] Feet(int elements)
    {
        var f = new bool[elements];
        f[0] = f[^1] = true;
        return f;
    }

    /// <summary>A second-order chain along a polyline, <paramref name="per"/> elements on each segment; s is 0 at vertex 1 (the
    /// start heel) when the chain starts with a foot, else at vertex 0.</summary>
    internal static ThermalWire Chain(string name, (double X, double Y, double Z)[] path, int[] per, double d, ThermalConductivity k,
                                      ElectricalConductivity sigma, bool startFoot = true)
    {
        var pts = new List<double>();
        var s = new List<double>();
        double arc = 0;
        void Add((double X, double Y, double Z) p, double at) { pts.AddRange([p.X, p.Y, p.Z]); s.Add(at); }
        Add(path[0], 0);
        for (int g = 0; g + 1 < path.Length; g++)
        {
            var (p, q) = (path[g], path[g + 1]);
            double len = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y) + (q.Z - p.Z) * (q.Z - p.Z));
            for (int j = 1; j <= 2 * per[g]; j++)
            {
                double f = (double)j / (2 * per[g]);
                Add((p.X + f * (q.X - p.X), p.Y + f * (q.Y - p.Y), p.Z + f * (q.Z - p.Z)), arc + f * len);
            }
            arc += len;
        }
        double s0 = startFoot ? s[2 * per[0]] : 0;
        return new ThermalWire
        {
            Name = name, Points = [.. pts], S = [.. s.Select(v => v - s0)], Area = Math.PI * d * d / 4, Diameter = d, K = k, Sigma = sigma,
        };
    }

    internal static ThermalWire With(ThermalWire w, IReadOnlyList<WireContact>? contacts = null, bool[]? onPad = null, double? h = null, double amb = 0)
        => new()
        {
            Name = w.Name, Points = w.Points, S = w.S, Area = w.Area, Diameter = w.Diameter, K = w.K, Sigma = w.Sigma,
            Contacts = contacts ?? w.Contacts, OnPad = onPad ?? w.OnPad, ConvectionH = h ?? w.ConvectionH, AmbientC = h is null ? w.AmbientC : amb,
        };

    /// <summary>The span nodes of a bench wire: from the start heel (node 2) to the end heel.</summary>
    private static IEnumerable<int> SpanNodes(ThermalWire w) => Enumerable.Range(2, w.NodeCount - 4);

    /// <summary>A reference CSV's rows: its columns by header, lines starting '#' skipped.</summary>
    private static List<Dictionary<string, double>> Csv(params string[] path)
    {
        var lines = File.ReadAllLines(TestMeshes.Testdata(path)).Where(l => l.Length > 0 && l[0] != '#').ToList();
        var head = lines[0].Split(',');
        return [.. lines.Skip(1).Select(l => l.Split(',')).Select(c => head.Select((h, i) => (h, ok: double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v), v))
                                                                         .Where(p => p.ok).ToDictionary(p => p.h, p => p.v))];
    }

    private static (double Rho0, double Alpha, double K) W1Parameters(string metal)
    {
        var p = JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("wire-rho", "wire-rho.json"))).RootElement.GetProperty("metals").GetProperty(metal).GetProperty("parameters");
        return (p.GetProperty("rho0_Ohm_m").GetDouble(), p.GetProperty("alpha_per_K").GetDouble(), p.GetProperty("k_W_mK").GetDouble());
    }

    // ── gate 1: W1 ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>W1 at 0.25, 0.5 and 0.9 of I* (gold, 1 mil, 1 mm, 25 °C ends; k constant): T along the span to 1e-5 of the
    /// rise, and the Newton steps, from a cold start, at most the first passing run's (3, 4 and 7).</summary>
    [Theory]
    [InlineData(0.25, 3)]
    [InlineData(0.5, 4)]
    [InlineData(0.9, 7)]
    public void Gate1_W1_ClosedForm_AlongTheWire(double fraction, int steps)
    {
        var (rho0, alpha, k) = W1Parameters("gold");
        var rows = Csv("wire-rho", "gold.csv").Where(r => r["d [m]"] == 2.54e-5 && r["L [m]"] == 1e-3 && r["T_end [degC]"] == 25 && r["I/I* [1]"] == fraction).ToList();
        Assert.Equal(101, rows.Count);
        var p = Bench(2.54e-5, 1e-3, 25, 25, rows[0]["I [A]"], ThermalConductivity.Constant(k), ElectricalConductivity.LinearResistivity(rho0, alpha));
        var s = ConductiveBalance.Solve(p, Direct with { KOfT = false });
        Assert.True(s.Converged, s.Failure);
        var t = s.WireTemperature[0];
        var w = p.Wires[0];
        double rise = rows.Max(r => r["T [degC]"]) - 25, worst = 0;
        foreach (var r in rows)
        {
            int node = 2 + (int)Math.Round(r["s [m]"] / 1e-3 * 100);
            Assert.Equal(r["s [m]"], w.S[node], 1e-15);
            worst = Math.Max(worst, Math.Abs(t[node] - r["T [degC]"]));
        }
        output.WriteLine($"W1 gold {fraction} I*: {s.Iterations} Newton step(s); worst |ΔT| {worst:G3} K on a {rise:G6} K rise ({worst / rise:G3}); " +
                         $"balance {s.Thermal.BalanceRelative:G3}; V {s.PortVoltage[0]:G6} V; I_wire {s.WireCurrentA[0]:G8} A");
        Assert.True(worst <= 1e-5 * rise, $"worst {worst:G3} K on a {rise:G6} K rise");
        Assert.True(s.Thermal.BalanceRelative <= 1e-6, $"balance {s.Thermal.BalanceRelative:G3}");
        Assert.Equal(rows[0]["I [A]"], s.WireCurrentA[0], 1e-6 * rows[0]["I [A]"]);
        Assert.True(s.Iterations <= steps, $"{s.Iterations} Newton steps, more than the {steps} the first passing run took");
    }

    // ── gate 2: runaway ─────────────────────────────────────────────────────────────────────────────

    /// <summary>W1's sweep past I* (gold, 1 mil, 1 mm, 25 °C ends): the continuation brackets the analytical I* within 0.5 %.</summary>
    [Fact]
    public void Gate2_W1_PastIStar_IsARunawayBracketingIt()
    {
        var (rho0, alpha, k) = W1Parameters("gold");
        double a = Math.PI * 2.54e-5 * 2.54e-5 / 4;
        double iStar = Math.PI / 1e-3 * a * Math.Sqrt(k / (rho0 * alpha));
        ElectrothermalProblem At(double i) => Bench(2.54e-5, 1e-3, 25, 25, i, ThermalConductivity.Constant(k), ElectricalConductivity.LinearResistivity(rho0, alpha), span: 20);
        var o = Direct with { KOfT = false };
        var sys = new ElectrothermalSystem(At(0));
        double[] sweep = [0.5 * iStar, 0.9 * iStar, 1.13 * iStar];
        ElectrothermalSolution? last = null;
        double from = 0;
        ContinuationResult? run = null;
        int total = 0;
        foreach (double target in sweep)
        {
            double f = from;
            run = Continuation.Advance(l => At(f + l * (target - f)), sys, last, o, (lo, hi) => (hi - lo) * (target - f) / (f + hi * (target - f)));
            total += run.Solves;
            if (run.Runaway) { from = f; break; }
            last = run.Solution;
            from = target;
        }
        Assert.NotNull(run);
        Assert.True(run!.Runaway, "the sweep past I* converged");
        double lo = from + run.ConvergedAt * (sweep[^1] - from), hi = from + run.FailedAt * (sweep[^1] - from);
        output.WriteLine($"I* = {iStar:G8} A; bracket [{lo:G8}, {hi:G8}] A ({(hi - lo) / hi:G3} wide), {total} solve(s); " +
                         $"last converged centre {run.LastConverged!.WireTemperature[0].Max():G6} °C");
        Assert.InRange(iStar, lo, hi);
        Assert.True((hi - lo) / hi <= 0.005);
    }

    // ── gate 3: W2 ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A metal's ρ(T) and k(T) tables (testdata/thermal/metals): piecewise-linear, held at the ends, as the references
    /// interpolate them.</summary>
    private static (ElectricalConductivity Sigma, ThermalConductivity K) Tables(string metal)
    {
        var rows = Csv("metals", metal + ".csv");
        double[] t = [.. rows.Select(r => r["T [degC]"])], rho = [.. rows.Select(r => r["rho [Ohm m]"])], kk = [.. rows.Select(r => r["k [W/(m K)]"])];
        (double V, double Slope) Pl(double[] y, double at)
        {
            if (at <= t[0]) return (y[0], 0);
            if (at >= t[^1]) return (y[^1], 0);
            int i = 0;
            while (i < t.Length - 2 && t[i + 1] <= at) i++;
            double sl = (y[i + 1] - y[i]) / (t[i + 1] - t[i]);
            return (y[i] + sl * (at - t[i]), sl);
        }
        var sigma = ElectricalConductivity.Varying(1 / Pl(rho, 20).V, at => { var (r, dr) = Pl(rho, at); return (1 / r, -dr / (r * r)); });
        var k = ThermalConductivity.Varying(Pl(kk, 25).V, at => Pl(kk, at));
        return (sigma, k);
    }

    /// <summary>
    /// The steady profile of a wire with ρ(T) and k(T) between equal ends, from its first integral (a quadrature, not a finite-
    /// element solve): with the Kirchhoff variable u = ∫k dT, ½A²(u′)² = I²∫ρk dT from T to the centre's T_c, so
    /// s(T) = ∫ k dT / √(2I²/A² ∫ρk dT). <paramref name="kOf"/> is k(T); T_c is found so s(T_end) = L/2.
    /// </summary>
    private static Func<double, double> QuadratureProfile(Func<double, double> rhoOf, Func<double, double> kOf, double a, double i, double l, double tEnd)
    {
        double SAt(double tc, double t)
        {
            // s from the centre to temperature t: substitute T = tc − w², so the √ singularity at the centre is integrable
            int n = 600;
            double wMax = Math.Sqrt(tc - t), sum = 0;
            for (int j = 0; j < n; j++)
            {
                double w0 = wMax * j / n, w1 = wMax * (j + 1) / n, wm = (w0 + w1) / 2;
                double tm = tc - wm * wm;
                double inner = Integrate(z => rhoOf(z) * kOf(z), tm, tc, 16);
                sum += kOf(tm) * 2 * wm / Math.Sqrt(2 * i * i / (a * a) * inner) * (w1 - w0);
            }
            return sum;
        }
        double lo = tEnd + 1e-9, hi = tEnd + 5000;
        for (int it = 0; it < 60; it++)
        {
            double mid = (lo + hi) / 2;
            if (SAt(mid, tEnd) < l / 2) lo = mid; else hi = mid;
        }
        double tcentre = (lo + hi) / 2;
        return s =>
        {
            double x = Math.Abs(s - l / 2);
            if (x <= 0) return tcentre;
            double a0 = tEnd, b0 = tcentre;
            for (int it = 0; it < 50; it++)
            {
                double mid = (a0 + b0) / 2;
                if (SAt(tcentre, mid) > x) a0 = mid; else b0 = mid;
            }
            return (a0 + b0) / 2;
        };
    }

    private static double Integrate(Func<double, double> f, double a, double b, int n)
    {
        double h = (b - a) / n, s = 0;
        for (int j = 0; j < n; j++)
        {
            double m = a + (j + 0.5) * h;
            s += f(m - h / (2 * Math.Sqrt(3))) + f(m + h / (2 * Math.Sqrt(3)));
        }
        return s * h / 2;
    }

    /// <summary>
    /// W2 (gold, 1 mil, 1 mm, 25 °C ends, 0.5 of its limit, both tables): the BVP reference along the wire; and with k(T) off,
    /// the peak moves by what the tables' own first integral predicts — both runs gated.
    /// </summary>
    [Fact]
    public void Gate3_W2_TheTables_AndKOfTOff()
    {
        var rows = Csv("wire-rho-k", "gold.csv").Where(r => r["d [m]"] == 2.54e-5 && r["L [m]"] == 1e-3 && r["T_end [degC]"] == 25 && r["I/I_limit [1]"] == 0.5).ToList();
        Assert.Equal(101, rows.Count);
        double i = rows[0]["I [A]"];
        var (sigma, k) = Tables("gold");
        var on = ConductiveBalance.Solve(Bench(2.54e-5, 1e-3, 25, 25, i, k, sigma, span: 100), Direct);
        Assert.True(on.Converged, on.Failure);
        var t = on.WireTemperature[0];
        double rise = rows.Max(r => r["T [degC]"]) - 25, worst = 0;
        foreach (var r in rows) worst = Math.Max(worst, Math.Abs(t[2 + 2 * (int)Math.Round(r["s [m]"] / 1e-5)] - r["T [degC]"]));

        var off = ConductiveBalance.Solve(Bench(2.54e-5, 1e-3, 25, 25, i, k, sigma, span: 100), Direct with { KOfT = false });
        Assert.True(off.Converged, off.Failure);
        double kNom = k.Nominal, a = Math.PI * 2.54e-5 * 2.54e-5 / 4;
        var ks = k.OfT!;
        Func<double, double> rhoOf = at => 1 / sigma.OfT!(at).Sigma;
        double refOn = rows.Max(r => r["T [degC]"]);
        double refOff = QuadratureProfile(rhoOf, _ => kNom, a, i, 1e-3, 25)(0.5e-3);
        double refOnQ = QuadratureProfile(rhoOf, at => ks(at).K, a, i, 1e-3, 25)(0.5e-3);
        double peakOn = on.WireTemperature[0].Max(), peakOff = off.WireTemperature[0].Max();
        output.WriteLine($"W2 gold 0.5 I_limit ({i:G6} A): worst |ΔT| {worst:G3} K on {rise:G6} K; peak {peakOn:G8} (ref {refOn:G8}, quadrature {refOnQ:G8}); " +
                         $"k(T) off: peak {peakOff:G8} (quadrature {refOff:G8}); move {peakOff - peakOn:G6} K (predicted {refOff - refOn:G6} K); " +
                         $"{on.Iterations} and {off.Iterations} Newton steps");
        Assert.True(worst <= 1e-4 * rise, $"worst {worst:G3} K on a {rise:G6} K rise");
        Assert.True(Math.Abs(refOnQ - refOn) <= 1e-4 * rise, $"the quadrature {refOnQ} disagrees with the committed reference {refOn}");
        Assert.True(Math.Abs(peakOff - refOff) <= 1e-4 * rise, $"k(T) off: peak {peakOff} against {refOff}");
        Assert.True(Math.Abs((peakOff - peakOn) - (refOff - refOn)) <= 2e-4 * rise);
    }

    // ── gate 4: Newton against Picard ─────────────────────────────────────────────────────────────────

    /// <summary>Where the fixed-point iteration converges (W2's tables at 0.25 of the limit, both switches on), Newton agrees
    /// with it to 1e-9 of the rise at every node, wire and mesh alike.</summary>
    [Fact]
    public void Gate4_NewtonAgreesWithPicard()
    {
        var (sigma, k) = Tables("gold");
        var p = Bench(2.54e-5, 1e-3, 25, 60, 0.6, k, sigma, span: 20);
        var newton = ConductiveBalance.Solve(p, Direct);
        var picard = ConductiveBalance.Picard(p, Direct);
        Assert.True(newton.Converged, newton.Failure);
        Assert.True(picard.Converged, picard.Failure);
        double rise = newton.WireTemperature[0].Max() - 25, worst = 0;
        for (int j = 0; j < newton.State.Length; j++) if (j < newton.State.Length) worst = Math.Max(worst, Math.Abs(newton.State[j] - picard.State[j]) * (j < p.Thermal.Mesh.NodeCount + p.Wires[0].NodeCount ? 1 : 0));
        output.WriteLine($"Newton {newton.Iterations} step(s), Picard {picard.Iterations} sweep(s); worst |ΔT| {worst:G3} K on {rise:G6} K");
        Assert.True(worst <= 1e-9 * rise, $"worst {worst:G3}");
    }

    // ── gate 8: W5 ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>W5 — the published temperature rise and fusing current of the bond-wire paper brief 72 names: the reference folder
    /// holds nothing until the owner supplies the paper, and its numbers are never reconstructed from memory.</summary>
    [Fact(Skip = "W5 waits for the paper brief 72 names (testdata/thermal/shah/README.md): until it arrives there are no published numbers to gate against")]
    public void Gate8_W5_ThePapersCases() => Assert.Fail("W5 has no reference yet.");

    // ── gate 9: the current share ─────────────────────────────────────────────────────────────────────

    /// <summary>Two parallel wires of different length between the same pads share the current in inverse proportion to their
    /// resistances at their solved temperatures (to 1e-6). The pads conduct a thousand times better than copper here: a real
    /// pad's spreading resistance under each foot (≈ ρ/4a, 3e-4 Ω for a 1 mil contact on copper) is in series with each wire and
    /// moves the share by 6e-4 — physics, but not the claim this gate makes.</summary>
    [Fact]
    public void Gate9_ParallelWires_ShareByTheirHotResistances()
    {
        var (sigma, k) = Tables("gold");
        double d = 2.54e-5, r = d / 2, l = 1e-3;
        var p0 = Bench(d, l, 25, 25, 2.0, k, sigma, span: 20);
        var direct = p0.Wires[0];
        // a second wire from pad A to pad B with a 400 µm high loop, bonded 30 µm off the first in y
        double y = 30 * Um;
        var loop = Chain("w2", [(-50 * Um, y, r), (0, y, r), (0, y, 400 * Um), (l, y, 400 * Um), (l, y, r), (l + 50 * Um, y, r)], [1, 8, 20, 8, 1], d, k, sigma);
        var am = TestMeshes.Box([-100 * Um, -50 * Um, -25 * Um, 0], [-50 * Um, -r, r, y - r, y + r, 50 * Um], [-50 * Um, -25 * Um, 0]);
        var bm = TestMeshes.Box([l, l + 25 * Um, l + 50 * Um, l + 100 * Um], [-50 * Um, -r, r, y - r, y + r, 50 * Um], [-50 * Um, -25 * Um, 0]);
        var m = MeshEdits.Merge(am, bm, 20);
        m = MeshEdits.Retag(m, TopA, PatchA, (x, yy, _) => x > -50 * Um && x < 0 && Math.Abs(yy) < r);
        m = MeshEdits.Retag(m, TopB, PatchB, (x, yy, _) => x > l && x < l + 50 * Um && Math.Abs(yy) < r);
        m = MeshEdits.Retag(m, TopA, 11, (x, yy, _) => x > -50 * Um && x < 0 && Math.Abs(yy - y) < r);
        m = MeshEdits.Retag(m, TopB, 31, (x, yy, _) => x > l && x < l + 50 * Um && Math.Abs(yy - y) < r);
        m = m.ToSecondOrder();
        int la = direct.NodeCount - 1, lb = loop.NodeCount - 1;
        var w1 = With(direct, [new WireContact(PatchA, 2, 2, 0), new WireContact(PatchB, la - 2, la - 2, 0)], Feet(direct.Elements));
        var w2 = With(loop, [new WireContact(11, 2, 2, 0), new WireContact(31, lb - 2, lb - 2, 0)], Feet(loop.Elements));
        var p = new ElectrothermalProblem
        {
            Thermal = new ThermalProblem
            {
                Mesh = m, Conductivity = [ThermalConductivity.Constant(400)],
                Fixed = [.. new[] { TopA, PatchA, 11, TopB, PatchB, 31 }.Select(t => new FixedTemperature(t, 25))],
            },
            Wires = [w1, w2], Sigma = [ElectricalConductivity.Constant(5.8e10)], Currents = [new CurrentTerminal(1, [BottomA], [BottomB], 2.0)],
        };
        var s = ConductiveBalance.Solve(p, Direct);
        Assert.True(s.Converged, string.Join(" | ", s.Thermal.Notes));
        double i1 = s.WireCurrentA[0], i2 = s.WireCurrentA[1], r1 = s.WireResistanceOhm[0], r2 = s.WireResistanceOhm[1];
        output.WriteLine($"I1 {i1:G8} A, I2 {i2:G8} A (sum {i1 + i2:G8}); R1 {r1:G8} Ω, R2 {r2:G8} Ω; I1/I2 {i1 / i2:G10}, R2/R1 {r2 / r1:G10}; " +
                         $"peaks {s.WireTemperature[0].Max():G6} and {s.WireTemperature[1].Max():G6} °C");
        Assert.Equal(2.0, i1 + i2, 1e-6);
        Assert.True(Math.Abs(i1 / i2 - r2 / r1) <= 1e-6 * (r2 / r1), $"I1/I2 {i1 / i2:G10} against R2/R1 {r2 / r1:G10}");
    }

    // ── gate 10: a contact is one equipotential ───────────────────────────────────────────────────────

    /// <summary>The current enters pad A through its bottom face: the potential over that face is one value (its spread below
    /// 1e-9 V), and the voltage the port reads is that value against pad B's.</summary>
    [Fact]
    public void Gate10_AContact_IsEquipotential()
    {
        var (rho0, alpha, k) = W1Parameters("gold");
        var p = Bench(2.54e-5, 1e-3, 25, 25, 1.0, ThermalConductivity.Constant(k), ElectricalConductivity.LinearResistivity(rho0, alpha), span: 10);
        var sys = new ElectrothermalSystem(p);
        var s = ConductiveBalance.Solve(p, Direct, sys);
        Assert.True(s.Converged, s.Failure);
        var m = p.Thermal.Mesh;
        var v = new List<double>();
        for (int t = 0; t < m.TriangleCount; t++)
            if (m.TriangleTag[t] == BottomA)
                for (int j = 0; j < m.NodesPerTriangle; j++) v.Add(s.State[sys.TUnknowns + sys.HostPhi[m.Triangles[m.NodesPerTriangle * t + j]]]);
        output.WriteLine($"contact potential {v.Min():G12} .. {v.Max():G12} V; port voltage {s.PortVoltage[0]:G8} V over {s.WireResistanceOhm[0]:G8} Ω of wire");
        Assert.True(v.Max() - v.Min() <= 1e-9);
        Assert.True(s.PortVoltage[0] > 0);
        // no current, both ends at one temperature: the start is the answer, and is taken as it is (no step down from round-off)
        var idle = ConductiveBalance.Solve(Bench(2.54e-5, 1e-3, 25, 25, 0, ThermalConductivity.Constant(k), ElectricalConductivity.LinearResistivity(rho0, alpha), span: 10), Direct);
        Assert.True(idle.Converged, idle.Failure);
        Assert.Equal(0, idle.Iterations);
    }
}

/// <summary>brief-em3d-77 gate 7 — W4: the wire on the axis of a mould cylinder, the cylinder meshed by Gmsh at two sizes.</summary>
public sealed class WellCouplingGateTests(ITestOutputHelper output)
{
    /// <summary>
    /// W4 (gold, 1 mil, 1 mm, r_o = 500 µm, everything at 25 °C, 0.5 of the mould's I*): T along the wire against the coax closed
    /// form, on host meshes of 40 µm and 20 µm at the wire. The mould's axial conductivity is a thousandth of its radial one here,
    /// because the closed form neglects the annulus' axial conduction (its README says so) and at r_o = 500 µm that is not small;
    /// what is left is the coupling itself. The wire ends are the copper end pads', through their faces on the mould.
    /// </summary>
    [GmshTheory]
    [InlineData(40.0)]
    [InlineData(20.0)]
    public void Gate7_W4_TheMould_OnTwoMeshSizes(double h)
    {
        string geo = $$"""
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
            outer[] = Surface In BoundingBox{-501, -501, -e, 501, 501, 1000 + e};
            outer[] -= ifA[];
            outer[] -= ifB[];
            sideA[] = Surface In BoundingBox{-501, -501, -50 - e, 501, 501, e};
            sideA[] -= botA[];
            sideA[] -= ifA[];
            sideB[] = Surface In BoundingBox{-501, -501, 1000 - e, 501, 501, 1050 + e};
            sideB[] -= topB[];
            sideB[] -= ifB[];
            Physical Volume("mould") = {mould[]};
            Physical Volume("padA") = {padA[]};
            Physical Volume("padB") = {padB[]};
            Physical Surface("outer") = {outer[]};
            Physical Surface("ifA") = {ifA[]};
            Physical Surface("ifB") = {ifB[]};
            Physical Surface("botA") = {botA[]};
            Physical Surface("topB") = {topB[]};
            Physical Surface("sides") = {sideA[], sideB[]};
            Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0; Mesh.MeshSizeFromCurvature = 12;
            Field[1] = MathEval; Field[1].F = "Min(150, {{h}} + 0.35 * Sqrt(x * x + y * y))";
            Background Field = 1;
            """;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (mesh, raw) = TestMeshes.Mesh(geo, ["mould", "padA", "padB"]);
        double meshing = clock.Elapsed.TotalSeconds;
        var j = JsonDocument.Parse(File.ReadAllText(TestMeshes.Testdata("wire-mould", "wire-mould.json"))).RootElement;
        double km = j.GetProperty("k_mould_W_mK").GetDouble();
        var par = j.GetProperty("metals").GetProperty("gold").GetProperty("parameters");
        double rho0 = par.GetProperty("rho0_Ohm_m").GetDouble(), alpha = par.GetProperty("alpha_per_K").GetDouble(), k = par.GetProperty("k_W_mK").GetDouble();
        var lines = File.ReadAllLines(TestMeshes.Testdata("wire-mould", "gold.csv")).Where(l => l.Length > 0 && l[0] != '#').Skip(1)
                        .Select(l => l.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray())
                        .Where(c => c[0] == 2.54e-5 && c[1] == 5e-4 && c[2] == 1e-3 && c[3] == 25 && c[4] == 25 && c[5] == 25 && c[6] == 0.5).ToList();
        Assert.Equal(101, lines.Count);
        double current = lines[0][7];
        int T(string name) => TestMeshes.Tag(raw, name);
        var wire = ElectrothermalGateTests.Chain("w", [(0, 0, 0), (0, 0, 1e-3)], [50], 2.54e-5, ThermalConductivity.Constant(k),
                                                 ElectricalConductivity.LinearResistivity(rho0, alpha), startFoot: false);
        int last = wire.NodeCount - 1;
        wire = new ThermalWire
        {
            Name = wire.Name, Points = wire.Points, S = wire.S, Area = wire.Area, Diameter = wire.Diameter, K = wire.K, Sigma = wire.Sigma,
            Contacts = [new WireContact(T("ifA"), 0, 0, 1), new WireContact(T("ifB"), last, last, 2)],
        };
        var p = new ElectrothermalProblem
        {
            Thermal = new ThermalProblem
            {
                Mesh = mesh, Conductivity = [ThermalConductivity.Diagonal(km, km, km * 1e-3), ThermalConductivity.Constant(400), ThermalConductivity.Constant(400)],
                Fixed = [.. new[] { "outer", "botA", "topB", "sides" }.Select(n => new FixedTemperature(T(n), 25))],
            },
            Wires = [wire], Sigma = [null, ElectricalConductivity.Constant(5.8e7), ElectricalConductivity.Constant(5.8e7)],
            Currents = [new CurrentTerminal(1, [T("botA")], [T("topB")], current)],
        };
        var sys = new ElectrothermalSystem(p);
        var s = ConductiveBalance.Solve(p, new ThermalSolveOptions { KOfT = false, Solver = ThermalSolverKind.Iterative, MaxIterations = 300 }, sys);
        output.WriteLine($"solver {s.Thermal.Solver}, lin its {s.Thermal.LinearIterations}; " + string.Join(" | ", s.Thermal.Notes.Where(n => n.Contains("iterative"))));
        Assert.True(s.Converged, s.Failure);
        var t = s.WireTemperature[0];
        double rise = lines.Max(c => c[9]) - 25, worst = 0;
        for (int i = 0; i < lines.Count; i++) worst = Math.Max(worst, Math.Abs(t[i] - lines[i][9]));
        output.WriteLine($"W4 h = {h} µm: {mesh.TetCount:N0} tetrahedra, {mesh.NodeCount:N0} nodes; centre {t[50]:G8} °C (ref {lines[50][9]:G8}); worst |ΔT| " +
                         $"{worst:G4} K on {rise:G6} K ({worst / rise:G3}); hosting {sys.WireHosting[0]}; ring {sys.WellRadius[0].Min * 1e6:G3}..{sys.WellRadius[0].Max * 1e6:G3} µm; {s.Iterations} Newton step(s); balance {s.Thermal.BalanceRelative:G3}; Gmsh {meshing:F1} s, total {clock.Elapsed.TotalSeconds:F1} s");
        Assert.True(worst <= 1e-3 * rise, $"worst {worst:G4} K on {rise:G6} K");
    }
}

/// <summary>A theory that skips, saying so, unless a Gmsh is found.</summary>
public sealed class GmshTheoryAttribute : TheoryAttribute
{
    public GmshTheoryAttribute()
    {
        if (TestMeshes.Gmsh.Value is null) Skip = "needs Gmsh (on PATH, or CIRCUITRF_GMSH)";
    }
}
