using CircuitRF.Core.Expressions;

namespace CircuitRF.Engine.Statistics;

/// <summary>
/// The special functions the statistics code needs (docs/design/yield.md): the complementary error
/// function, the normal distribution's CDF and its inverse, the log-gamma function and the regularized incomplete beta
/// function with its inverse. Pure numerics — no domain types (yield overview D11).
/// </summary>
public static class SpecialFunctions
{
    /// <summary>erfc x — <see cref="NormalDistribution.Erfc"/>. The normal distribution's functions live in
    /// <c>src/Core</c> because the expression engine needs Φ⁻¹ and cannot reference this layer; these forward to
    /// them so there is one implementation.</summary>
    public static double Erfc(double x) => NormalDistribution.Erfc(x);

    /// <summary>The standard normal CDF Φ(z) — <see cref="NormalDistribution.Cdf"/>.</summary>
    public static double NormalCdf(double z) => NormalDistribution.Cdf(z);

    /// <summary>Φ⁻¹(p) — <see cref="NormalDistribution.InverseCdf"/>.</summary>
    public static double InverseNormalCdf(double p) => NormalDistribution.InverseCdf(p);

    /// <summary>The standard normal density φ(z) — <see cref="NormalDistribution.Pdf"/>.</summary>
    public static double NormalPdf(double z) => NormalDistribution.Pdf(z);

    /// <summary>ln Γ(x) for x &gt; 0 — the Lanczos approximation (g = 7, nine coefficients), ~1e-15.</summary>
    public static double LogGamma(double x)
    {
        if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        ReadOnlySpan<double> c =
        [
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
            1.5056327351493116e-7,
        ];
        x -= 1;
        double a = c[0], t = x + 7.5;
        for (int i = 1; i < 9; i++) a += c[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    /// <summary>
    /// The regularized incomplete beta function I_x(a, b) — the CDF of a Beta(a, b) variable at x —
    /// by its continued fraction (modified Lentz), on whichever side of the mean converges fast and
    /// by symmetry on the other.
    /// </summary>
    public static double RegularizedBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lnFront = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x);
        return x < (a + 1) / (a + b + 2)
            ? Math.Exp(lnFront) * BetaFraction(x, a, b) / a
            : 1 - Math.Exp(lnFront) * BetaFraction(1 - x, b, a) / b;
    }

    private static double BetaFraction(double x, double a, double b)
    {
        const double tiny = 1e-300;
        double c = 1, d = 1 - (a + b) * x / (a + 1);
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1 / d;
        double h = d;
        for (int m = 1; m < 10_000; m++)
        {
            int m2 = 2 * m;
            double num = m * (b - m) * x / ((a + m2 - 1) * (a + m2));
            d = 1 + num * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + num / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            h *= d * c;

            num = -(a + m) * (a + b + m) * x / ((a + m2) * (a + m2 + 1));
            d = 1 + num * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + num / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-15) break;
        }
        return h;
    }

    /// <summary>The x at which I_x(a, b) = p — by bisection, which is monotone and cannot leave [0, 1];
    /// 200 halvings reach the limit of a double.</summary>
    public static double InverseRegularizedBeta(double p, double a, double b)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;
        double lo = 0, hi = 1;
        for (int i = 0; i < 200 && hi - lo > 1e-16; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (RegularizedBeta(mid, a, b) < p) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// The Student-t quantile: the t at which a t variable with <paramref name="df"/> degrees of freedom (any positive
    /// real, as Lenth's d = m/3 is) has CDF <paramref name="p"/> — through P(|T| &gt; t) = I_{df/(df+t²)}(df/2, 1/2).
    /// </summary>
    public static double StudentTQuantile(double p, double df)
    {
        if (p == 0.5) return 0;
        if (p < 0.5) return -StudentTQuantile(1 - p, df);
        double x = InverseRegularizedBeta(2 * (1 - p), df / 2, 0.5);
        return x <= 0 ? double.PositiveInfinity : Math.Sqrt(df * (1 - x) / x);
    }
}
