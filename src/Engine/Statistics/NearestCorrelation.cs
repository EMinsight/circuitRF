namespace CircuitRF.Engine.Statistics;

/// <summary>
/// The correlation matrix a set of <c>correlate</c> lines describes, and its repair when it is not
/// one (yield overview D3): the nearest correlation matrix in the Frobenius norm, by Higham's
/// alternating projections with Dykstra's correction — alternately the nearest positive semidefinite
/// matrix (negative eigenvalues set to zero) and the nearest matrix with a unit diagonal, until the
/// two agree. Pure numerics; written in-house.
/// </summary>
public static class NearestCorrelation
{
    /// <summary>True when the symmetric matrix has a Cholesky factor — every pivot positive.</summary>
    public static bool IsPositiveDefinite(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int j = 0; j < n; j++)
        {
            double d = a[j, j];
            for (int k = 0; k < j; k++) d -= l[j, k] * l[j, k];
            if (!(d > 1e-12)) return false;
            l[j, j] = Math.Sqrt(d);
            for (int i = j + 1; i < n; i++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                l[i, j] = s / l[j, j];
            }
        }
        return true;
    }

    /// <summary>
    /// The nearest correlation matrix to <paramref name="a"/> (symmetric, unit diagonal) and the largest
    /// change to any entry. The eigenvalue floor is a small positive number rather than zero, so the
    /// result is positive DEFINITE — a sampler has to factor it.
    /// </summary>
    public static (double[,] Matrix, double LargestChange) Repair(double[,] a, double eigenFloor = 1e-8)
    {
        int n = a.GetLength(0);
        var y = (double[,])a.Clone();
        var ds = new double[n, n];
        var x = new double[n, n];
        for (int iter = 0; iter < 1000; iter++)
        {
            var r = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) r[i, j] = y[i, j] - ds[i, j];
            x = ProjectSemidefinite(r, eigenFloor);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) ds[i, j] = x[i, j] - r[i, j];
            var next = (double[,])x.Clone();
            for (int i = 0; i < n; i++) next[i, i] = 1;

            double diff = 0, norm = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    diff = Math.Max(diff, Math.Abs(next[i, j] - y[i, j]));
                    norm = Math.Max(norm, Math.Abs(next[i, j]));
                }
            y = next;
            if (diff <= 1e-12 * Math.Max(1, norm)) break;
        }

        // The unit-diagonal projection can leave the last iterate a hair outside the cone; one more
        // semidefinite projection rescaled to a unit diagonal lands it inside.
        if (!IsPositiveDefinite(y))
        {
            var p = ProjectSemidefinite(y, eigenFloor);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) y[i, j] = p[i, j] / Math.Sqrt(p[i, i] * p[j, j]);
        }

        double largest = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) largest = Math.Max(largest, Math.Abs(y[i, j] - a[i, j]));
        return (y, largest);
    }

    private static double[,] ProjectSemidefinite(double[,] a, double floor)
    {
        int n = a.GetLength(0);
        var (values, vectors) = SymmetricEigen(a);
        var p = new double[n, n];
        for (int k = 0; k < n; k++)
        {
            double lambda = Math.Max(values[k], floor);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) p[i, j] += lambda * vectors[i, k] * vectors[j, k];
        }
        return p;
    }

    /// <summary>Eigenvalues and eigenvectors (columns) of a symmetric matrix, by cyclic Jacobi rotations.</summary>
    public static (double[] Values, double[,] Vectors) SymmetricEigen(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;

        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            if (off < 1e-30) break;

            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }

        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i, i];
        return (values, v);
    }
}
