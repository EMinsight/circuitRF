namespace CircuitRF.Engine.Statistics;

/// <summary>An ordinary least-squares fit: the coefficients, and how much of the response's variance they explain.</summary>
/// <param name="Coefficients">Per column of the design matrix, its coefficient.</param>
/// <param name="RSquared">1 − SS_res / SS_tot over the observations fitted; 1 when the response has no spread and the
/// fit reproduces it.</param>
/// <param name="Rank">The columns the fit could resolve; a column dependent on earlier ones gets coefficient 0.</param>
public sealed record LeastSquaresFit(double[] Coefficients, double RSquared, int Rank);

/// <summary>
/// Ordinary least squares by Householder QR (brief-yield-12 R-ya12-2) — no normal equations, so a response that the
/// columns span exactly is fitted to round-off and its R² is 1 to about 1e-15. Unlike <see cref="Regression"/>, nothing
/// is standardized and nothing is penalized: the coefficients are in the columns' own units, ready to predict with.
/// </summary>
public static class LeastSquares
{
    /// <summary>Fits <paramref name="y"/> on the columns of <paramref name="a"/> (a[i][j]: observation i, column j).
    /// An intercept is a column of ones the caller supplies.</summary>
    public static LeastSquaresFit Fit(IReadOnlyList<double[]> a, IReadOnlyList<double> y)
    {
        int n = y.Count, p = a.Count == 0 ? 0 : a[0].Length;
        if (n == 0 || p == 0) throw new ArgumentException("A fit needs at least one observation and one column.");
        var m = new double[n, p];
        var b = new double[n];
        double scale = 0;
        for (int i = 0; i < n; i++)
        {
            b[i] = y[i];
            for (int j = 0; j < p; j++) { m[i, j] = a[i][j]; scale = Math.Max(scale, Math.Abs(a[i][j])); }
        }

        // Householder: column c's reflection zeroes everything below the diagonal and is applied to b as it goes.
        var diag = new double[p];
        var live = new bool[p];
        double tol = 1e-12 * Math.Max(scale, 1) * Math.Max(n, p);
        int rows = Math.Min(n, p);
        for (int c = 0; c < rows; c++)
        {
            double norm = 0;
            for (int i = c; i < n; i++) norm += m[i, c] * m[i, c];
            norm = Math.Sqrt(norm);
            if (norm <= tol) { diag[c] = 0; continue; }
            double alpha = m[c, c] > 0 ? -norm : norm;
            m[c, c] -= alpha;
            double vv = 0;
            for (int i = c; i < n; i++) vv += m[i, c] * m[i, c];
            for (int k = c + 1; k < p; k++)
            {
                double s = 0;
                for (int i = c; i < n; i++) s += m[i, c] * m[i, k];
                s = 2 * s / vv;
                for (int i = c; i < n; i++) m[i, k] -= s * m[i, c];
            }
            double sb = 0;
            for (int i = c; i < n; i++) sb += m[i, c] * b[i];
            sb = 2 * sb / vv;
            for (int i = c; i < n; i++) b[i] -= sb * m[i, c];
            diag[c] = alpha;
            live[c] = true;
        }

        // Back substitution on R; a dependent column (no pivot) explains nothing the others do not.
        var x = new double[p];
        int rank = 0;
        for (int c = rows - 1; c >= 0; c--)
        {
            if (!live[c]) continue;
            rank++;
            double s = b[c];
            for (int k = c + 1; k < p; k++) s -= m[c, k] * x[k];
            x[c] = s / diag[c];
        }

        double mean = y.Average(), ssTot = 0, ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            double f = 0;
            for (int j = 0; j < p; j++) f += a[i][j] * x[j];
            ssRes += (y[i] - f) * (y[i] - f);
            ssTot += (y[i] - mean) * (y[i] - mean);
        }
        double yScale = y.Max(Math.Abs);
        double r2 = ssTot > 1e-24 * Math.Max(yScale * yScale, 1e-300) * n
            ? 1 - ssRes / ssTot
            : ssRes <= 1e-24 * Math.Max(yScale * yScale, 1e-300) * n ? 1 : 0;
        return new LeastSquaresFit(x, Math.Clamp(r2, 0, 1), rank);
    }
}
