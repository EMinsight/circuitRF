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
