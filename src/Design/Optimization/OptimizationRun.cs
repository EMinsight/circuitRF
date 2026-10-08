using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Optimization;
using RfCore.Data;

namespace CircuitRF.Design.Optimization;

/// <summary>What a caller may change about a run beyond what the design's own setup says.</summary>
public sealed record OptimizationOptions
{
    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides, applied before the tuned values as a run verb applies them.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>Called after every completed iteration, on the run's thread.</summary>
    public Action<OptimizationProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the run (exit 130); Stop keeps the best point.</summary>
    public CancellationToken Cancellation { get; init; }
}

/// <summary>How a run ended (overview D15 gives each its exit code).</summary>
public enum OptimizationOutcome
{
    /// <summary>Finished, and every enabled goal is met — exit 0.</summary>
    GoalsMet,
    /// <summary>Finished with at least one goal unmet — exit 3.</summary>
    GoalsUnmet,
    /// <summary>Refused before or at the first evaluation — exit 1.</summary>
    Refused,
    /// <summary>No evaluation converged — exit 2.</summary>
    NoConvergence,
    /// <summary>Cancelled — exit 130, and nothing is written.</summary>
    Cancelled,
}

/// <summary>One goal at the best point: its worst violation, where, and whether it is met.</summary>
public sealed record GoalReport(string Name, double WorstViolation, double? WorstAt, string? Axis, double WorstValue, bool Met);

/// <summary>What a run reports after every iteration (R-to6-9). The Optimizer window and MCP read the same one.</summary>
public sealed record OptimizationProgress(
    int                                 Iteration,
    long                                Evaluations,
    long                                Failures,
    long                                Infeasible,
    long                                CacheHits,
    TimeSpan                            Elapsed,
    double                              CurrentCost,
    double                              BestCost,
    IReadOnlyDictionary<string, string> BestValues,
    IReadOnlyList<GoalReport>           Goals,
    IReadOnlyList<RailedVariable>       Railed,
    DataSet?                            BestData);

/// <summary>One point the algorithm was told about, in order.</summary>
public sealed record EvaluationRecord(
    double[] Point, DecodedPoint Decoded, double Cost, bool Failed, bool Infeasible, bool Cached);

/// <summary>What a run produced.</summary>
public sealed class OptimizationResult
{
    public required OptimizationOutcome Outcome { get; init; }

    /// <summary>Overview D15: 0, 3, 1, 2 or 130.</summary>
    public int ExitCode => Outcome switch
    {
        OptimizationOutcome.GoalsMet      => 0,
        OptimizationOutcome.GoalsUnmet    => 3,
        OptimizationOutcome.Refused       => 1,
        OptimizationOutcome.NoConvergence => 2,
        _                                 => 130,
    };

    /// <summary>Why it stopped, as a sentence.</summary>
    public string FinishReason { get; init; } = "";

    /// <summary>The refusal, for <see cref="OptimizationOutcome.Refused"/> and
    /// <see cref="OptimizationOutcome.NoConvergence"/>.</summary>
    public Diagnostic? Refusal { get; init; }

    public string Algorithm { get; init; } = "";
    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];

    /// <summary>The best point's tuned values, value key → text (a complex value whole) — what lock-in
    /// stores and Push writes. Empty when nothing succeeded.</summary>
    public IReadOnlyDictionary<string, string> BestValues { get; init; } = new Dictionary<string, string>();
    public double[]? BestPoint { get; init; }
    public double? BestCost { get; init; }
    public IReadOnlyList<GoalReport> Goals { get; init; } = [];
    public IReadOnlyList<RailedVariable> Railed { get; init; } = [];
    public DataSet? BestData { get; init; }

    public int Iterations { get; init; }
    public long Evaluations { get; init; }
    public long Failures { get; init; }
    public long Infeasible { get; init; }
    public long CacheHits { get; init; }

    /// <summary>Every point the algorithm was told about, in order.</summary>
    public IReadOnlyList<EvaluationRecord> Log { get; init; } = [];

    /// <summary>R-to6-10: group <c>opt</c> — cubes over <c>eval</c> and over <c>iter</c>. The caller
    /// decides whether to write it.</summary>
    public DataSet? History { get; init; }
}

