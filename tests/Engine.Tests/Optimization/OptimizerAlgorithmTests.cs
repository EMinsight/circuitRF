using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Tests.Optimization;

/// <summary>
/// A 2-D Rosenbrock function mapped onto the unit box: x, y ∈ [−2, 2], optimum (1, 1) at u = (0.75, 0.75),
/// started from the classic (−1.2, 1). Its residual form r = (10·(y − x²), 1 − x) is what
/// Levenberg–Marquardt works on; the other methods see Σ r².
/// </summary>
internal static class Rosenbrock
{
    public static readonly double[] Start   = [0.2, 0.75];
    public static readonly double[] Optimum = [0.75, 0.75];

    public static Evaluation Evaluate(double[] u)
    {
        double x = -2 + 4 * u[0], y = -2 + 4 * u[1];
        double[] r = [10 * (y - x * x), 1 - x];
        return new Evaluation(r[0] * r[0] + r[1] * r[1], r);
    }

    public static IOptimizerAlgorithm Make(string id, ulong seed = 7) => id switch
    {
        LevenbergMarquardt.AlgorithmId => new LevenbergMarquardt(Start),
        BfgsB.AlgorithmId              => new BfgsB(Start),
        NelderMead.AlgorithmId         => new NelderMead(Start),
        _                              => new RandomSearch(Start, seed),
    };

    /// <summary>Asks and tells until the algorithm finishes or <paramref name="budget"/> points have been
    /// evaluated. Returns every point evaluated, in order, and the best.</summary>
    public static (List<double[]> Points, double[] Best, double BestCost, int Failures) Drive(
        IOptimizerAlgorithm alg, int budget, Func<double[], Evaluation>? evaluate = null)
    {
        evaluate ??= Evaluate;
        var points = new List<double[]>();
        double[] best = [];
        double bestCost = double.PositiveInfinity;
        int failures = 0;
        while (!alg.IsFinished && points.Count < budget)
        {
            var batch = alg.Ask();
            var results = batch.Select(evaluate).ToList();
            for (int k = 0; k < batch.Count; k++)
            {
                points.Add(batch[k]);
                if (results[k].Failed) { failures++; continue; }
                if (results[k].Cost < bestCost) (bestCost, best) = (results[k].Cost, batch[k]);
            }
            alg.Tell(results);
        }
        return (points, best, bestCost, failures);
    }

    public static double Distance(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (p, q) => (p - q) * (p - q)).Sum());
}

/// <summary>brief-tuneopt-6 §4: each algorithm on a 2-D Rosenbrock in the box.</summary>
public sealed class OptimizerAlgorithmTests
{
    [Theory]
    [InlineData(LevenbergMarquardt.AlgorithmId, 1e-4)]
    [InlineData(BfgsB.AlgorithmId,              1e-3)]
    [InlineData(NelderMead.AlgorithmId,         1e-3)]
    public void ReachesTheRosenbrockOptimum_FromAFixedStart(string id, double tol)
    {
        var alg = Rosenbrock.Make(id);
        var run = Rosenbrock.Drive(alg, budget: 5000);
        Assert.True(alg.IsFinished, $"{id} did not finish in 5000 evaluations");
        Assert.True(Rosenbrock.Distance(run.Best, Rosenbrock.Optimum) < tol,
            $"{id} ended at ({run.Best[0]:G6}, {run.Best[1]:G6}), cost {run.BestCost:G3}: {alg.FinishReason}");
    }

    [Fact]
    public void RandomSearch_GetsCloseInAFixedBudget_WithAFixedSeed()
    {
        var run = Rosenbrock.Drive(Rosenbrock.Make(RandomSearch.AlgorithmId, seed: 1), budget: 2000);
        Assert.Equal(2000, run.Points.Count);
        Assert.True(run.BestCost < 0.2, $"best cost {run.BestCost:G3}");
    }

    /// <summary>
    /// The first trial step each method takes is made to fail (a disk of failing points around it): the
    /// method must shrink the step, go around, and still arrive.
    /// </summary>
    [Theory]
    [InlineData(LevenbergMarquardt.AlgorithmId)]
    [InlineData(BfgsB.AlgorithmId)]
    public void AFailingRegion_IsSteppedAround(string id)
    {
        // Where the clean run's first trial step lands: the first one-point batch after the start.
        var probe = Rosenbrock.Make(id);
        double[]? firstTrial = null;
        probe.Tell([Rosenbrock.Evaluate(probe.Ask()[0])]);
        while (firstTrial is null)
        {
            var batch = probe.Ask();
            if (batch.Count == 1) firstTrial = batch[0];
            probe.Tell([.. batch.Select(Rosenbrock.Evaluate)]);
        }
        const double radius = 0.03;
        Assert.True(Rosenbrock.Distance(firstTrial, Rosenbrock.Optimum) > 2 * radius);

        var alg = Rosenbrock.Make(id);
        var run = Rosenbrock.Drive(alg, 5000, u => Rosenbrock.Distance(u, firstTrial) < radius
            ? new Evaluation(1e6, null, Failed: true)
            : Rosenbrock.Evaluate(u));

        Assert.True(run.Failures > 0);
        Assert.True(Rosenbrock.Distance(run.Best, Rosenbrock.Optimum) < 1e-3,
            $"{id} ended at ({run.Best[0]:G6}, {run.Best[1]:G6}): {alg.FinishReason}");
    }
}

/// <summary>R-to6-8 at the algorithm level: a run captured mid-way and restored into a fresh instance
/// continues exactly as the uninterrupted run, point for point.</summary>
public sealed class PauseResumeDeterminismTests
{
    [Theory]
    [InlineData(LevenbergMarquardt.AlgorithmId)]
    [InlineData(BfgsB.AlgorithmId)]
    [InlineData(NelderMead.AlgorithmId)]
    [InlineData(RandomSearch.AlgorithmId)]
    public void CaptureThenRestore_ContinuesExactlyAsTheUninterruptedRun(string id)
    {
        var whole = Rosenbrock.Drive(Rosenbrock.Make(id), budget: 120).Points;

        var first = Rosenbrock.Make(id);
        var head = Rosenbrock.Drive(first, budget: 40).Points;
        var resumed = Rosenbrock.Make(id);
        resumed.Restore(first.Capture());
        var tail = Rosenbrock.Drive(resumed, budget: 120 - head.Count).Points;

        Assert.Equal(whole.Count, head.Count + tail.Count);
        Assert.All(whole.Zip(head.Concat(tail)), p => Assert.Equal(p.First, p.Second));
    }
}
