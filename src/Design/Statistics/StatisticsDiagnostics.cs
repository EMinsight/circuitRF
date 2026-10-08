using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// What <see cref="StatisticsValidator"/> says about a setup's statistical part (docs/design/yield.md) —
/// <c>check</c> reports these, and a Monte Carlo or yield run refuses on the errors among them.
/// <c>{who}</c> names the line: <c>tune R1.R</c>, <c>correlate R1.R R2.R</c>, <c>statistics</c>,
/// <c>corner SS_hot</c>, <c>goal G1</c>.
/// </summary>
public static class StatisticsDiagnostics
{
    public static Diagnostic NotTunable(string who) => Diagnostic.Create(
        "yield.dist.not-tunable", DiagnosticSeverity.Error,
        "{who}: a distribution needs a value this design offers for tuning, and this key names none.", ("who", who));

    public static Diagnostic SpreadMissing(string who, string dist, string needs) => Diagnostic.Create(
        "yield.spread.missing", DiagnosticSeverity.Error,
        "{who}: dist={dist} needs {needs}.", ("who", who), ("dist", dist), ("needs", needs));

    public static Diagnostic SpreadExtra(string who, string dist, string key) => Diagnostic.Create(
        "yield.spread.extra", DiagnosticSeverity.Error,
        "{who}: {key}= is not a spread of dist={dist}.", ("who", who), ("key", key), ("dist", dist));

    public static Diagnostic SpreadNotANumber(string who, string key, string text) => Diagnostic.Create(
        "yield.spread.not-a-number", DiagnosticSeverity.Error,
        "{who}: {key}={text} is not a number, a percent or a value with its unit.", ("who", who), ("key", key), ("text", text));

    public static Diagnostic SpreadNotPositive(string who, string key, string text) => Diagnostic.Create(
        "yield.spread.not-positive", DiagnosticSeverity.Error,
        "{who}: {key}={text} must be above zero.", ("who", who), ("key", key), ("text", text));

    public static Diagnostic SpreadInverted(string who, string lo, string hi) => Diagnostic.Create(
        "yield.spread.inverted", DiagnosticSeverity.Error,
        "{who}: lo={lo} is not below hi={hi}.", ("who", who), ("lo", lo), ("hi", hi));

    public static Diagnostic StepNotDividing(string who, string by, string lo, string hi) => Diagnostic.Create(
        "yield.spread.step", DiagnosticSeverity.Error,
        "{who}: by={by} does not step from lo={lo} to hi={hi} a whole number of times.",
        ("who", who), ("by", by), ("lo", lo), ("hi", hi));

    public static Diagnostic LogNormNotPositive(string who, string value) => Diagnostic.Create(
        "yield.dist.lognorm-nonpositive", DiagnosticSeverity.Error,
        "{who}: dist=lognorm needs a value above zero, and the value is {value}.", ("who", who), ("value", value));

    public static Diagnostic ContinuousOnInteger(string who, string dist) => Diagnostic.Create(
        "yield.dist.integer", DiagnosticSeverity.Error,
        "{who}: the value takes whole numbers only, so dist={dist} cannot draw it; use discrete or unif.",
        ("who", who), ("dist", dist));

    public static Diagnostic MixedParts(string whole, string parts) => Diagnostic.Create(
        "yield.complex.mixed", DiagnosticSeverity.Error,
        "{whole}: tolerances on {parts} mix a rectangular and a polar part; a draw of the two describes no complex " +
        "number. Toleranced parts are real with imag, or mag with phase.",
        ("whole", whole), ("parts", parts));

    public static Diagnostic TooManyParts(string whole, string parts) => Diagnostic.Create(
        "yield.complex.too-many", DiagnosticSeverity.Error,
        "{whole}: tolerances on {parts}; at most two parts of one value carry one.", ("whole", whole), ("parts", parts));

    public static Diagnostic CorrelateNotStatistical(string who, string key) => Diagnostic.Create(
        "yield.correlate.not-stat", DiagnosticSeverity.Error,
        "{who}: {key} is not a statistical entry (a tune line with dist= and stat on).", ("who", who), ("key", key));

    public static Diagnostic CorrelateSameKey(string who) => Diagnostic.Create(
        "yield.correlate.same-key", DiagnosticSeverity.Error,
        "{who}: correlates a key with itself.", ("who", who));

    public static Diagnostic RhoOutOfRange(string who, double rho) => Diagnostic.Create(
        "yield.correlate.rho", DiagnosticSeverity.Error,
        "{who}: rho={rho} is not strictly between -1 and 1.", ("who", who), ("rho", rho));

