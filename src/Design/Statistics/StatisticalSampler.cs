using CircuitRF.Core.Design;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// One trial's place in z-space (yield overview D5): its number and, per stream, the standard normal
/// it draws AFTER correlation. This is the stored form of a trial — a value is nominal ⊕ spread(z), so
/// the same sample re-applied to a moved nominal is the same relative deviation.
/// </summary>
/// <param name="Trial">The trial number, from 1.</param>
/// <param name="Z">Stream key (a statistical entry's key) → its standard normal.</param>
public sealed record StatisticalSample(int Trial, IReadOnlyDictionary<string, double> Z);

/// <summary>
/// Turns a setup's statistical entries, correlations and settings into trial samples
/// (docs/design/yield.md §6). Each statistical entry is one stream, identified by its key text; the
/// plan (<see cref="SamplingPlan"/>) places trial t, and the correlated streams then pass through the
/// Gaussian copula. Every sample is a pure function of the setup and t — reproducible alone, in any
/// order and on any thread — and the sampler holds no mutable state, so one instance serves every
/// thread of a run.
/// </summary>
public sealed class StatisticalSampler
{
    private readonly SamplingPlan _plan;
    private readonly GaussianCopula? _copula;
    private readonly int[] _correlated;
    private readonly List<Diagnostic> _notes = [];
    private readonly List<string> _streams;

    /// <param name="streams">The stream keys, in the order samples list them.</param>
    /// <param name="correlation">The correlations (<see cref="StatisticsValidator.CorrelationOf"/>);
    /// keys it names that are not among <paramref name="streams"/> are dropped with their rows.</param>
    public StatisticalSampler(IEnumerable<string> streams, StatSampling sampling, int seed, int trials,
                              CorrelationMatrix? correlation = null)
    {
        _streams = [.. streams.Distinct(StringComparer.Ordinal)];
        Seed = seed;
        Sampling = sampling;
        Trials = trials;

        var method = sampling switch
        {
            StatSampling.Lhs   => SamplingMethod.LatinHypercube,
            StatSampling.Sobol => SamplingMethod.Sobol,
            _                  => SamplingMethod.Random,
        };
        _plan = SamplingPlan.Create(method, unchecked((ulong)seed), [.. Streams.Select(StatStreams.Id)], trials);
        if (_plan.RandomFallbacks > 0)
            _notes.Add(StatisticsDiagnostics.SobolPastTable(Sobol.MaxDimensions, _plan.RandomFallbacks));

        _correlated = [];
        if (correlation is not null)
        {
            var keep = correlation.Keys.Select((k, i) => (k, i)).Where(x => Streams.Contains(x.k)).ToList();
            if (keep.Count > 1)
            {
                var m = new double[keep.Count, keep.Count];
                for (int a = 0; a < keep.Count; a++)
                    for (int b = 0; b < keep.Count; b++) m[a, b] = correlation.Matrix[keep[a].i, keep[b].i];
                _copula = new GaussianCopula(m);
                _correlated = [.. keep.Select(x => _streams.IndexOf(x.k))];
                // A principal submatrix of a valid matrix is valid, so a repair here is the setup's own.
                if (correlation.Repaired || _copula.Repaired)
                    _notes.Add(StatisticsDiagnostics.CorrelationRepaired(Math.Max(correlation.LargestChange, _copula.LargestChange)));
            }
        }
    }

    /// <summary>The sampler for <paramref name="setup"/>: its statistical entries (stat on, with a
    /// distribution) in setup order, its <c>statistics</c> settings, its correlations.</summary>
    public static StatisticalSampler For(TuningSetup setup)
    {
        var s = setup.Statistics ?? new StatisticsSettings();
        return new StatisticalSampler(setup.Variables.Where(e => e.IsStatistical).Select(e => e.Key),
                                      s.Sampling, s.EffectiveSeed, s.EffectiveTrials, StatisticsValidator.CorrelationOf(setup));
    }

    public IReadOnlyList<string> Streams => _streams;
    public int Seed { get; }
    public StatSampling Sampling { get; }
    public int Trials { get; }

    /// <summary>What the run report states: a repaired correlation matrix, Sobol variables past its table.</summary>
    public IReadOnlyList<Diagnostic> Notes => _notes;

    /// <summary>Trial <paramref name="trial"/> (from 1).</summary>
    public StatisticalSample Sample(int trial)
    {
        var z = _plan.Normals(trial);
        if (_copula is not null)
        {
            var c = new double[_correlated.Length];
            for (int i = 0; i < c.Length; i++) c[i] = z[_correlated[i]];
            _copula.Correlate(c);
            for (int i = 0; i < c.Length; i++) z[_correlated[i]] = c[i];
        }
        var map = new Dictionary<string, double>(Streams.Count, StringComparer.Ordinal);
        for (int i = 0; i < Streams.Count; i++) map[Streams[i]] = z[i];
        return new StatisticalSample(trial, map);
    }
}