/// <summary>
/// One optimization of one prepared circuit (brief-tuneopt-6): the variables decoded from the unit
/// box, the goals scored into residuals, the evaluations cached, run in parallel where the design
/// allows, stopped by its limits, paused and resumed, reported per iteration, and kept as a history.
/// The algorithm only ever sees a cost, a residual vector and a failed flag.
///
/// <para><b>Evaluations</b> counts simulations actually run. A point the cache already holds costs
/// nothing (<see cref="CacheHits"/>); an infeasible point (D18) is not simulated
/// (<see cref="Infeasible"/>); a non-converged or failed simulation is a <see cref="Failures"/>.</para>
///
/// <para><b>Penalties.</b> A failed point costs 10·(1 + the largest successful cost seen so far,
/// this batch included), so it ranks below every real point; an infeasible one costs that plus its
/// normalized distance to the region, so ranking pushes toward feasibility. Penalties are assigned
/// after the whole batch, in batch order, so concurrency cannot change them.</para>
/// </summary>
public sealed class OptimizationRun
{
    /// <summary>The default iteration limit when the setup states none.</summary>
    public const int DefaultMaxIterations = 100;

    /// <summary>Stall: a run whose best cost improved by less than this fraction over
    /// <see cref="DefaultStallIterations"/> iterations stops (options <c>stall_tol</c>, <c>stall_iters</c>).</summary>
    public const double DefaultStallTolerance = 1e-9;
    public const int    DefaultStallIterations = 25;

    /// <summary>The algorithms this build has.</summary>
    public static IReadOnlyList<string> Available { get; } =
        [OptimizerAlgorithms.Auto, LevenbergMarquardt.AlgorithmId, BfgsB.AlgorithmId, NelderMead.AlgorithmId, RandomSearch.AlgorithmId];

    private sealed record PointOutcome(
        bool Failed, Diagnostic? Error, double Cost, double[] Residuals, IReadOnlyList<GoalScore> Scores, bool GoalError);

    private readonly PreparedCircuit _circuit;
    private readonly OptimizationOptions _options;
    private readonly TuningSetup _setup = new();
    private readonly OptimizerSettings _settings = new();
    private readonly List<OptimizationGoal> _goals = [];
    private readonly IReadOnlyList<string>? _analyses;
    private readonly bool _simulates;
    private readonly int _parallelism;
    private readonly List<Diagnostic> _notes = [];
    private readonly Dictionary<string, PointOutcome> _cache = new(StringComparer.Ordinal);
    private readonly List<EvaluationRecord> _log = [];
    private readonly List<(double Best, double[] Worst)> _iterHistory = [];
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly ManualResetEventSlim _held = new(false);
    private volatile bool _stop;
    private bool _serialNoted;
    private double _maxFeasible;
    private long _evaluations, _failures, _infeasible, _cacheHits;

    private int _bestIndex = -1;
    private DataSet? _bestData;
    private Diagnostic? _firstFailure;

