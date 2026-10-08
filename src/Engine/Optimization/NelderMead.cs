namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Nelder–Mead simplex (brief-tuneopt-6 R-to6-11), with the dimension-adaptive coefficients of Gao and
/// Han (2012) — reflection 1, expansion 1 + 2/n, contraction 0.75 − 1/(2n), shrink 1 − 1/n — which keep
/// the method from stalling as n grows. Bounds are handled by projecting every trial point onto the
/// box. A simplex that collapses (diameter below <c>xtol</c>) is rebuilt around its best vertex up to
/// <c>restarts</c> times; a restart that finds nothing better ends the run.
///
/// <para>It only ranks costs, so a failed point (a penalty cost) is simply a bad vertex. One
/// iteration is one reflect / expand / contract / shrink step.</para>
///
/// <para>Options: <c>step</c> (initial simplex edge in the unit box, default 0.1), <c>xtol</c>
/// (collapse diameter, default 1e-6), <c>restarts</c> (default 1).</para>
/// </summary>
public sealed class NelderMead : AskTellAlgorithm
{
    public const string AlgorithmId = "simplex";

    private readonly double _step, _xtol;
    private readonly int    _restarts;

    public NelderMead(double[] start, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        _step     = Math.Clamp(Option(options, "step", 0.1), 1e-6, 0.5);
        _xtol     = Option(options, "xtol", 1e-6);
        _restarts = Math.Max(0, (int)Option(options, "restarts", 1));
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        double alpha = 1, beta = 1 + 2.0 / n, gamma = 0.75 - 1.0 / (2 * n), delta = 1 - 1.0 / n;

        var x = new double[n + 1][];
        var f = new double[n + 1];
        double[] origin = Start;
        int restartsLeft = _restarts;
        double bestBeforeRestart = double.PositiveInfinity;

        while (true)
        {
            // ── A fresh simplex around the origin ─────────────────────────────
            x[0] = (double[])origin.Clone();
            for (int i = 0; i < n; i++)
            {
                x[i + 1] = (double[])origin.Clone();
                x[i + 1][i] += origin[i] + _step <= 1 ? _step : -_step;
            }
            yield return [.. x];
            for (int i = 0; i <= n; i++) f[i] = Results[i].Cost;

            while (true)
            {
                Sort(x, f);

                double diameter = 0;
                for (int i = 1; i <= n; i++) diameter = Math.Max(diameter, InfNorm(x[i], x[0]));
                if (diameter < _xtol) break;

                var c = new double[n];
                for (int i = 0; i < n; i++)
                    for (int d = 0; d < n; d++) c[d] += x[i][d] / n;

                var xr = Project(Along(c, x[n], -alpha));
                yield return [xr];
                double fr = Results[0].Cost;

                if (fr < f[0])
                {
                    var xe = Project(Along(c, xr, beta));
                    yield return [xe];
                    double fe = Results[0].Cost;
                    if (fe < fr) (x[n], f[n]) = (xe, fe); else (x[n], f[n]) = (xr, fr);
                }
                else if (fr < f[n - 1])
                {
                    (x[n], f[n]) = (xr, fr);
                }
                else
                {
                    bool outside = fr < f[n];
                    var xc = Project(outside ? Along(c, xr, gamma) : Along(c, x[n], gamma));
                    yield return [xc];
                    double fc = Results[0].Cost;
                    if (outside ? fc <= fr : fc < f[n])
                    {
                        (x[n], f[n]) = (xc, fc);
                    }
                    else
                    {
                        var shrunk = new double[n][];
                        for (int i = 1; i <= n; i++)
                        {
                            shrunk[i - 1] = new double[n];
                            for (int d = 0; d < n; d++) shrunk[i - 1][d] = x[0][d] + delta * (x[i][d] - x[0][d]);
                        }
                        yield return shrunk;
                        for (int i = 1; i <= n; i++) (x[i], f[i]) = (shrunk[i - 1], Results[i - 1].Cost);
                    }
                }
                CompleteIteration();
            }

            // ── Collapsed ─────────────────────────────────────────────────────
            bool improved = f[0] < bestBeforeRestart - 1e-12 * Math.Abs(bestBeforeRestart);
            if (restartsLeft == 0 || !improved)
            {
                Finish(restartsLeft == 0 || double.IsPositiveInfinity(bestBeforeRestart)
                    ? "the simplex collapsed onto its best point"
                    : "a restarted simplex found nothing better");
                yield break;
            }
            restartsLeft--;
            bestBeforeRestart = f[0];
            origin = x[0];
        }
    }

    /// <summary>c + t·(p − c).</summary>
    private static double[] Along(double[] c, double[] p, double t)
    {
        var r = new double[c.Length];
        for (int d = 0; d < c.Length; d++) r[d] = c[d] + t * (p[d] - c[d]);
        return r;
    }

    /// <summary>Best first. Stable, so a tie keeps the older vertex ahead — the rule that makes the
    /// method deterministic.</summary>
    private static void Sort(double[][] x, double[] f)
    {
        var order = Enumerable.Range(0, f.Length).OrderBy(i => f[i]).ToArray();
        var xs = order.Select(i => x[i]).ToArray();
        var fs = order.Select(i => f[i]).ToArray();
        Array.Copy(xs, x, x.Length);
        Array.Copy(fs, f, f.Length);
    }
}
