using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// What the <c>.cnl</c> reader says about a <c>tune</c>, <c>preset</c>, <c>goal</c> or <c>optimize</c>
/// line it cannot read (<see cref="TuningDirectiveText"/>).
/// </summary>
public static class TuningDirectiveDiagnostics
{
    public static Diagnostic UnknownKey(int line, string keyword, string key, string known) => Diagnostic.Create(
        "cnl.tuning.unknown-key", DiagnosticSeverity.Warning,
        "line {line}: '{keyword}' has no key '{key}'; it was kept but this version does not use it. Known keys: {known}.",
        ("line", line), ("keyword", keyword), ("key", key), ("known", known));

    public static Diagnostic Malformed(string keyword, string shape) => Diagnostic.Create(
        "cnl.tuning.malformed", DiagnosticSeverity.Error,
        "'{keyword}' is not written the way it reads: {shape}",
        ("keyword", keyword), ("shape", shape));

    public static Diagnostic KeyMalformed(string key) => Diagnostic.Create(
        "cnl.tuning.key-malformed", DiagnosticSeverity.Error,
        "'{key}' is not a tunable key. Spell it Instance.Parameter, Variable, or Cell:… of either.",
        ("key", key));

    public static Diagnostic NotKeyValue(string token) => Diagnostic.Create(
        "cnl.tuning.not-key-value", DiagnosticSeverity.Error,
        "'{token}' is not key=value.", ("token", token));

    public static Diagnostic ValueInvalid(string key, string value, string expected) => Diagnostic.Create(
        "cnl.tuning.value-invalid", DiagnosticSeverity.Error,
        "{key}={value} is not {expected}.", ("key", key), ("value", value), ("expected", expected));

    public static Diagnostic GoalProblem(string goal, string problem) => Diagnostic.Create(
        "cnl.tuning.goal-malformed", DiagnosticSeverity.Error,
        "goal {goal}: {problem}", ("goal", goal), ("problem", problem));

    public static Diagnostic SecondOptimize() => new(
        "cnl.tuning.optimize-repeated", DiagnosticSeverity.Error,
        "A netlist states at most one 'optimize' line.");
}

/// <summary>A tuning directive the reader refuses, carrying the diagnostic that says why. The
/// <c>.cnl</c> reader turns it into a <see cref="CnlReadException"/> naming the line.</summary>
public sealed class TuningDirectiveException(Diagnostic diagnostic) : FormatException(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}
