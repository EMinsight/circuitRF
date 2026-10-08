using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Tests.Optimization;

/// <summary>brief-tuneopt-8 §3: Bayesian optimization reaches the 2-D Branin minimum, counted in
/// evaluations, with a fixed seed.</summary>
public sealed class BayesianTests
{
    /// <summary>Branin on x₁ ∈ [−5, 10], x₂ ∈ [0, 15]; three global minima, all 0.397887.</summary>
    private static Evaluation Branin(double[] u)
    {
        double x1 = -5 + 15 * u[0], x2 = 15 * u[1];
        double b = 5.1 / (4 * Math.PI * Math.PI), c = 5 / Math.PI, t = 1 / (8 * Math.PI);
        double q = x2 - b * x1 * x1 + c * x1 - 6;
        return new Evaluation(q * q + 10 * (1 - t) * Math.Cos(x1) + 10);
    }

    private const double BraninMin = 0.397887357729738;

    [Fact]
    public void ReachesTheBraninMinimum_In40Evaluations()
    {
        var alg = TestFunctions.Make(Bayesian.AlgorithmId, [0.5, 0.5]);
        var run = Rosenbrock.Drive(alg, budget: 40, Branin);
        Assert.True(run.Points.Count <= 40);
        Assert.True(run.BestCost - BraninMin < 1e-3,
            $"best {run.BestCost:G6} at ({run.Best[0]:G4}, {run.Best[1]:G4}) after {run.Points.Count} evaluations");
    }
}
