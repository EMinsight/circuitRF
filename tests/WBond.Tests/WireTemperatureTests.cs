using System.Numerics;
using CircuitRF.WBond.Thermal;
using Xunit.Abstractions;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// brief-wbond-wire-temperature §8 — the one-dimensional wire temperature solve on its own: G1 (analytic, constant σ and k, and
/// the element count), G2 (analytic runaway), G4 (sharing) and G13 (no current is an ordinary answer).
/// </summary>
public sealed class WireTemperatureTests(ITestOutputHelper output)
{
    private const double D = 2.54e-5, L = 1e-3, Ta = 125, Tb = 85;
    private static double Area => Math.PI * D * D / 4;

    /// <summary>A metal whose σ and k do not move with temperature.</summary>
    private static readonly WireMaterial Flat = new("Flat", 4.1e7, 0, 0) { ThermalK = 318 };

    private static ThermalWireSpec Wire(WireMaterial m, double length = L) => new(length, D, m);

    private static WireArrayDrive Drive(double dc, params (double F, double Peak)[] h)
        => new(dc, [.. h.Select(x => x.F)], [[.. h.Select(x => x.Peak)]]);

    /// <summary>The G1 closed form's maximum: T = Ta + (Tb−Ta)s/L + q′s(L−s)/(2kA).</summary>
    private static double ClosedFormMax(double q, double k, double ta = Ta, double tb = Tb)
    {
        double s = L / 2 + (tb - ta) * k * Area / (q * L);
        if (!(s > 0 && s < L)) return Math.Max(ta, tb);
        return ta + (tb - ta) * s / L + q * s * (L - s) / (2 * k * Area);
    }

    private static double HeatOf(double dc, double f, double peak)
        => dc * dc / (Flat.Sigma20 * Area) + 0.5 * peak * peak * InternalImpedance.ResistanceWithSigmaSlope(f, D / 2, Flat.Sigma20).ResistancePerMetre;

    [Fact]
    public void G1_ConstantSigmaAndK_MatchesTheClosedForm()
    {
        var s = WireConductiveBalance.Solve([Wire(Flat)], Ta, Tb, Drive(1.0, (2e9, 0.5)));
        Assert.True(s.Converged, s.Failure);
        double expected = ClosedFormMax(HeatOf(1.0, 2e9, 0.5), 318);
        output.WriteLine($"max {s.MaxC:F6} °C against {expected:F6} °C, {s.Iterations} step(s)");
        Assert.True(expected > Ta + 1, "the peak should lie inside the wire");
        Assert.Equal(expected, s.MaxC, 0.01);
    }

    /// <summary>R-wbt-3a: the element count is enough that halving the element size moves a hot gold wire's maximum by < 0.01 K.</summary>
    [Fact]
    public void ElementCount_Converged()
    {
        var gold = WireMaterials.Gold;
        var drive = Drive(1.7, (2e9, 1.0), (4e9, 0.3));
        int n = WireConductiveBalance.Elements;
        var at = WireConductiveBalance.Solve([Wire(gold)], Ta, Tb, drive, elements: n);
        var half = WireConductiveBalance.Solve([Wire(gold)], Ta, Tb, drive, elements: 2 * n);
        var coarse = WireConductiveBalance.Solve([Wire(gold)], Ta, Tb, drive, elements: n / 2);
        Assert.True(at.Converged && half.Converged && coarse.Converged);
        output.WriteLine($"gold, 1.7 A DC + harmonics: {n / 2} elements {coarse.MaxC:F5} °C, {n} {at.MaxC:F5} °C, {2 * n} {half.MaxC:F5} °C");
        Assert.True(at.MaxC > 400, $"a hot wire: {at.MaxC:F1} °C");
        Assert.True(Math.Abs(half.MaxC - at.MaxC) < 0.01, $"{n} → {2 * n} elements moved the maximum {Math.Abs(half.MaxC - at.MaxC):G3} K");
    }

    /// <summary>
    /// G2 — linear resistivity and a constant k: u = 1 + α(T − 20) obeys u″ + β²u = 0, β² = I²α/(σ₂₀kA²), and steady states end
    /// at βL = π. Below it the solve matches the closed form; above it, it is a runaway.
    /// </summary>
    [Fact]
    public void G2_LinearResistivity_RunsAwayAtTheClosedFormCurrent()
    {
        const double alpha = 0.0039, sigma = 4.1e7, k = 318;
        var metal = new WireMaterial("Linear", sigma, alpha, 0) { ThermalK = k };
        double ic = Math.PI / L * Area * Math.Sqrt(sigma * k / alpha);

        double i = 0.99 * ic, beta = i * Math.Sqrt(alpha / (sigma * k)) / Area;
        double ua = 1 + alpha * (Ta - 20), ub = 1 + alpha * (Tb - 20);
        double b = (ub - ua * Math.Cos(beta * L)) / Math.Sin(beta * L);
        double sStar = Math.Atan2(b, ua) / beta;
        double uMax = sStar > 0 && sStar < L ? Math.Sqrt(ua * ua + b * b) : Math.Max(ua, ub);
        double expected = 20 + (uMax - 1) / alpha;

        var below = WireConductiveBalance.Advance([Wire(metal)], Ta, Tb, Drive(i));
        Assert.NotNull(below.Solution);
        double error = Math.Abs(below.Solution!.MaxC - expected) / (expected - Ta);
        output.WriteLine($"I_c = {ic:G6} A; at 0.99·I_c max {below.Solution.MaxC:F2} °C against {expected:F2} °C ({error:G3} of the rise), {below.Solves} solve(s)");
        Assert.True(error < 1e-3, $"{error:G3} of the rise");

        var above = WireConductiveBalance.Advance([Wire(metal)], Ta, Tb, Drive(1.01 * ic));
        Assert.Null(above.Solution);
        Assert.True(above.Runaway);
        Assert.InRange(above.ConvergedAt * 1.01, 0.97, 1.0);
    }