    private OptimizationRun(PreparedCircuit circuit, OptimizationOptions options)
    {
        _circuit = circuit;
        _options = options;

        if (circuit.ReadError is { } readError || circuit.Lib is not { } lib || circuit.Tb is not { } tb)
        {
            Refusal = OptimizationDiagnostics.EvaluationFailed(circuit.ReadError ?? "Netlist read failed.");
            return;
        }

        _setup    = options.Setup ?? tb.Tuning ?? new TuningSetup();
        _settings = _setup.Optimizer ?? new OptimizerSettings();

        var bench = tb;
        if (options.Setup is not null)
        {
            bench = new TestBench(tb.Name) { Tuning = options.Setup };
            bench.Instances.AddRange(tb.Instances);
            bench.GlobalVariables.AddRange(tb.GlobalVariables);
            bench.Analyses.AddRange(tb.Analyses);
        }
        var catalog = TunableCatalog.FromNetlist(bench, lib);
        foreach (var d in TuningValidator.Validate(bench, catalog))
        {
            if (d.Severity == DiagnosticSeverity.Error) { Refusal = d; return; }
            _notes.Add(d);
        }

        _goals.AddRange(_setup.Goals.Where(g => g.Enabled));
        if (_goals.Count == 0) { Refusal = OptimizationDiagnostics.NoGoals(); return; }

        Variables = OptimizationVariables.Build(_setup, catalog, out var refusal);
        if (refusal is not null || Variables is null) { Refusal = refusal ?? OptimizationDiagnostics.NoVariables(); return; }
        _notes.AddRange(Variables.Notes);

        AlgorithmId = _settings.Algorithm;
        if (!Available.Contains(AlgorithmId))
        {
            Refusal = OptimizerAlgorithms.Ids.Contains(AlgorithmId)
                ? OptimizationDiagnostics.AlgorithmUnavailable(AlgorithmId, string.Join(", ", Available))
                : TuningDiagnostics.UnknownAlgorithm(AlgorithmId, string.Join(", ", OptimizerAlgorithms.Ids));
            return;
        }
        if (AlgorithmId == OptimizerAlgorithms.Auto)
        {
            // The global stage of Auto (overview D13) is not built; its local polish is.
            AlgorithmId = _settings.Cost == OptimizerCost.Minimax ? NelderMead.AlgorithmId : LevenbergMarquardt.AlgorithmId;
            _notes.Add(OptimizationDiagnostics.AutoRuns(AlgorithmId == NelderMead.AlgorithmId
                ? "Simplex (Nelder–Mead)" : "Gradient (Levenberg–Marquardt)"));
        }
        if (AlgorithmId == LevenbergMarquardt.AlgorithmId && _settings.Cost == OptimizerCost.Minimax)
        {
            Refusal = OptimizationDiagnostics.LeastSquaresOnly();
            return;
        }

        var named = _goals.Select(g => g.Analysis).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        _simulates = named.Count > 0;
        _analyses  = _settings.Scope == OptimizerScope.All ? null : named;

        int cores = Math.Max(1, Environment.ProcessorCount - 1);
        _parallelism = Math.Max(1, _settings.Parallelism ?? cores);
    }

    /// <summary>Prepares a run of <paramref name="circuit"/>'s setup. A run that cannot start carries
    /// its <see cref="Refusal"/>; <see cref="Run"/> then returns it at once.</summary>
    public static OptimizationRun Create(PreparedCircuit circuit, OptimizationOptions? options = null)
        => new(circuit, options ?? new OptimizationOptions());

    public Diagnostic? Refusal { get; }
    public OptimizationVariables? Variables { get; }

    /// <summary>The algorithm that runs (Auto resolved).</summary>
    public string AlgorithmId { get; } = "";

    /// <summary>What the run noted: a key that names nothing, a start moved into range, one point at a
    /// time and why.</summary>
    public IReadOnlyList<Diagnostic> Notes => _notes;

    public long Evaluations => Interlocked.Read(ref _evaluations);
    public long Failures    => Interlocked.Read(ref _failures);
    public long Infeasible  => _infeasible;
    public long CacheHits   => _cacheHits;

    /// <summary>Holds the run after the batch in flight (D16).</summary>
    public void Pause() => _resume.Reset();

    public void Resume() => _resume.Set();

    /// <summary>Ends the run after the batch in flight, keeping the best point.</summary>
    public void Stop() { _stop = true; _resume.Set(); }

    /// <summary>Whether the run is holding at a pause.</summary>
    public bool IsPaused => _held.IsSet;

    /// <summary>Signalled while the run holds at a pause.</summary>
    public WaitHandle Held => _held.WaitHandle;

