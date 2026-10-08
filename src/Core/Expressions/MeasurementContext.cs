using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Holds the simulation results for each named analysis in a run.
/// Passed to <see cref="Evaluator"/> so that measurement expressions can resolve
/// qualified cube accessors like <c>HB1.V("n_drain", 1, All)</c>.
///
/// Optionally carries <see cref="ILinearBackSolver"/> instances (one per analysis)
/// so that <c>V(node)</c> accesses for linear-interior nodes can be back-solved lazily.
/// </summary>
public sealed class MeasurementContext
{
    private readonly IReadOnlyDictionary<string, DataSet>           _results;
    private readonly IReadOnlyDictionary<string, ILinearBackSolver>? _backSolvers;

    public MeasurementContext(
        IReadOnlyDictionary<string, DataSet>            results,
        IReadOnlyDictionary<string, ILinearBackSolver>? backSolvers = null)
    {
        _results     = results;
        _backSolvers = backSolvers;
    }

    public DataSet GetAnalysis(string name)
        => _results.TryGetValue(name, out var ds) ? ds
           : throw new KeyNotFoundException(
               $"No analysis named '{name}' in measurement context. Available: [{string.Join(", ", _results.Keys)}]");

    /// <summary>
    /// The analysis whose <c>wsp</c> cube IS <paramref name="wsp"/> (reference equality — a cube
    /// handed out by <c>SP1.wsp</c> is the DataSet's own object), so a multi-probe function can
    /// read that run's probe labels (<c>__WspProbes</c>) and termination metadata
    /// (<c>__WspTermZ</c>) without a second argument naming the analysis. A cube that has been
    /// sliced or re-shaped is a new object and is not found; the caller says what that costs.
    /// </summary>
    public bool TryFindWspOwner(DataCube wsp, out DataSet? owner)
    {
        foreach (var ds in _results.Values)
            if (ds.Contains("wsp") && ReferenceEquals(ds["wsp"], wsp))
            {
                owner = ds;
                return true;
            }
        owner = null;
        return false;
    }

    /// <summary>
    /// The analysis whose <paramref name="cubeName"/> cube IS <paramref name="cube"/> (reference
    /// equality, as <see cref="TryFindWspOwner"/>) — so a network metric handed <c>SP1.S</c> can read
    /// that run's per-port reference impedances (<c>SP1.Z0</c>) without a second argument.
    /// </summary>
    public bool TryFindOwner(DataCube cube, string cubeName, out DataSet? owner)
    {
        foreach (var ds in _results.Values)
            if (ds.Contains(cubeName) && ReferenceEquals(ds[cubeName], cube))
            {
                owner = ds;
                return true;
            }
        owner = null;
        return false;
    }

    /// <summary>
    /// Try to get the linear back-solver for the named analysis.
    /// Returns false (and null solver) when no back-solver is available.
    /// </summary>
    public bool TryGetBackSolver(string analysisName, out ILinearBackSolver? solver)
    {
        if (_backSolvers is not null && _backSolvers.TryGetValue(analysisName, out solver))
            return true;
        solver = null;
        return false;
    }
}
