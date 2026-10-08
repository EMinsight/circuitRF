namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Minimax (brief-tuneopt-7 R-to7-6): minimizes the LARGEST residual, F(x) = maxᵢ rᵢ(x), by sequential
/// linear programming in a trust region. Each iteration linearizes every residual with a
/// forward-difference Jacobian — one batch of n points, retried backward where a point fails, as
/// Levenberg–Marquardt's — and solves
/// <code>
///   minimize t   subject to   rᵢ + Jᵢ·d ≤ t  for every i,   |d|∞ ≤ Δ,   0 ≤ x + d ≤ 1
/// </code>
/// for the step d. The ratio of the actual to the predicted fall in F moves Δ as in any trust-region
/// method: the step is taken when F falls, and Δ shrinks when the prediction was poor.
///
/// <para>At a minimax optimum several residuals are equal and largest; the linear program finds that
/// vertex directly, which is why this is the method for an equal-ripple response — the step drives
/// the worst residuals level instead of trading one against another as a sum of squares does.</para>
///
/// <para>It needs the residual vector and the minimax cost form; selecting it sets that form. A
/// failed or infeasible trial point (R-to7-8) is a failed step: Δ shrinks to a quarter of it. The run
/// ends when no step within Δ predicts a fall, or Δ falls below <c>minradius</c>.</para>
///
/// <para>Options and their defaults: <c>minimax</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class Minimax : AskTellAlgorithm
{
    public const string AlgorithmId = "minimax";

    private readonly double _h, _radius0, _minRadius;

    public Minimax(double[] start, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        _h         = Math.Clamp(Option(options, "fdstep"), 1e-9, 0.1);
        _radius0   = Math.Clamp(Option(options, "radius"), 1e-9, 1.0);
        _minRadius = Option(options, "minradius");
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        var x = (double[])Start.Clone();
        yield return [x];
        if (Results[0] is not { Failed: false, Residuals: { Length: > 0 } r })
        {
            Finish("the start point could not be evaluated");
            yield break;
        }
        int m = r.Length;
        double F = Worst(r);
        double delta = _radius0;

        while (true)
        {
            // ── Jacobian: one batch forward, the failures retried backward ─────
            var pts = DifferencePoints(x, _h, [.. Enumerable.Range(0, n)], out var sign);
            yield return pts;
            var J = new double[m, n];
            var held = new bool[n];
            var retry = new List<int>();
            for (int i = 0; i < n; i++)
                if (!Column(J, i, Results[i], r, sign[i] * _h)) retry.Add(i);
            if (retry.Count > 0)
            {
                var back = new double[retry.Count][];
                for (int k = 0; k < retry.Count; k++)
                {
                    back[k] = (double[])x.Clone();
                    back[k][retry[k]] = Math.Clamp(x[retry[k]] - sign[retry[k]] * _h, 0, 1);
                }
                yield return back;
                for (int k = 0; k < retry.Count; k++)
                {
                    int i = retry[k];
                    double step = back[k][i] - x[i];
                    if (step == 0 || !Column(J, i, Results[k], r, step)) held[i] = true;
                }
            }

            // ── Trial steps until one lowers the worst residual ───────────────
            bool accepted = false;
            while (!accepted)
            {
                if (delta < _minRadius) { Finish("the trust radius fell below its minimum"); yield break; }

                var lo = new double[n]; var hi = new double[n];
                for (int i = 0; i < n; i++)
                {
                    lo[i] = held[i] ? 0 : Math.Max(-delta, -x[i]);
                    hi[i] = held[i] ? 0 : Math.Min(delta, 1 - x[i]);
                }
                var (d, t) = MinimaxLp.Solve(r, J, lo, hi);
                double pred = F - t;
                if (d is null || pred <= 1e-13 * Math.Max(1, Math.Abs(F)))
                {
                    Finish("no step lowers the worst residual");
                    yield break;
                }

                var xn = new double[n];
                for (int i = 0; i < n; i++) xn[i] = x[i] + d[i];
                xn = Project(xn);
                double dInf = InfNorm(xn, x);
                if (dInf < 1e-15) { Finish("the step fell below tolerance"); yield break; }

                yield return [xn];
                var e = Results[0];
                if (e.Failed || e.Residuals is not { } rn || rn.Length != m)
                {
                    delta = 0.25 * dInf;
                    continue;
                }
                double Fn = Worst(rn);
                double ratio = (F - Fn) / pred;
                if (ratio > 1e-4)
                {
                    (x, r, F) = (xn, rn, Fn);
                    accepted = true;
                    if (ratio > 0.75 && dInf >= 0.9 * delta) delta = Math.Min(2 * delta, 1);
                    else if (ratio < 0.25) delta = 0.5 * dInf;
                }
                else delta = 0.5 * dInf;
            }
            CompleteIteration();
        }
    }

    private static bool Column(double[,] J, int i, Evaluation e, double[] r, double step)
    {
        if (e.Failed || e.Residuals is not { } ri || ri.Length != r.Length) return false;
        for (int k = 0; k < r.Length; k++) J[k, i] = (ri[k] - r[k]) / step;
        return true;
    }
}

