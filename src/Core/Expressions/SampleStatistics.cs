namespace CircuitRF.Core.Expressions;

/// <summary>
/// The descriptive statistics of a sample (docs/design/measurements.md "Reductions over an axis") — what the <c>mean_over</c> family
/// reduces an axis with, and what a yield run's report and the Data Display's statistics table read. Pure functions
/// of the values given.
///
/// <para><b>A NaN is a missing value and is skipped</b>, never propagated: over a Monte Carlo run's <c>trial</c>
/// axis a NaN is a trial with no value (one that did not evaluate), and a mean that turned NaN because one trial in
/// five hundred failed would hide every other trial. (<c>min_over</c>/<c>max_over</c> keep their own rule — a worst
/// case over a band propagates NaN, so a bad grid point is never hidden behind a good one.)</para>
///
/// <para>Here rather than in <c>src/Engine/Statistics</c> beside the interval and the regression because the
/// expression engine is in <c>src/Core</c>, which may not reference the numeric layer.</para>
/// </summary>
public static class SampleStatistics
{
    /// <summary>The values that are not NaN, in order.</summary>
    public static double[] Present(IEnumerable<double> values) => [.. values.Where(v => !double.IsNaN(v))];

    public static double Mean(IReadOnlyList<double> x)
    {
        double sum = 0; int n = 0;
        foreach (double v in x) if (!double.IsNaN(v)) { sum += v; n++; }
        return n == 0 ? double.NaN : sum / n;
    }

    /// <summary>The sample standard deviation (divisor n − 1); NaN below two values.</summary>
    public static double StdDev(IReadOnlyList<double> x)
    {
        double m = Mean(x), ss = 0; int n = 0;
        foreach (double v in x) if (!double.IsNaN(v)) { ss += (v - m) * (v - m); n++; }
        return n < 2 ? double.NaN : Math.Sqrt(ss / (n - 1));
    }

    public static double Median(IReadOnlyList<double> x) => Percentile(x, 50);

    /// <summary>
    /// The <paramref name="p"/>-th percentile (0–100), interpolated linearly between order statistics at rank
    /// (n − 1)·p/100 — the common default definition, which gives the minimum at 0, the maximum at 100 and the
    /// median at 50.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> x, double p)
    {
        if (!(p >= 0 && p <= 100)) throw new ArgumentOutOfRangeException(nameof(p), "A percentile lies between 0 and 100.");
        var s = Present(x);
        if (s.Length == 0) return double.NaN;
        Array.Sort(s);
        double h = (s.Length - 1) * p / 100;
        int lo = (int)Math.Floor(h);
        int hi = Math.Min(lo + 1, s.Length - 1);
        // On an order statistic — or between two equal ones — there is nothing to interpolate, and 0 · (∞ − ∞) would be
        // NaN where the sample's extreme is infinite (dB of an exact zero; brief-yield-15 R-ya15-8).
        if (h == lo || s[hi] == s[lo]) return s[lo];
        return s[lo] + (h - lo) * (s[hi] - s[lo]);
    }

    /// <summary>The moment coefficient of skewness m₃ / m₂^{3/2} (central moments with divisor n); NaN below three
    /// values or with no spread.</summary>
    public static double Skewness(IReadOnlyList<double> x)
    {
        var (n, m2, m3, _) = CentralMoments(x);
        return n < 3 || m2 == 0 ? double.NaN : m3 / Math.Pow(m2, 1.5);
    }

    /// <summary>The excess kurtosis m₄ / m₂² − 3 (0 for a normal distribution); NaN below four values or with no
    /// spread.</summary>
    public static double ExcessKurtosis(IReadOnlyList<double> x)
    {
        var (n, m2, _, m4) = CentralMoments(x);
        return n < 4 || m2 == 0 ? double.NaN : m4 / (m2 * m2) - 3;
    }

    /// <summary>
    /// The process capability index C<sub>pk</sub>: the distance from the mean to the nearer specification limit in
    /// units of 3σ — min((hi − μ)/3σ, (μ − lo)/3σ) over the limits given (either may be null, not both).
    /// </summary>
    public static double Cpk(IReadOnlyList<double> x, double? lo, double? hi)
    {
        if (lo is null && hi is null) throw new ArgumentException("Cpk needs at least one limit.");
        double m = Mean(x), s = StdDev(x);
        double c = double.PositiveInfinity;
        if (hi is { } h) c = Math.Min(c, (h - m) / (3 * s));
        if (lo is { } l) c = Math.Min(c, (m - l) / (3 * s));
        return c;
    }