    /// <summary>G4 (i) — two wires of different length in one array, constant σ: the DC divides as R₂/(R₁ + R₂).</summary>
    [Fact]
    public void G4_TwoWiresInOneArray_DivideTheDcByResistance()
    {
        var s = WireConductiveBalance.Solve([Wire(Flat, 1e-3), Wire(Flat, 1.6e-3)], Ta, Tb, new WireArrayDrive(1.5, [], [[], []]));
        Assert.True(s.Converged, s.Failure);
        Assert.Equal(1.6 / 2.6, s.State.I[0] / 1.5, 1e-12);
        Assert.Equal(1.5, s.State.I[0] + s.State.I[1], 1e-12);
    }

    /// <summary>G4 (ii) — two arrays: the per-wire harmonic currents are ArrayShare.For's columns combined by complex phasor sums.</summary>
    [Fact]
    public void G4_TwoArrays_HarmonicCurrentsAreTheSharesPhasorSum()
    {
        var design = TestDesigns.ParallelArray(5, 3, 40, 6, arrays: 2);
        var model = WBondThermalModel.Create(design, new WireThermalSpec(true, 125, 125, 85),
                                             () => Core(design));
        var share = ArrayShare.For(design);
        Complex[] i0 = [new(0.7, -0.2), new(-0.1, 0.4)], i1 = [new(0.05, 0.02), new(0.3, 0.3)];
        var peaks = WBondWireTemperature.WirePeaks(model, [1e9, 2e9], [i0, i1]);
        double worst = 0;
        for (int w = 0; w < share.WireCount; w++)
            for (int f = 0; f < 2; f++)
            {
                var arr = f == 0 ? i0 : i1;
                var expected = share.PerUnitCurrent(0)[w] * arr[0] + share.PerUnitCurrent(1)[w] * arr[1];
                worst = Math.Max(worst, Math.Abs(peaks[w][f] - expected.Magnitude));
            }
        Assert.True(worst <= 1e-12, $"worst {worst:G3} A");

        static ArrayReduction Core(WBondDesign d)
        {
            var mesh = WireMesh.Build(d);
            return ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);
        }
    }

    // ── G13: no current is an ordinary answer (D8) ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Ta, Tb)]
    [InlineData(100.0, 100.0)]
    public void G13_ExactlyZeroCurrent_IsTheHotterEnd_InNoSteps(double ta, double tb)
    {
        var s = WireConductiveBalance.Advance([Wire(WireMaterials.Gold), Wire(WireMaterials.Gold, 1.3e-3)], ta, tb,
                                              new WireArrayDrive(0, [2e9], [[0.0], [0.0]]));
        Assert.NotNull(s.Solution);
        Assert.Equal(0, s.Solution!.Iterations);
        Assert.Equal(1, s.Solves);
        Assert.Equal(Math.Max(ta, tb), s.Solution.MaxC);
    }

    [Fact]
    public void G13_ARoundOffCurrent_IsTheZeroCurrentAnswer()
    {
        var zero = WireConductiveBalance.Solve([Wire(WireMaterials.Gold)], Ta, Tb, Drive(0));
        var tiny = WireConductiveBalance.Solve([Wire(WireMaterials.Gold)], Ta, Tb, Drive(1e-15, (2e9, 1e-15)));
        Assert.True(tiny.Converged, tiny.Failure);
        for (int j = 0; j < zero.State.T[0].Length; j++) Assert.True(Math.Abs(tiny.State.T[0][j] - zero.State.T[0][j]) <= 1e-9);
        Assert.Equal(Math.Max(Ta, Tb), tiny.MaxC, 1e-9);
    }

    [Fact]
    public void G13_CurrentToZero_IsContinuous()
    {
        var s = WireConductiveBalance.Solve([Wire(Flat)], Ta, Ta, Drive(1e-6));
        Assert.True(s.Converged, s.Failure);
        double expected = ClosedFormMax(HeatOf(1e-6, 1e9, 0), 318, Ta, Ta);
        Assert.True(expected > Ta);
        Assert.Equal(expected, s.MaxC, 1e-9);
    }

    [Fact]
    public void G13_ANegativeDcCurrent_HeatsExactlyAsThePositiveOne()
    {
        var wires = new[] { Wire(WireMaterials.Gold), Wire(WireMaterials.Gold, 1.4e-3) };
        var pos = WireConductiveBalance.Solve(wires, Ta, Tb, new WireArrayDrive(1.8, [2e9], [[0.4], [0.3]]));
        var neg = WireConductiveBalance.Solve(wires, Ta, Tb, new WireArrayDrive(-1.8, [2e9], [[0.4], [0.3]]));
        Assert.True(pos.Converged, pos.Failure);
        Assert.Equal(pos.MaxC, neg.MaxC);
        Assert.Equal(pos.Iterations, neg.Iterations);
        for (int w = 0; w < 2; w++)
        {
            Assert.Equal(pos.State.T[w], neg.State.T[w]);
            Assert.Equal(pos.State.I[w], -neg.State.I[w]);
        }
    }
}
