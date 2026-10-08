namespace CircuitRF.Core.Netlist;

/// <summary>
/// One of the four tuning/optimization directives a TestBench may carry — <c>tune</c>, <c>preset</c>,
/// <c>goal</c>, <c>optimize</c> — with every key it takes and the bare words its grammar uses.
/// </summary>
/// <param name="Keyword">The line's first word.</param>
/// <param name="Topic">The reference topic that prints it: <c>tuning</c> or <c>goals</c>.</param>
/// <param name="Syntax">The line's shape, for the reference page.</param>
/// <param name="Summary">One paragraph: what the line says.</param>
/// <param name="Keys">Every <c>key=value</c> key the reader knows, in the order the writer writes them.</param>
/// <param name="BareWords">Bare words the grammar uses, with what each means.</param>
/// <param name="Example">One line, as the writer writes it.</param>
public sealed record TuningDirectiveSpec(
    string                                   Keyword,
    string                                   Topic,
    string                                   Syntax,
    string                                   Summary,
    IReadOnlyList<AnalysisDirectiveKey>      Keys,
    IReadOnlyList<(string Word, string Summary)> BareWords,
    string                                   Example);

public static partial class AnalysisDirectiveSchema
{
    /// <summary>The <c>tuning</c> reference topic's name.</summary>
    public const string TuningTopic = "tuning";

    /// <summary>The <c>goals</c> reference topic's name.</summary>
    public const string GoalsTopic = "goals";

    // Enum spellings — the reader parses against these and the page prints them, so the two
    // cannot disagree about what is legal.
    public static readonly IReadOnlyList<string> ScaleTokens    = ["auto", "lin", "log"];
    public static readonly IReadOnlyList<string> DiscreteTokens = ["none", "integer", "preferred"];
    public static readonly IReadOnlyList<string> GoalTypeTokens = ["le", "ge", "eq", "in", "out"];
    public static readonly IReadOnlyList<string> CostTokens     = ["lsq", "minimax"];
    public static readonly IReadOnlyList<string> ScopeTokens    = ["goals", "all"];

    /// <summary>The prefix of an algorithm option key on an <c>optimize</c> line: <c>alg.popsize=20</c>.</summary>
    public const string AlgorithmOptionPrefix = "alg.";

