namespace CircuitRF.Engine.Statistics;

/// <summary>
/// A linear regression of one response on several variables, standardized (yield overview R-ya4-9): what says
/// which statistical variable drives a measurement.
/// </summary>
/// <param name="Coefficients">Per variable, the standardized coefficient β — the change in the response, in its own
/// standard deviations, per standard deviation of the variable, the others held fixed.</param>
/// <param name="Correlations">Per variable, its Pearson correlation with the response.</param>
/// <param name="Shares">Per variable, its share of the explained variance, β·r / R² (Pratt's measure): the shares
/// sum to 1, and with independent variables each is β² / Σβ². 0 when nothing is explained.</param>
/// <param name="RSquared">The fraction of the response's variance the fit explains.</param>
/// <param name="Ridge">The ridge penalty used; 0 for ordinary least squares.</param>
public sealed record RegressionFit(double[] Coefficients, double[] Correlations, double[] Shares, double RSquared, double Ridge)
{
    /// <summary>Whether there were too few observations for ordinary least squares and the fit is a ridge one.</summary>
    public bool Underdetermined => Ridge > 0;
}

/// <summary>
/// Standardized least squares and rank correlation. Each variable and the response are centred and scaled to unit
/// standard deviation, so β is unit-free and comparable across variables. With fewer than
/// <see cref="ObservationsPerVariable"/> observations per variable the fit is ridge regression (penalty
/// <see cref="RidgePenalty"/> × n on the standardized normal equations), which stays defined when the variables
/// outnumber the observations, and says so.
/// </summary>
public static class Regression
{
    /// <summary>Ordinary least squares needs at least this many observations per variable.</summary>
    public const int ObservationsPerVariable = 5;

    /// <summary>The ridge penalty per observation, on standardized variables.</summary>
    public const double RidgePenalty = 0.01;

    /// <summary>Regresses <paramref name="y"/> on the columns of <paramref name="x"/> (x[i][j]: observation i,
    /// variable j). A variable with no spread gets β = 0.</summary>
    public static RegressionFit Fit(IReadOnlyList<double[]> x, IReadOnlyList<double> y)
    {
        int n = y.Count, p = x.Count == 0 ? 0 : x[0].Length;
        if (n < 2) throw new ArgumentException("A regression needs at least two observations.", nameof(y));

        var (ys, ySd) = Standardize(y);
        var xs = new double[p][];
        var live = new bool[p];
        for (int j = 0; j < p; j++)
        {
            var col = new double[n];
            for (int i = 0; i < n; i++) col[i] = x[i][j];
            var (s, sd) = Standardize(col);
            xs[j] = s;
            live[j] = sd > 0;
        }

        var r = new double[p];
        if (ySd > 0)
            for (int j = 0; j < p; j++) r[j] = live[j] ? Dot(xs[j], ys) / (n - 1) : 0;

        double ridge = n >= ObservationsPerVariable * Math.Max(1, p) ? 0 : RidgePenalty * n;
        var beta = new double[p];
        int[] idx = [.. Enumerable.Range(0, p).Where(j => live[j])];
        if (ySd > 0 && idx.Length > 0)
        {
            // The normal equations in correlation form: (R + λ/(n−1)·I) β = r.
            int m = idx.Length;
            var a = new double[m, m];
            var b = new double[m];
            for (int u = 0; u < m; u++)
            {
                b[u] = r[idx[u]];
                for (int v = 0; v < m; v++)
                    a[u, v] = Dot(xs[idx[u]], xs[idx[v]]) / (n - 1) + (u == v ? ridge / (n - 1) : 0);
            }
            var sol = Solve(a, b);
            for (int u = 0; u < m; u++) beta[idx[u]] = sol[u];
        }

        double r2 = 0;
        for (int j = 0; j < p; j++) r2 += beta[j] * r[j];
        var shares = new double[p];
        if (r2 > 0) for (int j = 0; j < p; j++) shares[j] = beta[j] * r[j] / r2;
        return new RegressionFit(beta, r, shares, Math.Clamp(r2, 0, 1), ridge);
    }

    /// <summary>Spearman's rank correlation: Pearson's correlation of the ranks, ties sharing their mean rank.</summary>
    public static double Spearman(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var ra = Ranks(a);
        var rb = Ranks(b);
        var (sa, sda) = Standardize(ra);
        var (sb, sdb) = Standardize(rb);
        return sda > 0 && sdb > 0 ? Dot(sa, sb) / (a.Count - 1) : 0;
    }

    /// <summary>The 1-based ranks of <paramref name="x"/>, ties sharing their mean rank.</summary>
    public static double[] Ranks(IReadOnlyList<double> x)
    {
        int n = x.Count;
        var order = Enumerable.Range(0, n).OrderBy(i => x[i]).ToArray();
        var rank = new double[n];
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j + 1 < n && x[order[j + 1]] == x[order[i]]) j++;
            double mean = (i + j) / 2.0 + 1;
            for (int k = i; k <= j; k++) rank[order[k]] = mean;
            i = j + 1;
        }
        return rank;
    }

    private static (double[] Values, double Sd) Standardize(IReadOnlyList<double> v)
    {
        int n = v.Count;
        double mean = 0;
        foreach (double x in v) mean += x;
        mean /= n;
        double ss = 0;
        foreach (double x in v) ss += (x - mean) * (x - mean);
        double sd = n > 1 ? Math.Sqrt(ss / (n - 1)) : 0;
        var s = new double[n];
        for (int i = 0; i < n; i++) s[i] = sd > 0 ? (v[i] - mean) / sd : 0;
        return (s, sd);
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Gaussian elimination with partial pivoting; a pivot that vanishes leaves that unknown at 0 (a
    /// variable exactly collinear with others explains nothing they do not).</summary>
    private static double[] Solve(double[,] a, double[] b)
    {
        int m = b.Length;
        var x = new double[m];
        var piv = Enumerable.Range(0, m).ToArray();
        for (int c = 0; c < m; c++)
        {
            int best = c;
            for (int r = c + 1; r < m; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[best, c])) best = r;
            if (best != c)
            {
                for (int k = 0; k < m; k++) (a[c, k], a[best, k]) = (a[best, k], a[c, k]);
                (b[c], b[best]) = (b[best], b[c]);
            }
            if (Math.Abs(a[c, c]) < 1e-12) continue;
            for (int r = c + 1; r < m; r++)
            {
                double f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int k = c; k < m; k++) a[r, k] -= f * a[c, k];
                b[r] -= f * b[c];
            }
        }
        for (int c = m - 1; c >= 0; c--)
        {
            if (Math.Abs(a[c, c]) < 1e-12) { x[c] = 0; continue; }
            double s = b[c];
            for (int k = c + 1; k < m; k++) s -= a[c, k] * x[k];
            x[c] = s / a[c, c];
        }
        return x;
    }
}
