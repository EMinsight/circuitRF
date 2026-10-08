using CircuitRF.Cli;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>YA-1 R-ya1-5: <c>explain --analysis</c>'s expected yield interval is the Clopper–Pearson
/// half-width — checked against the interval computed here from the binomial distribution directly, which
/// shares no code with the Beta-quantile route the application takes.</summary>
public sealed class StatisticsExplainTests
{
    [Fact]
    public void TheExpectedInterval_IsTheClopperPearsonHalfWidth()
    {
        var (_, tb) = new CnlReader().Read("""
            R:R1 a 0 R=50 Ohm
            tune R1.R dist=gauss sd=2%
            statistics trials=500
            """);

        var report = ExplainStatistics.Collect(tb)!;

        Assert.Equal(500, report.Trials);
        Assert.Equal(90, report.AtYield);
        Assert.Equal(HalfWidth(450, 500) * 100, report.ExpectedHalfWidth, 6);

        // The trial count it names is the first under ±2 %.
        int n = report.TrialsForTwoPercent!.Value;
        Assert.True(HalfWidth((int)Math.Round(0.9 * n, MidpointRounding.AwayFromZero), n) < 0.02);
        Assert.True(HalfWidth((int)Math.Round(0.9 * (n - 1), MidpointRounding.AwayFromZero), n - 1) >= 0.02);
    }

    /// <summary>Half the 95 % Clopper–Pearson interval of k passes in n, each end found by bisection on the
    /// binomial tail it is defined by: P(X ≥ k | p_lo) = α/2 and P(X ≤ k | p_hi) = α/2.</summary>
    private static double HalfWidth(int k, int n)
    {
        const double halfAlpha = 0.025;
        double lo = k == 0 ? 0 : Bisect(p => 1 - Cdf(k - 1, n, p) - halfAlpha, increasing: true);
        double hi = k == n ? 1 : Bisect(p => Cdf(k, n, p) - halfAlpha, increasing: false);
        return (hi - lo) / 2;
    }

    private static double Cdf(int k, int n, double p)
    {
        double sum = 0, logC = 0;
        for (int i = 0; i <= k; i++)
        {
            if (i > 0) logC += Math.Log(n - i + 1) - Math.Log(i);
            sum += Math.Exp(logC + i * Math.Log(p) + (n - i) * Math.Log(1 - p));
        }
        return sum;
    }

    private static double Bisect(Func<double, double> f, bool increasing)
    {
        double a = 1e-12, b = 1 - 1e-12;
        for (int i = 0; i < 100; i++)
        {
            double m = (a + b) / 2;
            if (f(m) < 0 == increasing) a = m; else b = m;
        }
        return (a + b) / 2;
    }
}
