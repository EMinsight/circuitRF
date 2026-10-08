namespace CircuitRF.Engine.Optimization;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The optimizer's numeric half (brief-tuneopt-6 §2, overview D12): algorithms over the unit box
//  [0, 1]ⁿ that never call a simulator. Each is an ASK/TELL state machine — it hands out a batch of
//  points and is told what they cost — so pausing is "stop asking", parallel evaluation is "evaluate
//  the batch concurrently", and a run is repeatable because the seed is part of the state.
//
//  No domain types: a point is a double[], an evaluation a cost, a residual vector and a flag. What
//  a coordinate MEANS (a capacitance on a log scale, the magnitude of a complex load) is
//  src/Design/Optimization's business.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What one point cost. <paramref name="Residuals"/> is the goal-residual vector a least-squares
/// method works on (null when the evaluation produced none). <paramref name="Failed"/> marks a point
/// whose cost is a penalty, not a measurement — a simulation that did not converge, or a point no
/// design satisfies; its <paramref name="Cost"/> is still finite and ranks below every real one, so a
/// method that only compares costs needs nothing else, and a method that models the cost (a gradient,
/// a Jacobian) treats it as a step to shrink.
/// </summary>
public sealed record Evaluation(double Cost, double[]? Residuals = null, bool Failed = false);

/// <summary>A resumable optimizer over the unit box (brief-tuneopt-6 §2).</summary>
public interface IOptimizerAlgorithm
{
    /// <summary>The stable id (<c>lm</c>, <c>bfgsb</c>, <c>simplex</c>, <c>random</c>).</summary>
    string Id { get; }

    /// <summary>The number of coordinates.</summary>
    int Dimension { get; }

    /// <summary>The batch of points to evaluate next — one for a simplex step, n for a
    /// finite-difference Jacobian, the batch size for a random search. Asking again before telling
    /// returns the same batch. Empty once <see cref="IsFinished"/>.</summary>
    IReadOnlyList<double[]> Ask();

    /// <summary>The batch's results, one per point, in the order asked.</summary>
    void Tell(IReadOnlyList<Evaluation> results);

    bool IsFinished { get; }

    /// <summary>Why the algorithm finished on its own; null while it runs.</summary>
    string? FinishReason { get; }

    /// <summary>Iterations completed — each algorithm says what one is (a simplex step, an accepted
    /// or abandoned Levenberg–Marquardt step, a line search, a random batch).</summary>
    int Iterations { get; }

    /// <summary>Everything needed to continue later: the batches told so far.</summary>
    OptimizerState Capture();

    /// <summary>Continues from <paramref name="state"/>, which <see cref="Capture"/> of an algorithm
    /// built with the same arguments produced.</summary>
    void Restore(OptimizerState state);
}

/// <summary>
/// An algorithm's whole state, held as the batches it asked for and what each cost. Every algorithm
/// here is deterministic given its arguments and its seed, so replaying these batches rebuilds the
/// state exactly — and the record is plain numbers, which a checkpoint file can hold as it is.
/// </summary>
public sealed class OptimizerState(string algorithm, IReadOnlyList<(double[][] Points, Evaluation[] Results)> batches)
{
    public string Algorithm { get; } = algorithm;
    public IReadOnlyList<(double[][] Points, Evaluation[] Results)> Batches { get; } = batches;
}

/// <summary>
/// The shared machinery: an algorithm is written as one iterator, <see cref="Run"/>, that yields each
/// batch and reads <see cref="Results"/> when resumed. <b>Every piece of mutable state lives in that
/// iterator's locals</b> — so a fresh enumerator is a fresh algorithm, and <see cref="Restore"/> is a
/// replay of the told batches into it, checked point for point.
/// </summary>
public abstract class AskTellAlgorithm : IOptimizerAlgorithm
{
    private IEnumerator<double[][]>? _run;
    private double[][]? _pending;
    private readonly List<(double[][] Points, Evaluation[] Results)> _log = [];

    protected AskTellAlgorithm(string id, double[] start)
    {
        Id        = id;
        Dimension = start.Length;
        Start     = [.. start.Select(Clamp)];
    }

    public string Id { get; }
    public int Dimension { get; }
    public bool IsFinished { get; private set; }
    public string? FinishReason { get; private set; }
    public int Iterations { get; private set; }

    /// <summary>The start point, inside the box.</summary>
    protected double[] Start { get; }

    /// <summary>What the batch just yielded cost — read by <see cref="Run"/> after each yield.</summary>
    protected Evaluation[] Results { get; private set; } = [];

