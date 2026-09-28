// brief-em3d-80 R-em3d80-1b — one matrix, many right-hand sides: the factorisation (direct) or the AMG hierarchy (iterative)
// is built once and every solve reuses it. An Rth matrix over N sources is N solves of one matrix, and brief 72 Q5 measured a
// solve with a kept factor ~3× cheaper than a fresh one.
//
// An iterative solve that does not converge falls back to the direct one, factorised once on first need, and says so — as
// LinearSolver.Solve does.

using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;

namespace CircuitRF.Thermal.Solvers;

/// <summary>A matrix made ready to solve with many right-hand sides.</summary>
public sealed class PreparedSolve
{
    private readonly SparseRows _a;
    private readonly bool _symmetric;
    private readonly ThermalSolveOptions _options;
    private readonly SmoothedAggregationAmg? _amg;
    private SparseCholesky? _chol;
    private SparseLU? _lu;

    private PreparedSolve(SparseRows a, bool symmetric, ThermalSolverKind kind, ThermalSolveOptions options)
    {
        _a = a;
        _symmetric = symmetric;
        _options = options;
        Kind = kind;
        if (a.Rows == 0) return;
        if (kind == ThermalSolverKind.Direct) Factor();
        else _amg = SmoothedAggregationAmg.Build(symmetric ? a : a.SymmetricPart(), options.AmgTheta);
    }

    /// <summary>Which solver the solves use (Direct after a fallback).</summary>
    public ThermalSolverKind Kind { get; private set; }

    /// <summary>The AMG's level count; 0 on the direct path.</summary>
    public int AmgLevels => _amg?.LevelCount ?? 0;

    /// <summary>The first fallback's sentence, once one happened.</summary>
    public string? FallbackNote { get; private set; }

    /// <summary>Prepares <paramref name="a"/>: Cholesky / CG when <paramref name="symmetric"/>, LU / BiCGStab otherwise;
    /// <see cref="ThermalSolverKind.Auto"/> takes the direct solver below <see cref="ThermalSolveOptions.DirectBelow"/>.</summary>
    public static PreparedSolve Create(SparseRows a, bool symmetric, ThermalSolverKind kind, ThermalSolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(options);
        if (kind == ThermalSolverKind.Auto) kind = a.Rows < options.DirectBelow ? ThermalSolverKind.Direct : ThermalSolverKind.Iterative;
        return new PreparedSolve(a, symmetric, kind, options);
    }

    /// <summary>Solves A·x = b; <paramref name="x"/> is the iterative solve's initial guess.</summary>
    public LinearSolveReport Solve(double[] b, double[] x)
    {
        if (_a.Rows == 0) return new(Kind, 0, 0, true, null);
        _options.Cancellation.ThrowIfCancellationRequested();
        if (Kind == ThermalSolverKind.Iterative)
        {
            int it = _symmetric ? LinearSolver.Pcg(_a, b, x, _amg!.Apply, _options, out double rel)
                                : LinearSolver.BiCgStab(_a, b, x, _amg!.Apply, _options, out rel);
            if (rel <= _options.RelativeTolerance) return new(ThermalSolverKind.Iterative, it, rel, true, null, _amg.LevelCount, _amg.OperatorComplexity);
            FallbackNote ??= $"An iterative solve stopped at a relative residual of {rel:G3} after {it} iterations (asked for " +
                             $"{_options.RelativeTolerance:G3}); the direct solver was used from there on.";
            Kind = ThermalSolverKind.Direct;
            Factor();
        }
        if (_symmetric) _chol!.Solve(b, x); else _lu!.Solve(b, x);
        var y = new double[_a.Rows];
        _a.Multiply(x, y);
        double num = 0, den = 0;
        for (int i = 0; i < y.Length; i++) { num += (b[i] - y[i]) * (b[i] - y[i]); den += b[i] * b[i]; }
        return new(ThermalSolverKind.Direct, 0, den > 0 ? Math.Sqrt(num / den) : Math.Sqrt(num), true, FallbackNote);
    }

    private void Factor()
    {
        // CSparse is compressed-COLUMN: A's CSR arrays are Aᵀ's CSC arrays (LinearSolver.Direct says so too)
        var csc = _symmetric ? _a : _a.Transpose();
        var m = new SparseMatrix(_a.Rows, _a.Cols, csc.Val, csc.Idx, csc.Ptr);
        if (_symmetric) _chol = SparseCholesky.Create(m, ColumnOrdering.MinimumDegreeAtPlusA);
        else _lu = SparseLU.Create(m, ColumnOrdering.MinimumDegreeAtPlusA, 1.0);
    }
}
