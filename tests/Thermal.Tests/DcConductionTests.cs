// brief-emdc-1 §5 tests 1–6 — the DC conduction solve on structured box meshes, contacts given as node sets; no Gmsh.

using CircuitRF.Thermal.Conduction;

namespace CircuitRF.Thermal.Tests;

public sealed class DcConductionTests
{
    private const double Copper = 5.8e7, Z0 = 50;
    private const double L = 10e-3, W = 1e-3, T = 35e-6;

    /// <summary>Bands across y, each <see cref="W"/> wide, <see cref="L"/> long and <see cref="T"/> thick; band k is region
    /// <paramref name="regions"/>[k] (or <paramref name="byX"/>(x, k) when given).</summary>
    private static ThermalMesh Bands(int[] regions, double length = L, int cellsX = 10, Func<double, int, int>? byX = null, double[]? xs = null)
    {
        xs ??= TestMeshes.Linspace(0, length, cellsX);
        var ys = TestMeshes.Linspace(0, regions.Length * W, 2 * regions.Length);
        return TestMeshes.Box(xs, ys, TestMeshes.Linspace(0, T, 1), (x, y, _) =>
        {
            int k = Math.Min(regions.Length - 1, (int)(y / W));
            return byX?.Invoke(x, k) ?? regions[k];
        });
    }

    /// <summary>The nodes of band <paramref name="band"/> (or of every band, −1) where <paramref name="at"/> holds of x.</summary>
    private static int[] Nodes(ThermalMesh m, int band, Func<double, bool>? at = null)
        => [.. Enumerable.Range(0, m.NodeCount).Where(i =>
        {
            double x = m.Nodes[3 * i], y = m.Nodes[3 * i + 1];
            bool inBand = band < 0 || (y >= band * W - 1e-12 && y <= (band + 1) * W + 1e-12);
            return inBand && (at?.Invoke(x) ?? true);
        })];

    private static Func<double, bool> X(double x) => v => Math.Abs(v - x) < 1e-12;

    private static DcConductionProblem Problem(ThermalMesh m, double?[] sigma, params DcPort[] ports)
        => new(m, sigma, new Dictionary<int, double>(), ports);

    // ── 1. resistance ────────────────────────────────────────────────────────────────────────────

    /// <summary>A copper bar 10 mm × 1 mm × 35 µm, one port across its end faces: R from S₁₁ is L/(σA) = 4.926 mΩ to 0.5 %.</summary>
    [Fact]
    public void Resistance_OfACopperBar_FromS11_IsLOverSigmaA()
    {
        var m = Bands([0]).ToSecondOrder();
        var r = DcConduction.Solve(Problem(m, [Copper], new DcPort(1, Nodes(m, 0, X(L)), Nodes(m, 0, X(0)), Z0)));
        Assert.Null(r.Refusal);
        double s11 = r.S![0, 0], ohms = Z0 * (1 + s11) / (1 - s11);
        Assert.InRange(ohms / 4.926e-3, 0.995, 1.005);
    }

    // ── 2. open, short and through ───────────────────────────────────────────────────────────────