    private static readonly TuningDirectiveSpec[] _tuning =
    [
        new("tune", TuningTopic,
            "tune <key> [min=<v> [unit]] [max=<v> [unit]] [scale=…] [step=<v> [unit]] [discrete=…] [tune=1] [opt=1]",
            "Makes one value tunable and states its range. <key> is Instance.Parameter (R1.R), a variable's name " +
            "(Wline), or either of those inside a sub-cell as Cell:… (DUT:R3.R, DUT:Wline), where Cell is spelled as " +
            "the cell's instance lines spell its type. An instance's own cell parameter is Instance.Parameter " +
            "(X1.Rbias), one key per instance. Only a value written as a plain number with an optional unit can be " +
            "tuned; an expression cannot, and the variable it reads can. A complex value written with numbers only " +
            "(40+15j, complex(40,15), polar(42.7,20.6), with an optional unit) is tuned by its parts, each a key of " +
            "its own: real(<key>), imag(<key>), mag(<key>) and phase(<key>), the phase in deg. Any parts may be " +
            "combined; moving one holds its partner (real with imag, mag with phase), and the ranges of all of a " +
            "value's parts always hold together, so ranges that leave no value inside all of them are an error. " +
            "A complex value that reads a name (4+j*X) is an expression. One range serves both tuning and optimizing.",
            [
                new("min",      Summary: "Lower bound, in the value's own unit."),
                new("max",      Summary: "Upper bound."),
                new("scale",    Default: "auto", Summary: "auto | lin | log. auto is log when min > 0 and max/min >= 10."),
                new("step",     Summary: "Slider step. Absent = continuous."),
                new("discrete", Default: "none", Summary: "none | integer | preferred (standard component values)."),
                new("tune",     Default: "0", Summary: "1 = offered in the Tuning window."),
                new("opt",      Default: "0", Summary: "1 = varied by the optimizer."),
            ],
            [],
            "tune R1.R min=10 Ohm max=200 Ohm scale=log tune=1 opt=1"),

        new("preset", TuningTopic,
            "preset \"<name>\" [created=<UTC>] [lasttuned=1] <key>=<v> [unit] ...",
            "A named set of tuned values. The values are the text the schematic would hold, so recalling one is " +
            "the same as typing it — a complex value whole, under its own key (Zsrc=40+15j Ohm), never by its parts. " +
            "created and lasttuned come before the values. A key that names nothing in the " +
            "design is skipped when the preset is recalled, and check reports it as a warning.",
            [
                new("created",   Summary: "When it was locked in: yyyy-MM-ddTHH:mm:ssZ."),
                new("lasttuned", Default: "0", Summary: "1 = the automatic \"Last tuned\" preset (at most one)."),
            ],
            [],
            "preset \"wide band\" created=2026-10-07T12:00:00Z R1.R=47 Ohm DUT:Wline=212 um"),

        new("goal", GoalsTopic,
            "goal <Name> = <expression> [analysis=<A>] [over=<axis> lo=<v> [unit] hi=<v> [unit]] <type> <limit(s)> " +
            "[weight=<w>] [enabled=false]",
            "One optimization goal. The expression is written in the measure language and is evaluated on the " +
            "named analysis; quote it if it contains a word this table uses. over= restricts it to one swept axis " +
            "(freq by default), lo..hi inclusive, and the range must hold at least one grid point. A goal with no " +
            "analysis reads variables only and costs no simulation. Inside a goal line, a length in inches is " +
            "spelled inch, because in is a goal type.",
            [
                new("analysis", Summary: "The analysis the expression reads."),
                new("over",     Default: "freq", Summary: "The swept axis lo and hi are on: freq, a sweep variable, a power or harmonic axis."),
                new("lo",       Summary: "Low end of the range on that axis, inclusive."),
                new("hi",       Summary: "High end of the range, inclusive."),
                new("weight",   Default: "1", Summary: "Multiplies this goal's violation in the cost."),
                new("enabled",  Default: "true", Summary: "false keeps the goal without optimizing for it."),
            ],
            [
                ("le",  "le <limit> [to <limit>] — at or below. With to, the limit slopes linearly from lo to hi."),
                ("ge",  "ge <limit> [to <limit>] — at or above."),
                ("eq",  "eq <limit> [to <limit>] — equal to."),
                ("in",  "in <a> <b> — inside the band [a, b]."),
                ("out", "out <a> <b> — outside the band [a, b]."),
                ("to",  "Separates a sloped limit's value at lo from its value at hi."),
            ],
            "goal G1 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge -0.5"),

        new("optimize", TuningTopic,
            "optimize [algorithm=<id>] [maxiter=<n>] [maxevals=<n>] [timelimit=<v> [unit]] [cost=…] [analyses=…] " +
            "[seed=<n>] [parallel=<n>] [alg.<option>=<v>] ...",
            "The optimizer's settings. At most one line. Keys starting alg. are the chosen algorithm's own options.",
            [
                new("algorithm", Default: "auto", Summary: "One of: " + string.Join(", ", CircuitRF.Core.Design.OptimizerAlgorithms.Ids) + "."),
                new("maxiter",   Summary: "Iteration limit."),
                new("maxevals",  Summary: "Evaluation limit."),
                new("timelimit", Summary: "Wall-clock limit: a number and s, ms, min or h (60 s)."),
                new("cost",      Default: "lsq", Summary: "lsq (sum of squared violations) | minimax (the worst one)."),
                new("analyses",  Default: "goals", Summary: "goals (only the analyses a goal names) | all."),
                new("seed",      Summary: "Random seed, for a repeatable run."),
                new("parallel",  Summary: "How many evaluations may run at once."),
                new(AlgorithmOptionPrefix + "<option>", Summary: "An option of the chosen algorithm."),
            ],
            [],
            "optimize algorithm=lm maxiter=200 seed=1"),
    ];

    /// <summary>The four directives, in the order a <c>.cnl</c> writes them.</summary>
    public static IReadOnlyList<TuningDirectiveSpec> TuningDirectives => _tuning;

    /// <summary>The directive spec for a keyword, or null.</summary>
    public static TuningDirectiveSpec? FindTuningDirective(string keyword)
        => _tuning.FirstOrDefault(s => s.Keyword.Equals(keyword, StringComparison.Ordinal));

    /// <summary>True when <paramref name="key"/> is one this directive knows (case-insensitive;
    /// <c>alg.*</c> on <c>optimize</c>).</summary>
    public static bool IsKnownTuningKey(TuningDirectiveSpec spec, string key)
        => spec.Keys.Any(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
        || (spec.Keyword == "optimize" && key.StartsWith(AlgorithmOptionPrefix, StringComparison.OrdinalIgnoreCase)
            && key.Length > AlgorithmOptionPrefix.Length);
}
