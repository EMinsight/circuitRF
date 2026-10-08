using System.Text.Json.Serialization;

namespace CircuitRF.Core.Design;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The statistical half of a testbench's tuning setup (docs/design/yield.md): the Monte Carlo /
//  yield settings, correlations between statistical entries, and named corners. It lives in the ONE
//  tuning block (yield overview D1) — a tolerance is part of the variable entry, a yield spec is a
//  goal — so there is no second "statistics" block beside it.
//
//  As for the rest of the setup, every value a user writes is kept as TEXT or as the typed value it
//  is, and a default is held as null so neither serialization ever writes one.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a run's trials are placed (yield overview D5).</summary>
public enum StatSampling
{
    /// <summary>Independent pseudo-random draws — the default.</summary>
    Random,
    /// <summary>Latin hypercube: needs the trial count up front, so it is refused with auto-stop.</summary>
    Lhs,
    /// <summary>Scrambled low-discrepancy sequence; extensible, so it works with auto-stop.</summary>
    Sobol,
}

/// <summary>What a trial that does not evaluate counts as (yield overview D7).</summary>
public enum NonConvergedPolicy
{
    /// <summary>Counted as a fail and reported separately — the default.</summary>
    Fail,
    /// <summary>Excluded from the yield's denominator and reported as a warning.</summary>
    Warn,
}

/// <summary>
/// The Monte Carlo / yield settings — the <c>statistics</c> line. Every property is null (or the
/// enum's first member) at its default, and the defaults are the constants below.
/// </summary>
public sealed class StatisticsSettings
{
    public const int    DefaultTrials     = 100;
    public const int    DefaultSeed       = 1;
    public const double DefaultConfidence = 95;

    /// <summary>The number of trials — with auto-stop, the cap.</summary>
    public int? Trials { get; set; }

    public int? Seed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public StatSampling Sampling { get; set; }

    /// <summary>The yield target in percent; null for none.</summary>
    public double? Target { get; set; }

    /// <summary>The confidence of the yield interval in percent.</summary>
    public double? Confidence { get; set; }

    /// <summary>Stop when the yield interval clears <see cref="Target"/> either way (yield overview D8).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoStop { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public NonConvergedPolicy NonConverged { get; set; }

    /// <summary>What a run keeps: <c>scalars</c>, <c>all</c> or a trial count; null for <c>auto</c>.</summary>
    public string? Save { get; set; }

    /// <summary>Kit process draws; null is on.</summary>
    public bool? Process { get; set; }

    /// <summary>Kit mismatch draws; null is on.</summary>
    public bool? Mismatch { get; set; }

    /// <summary>Scales every kit σ; null is 1.</summary>
    public double? SigmaScale { get; set; }

    /// <summary>How many trials may run at once; null lets the evaluator decide.</summary>
    public int? Parallelism { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OptimizerScope Scope { get; set; }

    /// <summary>The corners a run covers: <c>all</c>, or names separated by commas; null for none.</summary>
    public string? Corners { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    [JsonIgnore] public int    EffectiveTrials     => Trials ?? DefaultTrials;
    [JsonIgnore] public int    EffectiveSeed       => Seed ?? DefaultSeed;
    [JsonIgnore] public double EffectiveConfidence => Confidence ?? DefaultConfidence;

    /// <summary>The corner names a run covers; empty for none, null for all.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? CornerNames => Corners is null ? []
        : Corners.Equals("all", StringComparison.OrdinalIgnoreCase) ? null
        : Corners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public StatisticsSettings Clone() => new()
    {
        Trials = Trials, Seed = Seed, Sampling = Sampling, Target = Target, Confidence = Confidence,
        AutoStop = AutoStop, NonConverged = NonConverged, Save = Save, Process = Process, Mismatch = Mismatch,
        SigmaScale = SigmaScale, Parallelism = Parallelism, Scope = Scope, Corners = Corners,
        Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>A correlation between two statistical entries (yield overview D3) — the <c>correlate</c> line.</summary>
public sealed class StatCorrelation
{
    public string First  { get; set; } = "";
    public string Second { get; set; } = "";
    public double Rho    { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    public StatCorrelation Clone() => new() { First = First, Second = Second, Rho = Rho, Extra = TunableEntry.CloneMap(Extra) };
}

/// <summary>
/// A named corner (yield overview D10) — the <c>corner</c> line. A set of bindings: kit corner-axis
/// selections (a <c>.csch</c> only — extraction resolves them into <see cref="Values"/>, so a
/// <c>.cnl</c> corner never names a kit file), the ambient <see cref="Temp"/>, and values for global
/// variables and tunables. A STATISTICAL corner names a trial of a run instead (<see cref="Trial"/>
/// with the seed, sampling and trial count that identify it) and replays that trial's draws.
/// </summary>
public sealed class CornerDefinition
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Kit corner-axis selections, keyed as the schematic's own corner selections are
    /// (<c>&lt;kit&gt;|&lt;kit-relative corner file&gt;</c>) → section. <c>.csch</c> only.</summary>
    public OrderedDictionary<string, string>? AxisSelections { get; set; }

    /// <summary>The ambient temperature as value text, in °C.</summary>
    public string? Temp { get; set; }

    /// <summary>Global-variable names and tunable keys → value text, in the order written.</summary>
    public OrderedDictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A statistical corner's trial number (1-based); null for a value corner.</summary>
    public int? Trial { get; set; }

    /// <summary>The seed of the run the trial came from.</summary>
    public int? Seed { get; set; }

    /// <summary>The sampling of the run the trial came from.</summary>
    public StatSampling? Sampling { get; set; }

    /// <summary>The trial count of the run the trial came from (LHS places trials by it).</summary>
    public int? Trials { get; set; }

    [JsonIgnore]
    public bool IsStatistical => Trial is not null;

    public CornerDefinition Clone() => new()
    {
        Name = Name, Enabled = Enabled, AxisSelections = TunableEntry.CloneMap(AxisSelections), Temp = Temp,
        Values = TunableEntry.CloneMap(Values)!, Trial = Trial, Seed = Seed, Sampling = Sampling, Trials = Trials,
    };
}
