using CircuitRF.Engine.Statistics;

namespace CircuitRF.Engine.Tests.Statistics;

/// <summary>
/// YA-2 R-ya2-1: Φ⁻¹ against tabulated quantiles, each distribution's sample moments against its
/// exact ones, and truncation sampled exactly. The moment checks use a fixed-seed sample from
/// <see cref="StatStreams"/>, so they are deterministic; the tolerance is 4 standard errors of the
/// sample statistic, estimated from the sample's own higher moments (SE of a mean σ/√n; of a variance
/// √((m₄ − s⁴)/n)).
/// </summary>
public sealed class DistributionTests
{
    private const int N = 20_000;

    [Fact]
    public void InverseNormalCdf_MatchesTabulatedQuantiles()
    {
        // Published standard normal quantiles (the familiar 1.959963984540054 at 0.975 among them).
        (double P, double X)[] table =
        [
            (1e-10, -6.361340902404056), (1e-5, -4.264890793922825), (0.001, -3.090232306167813),
            (0.025, -1.9599639845400545), (0.1, -1.2815515655446004), (0.3, -0.5244005127080409),
            (0.5, 0), (0.75, 0.6744897501960817), (0.975, 1.959963984540054), (0.999, 3.090232306167813),
        ];
        foreach (var (p, x) in table)
            Assert.True(Math.Abs(SpecialFunctions.InverseNormalCdf(p) - x) <= 1e-12, $"Φ⁻¹({p}) = {SpecialFunctions.InverseNormalCdf(p):R}, expected {x:R}");

        // Deep in the tail, relatively.
        Assert.Equal(-37.0470962993612, SpecialFunctions.InverseNormalCdf(1e-300), 1e-12 * 37);
    }

    [Fact]
    public void EachDistribution_SampleMeanAndVariance_WithinFourStandardErrors()
    {
        (string Name, Marginal M)[] cases =
        [
            ("gauss",          new NormalMarginal(10, 2)),
            ("gauss trunc=2",  new NormalMarginal(10, 2, 2)),
            ("lognorm",        new LogNormalMarginal(5, 0.3)),
            ("lognorm trunc=2", new LogNormalMarginal(5, 0.3, 2)),
            ("unif",           new UniformMarginal(1, 3)),
            ("discrete",       new DiscreteMarginal(4, 8, 2)),
        ];
        ulong stream = StatStreams.Id("DistributionTests");
        foreach (var (name, m) in cases)
        {
            var x = new double[N];
            for (int t = 0; t < N; t++) x[t] = m.FromNormal(StatStreams.Normal(1, t + 1, stream));
            double mean = x.Average();
            double s2 = x.Sum(v => (v - mean) * (v - mean)) / (N - 1);
            double m4 = x.Sum(v => Math.Pow(v - mean, 4)) / N;

            double seMean = Math.Sqrt(s2 / N), seVar = Math.Sqrt((m4 - s2 * s2) / N);
            Assert.True(Math.Abs(mean - m.Mean) <= 4 * seMean, $"{name}: mean {mean} vs {m.Mean} (SE {seMean})");
            Assert.True(Math.Abs(s2 - m.Variance) <= 4 * seVar, $"{name}: variance {s2} vs {m.Variance} (SE {seVar})");
        }
    }

    [Fact]
    public void Truncation_LeavesNothingBeyondK_AndPilesNothingAtTheEdge()
    {
        const double k = 1.5;
        var m = new NormalMarginal(0, 1, k);
        ulong stream = StatStreams.Id("TruncationTests");
        int edge = 0;
        for (int t = 0; t < N; t++)
        {
            double x = m.FromNormal(StatStreams.Normal(7, t + 1, stream));
            Assert.InRange(x, -k, k);
            if (x >= k - 0.1) edge++;
        }
        // Clipping would put the whole untruncated tail beyond k (~6.7 %) into this bin; the exact
        // truncated law puts ~1.6 % there.
        double p = 1 - m.Cdf(k - 0.1);
        double se = Math.Sqrt(p * (1 - p) / N);
        Assert.True(Math.Abs((double)edge / N - p) <= 4 * se, $"edge bin {(double)edge / N} vs {p} (SE {se})");
    }
}
