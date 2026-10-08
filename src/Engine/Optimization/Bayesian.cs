namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Bayesian optimization (brief-tuneopt-8 R-to8-1, the menu's "Bayesian (slow simulations)"): a
/// Gaussian-process surrogate of the cost (<see cref="GaussianProcess"/> — Matérn 5/2, one length
/// scale per variable, fitted by maximum likelihood) and the point of greatest <b>expected
/// improvement</b> over it as the next evaluation. It spends computation choosing each point, which
/// is the right trade only when a simulation costs seconds or more.
///
/// <para><b>Initial design.</b> A Latin hypercube of <c>initial</c> points (default 2n+1), the first
/// replaced by the start point — one batch, one iteration. After it, one batch per iteration.</para>
///
/// <para><b>The target</b> is the cost itself, standardized. A log warp, ln(cost − lowest + δ), was
/// measured and not kept: at δ = 1e-3, 1e-2 and 1e-1 of the spread it was worse than the raw cost on
/// Branin, a 2-D Rosenbrock and an L-section match alike (40 evaluations, six seeds) — it turns the
/// best point into an outlier the surrogate then refuses to leave.</para>
///
/// <para><b>Acquisition.</b> Expected improvement on the standardized target, maximized over the box
/// by multi-start local search: 200n uniform candidates (at most 2,000) plus Gaussian perturbations
/// of the five best observations, the four best of them refined by a compass search. A candidate
/// within 1e-6 of a point already observed or already in the batch is not taken.</para>
///
/// <para><b>Batch.</b> <c>batch</c> points per iteration (default 1; the run sets it to its
/// parallelism when the setup states one) by the <b>constant liar</b>: each chosen point is added to
/// the surrogate at the lowest observed target before the next is chosen, so the batch spreads out.</para>
///
/// <para><b>Trust-region variant</b> above <c>tr_dims</c> variables (default 10), where one global
/// surrogate is a poor model of the box: a local surrogate on the points of the current region, a box
/// around the best point (side 0.8, scaled per coordinate by the fitted length scales) that doubles
/// after 3 consecutive improving iterations and halves after max(4, n) failing ones, and a restart from
/// a fresh Latin hypercube when the side falls below 2⁻⁷.</para>
///
/// <para><b>Failed and infeasible points</b> are not observations: the surrogate never sees their
/// penalty cost. In the global variant a candidate within 0.02 of one is not taken; in the
/// trust-region variant an iteration whose points all failed counts as a failure and shrinks the
/// region.</para>
///
/// <para><b>Cost.</b> Fitting is O(N³) in the observations N; only the <c>archive</c> most recent
/// (default 500, the best always among them) are fitted. It does not finish on its own unless no
/// candidate is left to take — the run's limits end it.</para>
///
/// <para>Options and their defaults: <c>bayes</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class Bayesian : AskTellAlgorithm
{
    public const string AlgorithmId = "bayes";

    private const double DuplicateTol = 1e-6;
    private const double FailedRadius = 0.02;

    private readonly ulong _seed;
    private readonly int _initial, _archive, _batch, _trDims;

    public Bayesian(double[] start, ulong seed, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        int n = start.Length;
        _seed    = seed;
        _initial = Math.Max(2, (int)Option(options, "initial", 2 * n + 1));
        _archive = Math.Max(_initial, (int)Option(options, "archive"));
        _batch   = Math.Max(1, (int)Option(options, "batch"));
        _trDims  = Math.Max(1, (int)Option(options, "tr_dims"));
    }

    protected override IEnumerable<double[][]> Run()
    {
        var rng = new SplitMix64(_seed);
        return Dimension > _trDims ? TrustRegion(rng) : Global(rng);
    }

    // ── The global variant ───────────────────────────────────────────────────

    private IEnumerable<double[][]> Global(SplitMix64 rng)
    {
        var xs = new List<double[]>();
        var ys = new List<double>();
        var failed = new List<double[]>();

        var init = RandomSearch.LatinHypercube(rng, _initial, Dimension);
        init[0] = (double[])Start.Clone();
        yield return init;
        Observe(init, xs, ys, failed);
        CompleteIteration();

        double[]? theta = null;
        while (true)
        {
            double[][] batch;
            if (xs.Count < 2)
            {
                // Nothing to model yet: every point so far failed. Survey again.
                batch = RandomSearch.LatinHypercube(rng, Math.Max(_batch, _initial), Dimension);
            }
            else
            {
                var (ax, ay) = Archive(xs, ys);
                var gp = GaussianProcess.Fit(ax, Target(ay), theta, rng);
                if (gp is null) { Finish("the surrogate could not be fitted to the points observed"); yield break; }
                theta = gp.Theta;
                batch = Choose(gp, rng, lo: null, hi: null, center: null, observed: xs, failed: failed);
                if (batch.Length == 0) { Finish("no point is left that the surrogate expects to improve on"); yield break; }
            }
            yield return batch;
            Observe(batch, xs, ys, failed);
            CompleteIteration();
        }
    }

    // ── The trust-region variant ─────────────────────────────────────────────

    private IEnumerable<double[][]> TrustRegion(SplitMix64 rng)
    {
        int n = Dimension;
        const double side0 = 0.8, sideMin = 1.0 / 128, sideMax = 1.6;
        const int succTol = 3;
        int failTol = Math.Max(4, (int)Math.Ceiling((double)n / _batch));
        var everything = new List<double[]>();
        bool first = true;

        while (true)
        {
            var xs = new List<double[]>();
            var ys = new List<double>();
            var failed = new List<double[]>();

            var init = RandomSearch.LatinHypercube(rng, _initial, n);
            if (first) { init[0] = (double[])Start.Clone(); first = false; }
            yield return init;
            Observe(init, xs, ys, failed);
            everything.AddRange(init);
            CompleteIteration();

            double side = side0;
            int succ = 0, fail = 0;
            double[]? theta = null;
            while (side >= sideMin)
            {
                double[][] batch;
                double bestBefore = ys.Count > 0 ? ys.Min() : double.PositiveInfinity;
                if (xs.Count < 2)
                    batch = RandomSearch.LatinHypercube(rng, Math.Max(_batch, _initial), n);
                else
                {
                    var center = xs[ys.IndexOf(ys.Min())];
                    var (ax, ay) = Archive(xs, ys, center);
                    var gp = GaussianProcess.Fit(ax, Target(ay), theta, rng);
                    if (gp is null) break;
                    theta = gp.Theta;

                    // The box: side scaled per coordinate by the length scales (geometric mean 1).
                    var ell = gp.LengthScales;
                    double gm = Math.Exp(ell.Average(Math.Log));
                    var lo = new double[n]; var hi = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        double half = side * ell[i] / gm / 2;
                        lo[i] = Math.Max(0, center[i] - half);
                        hi[i] = Math.Min(1, center[i] + half);
                    }
                    batch = Choose(gp, rng, lo, hi, center, everything, failed);
                    if (batch.Length == 0) break;
                }

                yield return batch;
                int okBefore = xs.Count;
                Observe(batch, xs, ys, failed);
                everything.AddRange(batch);
                CompleteIteration();

                bool improved = false;
                for (int k = okBefore; k < ys.Count; k++)
                    if (ys[k] < bestBefore - 1e-3 * Math.Abs(bestBefore)) improved = true;
                if (improved) { succ++; fail = 0; } else { fail++; succ = 0; }
                if (succ >= succTol) { side = Math.Min(2 * side, sideMax); succ = 0; }
                if (fail >= failTol) { side /= 2; fail = 0; }
            }
            // The region collapsed: start again elsewhere. The run's limits end the algorithm.
        }
    }

    // ── Shared steps ─────────────────────────────────────────────────────────

    /// <summary>Adds a told batch: a point that evaluated is an observation, one that failed is not.</summary>
    private void Observe(double[][] batch, List<double[]> xs, List<double> ys, List<double[]> failed)
    {
        for (int k = 0; k < batch.Length; k++)
        {
            var e = Results[k];
            if (e.Failed || !double.IsFinite(e.Cost)) failed.Add(batch[k]);
            else { xs.Add(batch[k]); ys.Add(e.Cost); }
        }
    }

    /// <summary>
    /// The observations the surrogate is fitted to: all of them up to <c>archive</c>, else the most
    /// recent (or, in a trust region, the nearest the centre) with the best always kept.
    /// </summary>
    private (double[][] X, double[] Y) Archive(List<double[]> xs, List<double> ys, double[]? center = null)
    {
        if (xs.Count <= _archive) return ([.. xs], [.. ys]);
        int best = ys.IndexOf(ys.Min());
        IEnumerable<int> order = center is null
            ? Enumerable.Range(0, xs.Count).Reverse()
            : Enumerable.Range(0, xs.Count).OrderBy(i => InfNorm(xs[i], center));
        var keep = new List<int> { best };
        foreach (int i in order)
        {
            if (keep.Count >= _archive) break;
            if (i != best) keep.Add(i);
        }
        return ([.. keep.Select(i => xs[i])], [.. keep.Select(i => ys[i])]);
    }

    /// <summary>The standardized target (see the class remark).</summary>
    private static double[] Target(double[] cost)
    {
        var y = (double[])cost.Clone();
        double mean = y.Average();
        double sd = Math.Sqrt(y.Sum(v => (v - mean) * (v - mean)) / y.Length);
        if (!(sd > 1e-12)) sd = 1;
        for (int i = 0; i < y.Length; i++) y[i] = (y[i] - mean) / sd;
        return y;
    }

    /// <summary>
    /// The batch: the expected-improvement maximizer, then — by the constant liar — the next, with the
    /// surrogate told each chosen point sits at the lowest target. Bounded by [lo, hi] (the whole box
    /// when null). Empty when nothing new can be chosen.
    /// </summary>
    private double[][] Choose(GaussianProcess gp, SplitMix64 rng, double[]? lo, double[]? hi, double[]? center,
                              List<double[]> observed, List<double[]> failed)
    {
        int n = Dimension;
        lo ??= new double[n];
        hi ??= Enumerable.Repeat(1.0, n).ToArray();
        double lie = gp.Y.Min();
        var chosen = new List<double[]>();
        var model = gp;

        for (int q = 0; q < _batch; q++)
        {
            var x = MaximizeEi(model, rng, lo, hi, center, observed, failed, chosen);
            if (x is null) break;
            chosen.Add(x);
            if (q + 1 < _batch && model.With(x, lie) is { } next) model = next;
        }
        return [.. chosen];
    }

    private double[]? MaximizeEi(GaussianProcess gp, SplitMix64 rng, double[] lo, double[] hi, double[]? center,
                                 List<double[]> observed, List<double[]> failed, List<double[]> chosen)
    {
        int n = Dimension;
        double best = gp.Y.Min();
        bool Allowed(double[] x)
        {
            foreach (var o in observed) if (InfNorm(o, x) < DuplicateTol) return false;
            foreach (var o in chosen)   if (InfNorm(o, x) < DuplicateTol) return false;
            if (center is null)
                foreach (var o in failed) if (InfNorm(o, x) < FailedRadius) return false;
            return true;
        }
        double Score(double[] x) => LogEi(gp, x, best);

        // Candidates: uniform in the box, and perturbations of the best observations inside it.
        var cands = new List<double[]>();
        int uniform = Math.Min(2000, 200 * n);
        for (int k = 0; k < uniform; k++)
        {
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = lo[i] + rng.NextDouble() * (hi[i] - lo[i]);
            cands.Add(x);
        }
        var seeds = observed.Count == 0 ? [] : TopObserved(gp, observed, 5);
        if (center is not null) seeds.Insert(0, center);
        foreach (var s in seeds)
            for (int k = 0; k < 20; k++)
            {
                var x = new double[n];
                for (int i = 0; i < n; i++)
                    x[i] = Math.Clamp(s[i] + 0.05 * (hi[i] - lo[i] + 1e-12) * rng.NextGaussian(), lo[i], hi[i]);
                cands.Add(x);
            }

        var scored = cands.Where(Allowed).Select(x => (X: x, S: Score(x))).Where(c => !double.IsNaN(c.S))
                          .OrderByDescending(c => c.S).Take(4).ToList();
        double[]? pick = null;
        double pickScore = double.NegativeInfinity;
        foreach (var (x0, s0) in scored)
        {
            var (x, s) = Compass(x0, s0, Score, lo, hi);
            if (!Allowed(x)) (x, s) = (x0, s0);
            if (s > pickScore) (pick, pickScore) = (x, s);
        }
        return pick;
    }

    /// <summary>The observed points with the lowest predicted mean (the surrogate's own view of best).</summary>
    private static List<double[]> TopObserved(GaussianProcess gp, List<double[]> observed, int count)
        => [.. observed.OrderBy(x => gp.Predict(x).Mean).Take(count)];

    /// <summary>Compass search on the acquisition, inside [lo, hi]: ±step on each coordinate,
    /// halving the step on a sweep with no gain, from a twentieth of the box to 1e-5.</summary>
    private static (double[] X, double Score) Compass(double[] x0, double s0, Func<double[], double> score,
                                                      double[] lo, double[] hi)
    {
        var x = (double[])x0.Clone();
        double s = s0;
        for (double step = 0.05; step >= 1e-5; )
        {
            bool gained = false;
            for (int i = 0; i < x.Length; i++)
                foreach (int sign in (int[])[1, -1])
                {
                    var t = (double[])x.Clone();
                    t[i] = Math.Clamp(t[i] + sign * step * (hi[i] - lo[i] + 1e-12), lo[i], hi[i]);
                    if (t[i] == x[i]) continue;
                    double st = score(t);
                    if (st > s) { (x, s, gained) = (t, st, true); break; }
                }
            if (!gained) step /= 2;
        }
        return (x, s);
    }

    /// <summary>
    /// ln EI for minimization: EI = σ·(z·Φ(z) + φ(z)), z = (best − μ)/σ — computed in logs, so a
    /// candidate far from any improvement still ranks by how far. Φ comes from an erfc accurate to
    /// rounding in RELATIVE terms, which is what keeps z·Φ + φ meaningful down to z ≈ −30; below
    /// that the Mills-ratio expansion φ(z)/z²·(1 − 3/z²) takes over.
    /// </summary>
    internal static double LogEi(GaussianProcess gp, double[] x, double best)
    {
        var (mu, sd) = gp.Predict(x);
        if (!(sd > 1e-12)) return mu < best ? Math.Log(best - mu) : -1e300;
        double z = (best - mu) / sd;
        double logPhi = -0.5 * z * z - 0.9189385332046727;
        if (z > -30)
        {
            double v = z * 0.5 * Erfc(-z / Math.Sqrt(2)) + Math.Exp(logPhi);
            if (v > 0) return Math.Log(sd) + Math.Log(v);
        }
        return Math.Log(sd) + logPhi - 2 * Math.Log(Math.Abs(z)) + Math.Log(1 - 3 / (z * z));
    }

    /// <summary>
    /// The complementary error function: the Maclaurin series of erf below 2, and above it the
    /// classical continued fraction erfc x = e^(−x²)/√π · 1/(x + ½/(x + 1/(x + 3⁄2/(x + …)))) by
    /// the modified Lentz method — relative accuracy near rounding in the tail, where it matters.
    /// </summary>
    internal static double Erfc(double x)
    {
        if (x < 0) return 2 - Erfc(-x);
        if (x < 2)
        {
            double sum = x, term = x, x2 = x * x;
            for (int k = 1; k < 200; k++)
            {
                term *= -x2 / k;
                double add = term / (2 * k + 1);
                sum += add;
                if (Math.Abs(add) < 1e-17 * Math.Abs(sum)) break;
            }
            return 1 - 2 / Math.Sqrt(Math.PI) * sum;
        }
        const double tiny = 1e-300;
        double f = x, c = x, d = 0;
        for (int k = 1; k < 1000; k++)
        {
            double a = k / 2.0;
            d = x + a * d; if (d == 0) d = tiny;
            c = x + a / c; if (c == 0) c = tiny;
            d = 1 / d;
            double delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1) < 1e-16) break;
        }
        return Math.Exp(-x * x) / (Math.Sqrt(Math.PI) * f);
    }
}
