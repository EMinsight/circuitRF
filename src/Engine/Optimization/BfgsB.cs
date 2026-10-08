namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Bound-constrained limited-memory quasi-Newton on the scalar cost (brief-tuneopt-6 R-to6-11, the
/// menu's "Quasi-Newton (BFGS-B)"). The gradient is a forward difference evaluated as ONE batch of n
/// points; the direction is the L-BFGS two-loop product over the last <c>memory</c> steps, restricted
/// to the FREE coordinates — a coordinate on a bound whose gradient pushes it outward is held there —
/// and the line search is a projected backtracking (Armijo) search along P(x + α·d).
///
/// <para>A failed trial point is a step too long: α shrinks, faster than for a merely worse point —
/// how the method steps around a region where the design cannot be evaluated. A failed difference
/// point is retried on the other side; a coordinate whose both sides fail is held for that iteration.
/// A direction that is not downhill (curvature lost to noise) resets the memory to steepest descent.</para>
///
/// <para>One iteration is one gradient and its line search.</para>
///
/// <para>Options: <c>fdstep</c> (difference step in the unit box, default 1e-6), <c>memory</c>
/// (correction pairs kept, default 5).</para>
/// </summary>
public sealed class BfgsB : AskTellAlgorithm
{
    public const string AlgorithmId = "bfgsb";

    private readonly double _h;
    private readonly int    _memory;

    public BfgsB(double[] start, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        _h      = Math.Clamp(Option(options, "fdstep", LevenbergMarquardt.DefaultStep), 1e-9, 0.1);
        _memory = Math.Max(1, (int)Option(options, "memory", 5));
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        var x = (double[])Start.Clone();
        yield return [x];
        if (Results[0].Failed)
        {
            Finish("the start point could not be evaluated");
            yield break;
        }
        double f = Results[0].Cost;

        var S = new List<double[]>();
        var Y = new List<double[]>();
        double[]? xPrev = null, gPrev = null;

        while (true)
        {
            if (f == 0) { Finish("the cost is zero"); yield break; }

            // ── Gradient: one batch forward, the failures retried backward ─────
            var pts = DifferencePoints(x, _h, [.. Enumerable.Range(0, n)], out var sign);
            yield return pts;
            var g = new double[n];
            var held = new bool[n];
            var retry = new List<int>();
            for (int i = 0; i < n; i++)
                if (Results[i].Failed) retry.Add(i);
                else g[i] = (Results[i].Cost - f) / (sign[i] * _h);
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
                    if (step == 0 || Results[k].Failed) held[i] = true;
                    else g[i] = (Results[k].Cost - f) / step;
                }
            }

            // ── Curvature pair from the last accepted step ─────────────────────
            if (xPrev is not null && gPrev is not null)
            {
                var s = new double[n]; var y = new double[n];
                for (int i = 0; i < n; i++) { s[i] = x[i] - xPrev[i]; y[i] = g[i] - gPrev[i]; }
                double sy = DenseLinear.Dot(s, y);
                if (sy > 1e-10 * Math.Sqrt(DenseLinear.Dot(s, s) * DenseLinear.Dot(y, y)))
                {
                    S.Add(s); Y.Add(y);
                    if (S.Count > _memory) { S.RemoveAt(0); Y.RemoveAt(0); }
                }
            }

            // ── Free coordinates, and the stopping test on the projected gradient ──
            var free = new bool[n];
            double pg = 0;
            for (int i = 0; i < n; i++)
            {
                free[i] = !held[i] && !(x[i] <= 0 && g[i] > 0) && !(x[i] >= 1 && g[i] < 0);
                if (free[i]) pg = Math.Max(pg, Math.Abs(g[i]));
            }
            if (pg <= 1e-12 * Math.Max(1, f))
            {
                Finish(pg == 0 && free.All(v => !v) ? "every coordinate is held at a bound" : "the gradient vanished");
                yield break;
            }

            var d = Direction(g, free, S, Y);
            if (DenseLinear.Dot(d, g) >= 0)
            {
                S.Clear(); Y.Clear();
                for (int i = 0; i < n; i++) d[i] = free[i] ? -g[i] : 0;
            }

            // ── Projected backtracking line search ─────────────────────────────
            double dInf = d.Max(Math.Abs);
            double alpha = S.Count == 0 ? Math.Min(1, 0.1 / dInf) : 1;
            bool accepted = false;
            for (int tries = 0; tries < 40 && !accepted; tries++)
            {
                var xn = new double[n];
                for (int i = 0; i < n; i++) xn[i] = x[i] + alpha * d[i];
                xn = Project(xn);
                if (InfNorm(xn, x) < 1e-12) break;

                double descent = 0;
                for (int i = 0; i < n; i++) descent += g[i] * (xn[i] - x[i]);
                yield return [xn];
                var e = Results[0];
                if (!e.Failed && e.Cost <= f + 1e-4 * descent && e.Cost < f)
                {
                    (xPrev, gPrev) = (x, g);
                    (x, f) = (xn, e.Cost);
                    accepted = true;
                }
                else alpha *= e.Failed ? 0.25 : 0.5;
            }
            CompleteIteration();
            if (!accepted)
            {
                Finish("the line search found no lower cost");
                yield break;
            }
        }
    }

    /// <summary>−H·g by the two-loop recursion, over the free coordinates only.</summary>
    private static double[] Direction(double[] g, bool[] free, List<double[]> S, List<double[]> Y)
    {
        int n = g.Length, k = S.Count;
        var q = new double[n];
        for (int i = 0; i < n; i++) q[i] = free[i] ? g[i] : 0;
        var a = new double[k];
        for (int j = k - 1; j >= 0; j--)
        {
            double rho = 1 / DenseLinear.Dot(Y[j], S[j]);
            a[j] = rho * Masked(S[j], q, free);
            for (int i = 0; i < n; i++) if (free[i]) q[i] -= a[j] * Y[j][i];
        }
        double gamma = k == 0 ? 1 : DenseLinear.Dot(S[k - 1], Y[k - 1]) / DenseLinear.Dot(Y[k - 1], Y[k - 1]);
        for (int i = 0; i < n; i++) q[i] *= gamma;
        for (int j = 0; j < k; j++)
        {
            double rho = 1 / DenseLinear.Dot(Y[j], S[j]);
            double b = rho * Masked(Y[j], q, free);
            for (int i = 0; i < n; i++) if (free[i]) q[i] += S[j][i] * (a[j] - b);
        }
        for (int i = 0; i < n; i++) q[i] = free[i] ? -q[i] : 0;
        return q;
    }

    private static double Masked(double[] a, double[] b, bool[] free)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) if (free[i]) s += a[i] * b[i];
        return s;
    }
}