    /// <summary>How many standard deviations the <paramref name="limit"/> lies from the mean: (limit − μ)/σ —
    /// positive when the limit is above the mean.</summary>
    public static double SigmaTo(IReadOnlyList<double> x, double limit) => (limit - Mean(x)) / StdDev(x);

    /// <summary>The fraction of the present values that are true (non-zero).</summary>
    public static double FractionTrue(IReadOnlyList<double> x)
    {
        int n = 0, t = 0;
        foreach (double v in x) if (!double.IsNaN(v)) { n++; if (v != 0) t++; }
        return n == 0 ? double.NaN : (double)t / n;
    }

    /// <summary>
    /// Equal-width bins over [<paramref name="lo"/>, <paramref name="hi"/>] (the present values' extent when not
    /// given): each bin's centre and the bin index of every value, −1 for a NaN or a value outside. A bin holds its
    /// lower edge; the last also holds the upper. A sample with no spread gets bins of width 1 centred on it.
    /// </summary>
    public static (double[] Centres, double Width, int[] Index) Bin(IReadOnlyList<double> x, int bins, double? lo = null, double? hi = null)
    {
        if (bins < 1) throw new ArgumentOutOfRangeException(nameof(bins), "There must be at least one bin.");
        var present = Present(x);
        double a = lo ?? (present.Length == 0 ? 0 : present.Min());
        double b = hi ?? (present.Length == 0 ? 1 : present.Max());
        if (b < a) (a, b) = (b, a);
        if (b == a) { a -= 0.5 * bins; b += 0.5 * bins; }
        double width = (b - a) / bins;
        var centres = new double[bins];
        for (int k = 0; k < bins; k++) centres[k] = a + (k + 0.5) * width;
        var index = new int[x.Count];
        for (int i = 0; i < x.Count; i++)
        {
            double v = x[i];
            if (double.IsNaN(v) || v < a || v > b) { index[i] = -1; continue; }
            index[i] = Math.Min(bins - 1, (int)Math.Floor((v - a) / width));
        }
        return (centres, width, index);
    }

    /// <summary>
    /// The Freedman–Diaconis bin count for a histogram of the present values: width 2·IQR·n^(−1/3), so the count
    /// is ⌈(max − min)/width⌉, between 1 and <paramref name="max"/>. A sample whose interquartile range is zero
    /// (most values equal) falls back to Sturges' ⌈log₂ n⌉ + 1, which needs no spread.
    /// </summary>
    public static int FreedmanDiaconisBins(IReadOnlyList<double> x, int max = 100)
    {
        var s = Present(x);
        if (s.Length < 2) return 1;
        double lo = s.Min(), hi = s.Max();
        if (!(hi > lo)) return 1;
        double iqr = Percentile(s, 75) - Percentile(s, 25);
        int bins = iqr > 0
            ? (int)Math.Ceiling((hi - lo) / (2 * iqr * Math.Pow(s.Length, -1.0 / 3)))
            : (int)Math.Ceiling(Math.Log2(s.Length)) + 1;
        return Math.Clamp(bins, 1, max);
    }

    /// <summary>
    /// The normal probability plot of the present values: the values sorted, each against Φ⁻¹ of its plotting
    /// position (i − 3/8)/(n + 1/4) (Blom's, the usual choice for a normal plot). A Gaussian sample lies on a
    /// straight line of slope 1/σ through (μ, 0).
    /// </summary>
    public static (double[] Sorted, double[] Z) NormalScores(IReadOnlyList<double> x)
    {
        var sorted = Present(x);
        Array.Sort(sorted);
        var z = new double[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
            z[i] = NormalDistribution.InverseCdf((i + 1 - 0.375) / (sorted.Length + 0.25));
        return (sorted, z);
    }

    private static (int N, double M2, double M3, double M4) CentralMoments(IReadOnlyList<double> x)
    {
        double m = Mean(x), m2 = 0, m3 = 0, m4 = 0; int n = 0;
        foreach (double v in x)
        {
            if (double.IsNaN(v)) continue;
            double d = v - m, d2 = d * d;
            m2 += d2; m3 += d2 * d; m4 += d2 * d2; n++;
        }
        return n == 0 ? (0, 0, 0, 0) : (n, m2 / n, m3 / n, m4 / n);
    }
}
