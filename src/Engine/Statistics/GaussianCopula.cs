namespace CircuitRF.Engine.Statistics;

/// <summary>
/// The Gaussian copula's correlation step (yield overview D3): independent standard normals in,
/// correlated standard normals out, z ← L·z with L the Cholesky factor of the correlation matrix. It
/// acts on z BEFORE each marginal's inverse CDF, so it correlates values of any distributions. A matrix
/// that is not positive definite is replaced by the nearest one that is (<see cref="NearestCorrelation"/>),
/// and says so.
/// </summary>
public sealed class GaussianCopula
{
    private readonly double[,] _lower;

    public GaussianCopula(double[,] correlation)
    {
        int n = correlation.GetLength(0);
        Matrix = (double[,])correlation.Clone();
        if (!NearestCorrelation.IsPositiveDefinite(Matrix))
        {
            (Matrix, LargestChange) = NearestCorrelation.Repair(Matrix);
            Repaired = true;
        }
        _lower = Cholesky(Matrix);
        Size = n;
    }

    /// <summary>How many variables it correlates.</summary>
    public int Size { get; }

    /// <summary>The matrix sampled with — the one given, or its repair.</summary>
    public double[,] Matrix { get; }

    /// <summary>Whether the matrix given was not positive definite and was repaired.</summary>
    public bool Repaired { get; }

    /// <summary>The largest change the repair made to any entry; 0 when none was needed.</summary>
    public double LargestChange { get; }

    /// <summary>The lower-triangular factor L, with L·Lᵀ = <see cref="Matrix"/>.</summary>
    public double[,] Lower => (double[,])_lower.Clone();

    /// <summary>Correlates <paramref name="z"/> in place.</summary>
    public void Correlate(Span<double> z)
    {
        if (z.Length != Size) throw new ArgumentOutOfRangeException(nameof(z), $"Expected {Size} values, got {z.Length}.");
        // Row i of L·z reads z[0..i], so walking from the last row up leaves each needed input intact.
        for (int i = Size - 1; i >= 0; i--)
        {
            double s = 0;
            for (int j = 0; j <= i; j++) s += _lower[i, j] * z[j];
            z[i] = s;
        }
    }

    /// <summary>The Cholesky factor of a symmetric positive definite matrix.</summary>
    public static double[,] Cholesky(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int j = 0; j < n; j++)
        {
            double d = a[j, j];
            for (int k = 0; k < j; k++) d -= l[j, k] * l[j, k];
            if (!(d > 0)) throw new ArgumentOutOfRangeException(nameof(a), "The matrix is not positive definite.");
            l[j, j] = Math.Sqrt(d);
            for (int i = j + 1; i < n; i++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                l[i, j] = s / l[j, j];
            }
        }
        return l;
    }
}
