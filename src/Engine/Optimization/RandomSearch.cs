namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Random search (brief-tuneopt-6 R-to6-11): batches of points over the whole box, uniform or a Latin
/// hypercube per batch. It never finishes on its own — the run's evaluation, iteration, time and stall
/// limits end it. The first batch carries the start point, so the best is never worse than where the
/// run began. One batch is one iteration.
///
/// <para>Options and their defaults: <c>random</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class RandomSearch : AskTellAlgorithm
{
    public const string AlgorithmId = "random";

    private readonly int    _batch;
    private readonly bool   _lhs;
    private readonly ulong  _seed;

    public RandomSearch(double[] start, ulong seed, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        _batch = Math.Max(1, (int)Option(options, "batch", Math.Max(8, 2 * start.Length)));
        _lhs   = Option(options, "lhs") != 0;
        _seed  = seed;
    }

    protected override IEnumerable<double[][]> Run()
    {
        var rng   = new SplitMix64(_seed);
        bool first = true;
        while (true)
        {
            var batch = _lhs ? LatinHypercube(rng, _batch, Dimension) : Uniform(rng, _batch, Dimension);
            if (first) { batch[0] = (double[])Start.Clone(); first = false; }
            yield return batch;
            CompleteIteration();
        }
    }

    private static double[][] Uniform(SplitMix64 rng, int count, int n)
    {
        var pts = new double[count][];
        for (int k = 0; k < count; k++)
        {
            pts[k] = new double[n];
            for (int i = 0; i < n; i++) pts[k][i] = rng.NextDouble();
        }
        return pts;
    }

    /// <summary>One point in each of <paramref name="count"/> equal slices of every coordinate, the
    /// slices paired at random.</summary>
    internal static double[][] LatinHypercube(SplitMix64 rng, int count, int n)
    {
        var pts = new double[count][];
        for (int k = 0; k < count; k++) pts[k] = new double[n];
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
