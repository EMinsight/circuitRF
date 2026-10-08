using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// What an optimization run says about itself — its refusals (exit 1), the notes it makes once, and
/// the errors a goal can raise. RETURNED, never posted: the Optimizer window, the CLI and MCP decide
/// where each one goes.
/// </summary>
public static class OptimizationDiagnostics
{
    public static Diagnostic NoGoals() => Diagnostic.Create(
        "opt.nothing.goals", DiagnosticSeverity.Error,
        "Nothing to optimize: the design has no enabled goal.");

    public static Diagnostic NoVariables() => Diagnostic.Create(
        "opt.nothing.variables", DiagnosticSeverity.Error,
        "Nothing to optimize: no value is enabled for optimizing (opt=1 on a tune line).");

    public static Diagnostic VariableSwept(string key, string why) => Diagnostic.Create(
        "opt.variable.swept", DiagnosticSeverity.Error,
        "{key} cannot be optimized while it is {why}; disable that sweep or the value's opt flag.",
        ("key", key), ("why", why));

    /// <summary>overview D18: a complex value has two degrees of freedom.</summary>
    public static Diagnostic TooManyComplexParts(string parts, string count, string whole) => Diagnostic.Create(
        "opt.complex.too-many-parts", DiagnosticSeverity.Error,
        "{parts} are {count} parts of {whole}, which has two degrees of freedom; optimize at most two — the others' ranges still limit it.",
        ("parts", parts), ("count", count), ("whole", whole));

    public static Diagnostic ComplexNoStart(string whole, string held) => Diagnostic.Create(
        "opt.complex.no-start", DiagnosticSeverity.Error,
        "{whole}: no value inside every range of its parts can be reached by moving {held}; widen a range or optimize another part.",
        ("whole", whole), ("held", held));

    public static Diagnostic AlgorithmUnavailable(string algorithm, string available) => Diagnostic.Create(
        "opt.algorithm.unavailable", DiagnosticSeverity.Error,
        "algorithm={algorithm} is not available; choose one of {available}.",
        ("algorithm", algorithm), ("available", available));

    public static Diagnostic LeastSquaresOnly() => Diagnostic.Create(
        "opt.algorithm.lsq-only", DiagnosticSeverity.Error,
        "Gradient (Levenberg–Marquardt) minimizes the sum of squared violations and cannot use cost=minimax; choose Minimax or Auto, or set cost=lsq.");

    public static Diagnostic AutoRuns(string algorithm) => Diagnostic.Create(
        "opt.algorithm.auto", DiagnosticSeverity.Info,
        "Auto runs {algorithm}.", ("algorithm", algorithm));

    public static Diagnostic StartMoved(string key, string value, string min, string max, string start) => Diagnostic.Create(
        "opt.start.outside", DiagnosticSeverity.Warning,
        "{key} = {value} is outside its range {min} .. {max}; the run starts at {start}.",
        ("key", key), ("value", value), ("min", min), ("max", max), ("start", start));

    public static Diagnostic ComplexStartMoved(string whole, string value, string start) => Diagnostic.Create(
        "opt.start.complex-outside", DiagnosticSeverity.Warning,
        "{whole} = {value} is outside the ranges of its parts; the run starts at {start}, the nearest value inside them.",
        ("whole", whole), ("value", value), ("start", start));

    public static Diagnostic RangeAssumed(string key, string min, string max) => Diagnostic.Create(
        "opt.range.assumed", DiagnosticSeverity.Info,
        "{key} states no range; the run uses {min} .. {max}.", ("key", key), ("min", min), ("max", max));

    public static Diagnostic PreferredContinuous(string key) => Diagnostic.Create(
        "opt.discrete.preferred", DiagnosticSeverity.Info,
        "{key}: discrete=preferred is optimized continuously; the Discrete algorithm snaps to preferred values.",
        ("key", key));

    public static Diagnostic Serial(string reason) => Diagnostic.Create(
        "opt.parallel.serial", DiagnosticSeverity.Info,
        "Evaluating one point at a time: {reason}", ("reason", reason));

    public static Diagnostic NoneConverged(long count, string first) => Diagnostic.Create(
        "opt.none-converged", DiagnosticSeverity.Error,
        "No evaluation converged ({count} tried). The first failure: {first}",
        ("count", count), ("first", first));

    // ── Goals (R-to6-2) ─────────────────────────────────────────────────────

    public static Diagnostic GoalComplex(string goal) => Diagnostic.Create(
        "opt.goal.complex", DiagnosticSeverity.Error,
        "goal {goal}: the value is complex, and a goal compares real numbers. Write dB(…), mag(…), phase(…) or real(…) of it.",
        ("goal", goal));

    public static Diagnostic GoalNotANumber(string goal) => Diagnostic.Create(
        "opt.goal.kind", DiagnosticSeverity.Error,
        "goal {goal}: the value is not a number.", ("goal", goal));

    public static Diagnostic GoalNoAxis(string goal, string axis, string axes) => Diagnostic.Create(
        "opt.goal.no-axis", DiagnosticSeverity.Error,
        "goal {goal}: the value has no '{axis}' axis. Its axes are: {axes}.",
        ("goal", goal), ("axis", axis), ("axes", axes));

    public static Diagnostic GoalScalarRange(string goal, string axis) => Diagnostic.Create(
        "opt.goal.scalar-range", DiagnosticSeverity.Error,
        "goal {goal}: the value is a single number, so it has no '{axis}' axis to take a range over.",
        ("goal", goal), ("axis", axis));

    public static Diagnostic GoalRangeEmpty(string goal, string axis, double lo, double hi, double min, double max) => Diagnostic.Create(
        "opt.goal.range-empty", DiagnosticSeverity.Error,
        "goal {goal}: no '{axis}' point lies in [{lo}, {hi}]; the axis runs from {min} to {max}.",
        ("goal", goal), ("axis", axis), ("lo", lo), ("hi", hi), ("min", min), ("max", max));

    public static Diagnostic GoalLimitNotANumber(string goal, string text) => Diagnostic.Create(
        "opt.goal.limit", DiagnosticSeverity.Error,
        "goal {goal}: the limit '{text}' is not a number.", ("goal", goal), ("text", text));

    public static Diagnostic GoalNonFinite(string goal) => Diagnostic.Create(
        "opt.goal.non-finite", DiagnosticSeverity.Error,
        "goal {goal}: the value is not finite at some point of its range.", ("goal", goal));

    public static Diagnostic GoalFailed(string goal, string message) => Diagnostic.Create(
        "opt.goal.failed", DiagnosticSeverity.Error,
        "goal {goal}: {message}", ("goal", goal), ("message", message));

    public static Diagnostic EvaluationFailed(string message) => Diagnostic.Create(
        "opt.evaluation.failed", DiagnosticSeverity.Error, "{message}", ("message", message));
}
