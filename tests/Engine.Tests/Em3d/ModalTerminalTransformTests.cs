using System.Numerics;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-em3d-115 §9 — ModalTerminalTransform's own algebra, on constructed inputs. The replays of brief 124's Palace runs
// (gates 1–4) read files through src/Design's readers and are in tests/Ui.Tests/Em3d/PalaceTerminalPortTests.cs.
public sealed class ModalTerminalTransformTests
{
    [Fact]
    public void TheCubicsRealRootsAreFound()
    {
        var roots = ModalTerminalTransform.CubicRealRoots(1, -0, -7, 6).OrderBy(r => r).ToList();   // (x + 3)(x − 1)(x − 2)
        Assert.Equal([-3, 1, 2], roots.Select(r => Math.Round(r, 12)));
        Assert.Single(ModalTerminalTransform.CubicRealRoots(1, 0, 1, 0));                            // x(x² + 1)
    }

    /// <summary>A Gram of g = 0.3 behind a made-up M: the fit recovers a g that meets both Z_PV, to round-off.</summary>
    [Fact]
    public void TheRealGramFitMeetsBothModeImpedances()
    {
        Complex m11 = new(4.1, 0.3), m12 = new(-2.2, 0.1), m21 = new(1.7, -0.2), m22 = new(3.9, 0.4);
        const double g0 = 0.3;
        double z1 = (m11 + g0 * m12).Magnitude * (m11 + g0 * m12).Magnitude, z2 = (m22 + g0 * m21).Magnitude * (m22 + g0 * m21).Magnitude;
        var (g, residual) = ModalTerminalTransform.FitRealG(m11, m12, m21, m22, z1, z2);
        Assert.True(residual < 1e-12, $"residual {residual}");
        Assert.Equal(z1, (m11 + g * m12).Magnitude * (m11 + g * m12).Magnitude, 1e-9);
    }

