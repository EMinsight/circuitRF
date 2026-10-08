namespace CircuitRF.Core.Design;

/// <summary>
/// The single thing you simulate: its own instance list (cell instances AND bare primitives,
/// including Port/Term), global variables, analyses, and measurements.
///
/// A TestBench IS the top-level container — it holds instances directly, exactly like a
/// top-level schematic. It does NOT point at a single TopCell; that artificial wrapper is gone.
/// Nothing ever instantiates a TestBench from above, so it has no Ports list.
/// Top-level port-ness comes from Port/Term primitives in the Instances list.
///
/// Analyses and measurements attach HERE, never to a Cell (data-model §2.1 invariant).
/// </summary>
public sealed class TestBench(string name)
{
    public string Name { get; } = name;

    /// <summary>Top-level contents: cell instances and bare primitives (incl. Port/Term).</summary>
    public List<Instance>     Instances       { get; } = [];
    public List<Variable>     GlobalVariables { get; } = [];

    /// <summary>
    /// The netlist's own technology — the <c>technology "&lt;path&gt;"</c> statement, a path relative to the
    /// <c>.cnl</c> (or absolute). Null when the netlist states none, which is every netlist written before it
    /// existed: its microstrip, via and planar lines then take the workspace default, as they always did.
    /// Nothing in a run reads it; the reader of a stackup-bound line does (brief-artsch-6 R-as6-1, D20).
    /// </summary>
    public string? Technology { get; set; }

    /// <summary>
    /// User-defined expression functions declared at netlist top level (`name(a, b) = expr`).
    /// The expression engine has supported these since v1; this is where a .cnl declares them.
    /// </summary>
    public List<CircuitRF.Core.Expressions.UserFunction> Functions { get; } = [];
    public List<Analysis>     Analyses        { get; } = [];
    public List<Measurement>  Measurements    { get; } = [];

    /// <summary>
    /// The tunable entries, presets, goals and optimizer settings — the <c>tune</c>, <c>preset</c>,
    /// <c>goal</c> and <c>optimize</c> directives. Null when the netlist states none, which is every
    /// netlist written before they existed. Nothing in a run reads it; it rides along so a
    /// <c>.csch</c> and the <c>.cnl</c> it extracts to say the same thing.
    /// </summary>
    public TuningSetup? Tuning { get; set; }

    /// <summary>
    /// Verbatim analysis/measure lines from .cnl that the reader cannot yet interpret.
    /// Preserved for round-trip fidelity. Replaced by typed entries once the directive
    /// grammar is settled in Phase 2.
    /// Kind = "analysis" or "measure"; RawLine = verbatim remainder after the keyword.
    /// </summary>
    public List<RawDirective> RawDirectives { get; } = [];

    /// <summary>
    /// What READING the netlist decided that its author should hear, before any of it is simulated:
    /// a line the reader did not recognise and skipped, a substrate a microstrip line took from the
    /// workspace technology. The elaborator forwards both lists into the run's own
    /// <c>Warnings</c> and <c>Notes</c>, so everything that reports those (the Messages pane,
    /// <c>check</c>, a run verb) reports these too. A skipped line used to vanish without a word,
    /// so a misspelled statement simply did not happen.
    /// </summary>
    public List<string> ReadWarnings { get; } = [];

    /// <inheritdoc cref="ReadWarnings"/>
    public List<string> ReadNotes    { get; } = [];

    /// <summary>
    /// Net names that came from a user-placed net label in the schematic (provenance set).
    /// Populated by NetExtractor; empty for hand-written netlists. Propagated to
    /// NodeMap.LabeledNames by the Elaborator and persisted in the __LabeledNodes DataCube.
    /// </summary>
    public HashSet<string> LabeledNets { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The draws of one Monte Carlo trial (docs/design/yield.md §8), or null for an ordinary run. A RUN-TIME
    /// attachment, never written: it rides on the bench rather than on one <c>Elaborator</c> because an engine
    /// re-elaborates the same bench — a parametric sweep per point, a parallel S-parameter run per worker — and
    /// every one of those elaborations must draw the trial's values, not the nominal.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Expressions.IStatisticalDraws? StatisticalDraws { get; set; }
}

/// <summary>
/// Opaque round-trippable record for a .cnl directive whose grammar is deferred.
/// </summary>
public sealed class RawDirective(string kind, string rawLine)
{
    public string Kind    { get; } = kind;    // "analysis" or "measure"
    public string RawLine { get; } = rawLine; // verbatim remainder of the line
}