    /// <summary>Runs to the end on the calling thread.</summary>
    public OptimizationResult Run()
    {
        if (Refusal is not null || Variables is null) return Refused(Refusal ?? OptimizationDiagnostics.NoVariables());

        var ct = _options.Cancellation;
        var sw = Stopwatch.StartNew();
        ulong seed = (ulong)(_settings.Seed ?? 1);
        var options = _settings.Options is null ? null
            : _settings.Options.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        IOptimizerAlgorithm alg = AlgorithmId switch
        {
            LevenbergMarquardt.AlgorithmId => new LevenbergMarquardt(Variables.Start, options),
            BfgsB.AlgorithmId              => new BfgsB(Variables.Start, options),
            NelderMead.AlgorithmId         => new NelderMead(Variables.Start, options),
            _                              => new RandomSearch(Variables.Start, seed, options),
        };

        int maxIter     = _settings.MaxIterations ?? DefaultMaxIterations;
        double? limitS  = _settings.TimeLimit is { } tl ? TuningValidator.TimeLimitSeconds(tl) : null;
        double stallTol = Option(options, "stall_tol", DefaultStallTolerance);
        int stallIters  = (int)Option(options, "stall_iters", DefaultStallIterations);

        string reason;
        bool first = true;
        int lastIteration = 0;
        try
        {
            while (true)
            {
                if (_stop) { reason = "stopped"; break; }
                if (!_resume.IsSet)
                {
                    _held.Set();
                    _resume.Wait(ct);
                    _held.Reset();
                    if (_stop) { reason = "stopped"; break; }
                }
                ct.ThrowIfCancellationRequested();

                var batch = alg.Ask();
                if (batch.Count == 0) { reason = alg.FinishReason ?? "the algorithm finished"; break; }
                int before = _log.Count;
                var results = EvaluateBatch(batch, ct);

                // A goal that cannot be scored at the start point (a complex value, an axis it does not
                // have) will not be scored anywhere: that is a refusal, not 100 failed evaluations.
                if (first)
                {
                    first = false;
                    if (_cache.TryGetValue(_log[before].Decoded.CacheKey, out var start) && start.GoalError)
                        return Refused(start.Error!);
                }

                alg.Tell(results);
                if (alg.Iterations > lastIteration)
                {
                    lastIteration = alg.Iterations;
                    RecordIteration(lastIteration, sw.Elapsed, before);
                }

                if (BestCost() == 0)                                  { reason = "every enabled goal is met"; break; }
                if (alg.IsFinished)                                   { reason = alg.FinishReason ?? "the algorithm finished"; break; }
                if (lastIteration >= maxIter)                         { reason = $"the iteration limit ({maxIter}) was reached"; break; }
                if (_settings.MaxEvaluations is { } me && Evaluations >= me) { reason = $"the evaluation limit ({me}) was reached"; break; }
                if (limitS is { } s && sw.Elapsed.TotalSeconds >= s)  { reason = $"the time limit ({_settings.TimeLimit}) was reached"; break; }
                if (Stalled(stallIters, stallTol))
                {
                    reason = $"the best cost improved by less than {stallTol:G3} of itself over {stallIters} iterations";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Finish(OptimizationOutcome.Cancelled, "cancelled", alg.Iterations, null);
        }

        if (_bestIndex < 0)
            return Finish(OptimizationOutcome.NoConvergence, reason, alg.Iterations,
                          OptimizationDiagnostics.NoneConverged(Evaluations,
                              _firstFailure?.Render() ?? "every point was infeasible"));

        var best = _cache[_log[_bestIndex].Decoded.CacheKey];
        return Finish(best.Scores.All(s => s.Met) ? OptimizationOutcome.GoalsMet : OptimizationOutcome.GoalsUnmet,
                      reason, alg.Iterations, null);
    }

    // ── One batch ────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates one batch: decode, skip what is infeasible, take what the cache holds, simulate the
    /// rest (concurrently when the design allows), then assign penalties in batch order.
    /// </summary>
    internal IReadOnlyList<Evaluation> EvaluateBatch(IReadOnlyList<double[]> batch, CancellationToken ct = default)
    {
        var decoded = batch.Select(Variables!.Decode).ToArray();
        var cached  = new bool[batch.Count];
        var toRun   = new List<(string Key, DecodedPoint Point)>();
        var queued  = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < batch.Count; k++)
        {
            var d = decoded[k];
            if (d.Infeasible) { _infeasible++; continue; }
            if (_cache.ContainsKey(d.CacheKey) || !queued.Add(d.CacheKey)) { _cacheHits++; cached[k] = true; continue; }
            toRun.Add((d.CacheKey, d));
        }

        var outcomes = new (PointOutcome Outcome, DataSet? Data)[toRun.Count];
        int degree = Math.Min(_parallelism, toRun.Count);
        string? serialWhy = _simulates ? _circuit.NotReentrantReason : null;
        if (serialWhy is not null && degree > 1 && !_serialNoted)
        {
            _serialNoted = true;
            _notes.Add(OptimizationDiagnostics.Serial(serialWhy));
        }
        if (serialWhy is not null || degree <= 1)
            for (int i = 0; i < toRun.Count; i++) outcomes[i] = EvaluatePoint(toRun[i].Point, ct);
        else
            Parallel.For(0, toRun.Count, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                         i => outcomes[i] = EvaluatePoint(toRun[i].Point, ct));

        for (int i = 0; i < toRun.Count; i++)
        {
            _cache[toRun[i].Key] = outcomes[i].Outcome;
            if (outcomes[i].Outcome is { Failed: false } ok) _maxFeasible = Math.Max(_maxFeasible, ok.Cost);
            else _firstFailure ??= outcomes[i].Outcome.Error;
        }

        double penalty = 10 * (1 + _maxFeasible);
        var results = new Evaluation[batch.Count];
        for (int k = 0; k < batch.Count; k++)
        {
            var d = decoded[k];
            if (d.Infeasible)
            {
                results[k] = new Evaluation(penalty + d.Distance, null, Failed: true);
                _log.Add(new EvaluationRecord(batch[k], d, results[k].Cost, false, true, false));
                continue;
            }
            var o = _cache[d.CacheKey];
            results[k] = o.Failed ? new Evaluation(penalty, null, Failed: true) : new Evaluation(o.Cost, o.Residuals);
            _log.Add(new EvaluationRecord(batch[k], d, results[k].Cost, o.Failed, false, cached[k]));

            if (!o.Failed && (_bestIndex < 0 || o.Cost < _log[_bestIndex].Cost))
            {
                _bestIndex = _log.Count - 1;
                int run = toRun.FindIndex(t => t.Key == d.CacheKey);
                if (run >= 0) _bestData = outcomes[run].Data;
            }
        }
        return results;
    }

    private (PointOutcome, DataSet?) EvaluatePoint(DecodedPoint point, CancellationToken ct)
    {
        Interlocked.Increment(ref _evaluations);
        try
        {
            var (values, errors, data, failure) = _simulates ? Simulate(point, ct) : VariablesOnly(point);
            if (failure is not null)
            {
                Interlocked.Increment(ref _failures);
                return (new PointOutcome(true, failure, 0, [], [], false), null);
            }

            var scores = _goals.Select((g, i) => GoalResiduals.Score(g, values[i], errors[i])).ToList();
            if (scores.FirstOrDefault(s => s.Error is not null) is { } bad)
            {
                Interlocked.Increment(ref _failures);
                return (new PointOutcome(true, bad.Error, 0, [], scores, GoalError: true), null);
            }
            double[] residuals = [.. scores.SelectMany(s => s.Residuals)];
            double cost = GoalResiduals.Cost(residuals, _settings.Cost);
            return (new PointOutcome(false, null, cost, residuals, scores, false), data);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            return (new PointOutcome(true, OptimizationDiagnostics.EvaluationFailed(ex.Message), 0, [], [], false), null);
        }
    }

    private (Value?[] Values, string?[] Errors, DataSet? Data, Diagnostic? Failure) Simulate(DecodedPoint point, CancellationToken ct)
    {
        var rr = CircuitEvaluation.Evaluate(_circuit, new CircuitEvaluationRequest
        {
            Sets         = _options.Sets,
            Tunables     = point.Values,
            Analyses     = _analyses,
            Measurements = true,
            Expressions  = [.. _goals.Select(g => g.Expression)],
            Control      = new RunControl { Token = ct },
        });
        if (rr.Status == RunStatus.Cancelled) throw new OperationCanceledException(ct);
        if (rr.Status != RunStatus.Success)
            return ([], [], null, OptimizationDiagnostics.EvaluationFailed(rr.StatusMessage));
        if (rr.Convergence.FirstOrDefault(c => c.Converged == false) is { } nc)
            return ([], [], null, OptimizationDiagnostics.EvaluationFailed($"'{nc.Name}' did not converge{(nc.Detail is null ? "" : ": " + nc.Detail)}"));

        var values = new Value?[_goals.Count];
        var errors = new string?[_goals.Count];
        for (int i = 0; i < _goals.Count; i++)
        {
            var o = i < rr.Expressions.Count ? rr.Expressions[i] : null;
            values[i] = o?.Value;
            errors[i] = o is null ? "the run evaluated no expressions" : o.Error;
        }
        return (values, errors, rr.GroupedResults, null);
    }

    /// <summary>Goals that read only variables cost no simulation (D10): they are evaluated in the
    /// bench's own variable scope with the tuned values applied.</summary>
    private (Value?[] Values, string?[] Errors, DataSet? Data, Diagnostic? Failure) VariablesOnly(DecodedPoint point)
    {
        var tuned = TunableOverrides.Apply(_circuit.Tb!, _circuit.Lib!, point.Values,
                                           _options.Sets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal));
        if (tuned.Refusal is { } refusal || tuned.TestBench is not { } tb)
            return ([], [], null, OptimizationDiagnostics.EvaluationFailed(tuned.Refusal ?? ""));
        foreach (var (name, expr) in _options.Sets) HbCircuitRun.ApplySet(tb, name, expr);

        var scope = new Scope("globals");
        foreach (var v in tb.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        var ev = new Evaluator();
        foreach (var fn in tb.Functions) ev.RegisterFunction(fn);

        var values = new Value?[_goals.Count];
        var errors = new string?[_goals.Count];
        for (int i = 0; i < _goals.Count; i++)
        {
            try { values[i] = ev.Eval(_goals[i].Expression, scope); }
            catch (Exception ex) { errors[i] = ex.Message; }
        }
        return (values, errors, null, null);
    }

    // ── Bookkeeping ──────────────────────────────────────────────────────────

    private double BestCost() => _bestIndex < 0 ? double.PositiveInfinity : _log[_bestIndex].Cost;

    private bool Stalled(int k, double tol)
    {
        if (k <= 0 || _iterHistory.Count <= k) return false;
        double then = _iterHistory[^(k + 1)].Best, now = _iterHistory[^1].Best;
        return double.IsFinite(then) && then - now <= tol * Math.Abs(then);
    }

    private IReadOnlyList<GoalReport> BestGoals()
    {
        if (_bestIndex < 0) return [];
        return [.. _cache[_log[_bestIndex].Decoded.CacheKey].Scores
            .Select(s => new GoalReport(s.Name, s.WorstViolation, s.WorstAt, s.Axis, s.WorstValue, s.Met))];
    }

    private void RecordIteration(int iteration, TimeSpan elapsed, int firstOfIteration)
    {
        var goals = BestGoals();
        _iterHistory.Add((BestCost(), [.. _goals.Select(g => goals.FirstOrDefault(r => r.Name == g.Name)?.WorstViolation ?? double.NaN)]));
        if (_options.Progress is not { } progress) return;

        double current = double.PositiveInfinity;
        for (int i = firstOfIteration; i < _log.Count; i++)
            if (!_log[i].Failed && !_log[i].Infeasible) current = Math.Min(current, _log[i].Cost);
        progress(new OptimizationProgress(
            iteration, Evaluations, Failures, _infeasible, _cacheHits, elapsed, current, BestCost(),
            _bestIndex < 0 ? new Dictionary<string, string>() : _log[_bestIndex].Decoded.Values,
            goals, _bestIndex < 0 ? [] : Variables!.Railed(_log[_bestIndex].Point), _bestData));
    }

    private OptimizationResult Refused(Diagnostic why) => new()
    {
        Outcome = OptimizationOutcome.Refused, Refusal = why, FinishReason = why.Render(),
        Algorithm = AlgorithmId, Notes = _notes,
        Evaluations = Evaluations, Failures = Failures, Infeasible = _infeasible, CacheHits = _cacheHits,
    };

    private OptimizationResult Finish(OptimizationOutcome outcome, string reason, int iterations, Diagnostic? refusal)
    {
        bool cancelled = outcome == OptimizationOutcome.Cancelled;
        var bestRecord = _bestIndex >= 0 && !cancelled ? _log[_bestIndex] : null;
        return new OptimizationResult
        {
            Outcome      = outcome,
            FinishReason = reason,
            Refusal      = refusal,
            Algorithm    = AlgorithmId,
            Notes        = _notes,
            BestValues   = bestRecord?.Decoded.Values ?? new Dictionary<string, string>(),
            BestPoint    = bestRecord?.Point,
            BestCost     = bestRecord?.Cost,
            Goals        = cancelled ? [] : BestGoals(),
            Railed       = bestRecord is null ? [] : Variables!.Railed(bestRecord.Point),
            BestData     = cancelled ? null : _bestData,
            Iterations   = iterations,
            Evaluations  = Evaluations,
            Failures     = Failures,
            Infeasible   = _infeasible,
            CacheHits    = _cacheHits,
            Log          = _log,
            History      = cancelled ? null : History(),
        };
    }

    /// <summary>R-to6-10: the run as a <c>DataSet</c> group <c>opt</c>, so a convergence plot is an
    /// ordinary trace. Values are in base SI with their base unit.</summary>
    private DataSet History()
    {
        var ds = new DataSet();
        const string group = "opt";
        int e = _log.Count;
        var evalAxis = new Axis("eval", [.. Enumerable.Range(1, e).Select(i => (double)i)]);
        ds.AddToGroup(group, "cost", new DataCube([evalAxis], [.. _log.Select(r => r.Cost)]));
        ds.AddToGroup(group, "failed", new DataCube([evalAxis], [.. _log.Select(r => r.Failed ? 1.0 : 0.0)]));
        ds.AddToGroup(group, "infeasible", new DataCube([evalAxis], [.. _log.Select(r => r.Infeasible ? 1.0 : 0.0)]));
        ds.AddToGroup(group, "cached", new DataCube([evalAxis], [.. _log.Select(r => r.Cached ? 1.0 : 0.0)]));

        foreach (var key in Variables!.ValueKeys)
        {
            string unit = Variables.UnitOf(key);
            double scale = unit.Length == 0 ? 1 : Units.Scale(unit) ?? 1;
            string baseUnit = unit.Length == 0 ? "" : Units.BaseUnit(unit);
            var q = _log.Select(r => r.Decoded.Quantities[key] * scale).ToArray();
            var cube = Variables.IsComplex(key)
                ? new DataCube([evalAxis], q)
                : new DataCube([evalAxis], [.. q.Select(c => c.Real)]);
            cube.Unit = baseUnit;
            ds.AddToGroup(group, key, cube);
        }

        if (_iterHistory.Count > 0)
        {
            var iterAxis = new Axis("iter", [.. Enumerable.Range(1, _iterHistory.Count).Select(i => (double)i)]);
            ds.AddToGroup(group, "best_cost", new DataCube([iterAxis], [.. _iterHistory.Select(h => h.Best)]));
            for (int g = 0; g < _goals.Count; g++)
                ds.AddToGroup(group, "worst_" + _goals[g].Name, new DataCube([iterAxis], [.. _iterHistory.Select(h => h.Worst[g])]));
        }
        return ds;
    }

    private static double Option(IReadOnlyDictionary<string, string>? options, string name, double fallback)
        => options is not null && options.TryGetValue(name, out var text)
           && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : fallback;
}
