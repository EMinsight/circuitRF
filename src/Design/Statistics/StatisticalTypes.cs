using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Statistics;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>What a statistical run is for (docs/design/yield.md §8).</summary>
public enum StatisticalMode
{
    /// <summary>The spread alone: needs no goal, and scores every enabled goal it finds.</summary>
    MonteCarlo,
    /// <summary>Pass/fail against the yield specs — the enabled goals with <c>use=yield|both</c>; needs one.</summary>
    Yield,
}

/// <summary>What a caller may change about a run beyond what the design's setup says.</summary>
public sealed record StatisticalOptions
{
    public StatisticalMode Mode { get; init; } = StatisticalMode.Yield;

    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides, applied before the trial's values as a run verb applies them.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>Told after every batch.</summary>
    public IProgress<StatisticalProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the run and writes nothing (exit 130); Stop keeps every finished trial.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Trials per batch; null is the evaluator's parallelism.</summary>
    public int? BatchSize { get; init; }

    /// <summary>Where the result is written when the run finishes or is stopped
    /// (<see cref="StatisticalRun.ResultPathFor"/>); null writes nothing.</summary>
    public string? ResultPath { get; init; }

    /// <summary>The run's result so far, for the in-memory Data Display source (yield overview D14) — at most once
    /// per <see cref="PublishInterval"/>, the newest state, and once more at the end.</summary>
    public Action<DataSet>? Publish { get; init; }

    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>One goal's yield.</summary>
public sealed record GoalYield(string Goal, YieldEstimate Estimate);

/// <summary>What a run reports after every batch (R-ya4-1).</summary>
/// <param name="Trials">Trials done.</param>
/// <param name="DidNotEvaluate">Of those, how many did not evaluate.</param>
/// <param name="Yield">Overall: passes, counted trials, yield and interval; <see cref="YieldEstimate.None"/> with no goal.</param>
public sealed record StatisticalProgress(
    int                       Trials,
    int                       DidNotEvaluate,
    YieldEstimate             Yield,
    IReadOnlyList<GoalYield>  Goals,
    TimeSpan                  Elapsed);

/// <summary>How a run ended (yield overview D13 gives each its exit code).</summary>
public enum StatisticalOutcome
{
    /// <summary>Finished or stopped, and the yield is at or above the target, or there is no target — exit 0.</summary>
    Finished,
    /// <summary>Finished or stopped with the yield below the target — exit 3.</summary>
    BelowTarget,
    /// <summary>Refused before the first trial — exit 1.</summary>
    Refused,
    /// <summary>No trial evaluated — exit 2.</summary>
    NoneEvaluated,
    /// <summary>Cancelled — exit 130, and nothing is written.</summary>
    Cancelled,
}

/// <summary>
/// One trial (R-ya4-2): what was drawn, what it evaluated to.
/// </summary>
/// <param name="Trial">Its number, from 1, in the order trials are drawn.</param>
/// <param name="Values">The value map evaluated (key → text): every statistical entry's drawn value.</param>
/// <param name="Draws">Entry key → the drawn number in base SI.</param>
/// <param name="Z">Entry key → its standard normal after correlation (yield overview D5).</param>
/// <param name="Kit">Kit stream → its kind and standard normal, for every distribution call the trial drew.</param>
/// <param name="Goals">Each scored goal's score; empty when the trial did not evaluate.</param>
/// <param name="Scalars">Every real scalar measurement the trial produced, by name.</param>
/// <param name="Data">The trial's analysis results, when the save policy keeps them.</param>
public sealed record TrialRecord(
    int                                                         Trial,
    PointStatus                                                 Status,
    Diagnostic?                                                 Reason,
    IReadOnlyDictionary<string, string>                         Values,
    IReadOnlyDictionary<string, double>                         Draws,
    IReadOnlyDictionary<string, double>                         Z,
    IReadOnlyDictionary<string, (StatisticalKind Kind, double Z)> Kit,
    IReadOnlyList<GoalScore>                                    Goals,
    IReadOnlyDictionary<string, double>                         Scalars,
    DataSet?                                                    Data)
{
    public bool Evaluated => Status == PointStatus.Evaluated;

    /// <summary>It evaluated and every scored goal is met.</summary>
    public bool Pass => Evaluated && Goals.All(g => g.Met);
}

/// <summary>What the save policy kept (R-ya4-5).</summary>
/// <param name="Requested">The <c>save=</c> text as written, <c>auto</c> when absent.</param>
/// <param name="KeptTrials">How many leading trials keep their analysis results.</param>
/// <param name="BytesPerTrial">The estimate one trial's analysis results take, from the nominal's.</param>
public sealed record SaveReport(string Requested, int KeptTrials, long BytesPerTrial, string Sentence);

/// <summary>One of a goal's worst trials (R-ya4-10).</summary>
public sealed record WorstTrial(int Trial, double Margin, double Worst, IReadOnlyDictionary<string, string> Values);

/// <summary>One contributor to a scalar's spread (R-ya4-9): a statistical entry, a process draw, or an
/// instance's mismatch draws together.</summary>
/// <param name="Coefficient">The standardized regression coefficient; for an instance's group, the root sum of
/// its streams' squares (unsigned).</param>
/// <param name="Share">Its share of the explained variance (the shares sum to 1).</param>
/// <param name="Spearman">The rank correlation of the scalar with the contributor (for a group, with its fitted
/// linear part).</param>
public sealed record Contributor(string Name, string Kind, double Coefficient, double Share, double Spearman, int Streams);

/// <summary>A contribution ranking, largest share first (R-ya4-9).</summary>
public sealed record ContributionReport(
    string                      Of,
    IReadOnlyList<Contributor>  Contributors,
    double                      RSquared,
    bool                        Underdetermined,
    int                         Trials,
    Diagnostic?                 Refusal = null);

/// <summary>What a run produced.</summary>
public sealed class StatisticalResult
{
    public required StatisticalOutcome Outcome { get; init; }

