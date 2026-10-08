using NumFlat;

namespace CircuitRF.Engine.Optimization;

/// <summary>
/// The Bayesian optimizer's surrogate (brief-tuneopt-8 R-to8-1): a zero-mean Gaussian process on
/// standardized targets with a Matérn 5/2 kernel and one length scale per coordinate (ARD),
///
/// <para>k(x, y) = σ²·(1 + √5·r + 5r²/3)·exp(−√5·r),  r² = Σ ((xᵢ − yᵢ)/ℓᵢ)²,</para>
///
/// plus a noise variance on the diagonal. The hyperparameters θ = (ln ℓ₁…ln ℓₙ, ln σ², ln σₙ²) are
/// fitted by maximizing the log marginal likelihood with its analytic gradient, by a bounded BFGS from
/// the previous fit and from fixed and seeded starts. The factorization is NumFlat's Cholesky.
///
/// <para><b>Cost.</b> One likelihood evaluation factors the N×N kernel matrix and forms its inverse —
/// O(N³) in the number of observations — and a fit takes up to a few hundred of them. That is why the
/// optimizer caps the observations it keeps (its <c>archive</c> option) and why its use-when line says
/// it is for slow simulations.</para>
/// </summary>
internal sealed class GaussianProcess
{
    private const double Sqrt5 = 2.23606797749979;

    // Bounds of θ, in the unit box and on standardized targets.
    private static readonly double LogEllMin = Math.Log(0.01), LogEllMax = Math.Log(20.0);
    private static readonly double LogSfMin  = Math.Log(0.05), LogSfMax  = Math.Log(20.0);
    private static readonly double LogSnMin  = Math.Log(1e-6), LogSnMax  = Math.Log(0.1);

    private readonly double[][] _x;
    private readonly double[] _ell;
    private readonly double _sf2, _sn2;
    private readonly double[,] _l;        // lower Cholesky factor of K
    private readonly double[] _alpha;     // K⁻¹·y

    private GaussianProcess(double[][] x, double[] y, double[] theta, double[,] l, double[] alpha)
    {
        _x = x; Y = y; Theta = theta; _l = l; _alpha = alpha;
        int n = x[0].Length;
        _ell = new double[n];
        for (int i = 0; i < n; i++) _ell[i] = Math.Exp(theta[i]);
        _sf2 = Math.Exp(theta[n]);
        _sn2 = Math.Exp(theta[n + 1]);
    }

    /// <summary>The fitted hyperparameters, for the next fit to start from.</summary>
    public double[] Theta { get; }

    /// <summary>The (standardized) targets the process was conditioned on.</summary>
    public double[] Y { get; }

    /// <summary>The length scales, in the unit box.</summary>
    public IReadOnlyList<double> LengthScales => _ell;

    /// <summary>
    /// Fits a process to <paramref name="x"/>, <paramref name="y"/> (y already standardized). Null when
    /// no θ gives a positive-definite kernel matrix — two identical points with no noise, say.
    /// </summary>
    public static GaussianProcess? Fit(double[][] x, double[] y, double[]? previous, SplitMix64 rng)
    {
        int n = x[0].Length, p = n + 2;
        var starts = new List<double[]>();
        if (previous is { } prev && prev.Length == p) starts.Add((double[])prev.Clone());
        var def = new double[p];
        for (int i = 0; i < n; i++) def[i] = Math.Log(0.3);
        def[n] = 0; def[n + 1] = Math.Log(1e-4);
        starts.Add(def);
        // Seeded starts, fewer as the archive grows (each costs O(N³) per likelihood).
        int extra = x.Length <= 100 ? 2 : 0;
        for (int s = 0; s < extra; s++)
        {
            var t = new double[p];
            for (int i = 0; i < n; i++) t[i] = LogEllMin + rng.NextDouble() * (Math.Log(2.0) - LogEllMin);
            t[n] = Math.Log(0.3) + rng.NextDouble() * (Math.Log(3.0) - Math.Log(0.3));
            t[n + 1] = Math.Log(1e-5) + rng.NextDouble() * (Math.Log(1e-2) - Math.Log(1e-5));
            starts.Add(t);
        }

        double[]? best = null;
        double bestNll = double.PositiveInfinity;
        int iterations = x.Length <= 100 ? 60 : 25;
        foreach (var s in starts)
        {
            var (t, nll) = Bfgs(s, th => NegLogLikelihood(x, y, th), iterations);
            if (nll < bestNll) (bestNll, best) = (nll, t);
        }
        if (best is null) return null;

        var f = Factor(x, best);
        if (f is null) return null;
        var alpha = SolveL(f.Value.L, y);
        return new GaussianProcess(x, y, best, f.Value.L, alpha);
    }

