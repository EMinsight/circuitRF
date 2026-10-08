namespace CircuitRF.Engine.Statistics;

/// <summary>
/// The special functions the statistics code needs (docs/design/yield.md): the complementary error
/// function, the normal distribution's CDF and its inverse, the log-gamma function and the regularized incomplete beta
/// function with its inverse. Pure numerics — no domain types (yield overview D11).
/// </summary>
public static class SpecialFunctions
{
    /// <summary>
    /// erfc x to near machine precision, RELATIVE precision in the upper tail — which is where a
    /// probability of a non-physical draw (~1e-9) is read. Below x = 2 the Maclaurin series of erf, whose
    /// terms are all well scaled there; above it the classical continued fraction
    /// erfc x = e^(−x²)/√π · 1/(x + ½/(x + 1/(x + 3⁄2/(x + …)))), evaluated by the modified Lentz method.
    /// </summary>
    public static double Erfc(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x < 0) return 2 - Erfc(-x);
        if (x < 2)
        {
            double sum = 0, term = x, x2 = x * x;
            for (int n = 0; n < 200; n++)
            {
                double add = term / (2 * n + 1);
                sum += add;
                if (Math.Abs(add) < 1e-17 * Math.Abs(sum)) break;
                term *= -x2 / (n + 1);
            }
            return 1 - 2 / Math.Sqrt(Math.PI) * sum;
        }
        if (x > 27) return 0;

        // f = x + a1/(x + a2/(x + …)), a_k = k/2.
        const double tiny = 1e-300;
        double f = x, c = x, d = 0;
        for (int k = 1; k < 500; k++)
        {
            double a = k / 2.0;
            d = x + a * d;
            if (Math.Abs(d) < tiny) d = tiny;
            c = x + a / c;
            if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            double delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1) < 1e-16) break;
        }
        return Math.Exp(-x * x) / Math.Sqrt(Math.PI) / f;
    }

    /// <summary>The standard normal CDF Φ(z), accurate relatively in the lower tail.</summary>
    public static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

    /// <summary>
    /// Φ⁻¹(p), the standard normal quantile, to full double precision: Acklam's rational approximation
    /// (relative error 1.15e-9) refined by one Halley step against <see cref="NormalCdf"/>. The upper
    /// half is taken by symmetry, Φ⁻¹(p) = −Φ⁻¹(1 − p), where 1 − p is exact, so both tails are read
    /// where <see cref="NormalCdf"/> is relatively accurate. −∞ at 0, +∞ at 1.
    /// </summary>
    public static double InverseNormalCdf(double p)
    {
        if (double.IsNaN(p) || p < 0 || p > 1) return double.NaN;
        if (p == 0) return double.NegativeInfinity;
        if (p == 1) return double.PositiveInfinity;
        if (p > 0.5) return -InverseNormalCdf(1 - p);

        double x;
        if (p < 0.02425)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q
                    - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
              / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q
                    + 3.754408661907416e+00) * q + 1);
        }
        else
        {
            double q = p - 0.5, r = q * q;
            x = (((((-3.969683028665376e+01 * r + 2.209460984245205e+02) * r - 2.759285104469687e+02) * r
                    + 1.383577518672690e+02) * r - 3.066479806614716e+01) * r + 2.506628277459239e+00) * q
              / (((((-5.447609879822406e+01 * r + 1.615858368580409e+02) * r - 1.556989798598866e+02) * r
                    + 6.680131188771972e+01) * r - 1.328068155288572e+01) * r + 1);
        }

        // Halley: e = Φ(x) − p, u = e/φ(x), x ← x − u/(1 + x·u/2).
        double e = NormalCdf(x) - p;
        double u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
        return x - u / (1 + x * u / 2);
    }

    /// <summary>The standard normal density φ(z).</summary>
    public static double NormalPdf(double z) => Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI);

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
}
