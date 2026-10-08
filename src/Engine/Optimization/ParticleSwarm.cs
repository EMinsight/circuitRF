namespace CircuitRF.Engine.Optimization;

/// <summary>
/// Particle swarm in Clerc and Kennedy's constriction-factor form (brief-tuneopt-7 R-to7-2):
/// vᵢ ← χ·(vᵢ + c₁·r₁·(pᵢ − xᵢ) + c₂·r₂·(lᵢ − xᵢ)), xᵢ ← xᵢ + vᵢ, with
/// χ = 2 / |2 − φ − √(φ² − 4φ)|, φ = c₁ + c₂ (0.7298 at the default 2.05 + 2.05), r₁ and r₂ uniform per
/// coordinate, pᵢ the particle's own best and lᵢ its neighbourhood's best — its two ring neighbours
/// and itself (<c>topology=ring</c>, the default: slower to agree, so better on many local minima) or
/// the whole swarm (<c>global</c>).
///
/// <para>Each velocity coordinate is clamped to ±<c>vmax</c>. A particle that leaves the box is
/// <b>reflected</b> back into it, its velocity coordinate reversed.</para>
///
/// <para><b>One swarm step is one batch and one iteration.</b> The first particle starts at the start
/// point; the rest are a Latin hypercube over the box, each with a velocity of half the way to another
/// random point. The run ends when every personal best is within <c>xtol</c> of the swarm's best and
/// no particle moves more than that.</para>
///
/// <para><b>Failed and infeasible points</b> (R-to7-8) never become a personal best over an evaluated
/// one, whatever their penalty costs; among themselves they rank by cost, so a particle that starts
/// infeasible still moves toward feasibility.</para>
///
/// <para>Options and their defaults: <c>pso</c> in <see cref="CircuitRF.Core.Design.OptimizerAlgorithms"/>.</para>
/// </summary>
public sealed class ParticleSwarm : AskTellAlgorithm
{
    public const string AlgorithmId = "pso";

    private readonly ulong  _seed;
    private readonly int    _swarm;
    private readonly bool   _ring;
    private readonly double _vmax, _c1, _c2, _chi, _xtol;

    public ParticleSwarm(double[] start, ulong seed, IReadOnlyDictionary<string, string>? options = null)
        : base(AlgorithmId, start)
    {
        int n = start.Length;
        _seed  = seed;
        _swarm = Math.Max(3, (int)Option(options, "swarm", Math.Max(30, 10 * n)));
        string topology = WordOption(options, "topology");
        _ring  = topology switch
        {
            "ring"   => true,
            "global" => false,
            _        => throw new ArgumentException($"pso: topology={topology} is not ring or global."),
        };
        _vmax  = Math.Clamp(Option(options, "vmax"), 1e-6, 1.0);
        _c1    = Option(options, "c1");
        _c2    = Option(options, "c2");
        double phi = _c1 + _c2;
        _chi   = phi > 4 ? 2 / Math.Abs(2 - phi - Math.Sqrt(phi * phi - 4 * phi)) : 0.7298;
        _xtol  = Option(options, "xtol");
    }

    protected override IEnumerable<double[][]> Run()
    {
        int n = Dimension, s = _swarm;
        var rng = new SplitMix64(_seed);

        var x = new double[s][];
        var v = new double[s][];
        x[0] = (double[])Start.Clone();
        var lhs = DifferentialEvolution.LatinHypercube(rng, s - 1, n);
        for (int i = 1; i < s; i++) x[i] = lhs[i - 1];
        for (int i = 0; i < s; i++)
        {
            v[i] = new double[n];
            for (int d = 0; d < n; d++) v[i][d] = Math.Clamp((rng.NextDouble() - x[i][d]) / 2, -_vmax, _vmax);
        }

        yield return [.. x.Select(p => (double[])p.Clone())];
        var p  = x.Select(q => (double[])q.Clone()).ToArray();
        var pf = Results.ToArray();
        CompleteIteration();

        while (true)
        {
            int g = Ranking(pf)[0];
            double spread = 0, speed = 0;
            for (int i = 0; i < s; i++)
            {
                spread = Math.Max(spread, InfNorm(p[i], p[g]));
                speed  = Math.Max(speed, v[i].Max(Math.Abs));
            }
            if (spread < _xtol && speed < _xtol)
            {
                Finish("the swarm converged onto its best point");
                yield break;
            }

            for (int i = 0; i < s; i++)
            {
                int l = g;
                if (_ring)
                {
                    int a = (i + s - 1) % s, b = (i + 1) % s;
                    l = i;
                    if (Better(pf[a], pf[l])) l = a;
                    if (Better(pf[b], pf[l])) l = b;
                }
                for (int d = 0; d < n; d++)
                {
                    double vd = _chi * (v[i][d] + _c1 * rng.NextDouble() * (p[i][d] - x[i][d])
                                                + _c2 * rng.NextDouble() * (p[l][d] - x[i][d]));
                    vd = Math.Clamp(vd, -_vmax, _vmax);
                    double xd = x[i][d] + vd;
                    for (int k = 0; k < 4 && (xd < 0 || xd > 1); k++)
                    {
                        if (xd < 0) { xd = -xd; vd = -vd; }
                        if (xd > 1) { xd = 2 - xd; vd = -vd; }
                    }
                    x[i][d] = Math.Clamp(xd, 0, 1);
                    v[i][d] = vd;
                }
            }

            yield return [.. x.Select(q => (double[])q.Clone())];
            for (int i = 0; i < s; i++)
                if (Better(Results[i], pf[i])) { p[i] = (double[])x[i].Clone(); pf[i] = Results[i]; }
            CompleteIteration();
        }
    }
}