    /// <summary>
    /// The same hyperparameters conditioned on one more observation — the constant liar's fantasy
    /// point. No refit: the liar should change where the next point goes, not what the response looks
    /// like. Null when the extra point makes the matrix singular.
    /// </summary>
    public GaussianProcess? With(double[] x, double y)
    {
        double[][] xs = [.. _x, x];
        double[] ys = [.. Y, y];
        var f = Factor(xs, Theta);
        if (f is null) return null;
        return new GaussianProcess(xs, ys, Theta, f.Value.L, SolveL(f.Value.L, ys));
    }

    /// <summary>The posterior mean and standard deviation of the latent function at <paramref name="x"/>.</summary>
    public (double Mean, double Sd) Predict(double[] x)
    {
        int m = _x.Length;
        var k = new double[m];
        double mean = 0;
        for (int i = 0; i < m; i++)
        {
            k[i] = Kernel(_x[i], x, _ell, _sf2);
            mean += k[i] * _alpha[i];
        }
        // v = L⁻¹k; var = σ² − v·v.
        double vv = 0;
        for (int i = 0; i < m; i++)
        {
            double s = k[i];
            for (int j = 0; j < i; j++) s -= _l[i, j] * k[j];
            k[i] = s / _l[i, i];
            vv += k[i] * k[i];
        }
        return (mean, Math.Sqrt(Math.Max(_sf2 - vv, 1e-18)));
    }

    // ── The kernel ───────────────────────────────────────────────────────────