    /// <summary>Every voltage path reversed (V → −V) is every terminal's sign flipped together: the terminal S is unchanged. So
    /// the direction brief 114 writes a terminal's path in (reference to conductor) is immaterial as long as it is one rule.</summary>
    [Fact]
    public void ReversingEveryPathLeavesTheTerminalSUnchanged()
    {
        var sample = Sample();
        var flipped = sample with { V = Scale(sample.V, -1) };
        var a = ModalTerminalTransform.Solve(sample, [[0, 1]], [50, 50]);
        var b = ModalTerminalTransform.Solve(flipped, [[0, 1]], [50, 50]);
        Assert.Null(a.Error);
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) Assert.True((a.S![i, j] - b.S![i, j]).Magnitude < 1e-12);
    }

    /// <summary>A face whose two kₙ differ by more than the tolerance takes G = 1 and K = Re k₁ / Re k₂; within it, it is
    /// degenerate and fitted.</summary>
    [Fact]
    public void DegeneracyIsDecidedByTheLoggedWavenumbers()
    {
        var apart = ModalTerminalTransform.Solve(Sample(k2: 131.2), [[0, 1]], [50, 50]);
        Assert.False(apart.Faces[0].Degenerate);
        Assert.Equal(0, apart.Faces[0].G.Real);
        Assert.Equal(142.0 / 131.2, apart.K[1], 12);
        var equal = ModalTerminalTransform.Solve(Sample(k2: 142.0), [[0, 1]], [50, 50]);
        Assert.True(equal.Faces[0].Degenerate);
        Assert.Equal(1.0, equal.K[1]);
    }

    /// <summary>The kₙ a run's own face solves gave: the 3D Wave Ports Pair's stripline (one speed, refused on Palace) and brief
    /// 124's microstrip pair (7 % apart, converted).</summary>
    [Fact]
    public void AStriplinesModesAreDegenerate_AMicrostripPairsAreNot()
    {
        Assert.True(ModalTerminalTransform.IsDegenerate([new Complex(303.7167996658, -0.0304), new Complex(303.7167996657, -0.0304)]));
        Assert.False(ModalTerminalTransform.IsDegenerate([new Complex(106.3353685537, 0), new Complex(98.40014178105, 0)]));
    }

    /// <summary>
    /// brief-emw-3 §7 test 1 — EMerge's Robin form on a non-degenerate two-mode face: the currents are I = M⁻ᴴ K (2 − C), K on
    /// the incident term too (G = 1, so C = P = 1 + S and M = V P⁻¹), checked against that formula written out here; on a
    /// degenerate face (K = 1) the form equals Palace's.
    /// </summary>
    [Fact]
    public void SharedWavenumber_IsKTimesTwoMinusC_AndOnADegenerateFaceIsPalacesForm()
    {
        var sample = Sample();
        double[] z0 = [50, 50];
        var solved = ModalTerminalTransform.Solve(sample, [[0, 1]], z0, robin: ModalRobinForm.SharedWavenumber);
        Assert.Null(solved.Error);

        var P = Add(sample.ModalS, Id2);
        var M = Mul(sample.V, Inv(P));
        double[] k = [1, 142.0 / 131.2];
        var D = new Complex[2, 2];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) D[i, j] = k[i] * ((i == j ? 2 : 0) - P[i, j]);
        var I = Mul(ConjT(Inv(M)), D);
        var A = new Complex[2, 2];
        var B = new Complex[2, 2];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) { A[i, j] = sample.V[i, j] + z0[i] * I[i, j]; B[i, j] = sample.V[i, j] - z0[i] * I[i, j]; }
        var expected = Mul(B, Inv(A));
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
            Assert.True((solved.S![i, j] - expected[i, j]).Magnitude < 1e-12, $"S{i + 1}{j + 1}: {solved.S[i, j]} against {expected[i, j]}");
        var palace = ModalTerminalTransform.Solve(sample, [[0, 1]], z0);
        Assert.True((solved.S![0, 1] - palace.S![0, 1]).Magnitude > 1e-3);      // K ≠ 1 here: the forms differ

        var degenerate = Sample(k2: 142.0);
        var shared = ModalTerminalTransform.Solve(degenerate, [[0, 1]], z0, robin: ModalRobinForm.SharedWavenumber);
        var real = ModalTerminalTransform.Solve(degenerate, [[0, 1]], z0);
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) Assert.True((shared.S![i, j] - real.S![i, j]).Magnitude < 1e-14);
    }

    private static readonly Complex[,] Id2 = { { 1, 0 }, { 0, 1 } };

    private static Complex[,] Add(Complex[,] a, Complex[,] b)
        => new[,] { { a[0, 0] + b[0, 0], a[0, 1] + b[0, 1] }, { a[1, 0] + b[1, 0], a[1, 1] + b[1, 1] } };

    private static Complex[,] Mul(Complex[,] a, Complex[,] b)
        => new[,] { { a[0, 0] * b[0, 0] + a[0, 1] * b[1, 0], a[0, 0] * b[0, 1] + a[0, 1] * b[1, 1] },
                    { a[1, 0] * b[0, 0] + a[1, 1] * b[1, 0], a[1, 0] * b[0, 1] + a[1, 1] * b[1, 1] } };

    private static Complex[,] Inv(Complex[,] a)
    {
        var d = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        return new[,] { { a[1, 1] / d, -a[0, 1] / d }, { -a[1, 0] / d, a[0, 0] / d } };
    }

    private static Complex[,] ConjT(Complex[,] a)
        => new[,] { { Complex.Conjugate(a[0, 0]), Complex.Conjugate(a[1, 0]) }, { Complex.Conjugate(a[0, 1]), Complex.Conjugate(a[1, 1]) } };

    private static ModalTerminalSample Sample(double k2 = 131.2)
        => new(new Complex[,] { { new(0.1, 0.05), new(0.02, -0.01) }, { new(0.02, -0.01), new(-0.08, 0.03) } },
               new Complex[,] { { new(5.1, 0.01), new(-0.4, 0.2) }, { new(0.6, -0.1), new(4.2, 0.02) } },
               [26.3, 16.1], [new Complex(142.0, 0), new Complex(k2, 0)]);

    private static Complex[,] Scale(Complex[,] a, double k)
    {
        var r = new Complex[a.GetLength(0), a.GetLength(1)];
        for (int i = 0; i < r.GetLength(0); i++) for (int j = 0; j < r.GetLength(1); j++) r[i, j] = k * a[i, j];
        return r;
    }
}
