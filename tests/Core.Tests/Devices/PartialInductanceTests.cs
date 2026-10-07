using CircuitRF.Core.Devices;
using Xunit;
using Seg = CircuitRF.Core.Devices.PartialInductance.Segment;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The partial-inductance sum the spirals' L comes from. Every expected value is an independent
/// statement — a textbook formula or a brute-force Neumann integral — never the summation's own
/// algebra read back.
/// </summary>
public class PartialInductanceTests
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double W = 10e-6, T = 3e-6;

    /// <summary>A closed square loop is four self terms and two antiparallel pairs; the opposite sides'
    /// mutual is Grover's equal-parallel-filament form, (µ₀/2π)·l·[ln(l/d + √(1 + l²/d²)) − √(1 + d²/l²) + d/l],
    /// written out here at d = l.</summary>
    [Fact]
    public void ASquareLoop_IsFourSelfTermsLessItsOppositeSides()
    {
        double a = 300e-6;
        Seg[] loop = [new(0, 0, a, 0, 0), new(a, 0, a, a, 0), new(a, a, 0, a, 0), new(0, a, 0, 0, 0)];
        double self = Mu0 / (2 * Math.PI) * a * (Math.Log(2 * a / (W + T)) + 0.5 + 0.2235 * (W + T) / a);
        double opposite = Mu0 / (2 * Math.PI) * a * (Math.Log(1 + Math.Sqrt(2)) - Math.Sqrt(2) + 1);
        double expected = 4 * self - 4 * opposite;

        Assert.Equal(expected, PartialInductance.OfPath(loop, W, T, groundDepth: 0), expected * 1e-9);
    }

    /// <summary>A long straight conductor over a ground plane is the transmission-line inductance
    /// (µ₀/2π)·ℓ·ln(2h/GMD) per unit length, the GMD of the strip being 0.2235·(w + t).</summary>
    [Fact]
    public void ALongConductorOverGround_IsTheTransmissionLineInductance()
    {
        double l = 0.5, h = 100e-6;
        double expected = Mu0 / (2 * Math.PI) * l * Math.Log(2 * h / (0.2235 * (W + T)));
        double sum = PartialInductance.OfPath([new Seg(0, 0, l, 0, 0)], W, T, groundDepth: h);
        Assert.Equal(expected, sum, expected * 2e-3);
    }

    /// <summary>The two closed forms for segments that are not parallel — in one plane (a spiral's 45°
    /// corner) and in parallel planes (that side against an image, or against the bridge) — against
    /// Neumann's integral done by brute force.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(40e-6)]
    public void ANonParallelMutual_IsNeumannsIntegral(double dz)
    {
        var a = new Seg(0, 0, 100e-6, 0, 0);
        var b = new Seg(150e-6, 20e-6, 150e-6 + 60e-6, 20e-6 + 60e-6, dz);    // 45°, clear of a
        Assert.Equal(Brute(a, b), PartialInductance.Mutual(a, b, 0), Math.Abs(Brute(a, b)) * 1e-4);
    }

    private static double Brute(Seg a, Seg b, int n = 600)
    {
        double la = a.Length, lb = b.Length, sum = 0;
        double ux = (a.X2 - a.X1) / la, uy = (a.Y2 - a.Y1) / la, vx = (b.X2 - b.X1) / lb, vy = (b.Y2 - b.Y1) / lb;
        for (int i = 0; i < n; i++)
        {
            double sa = (i + 0.5) * la / n, px = a.X1 + ux * sa, py = a.Y1 + uy * sa;
            for (int j = 0; j < n; j++)
            {
                double sb = (j + 0.5) * lb / n, qx = b.X1 + vx * sb - px, qy = b.Y1 + vy * sb - py, dz = b.Z - a.Z;
                sum += 1 / Math.Sqrt(qx * qx + qy * qy + dz * dz);
            }
        }
        return 1e-7 * (ux * vx + uy * vy) * sum * (la / n) * (lb / n);
    }
}
