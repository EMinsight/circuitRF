namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Mesh adaptive direct search (brief-tuneopt-7 R-to7-5), in the orthogonal form of Abramson, Audet,
/// Dennis and Le Digabel's published OrthoMADS: every poll is a positive spanning set of 2n
/// directions ±hⱼ, the columns of a Householder reflection H = I − 2uuᵀ of a fresh random unit vector
/// u, each scaled to the poll size and rounded onto the mesh.
///
/// <para><b>Mesh.</b> The mesh size is Δm = Δp², Δp the poll size, so the mesh refines faster than the
/// poll and the directions grow dense as both shrink. A poll that finds a lower cost <b>coarsens</b>
/// (Δp doubles, up to its initial value) and moves there; one that does not <b>refines</b> (Δp halves).
/// The run ends when Δp falls below <c>minpoll</c>. With <c>speculative=1</c> a success is followed
/// by one point twice as far along the same step, in the next batch beside the poll.</para>
///
/// <para><b>Model search</b> (<c>model=1</c>, the quadratic-model search step of published MADS
/// work). The next batch adds the minimum, within two poll lengths, of a quadratic through the
/// (n+1)(n+2)/2 evaluated points nearest the incumbent — the trust-region method's own least-change
/// model and step, so the two methods share one implementation — rounded onto the mesh. Failed points
/// never enter the fit. In a curved valley this is what makes progress along the valley floor; it
/// changes no mesh rule, since the point is one more mesh point to try.</para>
///
/// <para><b>Batch = the poll set</b> (plus that one speculative point); one poll is one iteration.
/// A poll point outside the box is not asked for — the extreme barrier — and counts as a failed
/// poll.</para>
///
/// <para><b>Failures</b> (R-to7-8). A failed, non-converged or infeasible point is simply a poll point
/// that did not improve. Nothing is modelled, so a region where the simulator fails, or a response
/// that jumps, costs this method nothing but the points it spent there — the reason to choose it for
/// an HB that does not converge everywhere.</para>
///
/// <para>Options and their defaults: <c>pattern</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class PatternSearch : AskTellAlgorithm
{
    public const string AlgorithmId = "pattern";

    private readonly ulong  _seed;
    private readonly double _poll0, _minPoll;
    private readonly bool   _speculative, _model;

    public PatternSearch(double[] start, ulong seed, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        _seed        = seed;
        _poll0       = Math.Clamp(Option(options, "poll"), 1e-9, 1.0);
        _minPoll     = Option(options, "minpoll");
        _speculative = Option(options, "speculative") != 0;
        _model       = Option(options, "model") != 0;
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        var rng = new SplitMix64(_seed);
        var x = (double[])Start.Clone();
        yield return [x];
        var fx = Results[0];
        double poll = _poll0;
        double[]? lastStep = null, modelPoint = null;
        var seen = new List<(double[] X, double F)>();
        if (!fx.Failed) seen.Add((x, fx.Cost));

        while (true)
        {
            if (poll < _minPoll) { Finish("the poll size fell below its minimum"); yield break; }
            double mesh = poll * poll;

            // ── Poll directions: ±columns of a random Householder reflection, on the mesh ──
            var u = new double[n];
            double un = 0;
            while (un < 1e-12)
            {
                for (int i = 0; i < n; i++) u[i] = rng.NextGaussian();
                un = Math.Sqrt(DenseLinear.Dot(u, u));
            }
            for (int i = 0; i < n; i++) u[i] /= un;

            var trials = new List<double[]>(2 * n + 2);
            for (int j = 0; j < n; j++)
            {
                var h = new double[n];
                double hInf = 0;
                for (int i = 0; i < n; i++)
                {
                    h[i] = (i == j ? 1 : 0) - 2 * u[i] * u[j];
                    hInf = Math.Max(hInf, Math.Abs(h[i]));
                }
                // Integer mesh steps whose longest is poll/mesh: the direction scaled to the poll size.
                var d = new double[n];
                for (int i = 0; i < n; i++) d[i] = Math.Round(h[i] / hInf * poll / mesh) * mesh;
                foreach (int sign in (int[])[1, -1])
                {
                    var t = new double[n];
                    bool inside = true;
                    for (int i = 0; i < n; i++)
                    {
                        t[i] = x[i] + sign * d[i];
                        if (t[i] < 0 || t[i] > 1) inside = false;
                    }
                    if (inside && InfNorm(t, x) > 0) trials.Add(t);
                }
            }
            if (modelPoint is not null && trials.All(t => InfNorm(t, modelPoint) > 0) && InfNorm(modelPoint, x) > 0)
                trials.Add(modelPoint);
            if (_speculative && lastStep is not null)
            {
                var t = new double[n];
                bool inside = true;
                for (int i = 0; i < n; i++)
                {
                    t[i] = x[i] + 2 * lastStep[i];
                    if (t[i] < 0 || t[i] > 1) inside = false;
                }
                if (inside) trials.Add(t);
            }

            int bestK = -1;
            if (trials.Count > 0)
            {
                yield return [.. trials];
                for (int k = 0; k < trials.Count; k++)
                {
                    if (Results[k].Failed) continue;
                    seen.Add((trials[k], Results[k].Cost));
                    if (Better(Results[k], bestK < 0 ? fx : Results[bestK])) bestK = k;
                }
                if (seen.Count > 40 * n) seen.RemoveRange(0, seen.Count - 40 * n);
            }
            CompleteIteration();

            if (bestK >= 0)
            {
                lastStep = new double[n];
                for (int i = 0; i < n; i++) lastStep[i] = trials[bestK][i] - x[i];
                (x, fx) = (trials[bestK], Results[bestK]);
                poll = Math.Min(2 * poll, _poll0);
            }
            else
            {
                lastStep = null;
                poll /= 2;
            }
            modelPoint = _model && !fx.Failed ? ModelPoint(x, seen, poll) : null;
        }
    }

    /// <summary>The model-search point (see the class remark), or null when the nearby points do not
    /// determine a model.</summary>
    private static double[]? ModelPoint(double[] x, List<(double[] X, double F)> seen, double poll)
    {
        int n = x.Length, q = (n + 1) * (n + 2) / 2;
        var Y = new List<double[]>(); var F = new List<double>();
        foreach (var (p, f) in seen.OrderBy(e => InfNorm(e.X, x)))
        {
            if (InfNorm(p, x) > 8 * poll || Y.Count == q) break;
            if (Y.Any(y => InfNorm(y, p) < 1e-3 * poll)) continue;
            Y.Add(p); F.Add(f);
        }
        if (Y.Count < n + 2 || InfNorm(Y[0], x) > 0) return null;

        var g = TrustRegionModel.Model(Y, F, 0, poll, new double[n, n], out var H);
        if (g is null) return null;
        var s = TrustRegionModel.Step(g, H!, x, 2 * poll);
        double mesh = poll * poll;
        var m = new double[n];
        for (int i = 0; i < n; i++) m[i] = Math.Clamp(x[i] + Math.Round(s[i] / mesh) * mesh, 0, 1);
        return InfNorm(m, x) > 0 ? m : null;
    }
}
