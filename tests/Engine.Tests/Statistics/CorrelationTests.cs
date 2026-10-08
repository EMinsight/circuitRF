using CircuitRF.Engine.Statistics;

namespace CircuitRF.Engine.Tests.Statistics;

/// <summary>YA-2 R-ya2-2: the Gaussian copula correlates values of different distributions, and a
/// matrix that is not positive definite is repaired and says so.</summary>
public sealed class CorrelationTests
{
    [Fact]
    public void GaussianWithUniform_AtRho08_HasTheCopulasCorrelation()
    {
        const int n = 20_000;
        const double rho = 0.8;
        var copula = new GaussianCopula(new[,] { { 1, rho }, { rho, 1 } });
        var x = new NormalMarginal(0, 1);
        var y = new UniformMarginal(0, 1);
        ulong a = StatStreams.Id("R1.R"), b = StatStreams.Id("C1.C");

        var xs = new double[n];
        var ys = new double[n];
        var z = new double[2];
        for (int t = 0; t < n; t++)
        {
            z[0] = StatStreams.Normal(1, t + 1, a);
            z[1] = StatStreams.Normal(1, t + 1, b);
            copula.Correlate(z);
            xs[t] = x.FromNormal(z[0]);
            ys[t] = y.FromNormal(z[1]);
        }

        // Pearson's r of a normal with Φ of a correlated normal is ρ·corr(Z, Φ(Z)) = ρ·√(3/π).
        double expected = rho * Math.Sqrt(3 / Math.PI);
        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (int t = 0; t < n; t++)
        {
            sxy += (xs[t] - mx) * (ys[t] - my);
            sxx += (xs[t] - mx) * (xs[t] - mx);
            syy += (ys[t] - my) * (ys[t] - my);
        }
        double r = sxy / Math.Sqrt(sxx * syy);
        double se = (1 - expected * expected) / Math.Sqrt(n);   // large-sample SE of Pearson's r
        Assert.True(Math.Abs(r - expected) <= 4 * se, $"r = {r}, expected {expected} (SE {se})");
    }

    [Fact]
    public void NotPositiveDefinite_IsRepaired_Reported_AndFactors()
    {
        // A and B strongly alike, B and C strongly alike, A and C strongly opposite: no such variables exist.
        var bad = new[,] { { 1, 0.9, -0.9 }, { 0.9, 1, 0.9 }, { -0.9, 0.9, 1 } };
        Assert.False(NearestCorrelation.IsPositiveDefinite(bad));

        var copula = new GaussianCopula(bad);
        Assert.True(copula.Repaired);
        Assert.True(copula.LargestChange > 0.1, $"largest change {copula.LargestChange}");
        Assert.True(NearestCorrelation.IsPositiveDefinite(copula.Matrix));

        // L·Lᵀ is the repaired matrix, unit diagonal included.
        var l = copula.Lower;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += l[i, k] * l[j, k];
                Assert.Equal(copula.Matrix[i, j], s, 1e-12);
            }
        Assert.Equal(1, copula.Matrix[1, 1], 1e-12);
    }
}