    public static Diagnostic LhsWithAutoStop() => new(
        "yield.statistics.lhs-autostop", DiagnosticSeverity.Error,
        "statistics: sampling=lhs places every trial up front, so it cannot stop early; use sampling=sobol or random with autostop=1.");

    public static Diagnostic AutoStopNeedsTarget() => new(
        "yield.statistics.autostop-target", DiagnosticSeverity.Error,
        "statistics: autostop=1 stops when the yield is confidently above or below target=, and no target is given.");

    public static Diagnostic CornerUnknownKey(string who, string key) => Diagnostic.Create(
        "yield.corner.unknown-key", DiagnosticSeverity.Error,
        "{who}: {key} is neither a variable of this design nor a tunable key it offers.", ("who", who), ("key", key));

    public static Diagnostic CornerTempNotANumber(string who, string temp) => Diagnostic.Create(
        "yield.corner.temp", DiagnosticSeverity.Error,
        "{who}: temp={temp} is not a number of degC.", ("who", who), ("temp", temp));

    public static Diagnostic CornerTrialIncomplete(string who, string missing) => Diagnostic.Create(
        "yield.corner.trial-incomplete", DiagnosticSeverity.Error,
        "{who}: a statistical corner names its run too; {missing} is missing.", ("who", who), ("missing", missing));

