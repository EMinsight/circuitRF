using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// Statistical corners (brief-yield-6 R-ya6-4, yield overview D10): a Monte Carlo trial kept as a named corner —
/// <c>corner &lt;Name&gt; trial=n seed=s sampling=m trials=N</c> — that replays the trial's z-vector against the
/// CURRENT nominal. A percent spread follows a moved nominal and an absolute one keeps its width (D5); correlated
/// entries keep their correlation, the vector being the one drawn after it.
/// </summary>
public static class StatisticalCorner
{
    /// <summary>
    /// The <paramref name="k"/> worst trials of <paramref name="goal"/> in <paramref name="result"/> — the smallest
    /// margins, tightest first — as corner definitions naming the run (its effective seed, sampling and trial count),
    /// named <c>&lt;goal&gt;_t&lt;trial&gt;</c>. What the panel's and the CLI's "save as corner" propose.
    /// </summary>
    public static IReadOnlyList<CornerDefinition> FromRun(StatisticalResult result, string goal, int k = 1)
    {
        var s = result.Settings ?? new StatisticsSettings();
        return [.. result.Records
            .Where(r => r.Evaluated)
            .Select(r => (r.Trial, Score: r.Goals.FirstOrDefault(g => g.Name == goal)))
            .Where(x => x.Score is not null && !double.IsNaN(x.Score.Margin))
            .OrderBy(x => x.Score!.Margin).ThenBy(x => x.Trial)
            .Take(k)
            .Select(x => new CornerDefinition
            {
                Name = NameFor(goal, x.Trial), Trial = x.Trial,
                Seed = s.EffectiveSeed, Sampling = s.Sampling, Trials = s.EffectiveTrials,
            })];
    }

    /// <summary>Every goal's <paramref name="k"/> worst trials (<see cref="FromRun(StatisticalResult, string, int)"/>),
    /// a trial worst for two goals proposed once.</summary>
    public static IReadOnlyList<CornerDefinition> FromRun(StatisticalResult result, IEnumerable<string> goals, int k = 1)
    {
        var seen = new HashSet<int>();
        return [.. goals.SelectMany(g => FromRun(result, g, k)).Where(c => seen.Add(c.Trial!.Value))];
    }

    /// <summary>A corner name from a goal and a trial: one word, as a corner line's name must be.</summary>
    public static string NameFor(string goal, int trial)
    {
        var chars = goal.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        string g = new string(chars).Trim('_');
        if (g.Length == 0 || !(char.IsLetter(g[0]) || g[0] == '_')) g = "g" + g;
        return $"{g}_t{trial}";
    }

    /// <summary>
    /// Replays <paramref name="corner"/> on <paramref name="circuit"/> under <paramref name="setup"/> (null: the circuit's
    /// own): its trial drawn
    /// with the seed, sampling and trial count it names (and the setup's current process, mismatch and σ scale), over
    /// any values the corner binds itself. With <paramref name="run"/> — the result file of the run the corner names,
    /// when at hand — the trial's recorded z-vector is used as it stands (<see cref="RecordedTrial.FromDataSet"/>);
    /// otherwise it is drawn afresh and a note says a renamed stream cannot be told apart.
    /// </summary>
    public static StatisticalReplay Replay(PreparedCircuit circuit, TuningSetup? setup, CornerDefinition corner, DataSet? run,
                                           IReadOnlyList<(string Name, string Expression)>? sets = null)
    {
        var recorded = run is null ? null : RecordedTrial.FromDataSet(run, corner);
        var replay = Replay(circuit, setup, corner, recorded, sets);
        return recorded is not null || replay.Refusal is not null ? replay
            : replay with { Notes = [StatisticsDiagnostics.ReplayNotRecorded(corner.Name), .. replay.Notes] };
    }

    /// <summary><see cref="Replay(PreparedCircuit, TuningSetup?, CornerDefinition, DataSet?, IReadOnlyList{ValueTuple{string, string}}?)"/>
    /// with the recorded z-vector given directly — a run held in memory (<see cref="RecordedTrial.Of"/>) — or null to
    /// draw the trial afresh.</summary>
    public static StatisticalReplay Replay(PreparedCircuit circuit, TuningSetup? setup, CornerDefinition corner,
                                           RecordedTrial? recorded, IReadOnlyList<(string Name, string Expression)>? sets = null)
    {
        if (corner.Trial is not { } trial)
            return new StatisticalReplay(0, new Dictionary<string, string>(), null, [],
                StatisticsDiagnostics.CornerTrialIncomplete($"corner {corner.Name}", "trial="));

        var at = (setup ?? circuit.Tb?.Tuning ?? new TuningSetup()).Clone();
        at.Statistics = (at.Statistics ?? new StatisticsSettings()).Clone();
        at.Statistics.Seed     = corner.Seed;
        at.Statistics.Sampling = corner.Sampling ?? StatSampling.Random;
        at.Statistics.Trials   = corner.Trials;
        at.Statistics.AutoStop = false;   // a replay places one trial; the run's own stopping rule is not its concern

        var run = StatisticalRun.Create(circuit, new StatisticalOptions
        {
            Mode = StatisticalMode.MonteCarlo, Setup = at, Sets = sets ?? [], Bindings = CornerRun.BindingsOf(corner),
        });
        return run.Replay(trial, recorded);
    }
}