    /// <summary>Two disjoint bars, each a port to a separate ground body it does not touch: S = I.</summary>
    [Fact]
    public void Open_TwoDisjointBarsEachToASeparateGround_IsTheIdentity()
    {
        var m = Bands([0, 1, 2, 1, 0, 1, 2]);
        var p = new DcConductionProblem(m, [Copper, null, null], new Dictionary<int, double>(),
                                        [new DcPort(1, Nodes(m, 0, X(L)), Nodes(m, 2), Z0), new DcPort(2, Nodes(m, 4, X(L)), Nodes(m, 6), Z0)])
        {
            Ties = [Nodes(m, 2), Nodes(m, 6)],
        };
        var r = DcConduction.Solve(p);
        Assert.Null(r.Refusal);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++) Assert.Equal(i == j ? 1 : 0, r.S![i, j], 12);
    }

    /// <summary>One bar from a port's positive contact to its negative contact: S₁₁ = (R − Z₀)/(R + Z₀) to 1e-9 (a P1 field is
    /// exact on a uniform bar, so R is L/(σA)).</summary>
    [Fact]
    public void Short_OneBarAcrossThePort_IsItsOwnResistance()
    {
        const double ohms = 30;
        double sigma = L / (ohms * W * T);
        var m = Bands([0]);
        var r = DcConduction.Solve(Problem(m, [sigma], new DcPort(1, Nodes(m, 0, X(L)), Nodes(m, 0, X(0)), Z0)));
        Assert.Null(r.Refusal);
        Assert.Equal((ohms - Z0) / (ohms + Z0), r.S![0, 0], 9);
    }

    /// <summary>Two ports on one bar that touches no ground, each referenced to a separate PEC body: S₂₁ = 2Z₀/(2Z₀ + R).</summary>
    [Fact]
    public void Through_TwoPortsOnOneBar_IsASeriesResistance()
    {
        var m = Bands([0, 1, 2]);
        var p = new DcConductionProblem(m, [Copper, null, null], new Dictionary<int, double>(),
                                        [new DcPort(1, Nodes(m, 0, X(0)), Nodes(m, 2), Z0), new DcPort(2, Nodes(m, 0, X(L)), Nodes(m, 2), Z0)])
        {
            Ties = [Nodes(m, 2)],
        };
        var r = DcConduction.Solve(p);
        Assert.Null(r.Refusal);
        double rBar = L / (Copper * W * T);
        Assert.Equal(2 * Z0 / (2 * Z0 + rBar), r.S![1, 0], 9);
        Assert.True(1 - r.S[1, 0] < 1e-4);
    }

    // ── 3. ties ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A PEC body bridging two copper bars makes them one conductor: the through of a series 2R, with the PEC body's
    /// nodes merged into one unknown (no penalty term).</summary>
    [Fact]
    public void Ties_APecBodyBridgingTwoBars_IsOneConductor_AndOneUnknown()
    {
        var m = Bands([0, 1, 2], length: 3 * L, cellsX: 30, byX: (x, k) => k == 0 && x > L && x < 2 * L ? 3 : k);
        int[] pec = Nodes(m, 0, x => x >= L - 1e-12 && x <= 2 * L + 1e-12), ground = Nodes(m, 2);
        int[] left = Nodes(m, 0, X(0)), right = Nodes(m, 0, X(3 * L));
        var p = new DcConductionProblem(m, [Copper, null, null, null], new Dictionary<int, double>(),
                                        [new DcPort(1, left, ground, Z0), new DcPort(2, right, ground, Z0)])
        {
            Ties = [pec, ground],
        };
        var r = DcConduction.Solve(p);
        Assert.Null(r.Refusal);
        double rBar = L / (Copper * W * T);
        Assert.Equal(2 * Z0 / (2 * Z0 + 2 * rBar), r.S![1, 0], 9);
        int electrical = Nodes(m, 0).Length + ground.Length;
        int expected = electrical - (pec.Length - 1) - (ground.Length - 1) - (left.Length - 1) - (right.Length - 1) - 2;
        Assert.Equal(expected, r.Unknowns);
    }

    // ── 4. lumped elements ───────────────────────────────────────────────────────────────────────

    /// <summary>One port across a gap carrying a lumped element: R = 100 Ω gives S₁₁ = 1/3, L −1, C +1, series RC +1, parallel
    /// RC 1/3. The bars either side are copper, whose 10 mΩ move S₁₁ by under 1e-3.</summary>
    [Theory]
    [InlineData(100.0, null, null, false, 1.0 / 3)]
    [InlineData(null, 1e-9, null, false, -1.0)]
    [InlineData(null, null, 1e-12, false, 1.0)]
    [InlineData(100.0, null, 1e-12, true, 1.0)]
    [InlineData(100.0, null, 1e-12, false, 1.0 / 3)]
    public void LumpedElements_AcrossAGap_AreWhatTheyAreAtDc(double? r, double? l, double? c, bool series, double s11)
    {
        const double gap = 1e-3;
        var xs = TestMeshes.Linspace(0, L, 10).Concat(TestMeshes.Linspace(L + gap, 2 * L + gap, 10)).ToArray();
        var m = Bands([0], xs: xs, byX: (x, _) => x > L && x < L + gap ? 1 : 0);
        int[] a = Nodes(m, 0, X(L)), b = Nodes(m, 0, X(L + gap));
        var kind = DcConduction.LumpedAtDc(r, l, c, series);
        var p = Problem(m, [Copper, null], new DcPort(1, Nodes(m, 0, X(2 * L + gap)), Nodes(m, 0, X(0)), Z0)) with
        {
            Ties = kind == DcLumpedKind.Tie ? [[.. a, .. b]] : [],
            Resistors = kind == DcLumpedKind.Resistor ? [new DcResistor("R1", a, b, r!.Value)] : [],
        };
        var result = DcConduction.Solve(p);
        Assert.Null(result.Refusal);
        Assert.Equal(s11, result.S![0, 0], 1e-3);
    }

    // ── 5. a floating conductor ──────────────────────────────────────────────────────────────────

    /// <summary>A copper bar no port reaches changes nothing: the same S, the same unknown count, and a note that says so.</summary>
    [Fact]
    public void AFloatingConductor_ChangesNothing()
    {
        var m = Bands([0, 1, 2]).ToSecondOrder();
        var port = new DcPort(1, Nodes(m, 0, X(L)), Nodes(m, 0, X(0)), Z0);
        var without = DcConduction.Solve(Problem(m, [Copper, null, null], port));
        var with = DcConduction.Solve(Problem(m, [Copper, null, Copper], port));
        Assert.Null(with.Refusal);
        Assert.Equal(without.Unknowns, with.Unknowns);
        Assert.Equal(without.S![0, 0], with.S![0, 0], 15);
        Assert.Contains(with.Notes, x => x.Contains("touch no port's contact", StringComparison.Ordinal));
    }

    // ── 6. reciprocity and passivity ─────────────────────────────────────────────────────────────

    /// <summary>A three-port — both ends of a resistive bar and a line contact at its middle, each against a PEC ground, with a
    /// lumped resistor to ground — is reciprocal and passive to 1e-9.</summary>
    [Fact]
    public void AThreePort_IsReciprocalAndPassive()
    {
        double sigma = L / (40 * W * T);
        var m = Bands([0, 1, 2]).ToSecondOrder();
        int[] ground = Nodes(m, 2);
        int[] middle = [.. Nodes(m, 0, X(L / 2)).Where(i => Math.Abs(m.Nodes[3 * i + 1] - W) < 1e-12)];
        var p = new DcConductionProblem(m, [sigma, null, null], new Dictionary<int, double>(),
                                        [new DcPort(1, Nodes(m, 0, X(0)), ground, Z0), new DcPort(2, Nodes(m, 0, X(L)), ground, 75),
                                         new DcPort(3, middle, ground, 25)])
        {
            Ties = [ground],
            Resistors = [new DcResistor("R1", Nodes(m, 0, X(L / 4)), ground, 7)],
        };
        var r = DcConduction.Solve(p);
        Assert.Null(r.Refusal);
        var s = r.S!;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) Assert.True(Math.Abs(s[i, j] - s[j, i]) <= 1e-9, $"S{i + 1}{j + 1} − S{j + 1}{i + 1}");
        Assert.True(DcConduction.LargestSingularValue(s) <= 1 + 1e-9);
        Assert.True(Math.Abs(s[2, 0]) > 1e-3, "the middle contact sees the ends");
    }
}