    /// <summary>Yield overview D13: 0, 3, 1, 2 or 130.</summary>
    public int ExitCode => Outcome switch
    {
        StatisticalOutcome.Finished      => 0,
        StatisticalOutcome.BelowTarget   => 3,
        StatisticalOutcome.Refused       => 1,
        StatisticalOutcome.NoneEvaluated => 2,
        _                                => 130,
    };

    public StatisticalMode Mode { get; init; }

    /// <summary>Why it stopped, as a sentence.</summary>
    public string FinishReason { get; init; } = "";

    public Diagnostic? Refusal { get; init; }

    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];

    /// <summary>Trials done (after auto-stop, the count it stopped at).</summary>
    public int Trials { get; init; }

    public int DidNotEvaluate { get; init; }

    /// <summary>Overall; <see cref="YieldEstimate.None"/> with no goal.</summary>
    public YieldEstimate Yield { get; init; } = YieldEstimate.None;

    public IReadOnlyList<GoalYield> Goals { get; init; } = [];

    /// <summary>What auto-stop decided; null when it did not stop the run.</summary>
    public AutoStopVerdict? AutoStop { get; init; }

    public SaveReport? Save { get; init; }

    /// <summary>The nominal's evaluation.</summary>
    public PointEvaluation? Nominal { get; init; }

    /// <summary>Every trial, in trial order.</summary>
    public IReadOnlyList<TrialRecord> Records { get; init; } = [];

    /// <summary>The run's <c>DataSet</c> (docs/design/results-dataset-layout.md §"Monte Carlo and yield"); null
    /// when refused or cancelled.</summary>
    public DataSet? Data { get; init; }

    /// <summary>Where <see cref="Data"/> was written; null when it was not.</summary>
    public string? WrittenPath { get; init; }
}
