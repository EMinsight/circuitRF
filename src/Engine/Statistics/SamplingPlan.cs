namespace CircuitRF.Engine.Statistics;

/// <summary>How a run's trials are placed (yield overview D5).</summary>
public enum SamplingMethod
{
    /// <summary>Independent draws from each stream.</summary>
    Random,
    /// <summary>Latin hypercube: each stream's N draws fall one in each of N equal-probability strata.</summary>
    LatinHypercube,
    /// <summary>A scrambled Sobol sequence, one dimension per stream.</summary>
    Sobol,
}

/// <summary>
/// Where each trial sits in z-space: for trial t (1-based) one INDEPENDENT standard normal per stream,
/// in the order the streams were given. Correlation (<see cref="GaussianCopula"/>) and the marginals
/// come after. Every value is a pure function of (method, seed, trial count, trial, stream), so a trial
/// is the same computed alone or in any batch, on any thread.
///
/// <list type="bullet">
/// <item><b>Random</b> — Φ⁻¹ of the stream's uniform (<see cref="StatStreams"/>). A stream's draws do
/// not depend on which other streams exist.</item>
/// <item><b>Latin hypercube</b> — u = (π_s(t−1) + v)/N with π_s a permutation of 0 … N−1 and v a
/// jitter, both drawn from the stream, so each stream puts exactly one draw in each stratum. Needs N
/// up front.</item>
/// <item><b>Sobol</b> — streams take dimensions in ascending order of their 64-bit ids, so the mapping
/// does not depend on the order streams were discovered in; point t−1 of the scrambled sequence. A
/// stream past the table's last dimension is drawn at random, and <see cref="RandomFallbacks"/> says
/// how many were.</item>
/// </list>
/// </summary>
public sealed class SamplingPlan
{
    private readonly ulong _seed;
    private readonly ulong[] _streams;
    private readonly int[][]? _permutations;                       // LHS, per stream
    private readonly (int Index, uint[] V, uint Shift)?[]? _sobol;  // Sobol, per stream; null past the table

    private SamplingPlan(SamplingMethod method, ulong seed, ulong[] streams, int trials)
    {
        Method = method;
        _seed = seed;
        _streams = streams;
        Trials = trials;

        if (method == SamplingMethod.LatinHypercube)
        {
            if (trials < 1) throw new ArgumentOutOfRangeException(nameof(trials), "A Latin hypercube needs the trial count.");
            _permutations = [.. streams.Select(s => Permutation(seed, trials, s))];
        }
        else if (method == SamplingMethod.Sobol)
        {
            var order = streams.Select((s, i) => (s, i)).OrderBy(x => x.s).Select(x => x.i).ToArray();
            _sobol = new (int, uint[], uint)?[streams.Length];
            int max = Statistics.Sobol.MaxDimensions;
            for (int d = 0; d < order.Length && d < max; d++)
            {
                var (v, shift) = Statistics.Sobol.Scramble(d, seed);
                _sobol[order[d]] = (d, v, shift);
            }
            RandomFallbacks = Math.Max(0, streams.Length - max);
        }
    }

    /// <param name="streams">The stream ids (<see cref="StatStreams.Id"/>), one per variable.</param>
    /// <param name="trials">The trial count — required by the Latin hypercube, ignored otherwise.</param>
    public static SamplingPlan Create(SamplingMethod method, ulong seed, IReadOnlyList<ulong> streams, int trials = 0)
        => new(method, seed, [.. streams], trials);

    public SamplingMethod Method { get; }

    public int Trials { get; }

    /// <summary>How many streams a Sobol plan draws at random because they lie past its table's last
    /// dimension (<see cref="Statistics.Sobol.MaxDimensions"/>); 0 otherwise.</summary>
    public int RandomFallbacks { get; }

    /// <summary>The independent standard normals of <paramref name="trial"/> (1-based), one per stream.</summary>
    public double[] Normals(int trial)
    {
        var z = new double[_streams.Length];
        Normals(trial, z);
        return z;
    }

    /// <inheritdoc cref="Normals(int)"/>
    public void Normals(int trial, Span<double> z)
    {
        if (trial < 1) throw new ArgumentOutOfRangeException(nameof(trial), "Trials are numbered from 1.");
        if (_permutations is not null && trial > Trials)
            throw new ArgumentOutOfRangeException(nameof(trial), $"A Latin hypercube of {Trials} trials has no trial {trial}.");
        for (int s = 0; s < _streams.Length; s++)
            z[s] = SpecialFunctions.InverseNormalCdf(Uniform(trial, s));
    }

    private double Uniform(int trial, int s)
    {
        ulong stream = _streams[s];
        if (_permutations is { } perms)
            return (perms[s][trial - 1] + StatStreams.Uniform(_seed, trial, stream)) / Trials;
        if (_sobol?[s] is { } sob)
            return ((Statistics.Sobol.Combine(sob.V, trial - 1) ^ sob.Shift) + 0.5) / 4294967296.0;
        return StatStreams.Uniform(_seed, trial, stream);
    }

    /// <summary>A Fisher–Yates permutation of 0 … n−1 drawn from the stream. Its draws sit at a negative
    /// trial number — −n, which no trial has — so they never coincide with a trial's own draws.</summary>
    private static int[] Permutation(ulong seed, int n, ulong stream)
    {
        var p = new int[n];
        for (int i = 0; i < n; i++) p[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = (int)(StatStreams.Uniform(seed, -n, stream, i) * (i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        return p;
    }
}
