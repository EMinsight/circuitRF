using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Tests.Optimization;

/// <summary>Test functions on the unit box for brief-tuneopt-7's gates.</summary>
internal static class TestFunctions
{
    /// <summary>4-D Rastrigin on x ∈ [−4, 6]⁴ (so the optimum, x = 0, is at u = 0.4 and not the box's
    /// centre); 10⁴ local minima, the global one cost 0.</summary>
    public static readonly double[] RastriginOptimum = [0.4, 0.4, 0.4, 0.4];
    public static readonly double[] RastriginStart   = [0.9, 0.9, 0.9, 0.9];   // x = 5, a local minimum

    public static Evaluation Rastrigin(double[] u)
    {
        double f = 10 * u.Length;
        foreach (double ui in u)
        {
            double x = -4 + 10 * ui;
            f += x * x - 10 * Math.Cos(2 * Math.PI * x);
        }
        return new Evaluation(f);
    }

    /// <summary>4-D Rosenbrock on x ∈ [−2, 2]⁴; optimum x = 1 at u = 0.75, from x = 0.</summary>
    public static readonly double[] Rosenbrock4Optimum = [0.75, 0.75, 0.75, 0.75];
    public static readonly double[] Rosenbrock4Start   = [0.5, 0.5, 0.5, 0.5];

    public static Evaluation Rosenbrock4(double[] u)
    {
        var x = u.Select(v => -2 + 4 * v).ToArray();
        double f = 0;
        for (int i = 0; i < 3; i++) f += 100 * Math.Pow(x[i + 1] - x[i] * x[i], 2) + Math.Pow(1 - x[i], 2);
        return new Evaluation(f);
    }

    public static IOptimizerAlgorithm Make(string id, double[] start, ulong seed = 1, Dictionary<string, string>? options = null)
        => OptimizerFactory.Create(id, start, seed, options) ?? throw new ArgumentException(id);
}

/// <summary>R-to7-1/2/3: each population method finds the global optimum of a 4-D Rastrigin from a
/// local minimum, with a fixed seed, inside a stated evaluation budget.</summary>
public sealed class PopulationMethodTests
{
    [Theory]
    [InlineData(DifferentialEvolution.AlgorithmId, 4000)]     // its own default budget, 1000n; there at 3,782
    [InlineData(ParticleSwarm.AlgorithmId,         12000)]    // there at 9,127
    [InlineData(CmaEs.AlgorithmId,                 8000)]     // there at 5,530
    public void ReachTheRastriginGlobalOptimum_WithAFixedSeed(string id, int budget)
    {
        var alg = TestFunctions.Make(id, TestFunctions.RastriginStart);
        var run = Rosenbrock.Drive(alg, budget, TestFunctions.Rastrigin);
        Assert.True(run.BestCost < 1e-4 && Rosenbrock.Distance(run.Best, TestFunctions.RastriginOptimum) < 1e-3,
            $"{id}: best cost {run.BestCost:G3} after {run.Points.Count} evaluations: {alg.FinishReason}");
    }
}

/// <summary>R-to7-4/5: the trust-region model and pattern search reach a 4-D Rosenbrock optimum; pattern
/// search does so with 10 % of points forced to fail.</summary>
public sealed class DerivativeFreeLocalTests
{
    [Theory]
    [InlineData(TrustRegionModel.AlgorithmId, 3000)]
    [InlineData(PatternSearch.AlgorithmId,    5000)]
    public void ReachTheRosenbrock4Optimum(string id, int budget)
    {
        var alg = TestFunctions.Make(id, TestFunctions.Rosenbrock4Start);
        var run = Rosenbrock.Drive(alg, budget, TestFunctions.Rosenbrock4);
        Assert.True(alg.IsFinished, $"{id} did not finish in {budget} evaluations (best {run.BestCost:G3})");
        Assert.True(Rosenbrock.Distance(run.Best, TestFunctions.Rosenbrock4Optimum) < 1e-3,
            $"{id}: best cost {run.BestCost:G3} at {string.Join(", ", run.Best.Select(v => v.ToString("G6")))}: {alg.FinishReason}");
    }

    [Fact]
    public void PatternSearch_ArrivesWithATenthOfItsPointsFailing()
    {
        // A fixed tenth of the box fails: a point fails when a hash of its coordinates says so, so the
        // same point always fails — a simulator that cannot converge there, not a random one.
        static bool Fails(double[] u)
        {
            ulong h = 1469598103934665603UL;
            foreach (double v in u) h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(v)) * 1099511628211UL;
            h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32;
            return h % 10 == 0;
        }
        var alg = TestFunctions.Make(PatternSearch.AlgorithmId, TestFunctions.Rosenbrock4Start);
        var run = Rosenbrock.Drive(alg, 5000,
            u => Fails(u) ? new Evaluation(1e9, null, Failed: true) : TestFunctions.Rosenbrock4(u));

        double fraction = (double)run.Failures / run.Points.Count;
        Assert.InRange(fraction, 0.07, 0.13);
        Assert.True(Rosenbrock.Distance(run.Best, TestFunctions.Rosenbrock4Optimum) < 1e-3,
            $"best cost {run.BestCost:G3} with {run.Failures} of {run.Points.Count} failing: {alg.FinishReason}");
    }
}

/// <summary>
/// R-to7-6: the minimax line through eˣ on [0, 1] — a + b·t with residuals ±(a + b·t − eᵗ) on a grid.
/// Chebyshev's theorem says the best one equioscillates: its error is largest, and equal, at three
/// points. Minimax must land there, its worst residuals level; the analytic answer is b = e − 1 with
/// a maximum error of 0.10593.
/// </summary>
public sealed class MinimaxEqualRippleTests
{
    private static readonly double[] Grid = [.. Enumerable.Range(0, 101).Select(k => k / 100.0)];

    private static Evaluation Line(double[] u)
    {
        double a = 2 * u[0], b = 3 * u[1];
        var r = new double[2 * Grid.Length];
        for (int k = 0; k < Grid.Length; k++)
        {
            double e = a + b * Grid[k] - Math.Exp(Grid[k]);
            r[2 * k] = e; r[2 * k + 1] = -e;
        }
        return new Evaluation(r.Max(), r);
    }

    [Fact]
    public void LandsOnTheEqualRippleLine()
    {
        var alg = TestFunctions.Make(Minimax.AlgorithmId, [0.2, 0.2]);
        var run = Rosenbrock.Drive(alg, 2000, Line);
        Assert.True(alg.IsFinished);

        var r = Line(run.Best).Residuals!.OrderDescending().ToArray();
        Assert.True(r[0] - r[1] < 1e-6, $"worst {r[0]:G8}, next {r[1]:G8}");
        Assert.Equal(0.105933, r[0], 4);
        Assert.Equal(Math.E - 1, 3 * run.Best[1], 4);
    }
}
