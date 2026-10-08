namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Differential evolution with success-history parameter adaptation and linear population-size
/// reduction (brief-tuneopt-7 R-to7-1; the menu's genetic-family entry), written from Tanabe and
/// Fukunaga's published description of the SHADE family (2013, 2014).
///
/// <para><b>One generation is one batch and one iteration.</b> Each member i draws a crossover rate
/// CRᵢ ~ N(M_CR[r], 0.1) clipped to [0, 1] and a scale factor Fᵢ ~ Cauchy(M_F[r], 0.1) (redrawn while
/// ≤ 0, cut to 1) from a random memory slot r; mutates by current-to-pbest/1,
/// vᵢ = xᵢ + Fᵢ(x_pbest − xᵢ) + Fᵢ(x_r1 − x_r2) with x_pbest one of the best ⌈p·N⌉ and x_r2 drawn
/// from the population and the archive of replaced parents; crosses binomially with one coordinate
/// always taken from vᵢ; and repairs a coordinate outside the box to the midpoint between the bound
/// and the parent's own coordinate. A trial no worse than its parent replaces it; a strictly better
/// one records its CR and F, weighted by the improvement, and the memory slot in turn takes their
/// weighted Lehmer means.</para>
///
/// <para><b>Population.</b> It starts at <c>population</c> (the start point is its first member) and
/// shrinks linearly with evaluations spent to <c>minpopulation</c> at <c>budget</c>, dropping the
/// worst. The run ends when the budget is spent or every member is within <c>xtol</c> of the best.</para>
///
/// <para><b>Failed and infeasible points</b> (R-to7-8) rank behind every evaluated point whatever
/// their penalty costs, both in selection and in the pbest ranking — a failed trial never replaces an
/// evaluated parent.</para>
///
/// <para>Options and their defaults: <c>de</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class DifferentialEvolution : AskTellAlgorithm
{
    public const string AlgorithmId = "de";

    private readonly ulong  _seed;
    private readonly int    _nInit, _nMin, _memory, _budget;
    private readonly double _p, _archiveRate, _xtol;

    public DifferentialEvolution(double[] start, ulong seed, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        int n = start.Length;
        _seed        = seed;
        _nInit       = Math.Max(4, (int)Option(options, "population", 18 * n));
        _nMin        = Math.Clamp((int)Option(options, "minpopulation"), 4, _nInit);
        _budget      = Math.Max(_nInit, (int)Option(options, "budget", 1000 * n));
        _memory      = Math.Max(1, (int)Option(options, "memory"));
        _p           = Math.Clamp(Option(options, "pbest"), 0.0, 1.0);
        _archiveRate = Math.Max(0, Option(options, "archive"));
        _xtol        = Option(options, "xtol");
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension;
        var rng = new SplitMix64(_seed);

        // ── The first generation: the start point and a Latin hypercube around the box ──
        var pop = new List<double[]>(_nInit) { (double[])Start.Clone() };
        var lhs = LatinHypercube(rng, _nInit - 1, n);
        pop.AddRange(lhs);
        yield return [.. pop];
        var fit = Results.ToList();
        long spent = pop.Count;
        CompleteIteration();

        var mCr = Enumerable.Repeat(0.5, _memory).ToArray();
        var mF  = Enumerable.Repeat(0.5, _memory).ToArray();
        bool[] crTerminal = new bool[_memory];
        int slot = 0;
        var archive = new List<double[]>();

        while (true)
        {
            int np = pop.Count;
            if (spent >= _budget) { Finish("the evaluation budget was spent"); yield break; }
            var order = Ranking(fit);
            var best = pop[order[0]];
            double spread = pop.Max(x => InfNorm(x, best));
            if (spread < _xtol) { Finish("the population converged onto its best point"); yield break; }

            // ── Trial vectors ───────────────────────────────────────────────
            int pCount = Math.Max(2, (int)Math.Ceiling(_p * np));
            var trials = new double[np][];
            var cr = new double[np];
            var f  = new double[np];
            for (int i = 0; i < np; i++)
            {
                int r = rng.Next(_memory);
                cr[i] = crTerminal[r] ? 0 : Math.Clamp(mCr[r] + 0.1 * rng.NextGaussian(), 0, 1);
                double fi;
                do fi = rng.NextCauchy(mF[r], 0.1); while (fi <= 0);
                f[i] = Math.Min(fi, 1);

                var xp = pop[order[rng.Next(Math.Min(pCount, np))]];
                int r1; do r1 = rng.Next(np); while (r1 == i);
                double[] x2;
                while (true)
                {
                    int r2 = rng.Next(np + archive.Count);
                    if (r2 == i || r2 == r1) continue;
                    x2 = r2 < np ? pop[r2] : archive[r2 - np];
                    break;
                }
                var xi = pop[i];
                var v = new double[n];
                for (int d = 0; d < n; d++)
                {
                    v[d] = xi[d] + f[i] * (xp[d] - xi[d]) + f[i] * (pop[r1][d] - x2[d]);
                    if (v[d] < 0) v[d] = xi[d] / 2;
                    else if (v[d] > 1) v[d] = (1 + xi[d]) / 2;
                }
                int jRand = rng.Next(n);
                var u = new double[n];
                for (int d = 0; d < n; d++) u[d] = d == jRand || rng.NextDouble() < cr[i] ? v[d] : xi[d];
                trials[i] = u;
            }

            yield return trials;
            spent += np;

            // ── Selection and the success history ───────────────────────────
            var sCr = new List<double>(); var sF = new List<double>(); var sW = new List<double>();
            for (int i = 0; i < np; i++)
            {
                var t = Results[i];
                if (Better(fit[i], t)) continue;               // the parent stays
                if (Better(t, fit[i]))
                {
                    sCr.Add(cr[i]); sF.Add(f[i]);
                    sW.Add(Math.Abs(fit[i].Cost - t.Cost));
                    archive.Add(pop[i]);
                }
                pop[i] = trials[i];
                fit[i] = t;
            }
            if (sF.Count > 0)
            {
                double wSum = sW.Sum();
                var w = wSum > 0 ? sW.Select(x => x / wSum).ToList() : sW.Select(_ => 1.0 / sW.Count).ToList();
                mF[slot] = Lehmer(sF, w);
                if (crTerminal[slot] || sCr.Max() == 0) crTerminal[slot] = true;
                else mCr[slot] = Lehmer(sCr, w);
                slot = (slot + 1) % _memory;
            }

            // ── Linear population-size reduction ─────────────────────────────
            int target = (int)Math.Round(_nInit + (_nMin - _nInit) * Math.Min(1.0, (double)spent / _budget));
            target = Math.Max(_nMin, target);
            if (target < pop.Count)
            {
                var keep = Ranking(fit).Take(target).OrderBy(i => i).ToArray();
                pop = [.. keep.Select(i => pop[i])];
                fit = [.. keep.Select(i => fit[i])];
            }
            int archiveMax = (int)Math.Round(_archiveRate * pop.Count);
            while (archive.Count > archiveMax) archive.RemoveAt(rng.Next(archive.Count));

            CompleteIteration();
        }
    }

    private static double Lehmer(List<double> s, List<double> w)
    {
        double num = 0, den = 0;
        for (int k = 0; k < s.Count; k++) { num += w[k] * s[k] * s[k]; den += w[k] * s[k]; }
        return den > 0 ? num / den : 0;
    }

    /// <summary>One point in each of <paramref name="count"/> equal slices of every coordinate.</summary>
    internal static List<double[]> LatinHypercube(SplitMix64 rng, int count, int n)
    {
        var pts = new List<double[]>(count);
        for (int k = 0; k < count; k++) pts.Add(new double[n]);
        var perm = new int[count];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < count; k++) perm[k] = k;
            for (int k = count - 1; k > 0; k--)
            {
                int j = rng.Next(k + 1);
                (perm[k], perm[j]) = (perm[j], perm[k]);
            }
            for (int k = 0; k < count; k++) pts[k][i] = (perm[k] + rng.NextDouble()) / count;
        }
        return pts;
    }
}
