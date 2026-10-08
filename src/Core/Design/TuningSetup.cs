using System.Globalization;
using System.Text.Json.Serialization;

namespace CircuitRF.Core.Design;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The tuning and optimization setup a testbench carries (docs/design/tuning-optimization.md).
//
//  It lives HERE, beside Analysis and Measurement, and not in src/Design/Optimization where the
//  brief that introduced it first placed it: the `.cnl` reader and writer are in src/Core and a
//  TestBench carries the setup, and src/Core cannot reference src/Design. The orchestration —
//  the tunable catalog, the in-memory overrides and the `check` rules — is in src/Design.
//
//  Plain data with settable properties, so the `.csch` serializer binds it directly. Every value a
//  user writes (a bound, a preset value, a goal limit) is kept as the TEXT the schematic would hold
//  ("47 pF"), never as a resolved double: a pushed preset must be byte-identical to a typed value.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a tunable's slider and the optimizer's transform space its range.</summary>
public enum TuneScale
{
    /// <summary>Logarithmic when min &gt; 0 and max/min ≥ 10, linear otherwise.</summary>
    Auto,
    Lin,
    Log,
}

/// <summary>Which values a tunable may take.</summary>
public enum TuneDiscrete
{
    None,
    Integer,
    /// <summary>Standard component values (the Smith Chart tool's preferred-value series).</summary>
    Preferred,
}

/// <summary>How a goal's limit is met: <c>le</c> at or below, <c>ge</c> at or above, <c>eq</c> at,
/// <c>in</c> inside [a, b], <c>out</c> outside [a, b].</summary>
public enum GoalType { Le, Ge, Eq, In, Out }

/// <summary>How per-point violations combine into one cost.</summary>
public enum OptimizerCost
{
    /// <summary>Σ v² — the default.</summary>
    LeastSquares,
    /// <summary>max v.</summary>
    Minimax,
}

/// <summary>Which analyses an optimizer evaluation runs.</summary>
public enum OptimizerScope
{
    /// <summary>Only the analyses some enabled goal names.</summary>
    GoalAnalyses,
    All,
}

