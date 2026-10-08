using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// One corner made ready to be evaluated at ANY point of the design (brief-yield-7): the optimizer evaluates each
/// candidate at it, the Tuning panel evaluates the sliders at it. A value corner is its bindings over the point's
/// values — the corner is the condition, so a value it binds wins over the point's. A statistical corner replays its
/// trial's z-vector against the POINT's nominal (R-ya7-3), so a percent spread moves with the candidate; it is
/// prepared once (its <see cref="StatisticalRun"/> and recorded vector), and each point costs only the replay.
/// </summary>
public sealed class CornerPoint
{
    private readonly StatisticalRun? _replay;
    private readonly RecordedTrial? _recorded;

    private CornerPoint(string name, CornerDefinition? definition, StatisticalRun? replay = null, RecordedTrial? recorded = null)
    {
        Name       = name;
        Definition = definition;
        _replay    = replay;
        _recorded  = recorded;
    }

    /// <summary>The design itself: a point's values as they stand.</summary>
    public static CornerPoint Nominal { get; } = new(CornerRun.NominalName, null);

    /// <summary>The corner's name; <see cref="CornerRun.NominalName"/> for the nominal.</summary>
    public string Name { get; }

    /// <summary>The corner line; null for the nominal.</summary>
    public CornerDefinition? Definition { get; }

    /// <summary>
    /// The points a run is evaluated at: the nominal (unless <paramref name="nominal"/> is false), then each of
    /// <paramref name="names"/> among the setup's ENABLED corners in the order named (null: every enabled corner, in
    /// setup order). A name that is not an enabled corner, a corner binding a <c>--set</c> name, or a statistical
    /// corner that cannot be replayed is the refusal. <paramref name="recorded"/> is the run statistical corners came
    /// from (<c>&lt;design&gt;.yield.npy</c>), when at hand.
    /// </summary>
    public static CornerPoints Prepare(PreparedCircuit circuit, TuningSetup setup, IReadOnlyList<string>? names, bool nominal = true,
                                       DataSet? recorded = null, IReadOnlyList<(string Name, string Expression)>? sets = null)
    {
        var enabled = setup.Corners.Where(c => c.Enabled).ToList();
        var chosen = new List<CornerDefinition>();
        if (names is null) chosen.AddRange(enabled);
        else
            foreach (var name in names)
            {
                var c = enabled.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (c is null)
                    return CornerPoints.Refused(StatisticsDiagnostics.CornerUnknown(name,
                        enabled.Count == 0 ? "none" : string.Join(", ", enabled.Select(e => e.Name))));
                if (!chosen.Contains(c)) chosen.Add(c);
            }
        if (chosen.Count == 0) return CornerPoints.Refused(StatisticsDiagnostics.CornerNone());

        // A schematic's corner holds kit axis selections, which extraction resolved into the values the netlist's
        // corner line binds (yield overview D10): a setup that is not the netlist's own — the Optimizer panel's, the
        // schematic's — evaluates the extracted corner of the same name.
        if (circuit.Tb?.Tuning is { } extracted && !ReferenceEquals(extracted, setup))
            for (int i = 0; i < chosen.Count; i++)
                if (extracted.Corners.FirstOrDefault(e => e.Name.Equals(chosen[i].Name, StringComparison.OrdinalIgnoreCase)) is { } e)
                    chosen[i] = e;

        var setNames = (sets ?? []).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in chosen)
            foreach (var key in CornerRun.BindingsOf(c).Keys)
                if (setNames.Contains(key)) return CornerPoints.Refused(StatisticsDiagnostics.CornerSetConflict(c.Name, key));

