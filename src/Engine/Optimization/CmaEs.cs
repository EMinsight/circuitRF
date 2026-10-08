namespace CircuitRF.Engine.Optimization;

/// <summary>
/// CMA-ES (brief-tuneopt-7 R-to7-3): the standard (μ/μ_w, λ) evolution strategy with cumulative
/// step-size adaptation and rank-one plus rank-μ covariance updates, written from Hansen's published
/// tutorial (2016) with its default constants, and <b>IPOP restarts</b> (Auger and Hansen, 2005):
/// when a run stalls it starts again from a random point in the box with twice the population.
///
/// <para><b>One generation is one batch of λ points and one iteration.</b> The first run's mean is
/// the start point; a restart's is uniform in the box. σ starts at <c>sigma</c>.</para>
///
/// <para><b>Bounds — re-sampling.</b> A sample outside the box is drawn again, up to 100 times;
/// one still outside after that (a mean pressed into a corner) is projected onto the box, and the
/// update uses the projected point, so the distribution stays consistent with what was evaluated.
/// Chosen over a boundary penalty because re-sampling needs no penalty weight to tune, and every
/// point the run evaluates is one the design can take.</para>
///
/// <para><b>Stalled</b> means any of: the best costs of the last 10 + ⌈30n/λ⌉ generations and this
/// generation's costs all lie within <c>tolfun</c>; σ times the largest standard deviation or
/// evolution-path entry is below <c>tolx</c>; the covariance's condition number exceeds 1e14; or the
/// run has used 100 + 50(n+3)²/√λ generations. A stalled run restarts while <c>ipop=1</c> and
/// restarts remain, and otherwise ends the algorithm.</para>
///
/// <para><b>Failed and infeasible points</b> (R-to7-8) rank behind every evaluated point in the
/// selection, whatever their penalty costs.</para>
///
/// <para>Options and their defaults: <c>cmaes</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class CmaEs : AskTellAlgorithm
{
    public const string AlgorithmId = "cmaes";

    private readonly ulong  _seed;
    private readonly double _sigma0, _tolfun, _tolx;
    private readonly int    _lambda0, _restarts;
    private readonly bool   _ipop;

    public CmaEs(double[] start, ulong seed, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        int n = start.Length;
        _seed     = seed;
        _sigma0   = Math.Clamp(Option(options, "sigma"), 1e-6, 1.0);
        _lambda0  = Math.Max(2, (int)Option(options, "popsize", 4 + (int)Math.Floor(3 * Math.Log(n))));
        _ipop     = Option(options, "ipop") != 0;
        _restarts = Math.Max(0, (int)Option(options, "restarts"));
        _tolfun   = Option(options, "tolfun");
        _tolx     = Option(options, "tolx");
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        var rng = new SplitMix64(_seed);
        int lambda = _lambda0;
        double[] mean = (double[])Start.Clone();

        for (int restart = 0; ; restart++)
        {
            // ── Strategy constants for this population ──────────────────────
            int mu = lambda / 2;
            var w = new double[mu];
            for (int i = 0; i < mu; i++) w[i] = Math.Log(mu + 0.5) - Math.Log(i + 1);
            double wSum = w.Sum();
            for (int i = 0; i < mu; i++) w[i] /= wSum;
            double muEff = 1 / w.Sum(x => x * x);
            double cc = (4 + muEff / n) / (n + 4 + 2 * muEff / n);
            double cs = (muEff + 2) / (n + muEff + 5);
            double c1 = 2 / ((n + 1.3) * (n + 1.3) + muEff);
            double cmu = Math.Min(1 - c1, 2 * (muEff - 2 + 1 / muEff) / ((n + 2) * (n + 2) + muEff));
            double damps = 1 + 2 * Math.Max(0, Math.Sqrt((muEff - 1) / (n + 1)) - 1) + cs;
            double chiN = Math.Sqrt(n) * (1 - 1.0 / (4 * n) + 1.0 / (21 * n * n));
            int histLen = 10 + (int)Math.Ceiling(30.0 * n / lambda);
            int maxGen = 100 + (int)(50 * (n + 3) * (n + 3) / Math.Sqrt(lambda));

            // ── State ───────────────────────────────────────────────────────
            double sigma = _sigma0;
            var pc = new double[n]; var ps = new double[n];
            var C = new double[n, n]; var B = new double[n, n]; var D = new double[n];
            for (int i = 0; i < n; i++) { C[i, i] = 1; B[i, i] = 1; D[i] = 1; }
            var history = new List<double>();
            string? stalled = null;

            for (int gen = 0; stalled is null; gen++)
            {
                // ── Sample λ points inside the box ────────────────────────────
                var xs = new double[lambda][];
                for (int k = 0; k < lambda; k++)
                {
                    double[] x = new double[n];
                    for (int tries = 0; ; tries++)
                    {
                        var z = new double[n];
                        for (int i = 0; i < n; i++) z[i] = D[i] * rng.NextGaussian();
                        bool inside = true;
                        for (int i = 0; i < n; i++)
                        {
                            double y = 0;
                            for (int j = 0; j < n; j++) y += B[i, j] * z[j];
                            x[i] = mean[i] + sigma * y;
                            if (x[i] < 0 || x[i] > 1) inside = false;
                        }
                        if (inside) break;
                        if (tries == 99) { x = Project(x); break; }
                    }
                    xs[k] = x;
                }

                yield return xs;
                var order = Ranking(Results);
                CompleteIteration();

                // ── Recombination and the evolution paths ─────────────────────
                var old = mean;
                mean = new double[n];
                for (int k = 0; k < mu; k++)
                    for (int i = 0; i < n; i++) mean[i] += w[k] * xs[order[k]][i];
                var yw = new double[n];
                for (int i = 0; i < n; i++) yw[i] = (mean[i] - old[i]) / sigma;

                // C^(-1/2)·yw = B·D⁻¹·Bᵀ·yw
                var t = new double[n];
                for (int j = 0; j < n; j++)
                {
                    double s = 0;
                    for (int i = 0; i < n; i++) s += B[i, j] * yw[i];
                    t[j] = s / D[j];
                }
                double csf = Math.Sqrt(cs * (2 - cs) * muEff);
                for (int i = 0; i < n; i++)
                {
                    double s = 0;
                    for (int j = 0; j < n; j++) s += B[i, j] * t[j];
                    ps[i] = (1 - cs) * ps[i] + csf * s;
                }
                double psNorm = Math.Sqrt(DenseLinear.Dot(ps, ps));
                bool hsig = psNorm / Math.Sqrt(1 - Math.Pow(1 - cs, 2 * (gen + 1))) / chiN < 1.4 + 2.0 / (n + 1);
                double ccf = Math.Sqrt(cc * (2 - cc) * muEff);
                for (int i = 0; i < n; i++) pc[i] = (1 - cc) * pc[i] + (hsig ? ccf * yw[i] : 0);

                // ── Covariance: rank one plus rank μ ──────────────────────────
                double delta = hsig ? 0 : cc * (2 - cc);
                for (int i = 0; i < n; i++)
                    for (int j = 0; j <= i; j++)
                    {
                        double rankMu = 0;
                        for (int k = 0; k < mu; k++)
                        {
                            var xk = xs[order[k]];
                            rankMu += w[k] * (xk[i] - old[i]) * (xk[j] - old[j]);
                        }
                        rankMu /= sigma * sigma;
                        double c = (1 - c1 - cmu) * C[i, j] + c1 * (pc[i] * pc[j] + delta * C[i, j]) + cmu * rankMu;
                        C[i, j] = c; C[j, i] = c;
                    }
                sigma *= Math.Exp(cs / damps * (psNorm / chiN - 1));
                sigma = Math.Min(sigma, 1.0);

                var (values, vectors) = DenseLinear.SymmetricEigen(C);
                double dMax = 0, dMin = double.PositiveInfinity;
                for (int i = 0; i < n; i++)
                {
                    D[i] = Math.Sqrt(Math.Max(values[i], 1e-300));
                    dMax = Math.Max(dMax, D[i]); dMin = Math.Min(dMin, D[i]);
                }
                B = vectors;

                // ── Stalled? ───────────────────────────────────────────────────
                history.Add(Results[order[0]].Cost);
                if (history.Count > histLen) history.RemoveAt(0);
                double lo = Math.Min(history.Min(), Results[order[0]].Cost);
                double hi = Math.Max(history.Max(), order.Max(k => Results[k].Cost));
                double sdMax = 0;
                for (int i = 0; i < n; i++) sdMax = Math.Max(sdMax, Math.Max(Math.Abs(pc[i]), Math.Sqrt(C[i, i])));

                if (history.Count >= histLen && hi - lo < _tolfun && order.All(k => !Results[k].Failed))
                    stalled = "its costs stopped changing";
                else if (sigma * sdMax < _tolx)
                    stalled = "its step size collapsed";
                else if (dMax * dMax > 1e14 * dMin * dMin)
                    stalled = "its covariance became ill-conditioned";
                else if (gen + 1 >= maxGen)
                    stalled = "it used its generation limit";
            }

            if (!_ipop || restart >= _restarts)
            {
                Finish(_ipop ? $"the last of {_restarts} restarts stalled ({stalled})" : $"the run stalled ({stalled})");
                yield break;
            }
            lambda *= 2;
            mean = new double[n];
            for (int i = 0; i < n; i++) mean[i] = rng.NextDouble();
        }
    }
}