    private static double Kernel(double[] a, double[] b, double[] ell, double sf2)
    {
        double r2 = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = (a[i] - b[i]) / ell[i];
            r2 += d * d;
        }
        double r = Math.Sqrt(r2);
        return sf2 * (1 + Sqrt5 * r + 5.0 / 3.0 * r2) * Math.Exp(-Sqrt5 * r);
    }

    private static (double[,] L, CholeskyDecompositionDouble Chol)? Factor(double[][] x, double[] theta)
    {
        int m = x.Length, n = x[0].Length;
        var ell = new double[n];
        for (int i = 0; i < n; i++) ell[i] = Math.Exp(theta[i]);
        double sf2 = Math.Exp(theta[n]), sn2 = Math.Exp(theta[n + 1]);

        // A tiny jitter, grown if the factorization fails, keeps a near-duplicate pair from ending
        // the run; the noise bound already keeps it small against σ².
        for (double jitter = 1e-10; jitter <= 1e-4; jitter *= 100)
        {
            var k = new Mat<double>(m, m);
            for (int i = 0; i < m; i++)
            {
                k[i, i] = sf2 + sn2 + jitter * sf2;
                for (int j = 0; j < i; j++)
                {
                    double v = Kernel(x[i], x[j], ell, sf2);
                    k[i, j] = v; k[j, i] = v;
                }
            }
            CholeskyDecompositionDouble chol;
            try { chol = k.Cholesky(); }
            catch (Exception) { continue; }
            var l = new double[m, m];
            var lm = chol.L;
            bool ok = true;
            for (int i = 0; i < m && ok; i++)
                for (int j = 0; j <= i; j++)
                {
                    l[i, j] = lm[i, j];
                    if (i == j && !(l[i, i] > 0)) { ok = false; break; }
                }
            if (ok) return (l, chol);
        }
        return null;
    }

    /// <summary>K⁻¹·y by two triangular solves on L.</summary>
    private static double[] SolveL(double[,] l, double[] y)
    {
        int m = y.Length;
        var z = new double[m];
        for (int i = 0; i < m; i++)
        {
            double s = y[i];
            for (int j = 0; j < i; j++) s -= l[i, j] * z[j];
            z[i] = s / l[i, i];
        }
        var a = new double[m];
        for (int i = m - 1; i >= 0; i--)
        {
            double s = z[i];
            for (int j = i + 1; j < m; j++) s -= l[j, i] * a[j];
            a[i] = s / l[i, i];
        }
        return a;
    }

    /// <summary>
    /// −log p(y | θ) and its gradient: ½yᵀK⁻¹y + ½ln|K| (constant dropped), and
    /// ∂/∂θⱼ = ½ tr((K⁻¹ − ααᵀ)·∂K/∂θⱼ), α = K⁻¹y. Infinite when no jitter makes K factor.
    /// </summary>
    private static (double Value, double[] Gradient) NegLogLikelihood(double[][] x, double[] y, double[] theta)
    {
        int m = x.Length, n = x[0].Length, p = n + 2;
        var f = Factor(x, theta);
        if (f is null) return (double.PositiveInfinity, new double[p]);
        var (l, chol) = f.Value;

        var alpha = SolveL(l, y);
        double logDet = 0;
        for (int i = 0; i < m; i++) logDet += 2 * Math.Log(l[i, i]);
        double value = 0.5 * DenseLinear.Dot(y, alpha) + 0.5 * logDet;

        // W = K⁻¹ − ααᵀ, column by column.
        var w = new double[m, m];
        var e = new Vec<double>(m);
        var col = new Vec<double>(m);
        for (int c = 0; c < m; c++)
        {
            for (int r = 0; r < m; r++) e[r] = r == c ? 1 : 0;
            chol.Solve(e, col);
            for (int r = 0; r < m; r++) w[r, c] = col[r] - alpha[r] * alpha[c];
        }

        var ell = new double[n];
        for (int i = 0; i < n; i++) ell[i] = Math.Exp(theta[i]);
        double sf2 = Math.Exp(theta[n]), sn2 = Math.Exp(theta[n + 1]);

        var g = new double[p];
        for (int i = 0; i < m; i++)
        {
            // Diagonal: ∂K/∂ln σ² = σ² (the kernel's own value at r = 0), ∂K/∂ln σₙ² = σₙ².
            g[n]     += 0.5 * w[i, i] * sf2;
            g[n + 1] += 0.5 * w[i, i] * sn2;
            for (int j = 0; j < i; j++)
            {
                double r2 = 0;
                for (int d = 0; d < n; d++)
                {
                    double t = (x[i][d] - x[j][d]) / ell[d];
                    r2 += t * t;
                }
                double r = Math.Sqrt(r2), ex = Math.Exp(-Sqrt5 * r);
                double kv = sf2 * (1 + Sqrt5 * r + 5.0 / 3.0 * r2) * ex;
                double common = sf2 * 5.0 / 3.0 * (1 + Sqrt5 * r) * ex;   // ∂k/∂ln ℓ_d = common·(Δ_d/ℓ_d)²
                double wij = w[i, j];                                      // symmetric: counted twice
                g[n] += wij * kv;
                for (int d = 0; d < n; d++)
                {
                    double t = (x[i][d] - x[j][d]) / ell[d];
                    g[d] += wij * common * t * t;
                }
            }
        }
        return (value, g);
    }

    private static double[] Bounds(int p, bool upper)
    {
        var b = new double[p];
        for (int i = 0; i < p - 2; i++) b[i] = upper ? LogEllMax : LogEllMin;
        b[p - 2] = upper ? LogSfMax : LogSfMin;
        b[p - 1] = upper ? LogSnMax : LogSnMin;
        return b;
    }

    /// <summary>A small projected BFGS with a backtracking line search, for θ's handful of dimensions.</summary>
    private static (double[] Theta, double Value) Bfgs(double[] start, Func<double[], (double Value, double[] Gradient)> f, int iterations)
    {
        int p = start.Length;
        var lo = Bounds(p, upper: false);
        var hi = Bounds(p, upper: true);
        double[] Project(double[] t)
        {
            var r = new double[p];
            for (int i = 0; i < p; i++) r[i] = Math.Clamp(t[i], lo[i], hi[i]);
            return r;
        }

        var x = Project(start);
        var (fx, gx) = f(x);
        if (!double.IsFinite(fx)) return (x, fx);
        var h = new double[p, p];
        for (int i = 0; i < p; i++) h[i, i] = 1;

        for (int it = 0; it < iterations; it++)
        {
            // Direction −H·g, with coordinates on a bound pushing outward held.
            var d = new double[p];
            for (int i = 0; i < p; i++)
            {
                double s = 0;
                for (int j = 0; j < p; j++) s -= h[i, j] * gx[j];
                d[i] = s;
            }
            for (int i = 0; i < p; i++)
                if ((x[i] <= lo[i] && d[i] < 0) || (x[i] >= hi[i] && d[i] > 0)) d[i] = 0;
            double slope = DenseLinear.Dot(d, gx);
            if (slope >= 0)
            {
                for (int i = 0; i < p; i++) for (int j = 0; j < p; j++) h[i, j] = i == j ? 1 : 0;
                for (int i = 0; i < p; i++)
                    d[i] = (x[i] <= lo[i] && gx[i] > 0) || (x[i] >= hi[i] && gx[i] < 0) ? 0 : -gx[i];
                slope = DenseLinear.Dot(d, gx);
                if (slope >= -1e-14) break;
            }

            double step = 1;
            double[] xn = x; double fn = fx; double[] gn = gx;
            bool accepted = false;
            for (int ls = 0; ls < 30; ls++, step *= 0.5)
            {
                var trial = new double[p];
                for (int i = 0; i < p; i++) trial[i] = x[i] + step * d[i];
                trial = Project(trial);
                var (ft, gt) = f(trial);
                if (double.IsFinite(ft) && ft <= fx + 1e-4 * step * slope)
                {
                    (xn, fn, gn, accepted) = (trial, ft, gt, true);
                    break;
                }
            }
            if (!accepted) break;

            var sv = new double[p]; var yv = new double[p];
            for (int i = 0; i < p; i++) { sv[i] = xn[i] - x[i]; yv[i] = gn[i] - gx[i]; }
            double sy = DenseLinear.Dot(sv, yv);
            bool converged = Math.Abs(fx - fn) <= 1e-10 * (1 + Math.Abs(fx));
            (x, fx, gx) = (xn, fn, gn);
            if (converged) break;
            if (sy <= 1e-12) continue;

            // H ← (I − ρsyᵀ)H(I − ρysᵀ) + ρssᵀ
            double rho = 1 / sy;
            var hy = new double[p];
            for (int i = 0; i < p; i++) { double s = 0; for (int j = 0; j < p; j++) s += h[i, j] * yv[j]; hy[i] = s; }
            double yhy = DenseLinear.Dot(yv, hy);
            for (int i = 0; i < p; i++)
                for (int j = 0; j < p; j++)
                    h[i, j] += -rho * (hy[i] * sv[j] + sv[i] * hy[j]) + (rho * rho * yhy + rho) * sv[i] * sv[j];
        }
        return (x, fx);
    }
}