/// <summary>
/// The linear program of one minimax step, solved by a dense simplex method with Bland's rule (so
/// it cannot cycle). With yⱼ = dⱼ − loⱼ ∈ [0, uⱼ] and t = U − σ, U an upper bound on t over the box,
/// it becomes: maximize σ subject to Jᵢ·y + σ ≤ U − rᵢ − Jᵢ·lo and yⱼ ≤ uⱼ — every right-hand side
/// non-negative by the choice of U, so the slack basis is feasible and no first phase is needed.
/// </summary>
internal static class MinimaxLp
{
    public static (double[]? Step, double T) Solve(double[] r, double[,] J, double[] lo, double[] hi)
    {
        int m = r.Length, n = lo.Length;
        var u = new double[n];
        for (int j = 0; j < n; j++) u[j] = Math.Max(0, hi[j] - lo[j]);

        double U = double.NegativeInfinity;
        var jlo = new double[m];
        for (int i = 0; i < m; i++)
        {
            double top = r[i];
            for (int j = 0; j < n; j++)
            {
                jlo[i] += J[i, j] * lo[j];
                top    += Math.Max(J[i, j] * lo[j], J[i, j] * hi[j]);
            }
            U = Math.Max(U, top);
        }

        // Columns: y (n), σ (1), slacks (m + n). Rows: m residual rows, then n bound rows.
        int rows = m + n, cols = n + 1 + rows;
        var T = new double[rows + 1, cols + 1];
        var basis = new int[rows];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++) T[i, j] = J[i, j];
            T[i, n] = 1;
            T[i, n + 1 + i] = 1;
            T[i, cols] = Math.Max(0, U - r[i] - jlo[i]);
            basis[i] = n + 1 + i;
        }
        for (int j = 0; j < n; j++)
        {
            int i = m + j;
            T[i, j] = 1;
            T[i, n + 1 + i] = 1;
            T[i, cols] = u[j];
            basis[i] = n + 1 + i;
        }
        T[rows, n] = -1;                                   // objective row: maximize σ

        for (int iter = 0; iter < 50 * (rows + cols); iter++)
        {
            int enter = -1;
            for (int c = 0; c < cols; c++)
                if (T[rows, c] < -1e-12) { enter = c; break; }
            if (enter < 0) break;

            int leave = -1;
            double best = double.PositiveInfinity;
            for (int i = 0; i < rows; i++)
            {
                if (T[i, enter] <= 1e-12) continue;
                double ratio = T[i, cols] / T[i, enter];
                if (ratio < best - 1e-14 || (leave >= 0 && Math.Abs(ratio - best) <= 1e-14 && basis[i] < basis[leave]))
                    (best, leave) = (ratio, i);
            }
            if (leave < 0) return (null, double.NaN);      // unbounded: cannot happen with m ≥ 1

            double piv = T[leave, enter];
            for (int c = 0; c <= cols; c++) T[leave, c] /= piv;
            for (int i = 0; i <= rows; i++)
            {
                if (i == leave || T[i, enter] == 0) continue;
                double f = T[i, enter];
                for (int c = 0; c <= cols; c++) T[i, c] -= f * T[leave, c];
            }
            basis[leave] = enter;
        }

        var y = new double[n + 1];
        for (int i = 0; i < rows; i++)
            if (basis[i] <= n) y[basis[i]] = T[i, cols];
        var d = new double[n];
        for (int j = 0; j < n; j++) d[j] = lo[j] + Math.Clamp(y[j], 0, u[j]);

        double t = double.NegativeInfinity;
        for (int i = 0; i < m; i++)
        {
            double ri = r[i];
            for (int j = 0; j < n; j++) ri += J[i, j] * d[j];
            t = Math.Max(t, ri);
        }
        return (d, t);
    }
}