        var points = new List<CornerPoint>();
        var notes  = new List<Diagnostic>();
        if (nominal) points.Add(Nominal);
        foreach (var c in chosen)
        {
            if (!c.IsStatistical) { points.Add(new CornerPoint(c.Name, c)); continue; }
            var (run, refusal) = ReplayRun(circuit, setup, c, sets);
            if (refusal is not null) return CornerPoints.Refused(StatisticsDiagnostics.CornerReplayRefused(c.Name, refusal.Render()));
            var trial = recorded is null ? null : RecordedTrial.FromDataSet(recorded, c);
            if (trial is null) notes.Add(StatisticsDiagnostics.ReplayNotRecorded(c.Name));
            points.Add(new CornerPoint(c.Name, c, run, trial));
        }
        return new CornerPoints(points, notes, null);
    }

    /// <summary>One corner of the setup by name, nominal-free — the Tuning panel's Evaluate at.</summary>
    public static (CornerPoint? Point, IReadOnlyList<Diagnostic> Notes, Diagnostic? Refusal) For(
        PreparedCircuit circuit, string name, DataSet? recorded = null)
    {
        var setup = circuit.Tb?.Tuning ?? new TuningSetup();
        var prepared = Prepare(circuit, setup, [name], nominal: false, recorded);
        return (prepared.Refusal is null ? prepared.Points[0] : null, prepared.Notes, prepared.Refusal);
    }

    /// <summary>
    /// The value map (and a statistical corner's draws) the point <paramref name="values"/> evaluates at this corner:
    /// the values themselves at the nominal; the corner's bindings over them at a value corner; the trial replayed
    /// around them at a statistical one. A trial whose draw is not physical at this point is the refusal.
    /// </summary>
    public CornerPointValues At(IReadOnlyDictionary<string, string> values)
    {
        if (Definition is null) return new CornerPointValues(values, null, [], null);
        if (_replay is null)
        {
            var map = new Dictionary<string, string>(values, StringComparer.Ordinal);
            foreach (var (k, v) in CornerRun.BindingsOf(Definition)) map[k] = v;
            return new CornerPointValues(map, null, [], null);
        }
        var r = _replay.Replay(Definition.Trial!.Value, _recorded, values);
        return new CornerPointValues(r.Values, r.Draws, r.Notes, r.Refusal);
    }

    /// <summary>The run a statistical corner replays through — <see cref="StatisticalCorner.Replay(PreparedCircuit, TuningSetup?, CornerDefinition, RecordedTrial?, IReadOnlyList{ValueTuple{string, string}}?)"/>'s
    /// settings: the corner's seed, sampling and trial count over the setup's process, mismatch and σ scale.</summary>
    private static (StatisticalRun? Run, Diagnostic? Refusal) ReplayRun(PreparedCircuit circuit, TuningSetup setup, CornerDefinition corner,
                                                                        IReadOnlyList<(string Name, string Expression)>? sets)
    {
        if (corner.Trial is null) return (null, StatisticsDiagnostics.CornerTrialIncomplete($"corner {corner.Name}", "trial="));
        var at = setup.Clone();
        at.Statistics = (at.Statistics ?? new StatisticsSettings()).Clone();
        at.Statistics.Seed     = corner.Seed;
        at.Statistics.Sampling = corner.Sampling ?? StatSampling.Random;
        at.Statistics.Trials   = corner.Trials;
        at.Statistics.AutoStop = false;
        var run = StatisticalRun.Create(circuit, new StatisticalOptions
        {
            Mode = StatisticalMode.MonteCarlo, Setup = at, Sets = sets ?? [], Bindings = CornerRun.BindingsOf(corner),
        });
        return run.Refusal is { } refused ? (null, refused) : (run, null);
    }
}

/// <summary>A corner's map at one point: what to simulate, a statistical corner's draws, what the replay noticed (a
/// stream the design no longer has), and why it cannot be evaluated there, if it cannot.</summary>
public sealed record CornerPointValues(
    IReadOnlyDictionary<string, string> Values,
    IStatisticalDraws?                  Draws,
    IReadOnlyList<Diagnostic>           Notes,
    Diagnostic?                         Refusal);

/// <summary>The points <see cref="CornerPoint.Prepare"/> made — the nominal first when it is one of them — with what
/// preparing them noticed, or why they could not be.</summary>
public sealed record CornerPoints(IReadOnlyList<CornerPoint> Points, IReadOnlyList<Diagnostic> Notes, Diagnostic? Refusal)
{
    internal static CornerPoints Refused(Diagnostic why) => new([], [], why);
}
