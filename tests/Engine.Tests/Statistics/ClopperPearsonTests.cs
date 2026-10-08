using CircuitRF.Engine.Statistics;
using Xunit;

namespace CircuitRF.Engine.Tests.Statistics;

/// <summary>R-ya4-3: the exact binomial interval and the in-house regularized incomplete beta it is built on, against
/// tabulated values.</summary>
public sealed class ClopperPearsonTests
{
    [Theory]
    [InlineData(0.5, 2, 3, 0.6875)]       // Σ_{j≥2} C(4,j)/16 = 11/16
    [InlineData(0.2, 1, 5, 0.67232)]      // 1 − 0.8⁵
    [InlineData(0.4, 3, 2, 0.1792)]       // C(4,3)·0.4³·0.6 + 0.4⁴
    public void TheIncompleteBeta_MatchesItsBinomialTable(double x, double a, double b, double expected)
        => Assert.Equal(expected, SpecialFunctions.RegularizedBeta(x, a, b), 12);

    [Theory]
    [InlineData(10, 20, 0.2720, 0.7280)]
    [InlineData(50, 100, 0.3983, 0.6017)]
    [InlineData(1, 10, 0.0025, 0.4450)]
    public void TheInterval_MatchesTheTabulatedNinetyFivePercentInterval(int k, int n, double lo, double hi)
    {
        var (l, h) = ClopperPearson.Interval(k, n, 0.95);
        Assert.Equal(lo, l, 4);
        Assert.Equal(hi, h, 4);
    }

    [Fact]
    public void AllOrNoPasses_HaveTheClosedFormEnds()
    {
        // 0 of n: upper = 1 − (α/2)^(1/n); n of n: lower = (α/2)^(1/n).
        var (zero, upper) = ClopperPearson.Interval(0, 10, 0.95);
        Assert.Equal(0.0, zero);
        Assert.Equal(1 - Math.Pow(0.025, 0.1), upper, 12);
        var (lo, hi) = ClopperPearson.Interval(10, 10, 0.95);
        Assert.Equal(Math.Pow(0.025, 0.1), lo, 12);
        Assert.Equal(1.0, hi);
    }
}