    public static Diagnostic NonPhysical(string who, double probability) => Diagnostic.Create(
        "yield.dist.nonphysical", DiagnosticSeverity.Warning,
        "{who}: a draw at or below zero has probability {probability}; truncate it (trunc=) or use dist=lognorm.",
        ("who", who), ("probability", probability.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)));

    public static Diagnostic DistributionOff(string who) => Diagnostic.Create(
        "yield.dist.off", DiagnosticSeverity.Warning,
        "{who}: stat=0 keeps the distribution but does not draw it.", ("who", who));

    public static Diagnostic NothingVaries(string goal) => Diagnostic.Create(
        "yield.goal.nothing-varies", DiagnosticSeverity.Warning,
        "goal {goal}: use=yield, and no entry is statistical and no kit statistics are selected, so every trial is the nominal.",
        ("goal", goal));

    public static Diagnostic CorrelationRepaired(double largestChange) => Diagnostic.Create(
        "yield.correlate.repair", DiagnosticSeverity.Warning,
        "The correlations are not a valid correlation matrix; the nearest one is used, which changes an entry by up to {change}.",
        ("change", largestChange.ToString("G3", System.Globalization.CultureInfo.InvariantCulture)));

    // ── YA-2: sampling and trial values ─────────────────────────────────────

    public static Diagnostic SobolPastTable(int dimensions, int rest) => Diagnostic.Create(
        "yield.sampling.sobol-dimensions", DiagnosticSeverity.Warning,
        "sampling=sobol has {dimensions} dimensions; the other {rest} statistical variable(s) are drawn at random.",
        ("dimensions", dimensions), ("rest", rest));

    public static Diagnostic TrialNonPhysical(string key, string value) => Diagnostic.Create(
        "yield.trial.nonphysical", DiagnosticSeverity.Error,
        "{key} draws {value}, which is not physical; the trial does not evaluate.", ("key", key), ("value", value));

    public static Diagnostic TrialNoNominal(string key) => Diagnostic.Create(
        "yield.trial.no-nominal", DiagnosticSeverity.Error,
        "{key} names no value of this design; the trial does not evaluate.", ("key", key));

    public static Diagnostic TrialNoDraw(string key) => Diagnostic.Create(
        "yield.trial.no-draw", DiagnosticSeverity.Error,
        "{key} has no draw in this trial's sample; the trial does not evaluate.", ("key", key));

    public static Diagnostic TrialNoDistribution(string key) => Diagnostic.Create(
        "yield.trial.no-distribution", DiagnosticSeverity.Error,
        "{key}: its spread does not resolve to a distribution at this nominal; the trial does not evaluate.", ("key", key));

    public static Diagnostic TrialNoComplexValue(string key) => Diagnostic.Create(
        "yield.trial.complex", DiagnosticSeverity.Error,
        "{key}: no complex value has the drawn parts; the trial does not evaluate.", ("key", key));

    // ── YA-3: kit and expression statistics ─────────────────────────────────

    public static Diagnostic RunNothingVaries() => Diagnostic.Create(
        "yield.run.nothing-varies", DiagnosticSeverity.Error,
        "Nothing varies: no tune entry has a tolerance (dist=) and the design holds no distribution call, so every trial would be the nominal.");

    public static Diagnostic StatisticalSectionNotSelected(string axis, string sections) => Diagnostic.Create(
        "yield.kit.section-not-selected", DiagnosticSeverity.Info,
        "Kit corner {axis}: statistical {sections} not selected, so its models do not vary.",
        ("axis", axis), ("sections", sections));

    public static Diagnostic TrialDrawFailed(string problem) => Diagnostic.Create(
        "yield.trial.draw-failed", DiagnosticSeverity.Error,
        "A distribution could not be drawn ({problem}); the trial does not evaluate.", ("problem", problem));

    // ── YA-4: the run ──────────────────────────────────────────────────────

    public static Diagnostic RunNoYieldGoal() => Diagnostic.Create(
        "yield.run.no-yield-goal", DiagnosticSeverity.Error,
        "A yield run needs an enabled goal with use=yield or use=both to pass or fail each trial against; run a Monte Carlo to see the spread alone.");

    public static Diagnostic RunNominalFailed(string reason) => Diagnostic.Create(
        "yield.run.nominal-failed", DiagnosticSeverity.Error,
        "The nominal design does not evaluate ({reason}), so no trial would mean anything.", ("reason", reason));

    public static Diagnostic RunNoneEvaluated(int trials, string first) => Diagnostic.Create(
        "yield.run.none-evaluated", DiagnosticSeverity.Error,
        "None of the {trials} trials evaluated; the first: {first}.", ("trials", trials), ("first", first));

    public static Diagnostic RunDidNotEvaluate(int count, int trials, bool counted) => Diagnostic.Create(
        "yield.run.did-not-evaluate", DiagnosticSeverity.Warning,
        counted ? "{count} of {trials} trials did not evaluate — counted as fails."
                : "{count} of {trials} trials did not evaluate — excluded from the yield (nonconverged=warn).",
        ("count", count), ("trials", trials));

    public static Diagnostic RunSaved(string sentence) => Diagnostic.Create(
        "yield.run.saved", DiagnosticSeverity.Info, "{sentence}", ("sentence", sentence));

    public static Diagnostic RunWriteFailed(string path, string reason) => Diagnostic.Create(
        "yield.run.write-failed", DiagnosticSeverity.Warning,
        "The results could not be written to {path}: {reason}", ("path", path), ("reason", reason));

    public static Diagnostic TrialOutOfRange(int trial, string why) => Diagnostic.Create(
        "yield.trial.out-of-range", DiagnosticSeverity.Error,
        "There is no trial {trial}: {why}", ("trial", trial), ("why", why));

    public static Diagnostic ContributionUnknown(string name, string known) => Diagnostic.Create(
        "yield.contrib.unknown", DiagnosticSeverity.Error,
        "'{name}' is neither a goal the run scored nor a scalar measurement; it scored: {known}.", ("name", name), ("known", known));

    public static Diagnostic ContributionTooFew(string name, int trials) => Diagnostic.Create(
        "yield.contrib.too-few", DiagnosticSeverity.Error,
        "'{name}' has a value in {trials} trial(s); contributions need at least three.", ("name", name), ("trials", trials));

    public static Diagnostic ContributionNothingVaries() => Diagnostic.Create(
        "yield.contrib.nothing-varies", DiagnosticSeverity.Error,
        "No statistical variable was drawn in the run, so nothing can contribute.");

    // ── YA-9: a trial re-run from the Data Display ─────────────────────────

    public static Diagnostic ReplayNoDesign(string result) => Diagnostic.Create(
        "yield.replay.no-design", DiagnosticSeverity.Error,
        "{result} has no design beside it to run the trial again from — a result is re-run from the schematic or netlist of the same name.",
        ("result", result));

    public static Diagnostic ReplayNoData(int trial) => Diagnostic.Create(
        "yield.replay.no-data", DiagnosticSeverity.Error,
        "Trial {trial} ran but produced no analysis result to show.", ("trial", trial));

    // ── YA-6: corners ──────────────────────────────────────────────────────

    public static Diagnostic ReplayStreamsGone(int trial, IReadOnlyList<string> streams) => Diagnostic.Create(
        "yield.corner.streams-gone", DiagnosticSeverity.Warning,
        "Trial {trial} drew {streams}, which this design no longer has (renamed or removed); the corner is replayed without.",
        ("trial", trial), ("streams", string.Join(", ", streams)));

    public static Diagnostic ReplayNotRecorded(string corner) => Diagnostic.Create(
        "yield.corner.not-recorded", DiagnosticSeverity.Info,
        "corner {corner}: the run it names is not at hand, so its trial is drawn afresh from its seed — the same draws for every stream the run had, but a renamed or added variable cannot be told apart.",
        ("corner", corner));

    public static Diagnostic CornerUnknown(string name, string known) => Diagnostic.Create(
        "yield.corner.unknown", DiagnosticSeverity.Error,
        "There is no enabled corner '{name}'; the enabled corners are: {known}.", ("name", name), ("known", known));

    public static Diagnostic CornerNone() => Diagnostic.Create(
        "yield.corner.none", DiagnosticSeverity.Error,
        "The design has no enabled corner to run; add a corner line (yield corners --generate writes some).");

    public static Diagnostic CornerSetConflict(string corner, string name) => Diagnostic.Create(
        "yield.corner.set-conflict", DiagnosticSeverity.Error,
        "'{name}' is set both by --set and by corner {corner}; remove one of the two.", ("corner", corner), ("name", name));

    public static Diagnostic CornerProcessDoubleCounted() => Diagnostic.Create(
        "yield.corner.process-double-counted", DiagnosticSeverity.Warning,
        "statistics: corners= with process=1 — a process corner and a process draw answer one question, so a Monte Carlo at each corner draws mismatch only and leaves process at the corner.");

    public static Diagnostic CornerNominalFailed(string reason) => Diagnostic.Create(
        "yield.corner.nominal-failed", DiagnosticSeverity.Error,
        "No corner evaluated, the nominal included; the nominal: {reason}.", ("reason", reason));

    public static Diagnostic GeneratorTooMany(int count, int cap) => Diagnostic.Create(
        "yield.corner.generate-too-many", DiagnosticSeverity.Error,
        "That cross product is {count} corners; at most {cap} are generated. Fewer options, temperatures or values.",
        ("count", count), ("cap", cap));

    public static Diagnostic GeneratorMalformed(string part, string why) => Diagnostic.Create(
        "yield.corner.generate-malformed", DiagnosticSeverity.Error,
        "'{part}': {why}", ("part", part), ("why", why));

    public static Diagnostic CornerStatisticalNoMonteCarlo(string corner) => Diagnostic.Create(
        "yield.corner.statistical-no-mc", DiagnosticSeverity.Info,
        "corner {corner} is one trial of a run, so it has no Monte Carlo of its own; it is left out of this one.",
        ("corner", corner));

    // ── YA-7: tune and optimize across corners ─────────────────────────────

    public static Diagnostic CornerReplayRefused(string corner, string reason) => Diagnostic.Create(
        "yield.corner.replay-refused", DiagnosticSeverity.Error,
        "corner {corner} cannot be replayed: {reason}", ("corner", corner), ("reason", reason));

    public static Diagnostic CornerPointFailed(string corner, string reason) => Diagnostic.Create(
        "yield.corner.point-failed", DiagnosticSeverity.Error,
        "at corner {corner}: {reason}", ("corner", corner), ("reason", reason));

    // ── YA-11: design centering ────────────────────────────────────────────

    public static Diagnostic CenterNoDesignable() => Diagnostic.Create(
        "yield.center.no-designable", DiagnosticSeverity.Error,
        "Centering moves the nominals of the opt=1 entries, and this setup has none: add opt=1 and a range to the tune line of each value to centre.");

    public static Diagnostic CenterAlgorithm(string id, string suited) => Diagnostic.Create(
        "yield.center.algorithm", DiagnosticSeverity.Error,
        "center algorithm={id} is not one centering offers: a yield over a fixed set of trials is flat between them, " +
        "so it takes one of the methods that need no derivative — {suited}.", ("id", id), ("suited", suited));

    public static Diagnostic CenterNoneEvaluated(long evaluations, string first) => Diagnostic.Create(
        "yield.center.none-evaluated", DiagnosticSeverity.Error,
        "No candidate evaluated a single trial in {evaluations} simulations; the first: {first}.",
        ("evaluations", evaluations), ("first", first));

    public static Diagnostic CenterVerifyFailed(string point, string reason) => Diagnostic.Create(
        "yield.center.verify-failed", DiagnosticSeverity.Warning,
        "The verification of the {point} point did not run: {reason}", ("point", point), ("reason", reason));
}
