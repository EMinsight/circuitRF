using CircuitRF.Engine.Statistics;

namespace CircuitRF.Engine.Tests.Statistics;

/// <summary>brief-yield-15 R-ya15-2: a dependent column before an independent one costs nothing — the fit is the one
/// without it, and its coefficient is 0.</summary>
public sealed class LeastSquaresTests
{
    [Fact]
    public void ADependentColumnBeforeAnIndependentOne_LeavesTheOtherCoefficientsUnchanged()
    {
        // y = 1 + 2x + 3z plus a fixed wobble, over 12 points; the zero column sits between x and z.
        var x = Enumerable.Range(0, 12).Select(i => i / 11.0).ToArray();
        var z = Enumerable.Range(0, 12).Select(i => Math.Cos(1.7 * i)).ToArray();
        var y = Enumerable.Range(0, 12).Select(i => 1 + 2 * x[i] + 3 * z[i] + 0.05 * Math.Sin(5.3 * i + 0.4)).ToList();

        var with    = LeastSquares.Fit([.. Enumerable.Range(0, 12).Select(i => new[] { 1, x[i], 0, z[i] })], y);
        var without = LeastSquares.Fit([.. Enumerable.Range(0, 12).Select(i => new[] { 1, x[i], z[i] })], y);

        Assert.Equal(3, with.Rank);
        Assert.Equal(0, with.Coefficients[2]);
        Assert.Equal(without.Coefficients[0], with.Coefficients[0], 1e-12);
        Assert.Equal(without.Coefficients[1], with.Coefficients[1], 1e-12);
        Assert.Equal(without.Coefficients[2], with.Coefficients[3], 1e-12);
        Assert.Equal(without.RSquared, with.RSquared, 1e-12);
    }
}
