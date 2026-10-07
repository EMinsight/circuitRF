using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// Outcome of a <see cref="CircuitEvaluation"/> run.
/// </summary>
public enum RunStatus { Success, NoAnalysis, EngineError, Cancelled }

/// <summary>
/// A named DataSet produced by one analysis in a run.
/// </summary>
public sealed record AnalysisResult(string Name, DataSet Data);

/// <summary>
/// Whether one dispatched analysis converged. <paramref name="Converged"/> is true for a linear
/// analysis, the solver's own verdict for a single DC or HB solve, false for an analysis that failed,
/// and null where convergence is a per-point fact carried in the data (a sweep, a loadpull grid).
/// </summary>
public sealed record AnalysisConvergence(string Name, bool? Converged, string? Detail = null);

/// <summary>
/// Result returned by <see cref="CircuitEvaluation.Execute"/>: status, message, and the collected
/// per-analysis results.
/// </summary>
public sealed class RunResult(
    RunStatus                        status,
    string                           statusMessage,
    IReadOnlyList<AnalysisResult>?   results  = null,
    IReadOnlyList<string>?           warnings = null,
    DataSet?                         grouped  = null,
    IReadOnlyList<string>?           notes    = null,
    IReadOnlyList<AnalysisConvergence>? convergence = null,
    IReadOnlyList<ExpressionOutcome>?   expressions = null)
{
    public RunStatus                       Status        { get; } = status;
    public string                          StatusMessage { get; } = statusMessage;
    public IReadOnlyList<AnalysisResult>   Results       { get; } = results ?? [];

    /// <summary>
    /// Elaboration and engine run-time warnings drained from
    /// <see cref="ElaboratedNetlist.Warnings"/>.
    /// Non-empty even on <see cref="RunStatus.EngineError"/> when the run partially succeeded.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; } = warnings ?? [];

    /// <summary>
    /// What the run WORKED OUT and is reporting — drained from
    /// <see cref="ElaboratedNetlist.Notes"/>. Rendered at Info, because a
    /// resolution is not a complaint: a run that resolved everything correctly must not read as a
    /// run with problems, or the warnings that do need attention are harder to pick out for it.
    /// </summary>
    public IReadOnlyList<string> Notes { get; } = notes ?? [];

    /// <summary>
    /// One grouped DataSet for the whole run (one group per analysis + "measurements" group).
    /// Null when no analyses produced results.
    /// </summary>
    public DataSet? GroupedResults { get; } = grouped;

    /// <summary>One entry per dispatched analysis, in run order.</summary>
    public IReadOnlyList<AnalysisConvergence> Convergence { get; } = convergence ?? [];

    /// <summary>The request's extra expressions, evaluated in the measurement scope, in the order
    /// asked. Empty when none were asked for, or when the run produced no results to evaluate
    /// them against.</summary>
    public IReadOnlyList<ExpressionOutcome> Expressions { get; } = expressions ?? [];

    /// <summary>This result with <paramref name="leading"/> ahead of its own notes.</summary>
    internal RunResult WithLeadingNotes(IReadOnlyList<string> leading)
        => new(Status, StatusMessage, Results, Warnings, GroupedResults, [.. leading, .. Notes],
               Convergence, Expressions);

    // Convenience: callers that only need the DataSets (unchanged from Phase 6e).
    public IReadOnlyList<DataSet> DataSets => Results.Select(r => r.Data).ToList();
}

/// <summary>
/// One analysis the run is going to dispatch, worked out by <see cref="CircuitEvaluation.Plan"/>
/// before anything runs.
/// <para/>
/// <see cref="SelfTicks"/> distinguishes an engine that reports its OWN progress (a sweep per point,
/// an s-parameter per frequency, a loadpull per grid termination) from one that does not (a single HB
/// or DC solve, a pursuit search whose query count is decided by the search). The executor ticks
/// <see cref="WorkUnits"/> itself for the latter, so a run's total is reached either way.
/// </summary>
internal sealed record PlannedAnalysis(
    Analysis? Typed,
    string?   RawLine,
    string    ResultName,
    long      WorkUnits,
    bool      SelfTicks);

/// <summary>
/// What a run WILL do, worked out before any of it runs: the netlist read, elaborated once, and every
/// analysis that is going to be dispatched described in run order.
/// <para/>
/// <b>This exists so the user can read the plan and stop a wrong one before paying for it.</b> A
/// nested sweep can be tens of thousands of points and tens of minutes; reporting "11 pt(s) over VGS x
/// 101 pt(s) over VDS = 1,111 total pt(s)" only after those points have all been simulated is a
/// receipt, not a decision the user can act on.
/// <para/>
/// A failed plan (unreadable netlist, failed elaboration, nothing to run) carries its own status and
/// message; <see cref="CircuitEvaluation.Execute"/> passes it straight through, so a caller reports
/// exactly one failure whichever half produced it.
/// </summary>
public sealed class RunPlan
{
    public RunStatus Status        { get; }
    public string    StatusMessage { get; }

    /// <summary>One line per analysis that will run, in run order — the pre-flight description.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>
    /// What the PLAN worked out and is reporting, as opposed to what it will run: the sentence a
    /// narrowed run writes when the named card was promoted to the sweep wrapping it, and one line per
    /// tunable key that resolved to nothing. Separate from <see cref="Lines"/> because that list is
    /// one entry per dispatched analysis and a caller counts it.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Total leaf work units across every planned analysis. 0 = nothing countable.</summary>
    public long TotalWorkUnits { get; }

    internal Library?          Lib           { get; init; }
    internal TestBench?        Tb            { get; init; }
    internal ElaboratedNetlist? Nl           { get; init; }
    internal string?           BaseDirectory { get; init; }
    internal IReadOnlyList<PlannedAnalysis> Analyses { get; init; } = [];

    internal RunPlan(RunStatus status, string message,
                     IReadOnlyList<string>? lines = null, long totalWorkUnits = 0,
                     IReadOnlyList<string>? notes = null)
    {
        Status         = status;
        StatusMessage  = message;
        Lines          = lines ?? [];
        Notes          = notes ?? [];
        TotalWorkUnits = totalWorkUnits;
    }
}
