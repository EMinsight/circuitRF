namespace CircuitRF.Engine.Envelope;

/// <summary>
/// Dense real LU with partial pivoting, kept as an object so the envelope Newton can reuse one
/// factorization across many solves and steps (chord iteration). Row-major n×n input; the matrix
/// array is consumed in place.
/// </summary>
internal sealed class DenseLu
{
    private readonly double[] _a;
    private readonly int[] _piv;
    public int N { get; }

    private DenseLu(double[] a, int[] piv, int n) { _a = a; _piv = piv; N = n; }

    public static DenseLu? Factor(double[] a, int n)
    {
        var piv = new int[n];
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double best = Math.Abs(a[k * n + k]);
            for (int i = k + 1; i < n; i++)
            {
                double v = Math.Abs(a[i * n + k]);
                if (v > best) { best = v; p = i; }
            }
            if (best == 0.0 || double.IsNaN(best)) return null;
            piv[k] = p;
            if (p != k)
                for (int j = 0; j < n; j++) (a[k * n + j], a[p * n + j]) = (a[p * n + j], a[k * n + j]);
            double inv = 1.0 / a[k * n + k];
            for (int i = k + 1; i < n; i++)
            {
                double f = a[i * n + k] *= inv;
                if (f == 0.0) continue;
                int ri = i * n, rk = k * n;
                for (int j = k + 1; j < n; j++) a[ri + j] -= f * a[rk + j];
            }
        }
        return new DenseLu(a, piv, n);
    }

    /// <summary>Solves A x = b in place (b becomes x).</summary>
    public void Solve(double[] b)
    {
        int n = N;
        for (int k = 0; k < n; k++)
        {
            int p = _piv[k];
            if (p != k) (b[k], b[p]) = (b[p], b[k]);
        }
        for (int i = 1; i < n; i++)
        {
            double s = b[i];
            int ri = i * n;
            for (int j = 0; j < i; j++) s -= _a[ri + j] * b[j];
            b[i] = s;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = b[i];
            int ri = i * n;
            for (int j = i + 1; j < n; j++) s -= _a[ri + j] * b[j];
            b[i] = s / _a[ri + i];
        }
    }
}
