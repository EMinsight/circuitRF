using CircuitRF.Core.Design;

namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Builds an algorithm from its registry id (R-to7-7). The registry (<see cref="OptimizerAlgorithms"/>)
/// is the one list of what exists; this says which of those ids this build implements — <c>auto</c>
/// is resolved by the caller, and an id a later phase delivers returns null here until it does.
/// </summary>
public static class OptimizerFactory
{
    /// <summary>The algorithm for <paramref name="id"/>, or null when this build has none.</summary>
    public static IOptimizerAlgorithm? Create(string id, double[] start, ulong seed,
                                              IReadOnlyDictionary<string, string>? options = null) => id switch
    {
        LevenbergMarquardt.AlgorithmId    => new LevenbergMarquardt(start, options),
        BfgsB.AlgorithmId                 => new BfgsB(start, options),
        Minimax.AlgorithmId               => new Minimax(start, options),
        NelderMead.AlgorithmId            => new NelderMead(start, options),
        TrustRegionModel.AlgorithmId      => new TrustRegionModel(start, options),
        PatternSearch.AlgorithmId         => new PatternSearch(start, seed, options),
        RandomSearch.AlgorithmId          => new RandomSearch(start, seed, options),
        DifferentialEvolution.AlgorithmId => new DifferentialEvolution(start, seed, options),
        ParticleSwarm.AlgorithmId         => new ParticleSwarm(start, seed, options),
        CmaEs.AlgorithmId                 => new CmaEs(start, seed, options),
        _                                 => null,
    };

    /// <summary>Whether this build implements <paramref name="id"/> (<c>auto</c> included: it runs
    /// one of the others).</summary>
    public static bool IsBuilt(string id)
        => id == OptimizerAlgorithms.Auto || Create(id, [0.5], 1) is not null;
}
