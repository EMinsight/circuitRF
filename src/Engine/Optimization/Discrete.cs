namespace CircuitRF.Engine.Optimization;

/// <summary>
/// The discrete search (brief-tuneopt-8 R-to8-3, the menu's "Discrete"): every coordinate takes only
/// its listed LEVELS — the unit-box positions of an integer's values, a step's grid, or the preferred
/// values inside a range — and the search never asks for a point between them.
///
/// <para><b>Exhaustive</b> when the grid holds at most <c>cap</c> points (default 2,000): every point,
/// the start's nearest first, in blocks of <c>block</c> (default ⌈points/20⌉, so a grid takes about
/// twenty iterations). It finishes when the grid is done, and its best is the grid's optimum.</para>
///
/// <para><b>Coordinate-wise descent</b> otherwise: from the start's nearest levels, one batch per
/// iteration holding, for every coordinate alone, the levels 1, 2, 4 and 8 steps either side; the best
/// of the batch is taken when it ranks ahead of the current point, and a batch with nothing better is a
/// grid-local minimum. It then restarts from uniformly random levels, <c>restarts</c> times (default
/// 10), and finishes.</para>
///
/// <para>Without levels (a caller that has none to give) each coordinate is gridded at eleven points,
/// 0, 0.1 … 1.</para>
///
/// <para>Options and their defaults: <c>discrete</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class Discrete : AskTellAlgorithm
{
    public const string AlgorithmId = "discrete";

    private static readonly int[] Offsets = [1, -1, 2, -2, 4, -4, 8, -8];

    private readonly ulong _seed;
    private readonly double[][] _levels;
    private readonly double _cap;
    private readonly int _restarts;
    private readonly IReadOnlyDictionary<string, string>? _options;

    /// <param name="levels">Per coordinate, its legal unit-box positions, ascending and distinct.</param>
    public Discrete(double[] start, ulong seed, IReadOnlyList<double[]>? levels = null,
                    IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        _seed = seed;
        _options = options;
        _levels = levels is null
            ? [.. Enumerable.Range(0, start.Length).Select(_ => Enumerable.Range(0, 11).Select(k => k / 10.0).ToArray())]
            : [.. levels.Select(l => l.Length == 0 ? [0.0] : l.Select(Clamp).Distinct().Order().ToArray())];
        if (_levels.Length != start.Length)
            throw new ArgumentException($"{_levels.Length} level lists for {start.Length} coordinates.");
        _cap      = Math.Max(1, Option(options, "cap"));
        _restarts = Math.Max(0, (int)Option(options, "restarts"));
    }

    /// <summary>The number of points on the grid.</summary>
    public double GridSize => _levels.Aggregate(1.0, (p, l) => p * l.Length);

    protected override IEnumerable<double[][]> Run()
        => GridSize <= _cap ? Exhaustive() : Descent();

    private double[] At(int[] idx)
    {
        var x = new double[idx.Length];
        for (int i = 0; i < idx.Length; i++) x[i] = _levels[i][idx[i]];
        return x;
    }

    private int[] Nearest(double[] u)
    {
        var idx = new int[u.Length];
        for (int i = 0; i < u.Length; i++)
        {
            var l = _levels[i];
            int best = 0;
            for (int k = 1; k < l.Length; k++)
                if (Math.Abs(l[k] - u[i]) < Math.Abs(l[best] - u[i])) best = k;
            idx[i] = best;
        }
        return idx;
    }

    private IEnumerable<double[][]> Exhaustive()
    {
        int n = Dimension;
        int total = (int)GridSize;
        int block = Math.Max(1, (int)Option(_options, "block", Math.Ceiling(total / 20.0)));
        var start = Nearest(Start);
        int startFlat = Flat(start);

        // The start's grid point first, then every other in mixed-radix order.
        var order = new List<int>(total) { startFlat };
        for (int f = 0; f < total; f++) if (f != startFlat) order.Add(f);

        for (int b = 0; b < order.Count; b += block)
        {
            yield return [.. order.Skip(b).Take(block).Select(f => At(Unflat(f)))];
            CompleteIteration();
        }
        Finish($"every one of the {total} grid points was evaluated");

        int Flat(int[] idx)
        {
            int f = 0;
            for (int i = n - 1; i >= 0; i--) f = f * _levels[i].Length + idx[i];
            return f;
        }
        int[] Unflat(int f)
        {
            var idx = new int[n];
            for (int i = 0; i < n; i++) { idx[i] = f % _levels[i].Length; f /= _levels[i].Length; }
            return idx;
        }
    }

    private IEnumerable<double[][]> Descent()
    {
        int n = Dimension;
        var rng = new SplitMix64(_seed);
        var cur = Nearest(Start);
        yield return [At(cur)];
        var curEval = Results[0];
        CompleteIteration();

        for (int restart = 0; ; restart++)
        {
            while (true)
            {
                var moves = new List<int[]>();
                for (int i = 0; i < n; i++)
                    foreach (int d in Offsets)
                    {
                        int k = cur[i] + d;
                        if (k < 0 || k >= _levels[i].Length) continue;
                        var m = (int[])cur.Clone();
                        m[i] = k;
                        moves.Add(m);
                    }
                if (moves.Count == 0) break;

                yield return [.. moves.Select(At)];
                CompleteIteration();
                int best = Ranking(Results)[0];
                if (!Better(Results[best], curEval)) break;
                (cur, curEval) = (moves[best], Results[best]);
            }

            if (restart >= _restarts)
            {
                Finish($"a grid-local minimum was confirmed from {restart + 1} start{(restart == 0 ? "" : "s")}");
                yield break;
            }
            cur = new int[n];
            for (int i = 0; i < n; i++) cur[i] = rng.Next(_levels[i].Length);
            yield return [At(cur)];
            curEval = Results[0];
            CompleteIteration();
        }
    }
}
