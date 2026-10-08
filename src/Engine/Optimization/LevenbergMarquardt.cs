namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Levenberg–Marquardt on the goal-residual vector (brief-tuneopt-6 R-to6-11, the menu's "Gradient"):
/// the cost is Σ r², the Jacobian is a forward difference evaluated as ONE batch of n points, and each
/// step solves (JᵀJ + λ·s·I)·δ = −Jᵀr, s the largest diagonal entry of JᵀJ (so λ has no units).
///
/// <para><b>Levenberg's damping, not Marquardt's diag(JᵀJ).</b> The unit box already puts every
/// coordinate on one scale. Marquardt's scaling divides each coordinate's step by how strongly the
/// residuals feel it, so with fewer residuals than coordinates — one matching goal over two values — it
/// lengthens the step along the coordinate the residual barely feels, exactly where the linear model is
/// worst, and the run stalled short of a limit it could reach (an L-section load, 7 % above
/// |S11| = 1e-4). Identity damping tends to steepest descent as λ grows, which always makes progress.</para>
///
/// <para><b>Bounds.</b> A trial point is projected onto the box. A coordinate sitting on a bound whose
/// gradient pushes it outward is ACTIVE: it is held there and left out of the solve, so the step the
/// other coordinates take is the one they would take with it fixed — rather than a step computed as if
/// it could move and then clipped.</para>
///
/// <para><b>Failures.</b> A failed trial point (non-converged, infeasible) is a rejected step: λ grows
/// and the step shrinks, which is how the method steps around a region where the design cannot be
/// evaluated. A failed difference point is retried on the other side; a coordinate whose both sides
/// fail is held for that iteration.</para>
///
/// <para>One iteration is one Jacobian and the trial steps it takes to accept one, or to give up.</para>
///
/// <para>Options and their defaults: <c>lm</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class LevenbergMarquardt : AskTellAlgorithm
{
    public const string AlgorithmId = "lm";

    private readonly double _h, _lambda0;

    public LevenbergMarquardt(double[] start, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        _h       = Math.Clamp(Option(options, "fdstep"), 1e-9, 0.1);
        _lambda0 = Math.Max(1e-12, Option(options, "lambda"));
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        var x = (double[])Start.Clone();
        yield return [x];
        if (Results[0] is not { Failed: false, Residuals: { } r } first)
        {
            Finish("the start point could not be evaluated");
            yield break;
        }
        double cost = first.Cost;
        int m = r.Length;
        double lambda = _lambda0;

        while (true)
        {
            if (cost == 0 || m == 0) { Finish("every residual is zero"); yield break; }

            // ── Jacobian: one batch forward, the failures retried backward ─────
            var all = Enumerable.Range(0, n).ToList();
            var pts = DifferencePoints(x, _h, all, out var sign);
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

            // ── Gradient, and which coordinates are free ──────────────────────
            var g = new double[n];
            var diag = new double[n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < m; k++) { g[i] += J[k, i] * r[k]; diag[i] += J[k, i] * J[k, i]; }
            var free = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (held[i] || diag[i] == 0) continue;
                if (x[i] <= 0 && g[i] > 0) continue;
                if (x[i] >= 1 && g[i] < 0) continue;
                free.Add(i);
            }
            double pg = free.Count == 0 ? 0 : free.Max(i => Math.Abs(g[i]));
            if (pg <= 1e-14 * Math.Max(1, cost))
            {
                Finish(free.Count == 0 ? "every coordinate is held at a bound" : "the gradient vanished");
                yield break;
            }

            // ── Trial steps until one lowers the cost ─────────────────────────
            double dmax = free.Max(i => diag[i]);
            bool accepted = false;
            while (!accepted)
            {
                int nf = free.Count;
                var A = new double[nf, nf];
                var b = new double[nf];
                for (int a = 0; a < nf; a++)
                {
                    b[a] = -g[free[a]];
                    for (int c = 0; c < nf; c++)
                    {
                        double s = 0;
                        for (int k = 0; k < m; k++) s += J[k, free[a]] * J[k, free[c]];
                        A[a, c] = s;
                    }
                    A[a, a] += lambda * dmax;
                }
                var delta = DenseLinear.Solve(A, b);
                if (delta is null) { lambda *= 10; if (lambda > 1e16) break; continue; }

                var xn = (double[])x.Clone();
                for (int a = 0; a < nf; a++) xn[free[a]] += delta[a];
                xn = Project(xn);
                if (InfNorm(xn, x) < 1e-12)
                {
                    Finish("the step fell below tolerance");
                    yield break;
                }

                yield return [xn];
                var e = Results[0];
                if (!e.Failed && e.Residuals is { } rn && rn.Length == m && e.Cost < cost)
                {
                    (x, r, cost) = (xn, rn, e.Cost);
                    lambda = Math.Max(lambda / 3, 1e-12);
                    accepted = true;
                }
                else
                {
                    lambda *= e.Failed ? 10 : 4;
                    if (lambda > 1e16) break;
                }
            }
            CompleteIteration();
            if (!accepted)
            {
                Finish("no step lowers the cost");
                yield break;
            }
        }
    }

    /// <summary>Column <paramref name="i"/> of J from one difference point; false when it failed.</summary>
    private static bool Column(double[,] J, int i, Evaluation e, double[] r, double step)
    {
        if (e.Failed || e.Residuals is not { } ri || ri.Length != r.Length) return false;
        for (int k = 0; k < r.Length; k++) J[k, i] = (ri[k] - r[k]) / step;
        return true;
    }
}