    /// <summary>The algorithm. Yields each batch; finishes by calling <see cref="Finish"/> and
    /// breaking. Never touches a field it writes — locals only (see the class remark).</summary>
    protected abstract IEnumerable<double[][]> Run();

    protected void CompleteIteration() => Iterations++;

    protected void Finish(string reason) => FinishReason ??= reason;

    public IReadOnlyList<double[]> Ask()
    {
        if (IsFinished) return [];
        if (_pending is null) Advance();
        return _pending is null ? [] : [.. _pending.Select(p => (double[])p.Clone())];
    }

    public void Tell(IReadOnlyList<Evaluation> results)
    {
        if (_pending is null)
            throw new InvalidOperationException("Tell was called with no batch asked for.");
        if (results.Count != _pending.Length)
            throw new ArgumentException($"{results.Count} results for a batch of {_pending.Length} points.");
        Results = [.. results];
        _log.Add((_pending, Results));
        _pending = null;
        Advance();
    }

    public OptimizerState Capture() => new(Id, [.. _log]);

    public void Restore(OptimizerState state)
    {
        if (state.Algorithm != Id)
            throw new ArgumentException($"A '{state.Algorithm}' state cannot continue a '{Id}' run.");
        _run?.Dispose();
        _run = null; _pending = null; _log.Clear();
        IsFinished = false; FinishReason = null; Iterations = 0; Results = [];

        foreach (var (points, results) in state.Batches)
        {
            if (_pending is null) Advance();
            if (_pending is null || !SameBatch(_pending, points))
                throw new ArgumentException("The state was captured from a run with different arguments.");
            Tell(results);
        }
    }

    private void Advance()
    {
        _run ??= Run().GetEnumerator();
        while (true)
        {
            if (!_run.MoveNext())
            {
                IsFinished = true;
                FinishReason ??= "the algorithm finished";
                _pending = null;
                return;
            }
            if (_run.Current.Length == 0) continue;
            _pending = [.. _run.Current.Select(p => p.Select(Clamp).ToArray())];
            return;
        }
    }

    private static bool SameBatch(double[][] a, double[][] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
        return true;
    }

    /// <summary>Into the box. A NaN coordinate is a defect in the algorithm, not a point.</summary>
    protected static double Clamp(double v)
        => double.IsNaN(v) ? throw new InvalidOperationException("An optimizer produced a NaN coordinate.")
                           : Math.Clamp(v, 0.0, 1.0);

    /// <summary>The projection onto the box.</summary>
    protected static double[] Project(double[] x) => [.. x.Select(v => Math.Clamp(v, 0.0, 1.0))];

    protected static double InfNorm(double[] a, double[] b)
    {
        double m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    /// <summary>
    /// The points of a forward-difference gradient or Jacobian, as one batch: x + h·eᵢ, or x − h·eᵢ
    /// where the forward point would leave the box. <paramref name="sign"/> gives each step's
    /// direction (+1 forward, −1 backward).
    /// </summary>
    protected static double[][] DifferencePoints(double[] x, double h, IReadOnlyList<int> coords, out double[] sign)
    {
        var pts = new double[coords.Count][];
        sign = new double[coords.Count];
        for (int k = 0; k < coords.Count; k++)
        {
            int i = coords[k];
            sign[k] = x[i] + h <= 1.0 ? 1.0 : -1.0;
            pts[k] = (double[])x.Clone();
            pts[k][i] = x[i] + sign[k] * h;
        }
        return pts;
    }

    /// <summary>A number option, or <paramref name="fallback"/>.</summary>
    protected static double Option(IReadOnlyDictionary<string, string>? options, string name, double fallback)
        => options is not null && options.TryGetValue(name, out var text)
           && double.TryParse(text, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : fallback;
}

/// <summary>
/// SplitMix64 — a small generator whose output is fixed by its seed on every platform and runtime, which
/// <see cref="System.Random"/> does not promise. A run's repeatability rests on it.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong _s = seed;

    public ulong NextULong()
    {
        ulong z = _s += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, n).</summary>
    public int Next(int n) => (int)(NextDouble() * n);
}

/// <summary>Small dense linear algebra for the methods here (n is the number of optimized values).</summary>
internal static class DenseLinear
{
    /// <summary>Solves A·x = b by Gaussian elimination with partial pivoting; null when A is singular.</summary>
    public static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        double scale = 0;
        foreach (double v in a) scale = Math.Max(scale, Math.Abs(v));
        if (scale == 0) return null;

        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++)
                if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) <= 1e-14 * scale) return null;
            if (p != c)
            {
                for (int k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
                (x[c], x[p]) = (x[p], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = s / m[r, r];
        }
        return x;
    }

    public static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