/// <summary>
/// One tunable's entry — shared by tuning and optimizing (overview D4). The CURRENT tuned value is
/// session state and is not here.
/// </summary>
public sealed class TunableEntry
{
    /// <summary>The identity key (overview D3): <c>R1.R</c>, <c>Wline</c>, <c>DUT:R3.R</c>,
    /// <c>DUT:Wline</c>, <c>X1.Rbias</c>, <c>DUT:X5.Wf</c>.</summary>
    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Tune { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Opt { get; set; }

    /// <summary>Lower bound as value text, in the parameter's own unit (<c>10 Ohm</c>).</summary>
    public string? Min { get; set; }

    /// <summary>Upper bound as value text.</summary>
    public string? Max { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TuneScale Scale { get; set; }

    /// <summary>Slider / grid step as value text; null for continuous.</summary>
    public string? Step { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TuneDiscrete Discrete { get; set; }

    /// <summary>Keys this build does not know, kept verbatim so a file written by a later version
    /// (a yield tolerance, say) survives a round trip through this one.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    public TunableEntry Clone() => new()
    {
        Key = Key, Tune = Tune, Opt = Opt, Min = Min, Max = Max, Scale = Scale, Step = Step,
        Discrete = Discrete, Extra = CloneMap(Extra),
    };

    internal static OrderedDictionary<string, string>? CloneMap(OrderedDictionary<string, string>? m)
    {
        if (m is null) return null;
        var copy = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in m) copy.Add(k, v);
        return copy;
    }
}

/// <summary>A named set of tuned values (overview D6 for the automatic "Last tuned" one).</summary>
public sealed class TuningPreset
{
    public string Name { get; set; } = "";

    private DateTime? _created;

    /// <summary>When it was locked in, UTC, to the whole second — the precision the <c>.cnl</c>
    /// spelling carries, so the two serializations cannot disagree after a round trip.</summary>
    public DateTime? Created
    {
        get => _created;
        set => _created = value is { } v ? TruncateToSecondUtc(v) : null;
    }

    /// <summary>The automatic preset written when a document is saved or closed with unpushed tuned
    /// values. There is at most one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLastTuned { get; set; }

    /// <summary>The optimizer's cost at these values, when the Optimizer locked them in; null for a
    /// preset a person locked in.</summary>
    public double? Cost { get; set; }

    /// <summary>Key → value text, in the order the preset lists them.</summary>
    public OrderedDictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public TuningPreset Clone() => new()
    {
        Name = Name, Created = Created, IsLastTuned = IsLastTuned, Cost = Cost,
        Values = TunableEntry.CloneMap(Values)!,
    };

    private static DateTime TruncateToSecondUtc(DateTime v)
    {
        var utc = v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>The name of the automatic preset (overview D6).</summary>
    public const string LastTunedName = "Last tuned";

    /// <summary>The spelling both serializations use for <see cref="Created"/>.</summary>
    public static string FormatCreated(DateTime utc)
        => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The one swept axis a goal is evaluated over, and the part of it that counts.</summary>
public sealed class GoalRange
{
    /// <summary>The axis name: <c>freq</c>, a sweep variable, a power or harmonic axis.</summary>
    public string Axis { get; set; } = "freq";

    /// <summary>Lower end as value text (<c>1 GHz</c>).</summary>
    public string Lo { get; set; } = "";

    /// <summary>Upper end as value text.</summary>
    public string Hi { get; set; } = "";

    public GoalRange Clone() => new() { Axis = Axis, Lo = Lo, Hi = Hi };
}

/// <summary>One optimization goal (overview D10).</summary>
public sealed class OptimizationGoal
{
    public string Name { get; set; } = "";

    /// <summary>An expression in the one expression engine — the language <c>measure</c> uses.</summary>
    public string Expression { get; set; } = "";

    /// <summary>The analysis the goal reads. Null for a goal over variables only, which costs no
    /// simulation.</summary>
    public string? Analysis { get; set; }

    public GoalRange? Range { get; set; }

    public GoalType Type { get; set; }

    /// <summary><c>le</c>/<c>ge</c>/<c>eq</c>: the limit, or its value at the range's low end when
    /// <see cref="LimitAtHi"/> slopes it. <c>in</c>/<c>out</c>: the lower edge of the band.</summary>
    public string Limit { get; set; } = "";

    /// <summary><c>in</c>/<c>out</c> only: the upper edge of the band.</summary>
    public string? UpperLimit { get; set; }

    /// <summary><c>le</c>/<c>ge</c>/<c>eq</c> only: the limit at the range's high end — the limit is
    /// interpolated linearly between <see cref="Limit"/> and this across the range.</summary>
    public string? LimitAtHi { get; set; }

    public double Weight { get; set; } = 1.0;

    public bool Enabled { get; set; } = true;

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    public OptimizationGoal Clone() => new()
    {
        Name = Name, Expression = Expression, Analysis = Analysis, Range = Range?.Clone(), Type = Type,
        Limit = Limit, UpperLimit = UpperLimit, LimitAtHi = LimitAtHi, Weight = Weight, Enabled = Enabled,
        Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>The optimizer's own settings — the algorithm and what stops it.</summary>
public sealed class OptimizerSettings
{
    /// <summary>A stable id from <see cref="OptimizerAlgorithms.Ids"/>.</summary>
    public string Algorithm { get; set; } = OptimizerAlgorithms.Auto;

    /// <summary>The algorithm's own options (population size, restarts …), name → value text. Written
    /// <c>alg.&lt;name&gt;=</c> in a <c>.cnl</c>; each algorithm phase documents its own.</summary>
    public OrderedDictionary<string, string>? Options { get; set; }

    public int? MaxIterations { get; set; }
    public int? MaxEvaluations { get; set; }

    /// <summary>Wall-clock limit as value text (<c>60 s</c>).</summary>
    public string? TimeLimit { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OptimizerCost Cost { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OptimizerScope Scope { get; set; }

    public int? Seed { get; set; }

    /// <summary>How many evaluations may run at once; null lets the evaluator decide.</summary>
    public int? Parallelism { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    public OptimizerSettings Clone() => new()
    {
        Algorithm = Algorithm, Options = TunableEntry.CloneMap(Options), MaxIterations = MaxIterations,
        MaxEvaluations = MaxEvaluations, TimeLimit = TimeLimit, Cost = Cost, Scope = Scope, Seed = Seed,
        Parallelism = Parallelism, Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>The optimizer menu's stable ids, in the menu's order (overview D13).</summary>
public static class OptimizerAlgorithms
{
    public const string Auto = "auto";

    /// <summary>Every id, in menu order. A later phase adds the algorithm; the id is fixed here so a
    /// file written today names the same algorithm tomorrow.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        Auto, "lm", "bfgsb", "minimax", "simplex", "trust_region", "pattern", "random", "de", "pso",
        "cmaes", "bayes", "discrete",
    ];
}

/// <summary>
/// Everything a schematic says about tuning and optimizing it: the variable entries, the presets,
/// the goals and the optimizer settings (overview D5). Null on a schematic that never had any.
/// </summary>
public sealed class TuningSetup
{
    public List<TunableEntry>     Variables { get; set; } = [];
    public List<TuningPreset>     Presets   { get; set; } = [];
    public List<OptimizationGoal> Goals     { get; set; } = [];
    public OptimizerSettings?     Optimizer { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Variables.Count == 0 && Presets.Count == 0 && Goals.Count == 0 && Optimizer is null;

    public TuningSetup Clone() => new()
    {
        Variables = [.. Variables.Select(v => v.Clone())],
        Presets   = [.. Presets.Select(p => p.Clone())],
        Goals     = [.. Goals.Select(g => g.Clone())],
        Optimizer = Optimizer?.Clone(),
    };
}

/// <summary>
/// One part of a complex value (overview D18). A complex value is never tuned whole: each part is a
/// tunable of its own, keyed <c>real(K)</c>, <c>imag(K)</c>, <c>mag(K)</c> or <c>phase(K)</c> where
/// <c>K</c> is the value's own key. The spellings are the expression engine's own functions, so a key
/// reads as what it measures; <see cref="Phase"/> is in degrees, as <c>phase()</c> is.
/// </summary>
public enum ComplexPart { Real, Imag, Mag, Phase }

/// <summary>
/// A tunable key split into its parts (overview D3): <c>[Cell:]Instance.Parameter</c> or
/// <c>[Cell:]Variable</c>, optionally wrapped in a complex part — <c>mag(DUT:ZL)</c>.
/// <see cref="Cell"/> is the cell as the <c>.cnl</c> spells its instance type; null at the top level.
/// </summary>
public readonly record struct TunableKey(string? Cell, string? Instance, string Name, ComplexPart? Part = null)
{
    public bool IsVariable => Instance is null;

    /// <summary>The key of the whole value a part key names; the key itself when it names no part.</summary>
    public TunableKey Whole => this with { Part = null };

    public override string ToString()
    {
        string whole = (Cell is null ? "" : Cell + ":") + (Instance is null ? Name : Instance + "." + Name);
        return Part is { } p ? $"{PartWord(p)}({whole})" : whole;
    }

    /// <summary>The word a part is spelled with: <c>real</c>, <c>imag</c>, <c>mag</c>, <c>phase</c>.</summary>
    public static string PartWord(ComplexPart part) => part switch
    {
        ComplexPart.Real => "real",
        ComplexPart.Imag => "imag",
        ComplexPart.Mag  => "mag",
        _                => "phase",
    };

    /// <summary>The part of the whole value <paramref name="wholeKey"/>, spelled as a key.</summary>
    public static string PartKey(string wholeKey, ComplexPart part) => $"{PartWord(part)}({wholeKey})";

    /// <summary>Splits a key. False for an empty part (<c>:R1.R</c>, <c>R1.</c>, <c>mag()</c>).</summary>
    public static bool TryParse(string text, out TunableKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();

        ComplexPart? part = null;
        int open = s.IndexOf('(');
        if (open > 0 && s[^1] == ')')
        {
            part = s[..open] switch
            {
                "real"  => ComplexPart.Real,
                "imag"  => ComplexPart.Imag,
                "mag"   => ComplexPart.Mag,
                "phase" => ComplexPart.Phase,
                _       => null,
            };
            if (part is null) return false;
            s = s[(open + 1)..^1].Trim();
            if (s.Contains('(') || s.Contains(')')) return false;
        }
        else if (s.Contains('(') || s.Contains(')'))
        {
            return false;
        }

        string? cell = null;
        int colon = s.IndexOf(':');
        if (colon >= 0)
        {
            cell = s[..colon];
            s    = s[(colon + 1)..];
            if (cell.Length == 0) return false;
        }

        int dot = s.IndexOf('.');
        if (dot < 0)
        {
            if (s.Length == 0) return false;
            key = new TunableKey(cell, null, s, part);
            return true;
        }

        string inst = s[..dot], name = s[(dot + 1)..];
        if (inst.Length == 0 || name.Length == 0) return false;
        key = new TunableKey(cell, inst, name, part);
        return true;
    }
}
