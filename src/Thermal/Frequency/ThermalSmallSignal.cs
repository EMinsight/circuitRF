// brief-em3d-80 R-em3d80-1/-2 — the small-signal thermal system of one structure: the Rth matrix across its heat sources, and
// Z_th(jω) from (K + jωC)·T = q.
//
// HOMOGENEOUS BOUNDARIES (R-em3d80-1a). Every fixed temperature — a fixed face, and a submodel's fixed field — is 0, and
// every convection ambient is 0. The fixed nodes are still eliminated and the convection faces still carry h·∫NᵢNⱼ, so the
// matrix is the problem's own; only the offsets that do not scale with power are gone. Superposition then holds, and R is a
// property of the structure, not of the heatsink's temperature.
//
// ONE WATT PER SOURCE. A source's right-hand side is a uniform flux over its surface tags (or a uniform density through its
// regions), scaled so the load sums to 1 W. Its "Avg" rise is then fᵢᵀT — the load-weighted mean over its own place, which for
// a uniform source IS the area (volume) mean — and R_ij = fᵢᵀK⁻¹fⱼ is symmetric exactly when K is. "Max" is the largest nodal
// rise over the source's nodes, and is not expected to be symmetric.
//
// THE MATRIX. With constant k (or k(T) off) it is the secant K, symmetric and exact. With k(T) on and an operating point given
// it is the Newton tangent J = K(T₀) + ∫(dk/dT)Nⱼ∇Nᵢ·∇T₀ — the small-signal response about T₀, nonsymmetric, and the caller
// says so.

using System.Numerics;
using CircuitRF.Thermal.Solvers;

namespace CircuitRF.Thermal.Frequency;

/// <summary>A heat source as the small-signal analyses see it: 1 W spread uniformly over its surface tags (by area) and its
/// regions (by volume).</summary>
public sealed record SmallSignalSource(string Name, IReadOnlyList<int> SurfaceTags, IReadOnlyList<int> Regions);

/// <summary>Which statistic of a source's own place its rise is read as.</summary>
public enum RthStatistic
{
    /// <summary>The mean over the source's place (fᵢᵀT): the matrix is symmetric.</summary>
    Avg,
    /// <summary>The largest nodal rise over the source's place: not symmetric in general.</summary>
    Max,
}

/// <summary>The Rth matrix: R[i, j] = rise of source i per watt in source j, K/W.</summary>
public sealed class RthMatrix
{
    public required IReadOnlyList<string> Names { get; init; }
    public required double[,] R { get; init; }
    public required RthStatistic Statistic { get; init; }
    /// <summary>max |R_ij − R_ji| / max |R|.</summary>
    public required double Asymmetry { get; init; }
    /// <summary>True when the matrix is the tangent about an operating point (k(T) on).</summary>
    public required bool Tangent { get; init; }
    public required ThermalSolverKind Solver { get; init; }
    public required int Unknowns { get; init; }
    public string? FallbackNote { get; init; }
}

/// <summary>Z_th over a frequency list: Z[f][i, j], the mean rise phasor over source i's place per watt in source j, K/W.</summary>
public sealed class ZthSweep
{
    public required IReadOnlyList<double> FrequenciesHz { get; init; }
    public required IReadOnlyList<Complex[,]> Z { get; init; }
    public required ThermalSolverKind Solver { get; init; }
    /// <summary>The iterative solves' largest iteration count (0 on the direct path).</summary>
    public required int MaxIterations { get; init; }
    /// <summary>The largest relative residual any solve returned.</summary>
    public required double MaxResidual { get; init; }
    public string? FallbackNote { get; init; }
    /// <summary>brief-em3d-86 — how many AMG hierarchies the sweep built (a close frequency reuses the last one's).</summary>
    public int PreconditionerBuilds { get; init; }
}

public sealed class ThermalSmallSignal
{
    private readonly ThermalProblem _problem;
    private readonly ThermalAssembly _assembly;
    private readonly int[] _free;
    private readonly int _nFree;
    private readonly SparseRows _k;
    private readonly double[][] _loads;
    private readonly int[][] _nodes;

    /// <summary>
    /// The small-signal system of <paramref name="problem"/> (its sources and fixed values are ignored: every boundary is made
    /// homogeneous). With <paramref name="kOfT"/>, a region with k(T) and an <paramref name="operatingPoint"/>, the matrix is the
    /// tangent about that field; otherwise every region at its nominal k.
    /// </summary>
    public ThermalSmallSignal(ThermalProblem problem, IReadOnlyList<SmallSignalSource> sources, double[]? operatingPoint, bool kOfT,
                              ThermalAssembly? assembly = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(sources);
        if (problem.Fixed.Count == 0 && problem.FixedFields.Count == 0 && problem.Convection.Count == 0)
            throw new InvalidOperationException("no fixed-temperature or convection condition: no steady state");
        var m = problem.Mesh;
        _problem = problem;
        _assembly = assembly ?? new ThermalAssembly(m);
        Sources = sources;
        var (fixedT, _, _) = ThermalSolver.FixedNodes(problem);
        _free = new int[m.NodeCount];
        for (int i = 0; i < _free.Length; i++) _free[i] = double.IsNaN(fixedT[i]) ? _nFree++ : -1;
        if (_nFree == m.NodeCount && problem.Convection.Count == 0)
            throw new InvalidOperationException("the fixed-temperature faces select no triangle: no steady state");

        Tangent = kOfT && operatingPoint is not null && problem.Conductivity.Any(c => !c.IsConstant);
        if (operatingPoint is not null && operatingPoint.Length != m.NodeCount) throw new ArgumentException("operating point length", nameof(operatingPoint));
        var sys = _assembly.Assemble(problem, Tangent ? operatingPoint : null, kOfT: Tangent, tangent: Tangent);
        _k = ThermalSolver.Reduce(_assembly.Matrix(Tangent ? sys.Tangent ?? sys.Secant : sys.Secant), _free, _nFree);

        _loads = new double[sources.Count][];
        _nodes = new int[sources.Count][];
        for (int j = 0; j < sources.Count; j++)
        {
            var s = sources[j];
            var f = _assembly.SourceLoad([.. s.Regions.Select(r => new VolumeSource(r, 1))], [.. s.SurfaceTags.Select(t => new SurfaceSource(t, 1))]);
            double total = f.Sum();
            if (!(total > 0)) throw new ArgumentException($"heat source '{s.Name}' has no area or volume in the mesh", nameof(sources));
            for (int i = 0; i < f.Length; i++) f[i] /= total;
            _loads[j] = f;
            _nodes[j] = NodesOf(m, s);
        }
    }

    public IReadOnlyList<SmallSignalSource> Sources { get; }

    /// <summary>True when the matrix is the Newton tangent about the operating point (nonsymmetric).</summary>
    public bool Tangent { get; }

    /// <summary>The free (unfixed) node count.</summary>
    public int Unknowns => _nFree;

    /// <summary>Source <paramref name="j"/>'s 1 W load vector over every node.</summary>
    public double[] Load(int j) => _loads[j];

    /// <summary>R-em3d80-1 — the Rth matrix: one preparation, one solve per source. <paramref name="field"/>, when given,
    /// receives each source's rise field (every node, K per watt; fixed nodes 0).</summary>
    public RthMatrix Rth(RthStatistic stat, ThermalSolveOptions options, Action<int, double[]>? field = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        int n = Sources.Count;
        var prepared = PreparedSolve.Create(_k, !Tangent, options.Solver, options);
        var r = new double[n, n];
        var b = new double[_nFree];
        var x = new double[_nFree];
        var t = new double[_free.Length];
        for (int j = 0; j < n; j++)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            Reduce(_loads[j], b);
            Array.Clear(x);
            prepared.Solve(b, x);
            Expand(x, t);
            for (int i = 0; i < n; i++)
                r[i, j] = stat == RthStatistic.Avg ? Dot(_loads[i], t) : _nodes[i].Max(v => t[v]);
            field?.Invoke(j, t);
        }
        double big = 0, asym = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                big = Math.Max(big, Math.Abs(r[i, j]));
                asym = Math.Max(asym, Math.Abs(r[i, j] - r[j, i]));
            }
        return new RthMatrix
        {
            Names = [.. Sources.Select(s => s.Name)], R = r, Statistic = stat, Asymmetry = big > 0 ? asym / big : 0, Tangent = Tangent,
            Solver = prepared.Kind, Unknowns = _nFree, FallbackNote = prepared.FallbackNote,
        };
    }

    /// <summary>brief-em3d-86 — the most frequencies solved at once. Each holds its own preconditioner and vectors.</summary>
    public const int MaxParallelFrequencies = 6;

    /// <summary>
    /// R-em3d80-2 — Z_th at each of <paramref name="frequenciesHz"/> (0 allowed: the Rth case), with <paramref name="rhoC"/>
    /// the volumetric heat capacity of each region, J/(m³·K). <paramref name="each"/>, when given, receives every solved field
    /// (frequency index, source index, the rise phasor at every node per watt) — CONCURRENTLY for different frequencies, from
    /// several threads. <paramref name="stopAfter"/>, when given, is asked in frequency order, after each frequency (its index
    /// and its Z), whether to stop there: the sweep then ends early and holds what was asked about.
    /// <para>brief-em3d-86 — the frequencies are independent solves, and each is single-threaded, so up to
    /// <see cref="MaxParallelFrequencies"/> run at once: a plain sweep as contiguous CHUNKS (each warm-starting from its
    /// previous frequency and reusing a close one's preconditioner, as the serial sweep did), a stopping one in BATCHES of that
    /// many, asked about in order.</para>
    /// </summary>
    public ZthSweep Zth(IReadOnlyList<double> rhoC, IReadOnlyList<double> frequenciesHz, ThermalSolveOptions options,
                        Action<int, int, Complex[]>? each = null, Func<int, Complex[,], bool>? stopAfter = null)
    {
        ArgumentNullException.ThrowIfNull(rhoC);
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(options);
        if (rhoC.Count < _problem.Conductivity.Count) throw new ArgumentException("a region has no heat capacity", nameof(rhoC));
        var c = ThermalSolver.Reduce(_assembly.Matrix(_assembly.Mass(rhoC)), _free, _nFree);
        int n = Sources.Count, count = frequenciesHz.Count;
        var results = new Complex[count][,];
        var gate = new object();
        var kind = options.Solver;
        int maxIt = 0, builds = 0;
        double maxRes = 0;
        string? fallback = null;
        int degree = Math.Clamp(options.MaxDegreeOfParallelism ?? Environment.ProcessorCount, 1, MaxParallelFrequencies);
        var po = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = options.Cancellation };

        // frequencies [from, to), in order, on this thread: warm starts and a close frequency's preconditioner carried along
        void Chunk(int from, int to)
        {
            var b = new Complex[_nFree];
            var previous = new Complex[n][];
            var t = new Complex[_free.Length];
            var myKind = kind;
            ComplexSolve? last = null;
            for (int fi = from; fi < to; fi++)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                double omega = 2 * Math.PI * frequenciesHz[fi];
                var solve = ComplexSolve.Create(_k, c, omega, !Tangent, myKind, options, last);
                last = solve;
                var zf = new Complex[n, n];
                int it = 0;
                double res = 0;
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < _free.Length; i++) if (_free[i] >= 0) b[_free[i]] = _loads[j][i];
                    // the previous frequency's answer starts an iterative solve; the direct one ignores it
                    var x = previous[j] is { } p ? (Complex[])p.Clone() : new Complex[_nFree];
                    res = Math.Max(res, solve.Solve(b, x));
                    it = Math.Max(it, solve.LastIterations);
                    previous[j] = x;
                    for (int i = 0; i < _free.Length; i++) t[i] = _free[i] >= 0 ? x[_free[i]] : Complex.Zero;
                    for (int i = 0; i < n; i++)
                    {
                        Complex s = 0;
                        var li = _loads[i];
                        for (int v = 0; v < li.Length; v++) if (li[v] != 0) s += li[v] * t[v];
                        zf[i, j] = s;
                    }
                    each?.Invoke(fi, j, t);
                }
                myKind = solve.Kind;                             // once fallen back, stay direct
                results[fi] = zf;
                lock (gate)
                {
                    maxRes = Math.Max(maxRes, res);
                    maxIt = Math.Max(maxIt, it);
                    if (solve.BuiltPreconditioner) builds++;
                    fallback ??= solve.FallbackNote;
                    if (solve.Kind == ThermalSolverKind.Direct) kind = ThermalSolverKind.Direct;
                }
            }
        }

        var z = new List<Complex[,]>();
        if (stopAfter is null)
        {
            int chunks = Math.Min(degree, count);
            Parallel.For(0, chunks, po, k => Chunk(count * k / chunks, count * (k + 1) / chunks));
            z.AddRange(results);
        }
        else
            for (int start = 0; start < count; start += degree)
            {
                int end = Math.Min(count, start + degree);
                Parallel.For(start, end, po, fi => Chunk(fi, fi + 1));
                bool stop = false;
                for (int fi = start; fi < end && !stop; fi++)
                {
                    z.Add(results[fi]);
                    stop = stopAfter(fi, results[fi]);
                }
                if (stop) break;
            }
        return new ZthSweep { FrequenciesHz = [.. frequenciesHz.Take(z.Count)], Z = z, Solver = kind, MaxIterations = maxIt, MaxResidual = maxRes, FallbackNote = fallback,
                              PreconditionerBuilds = builds };
    }

    private void Reduce(double[] full, double[] reduced)
    {
        for (int i = 0; i < full.Length; i++) if (_free[i] >= 0) reduced[_free[i]] = full[i];
    }

    private void Expand(double[] reduced, double[] full)
    {
        for (int i = 0; i < full.Length; i++) full[i] = _free[i] >= 0 ? reduced[_free[i]] : 0;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != 0) s += a[i] * b[i];
        return s;
    }

    /// <summary>Every node of the source's triangles and tetrahedra.</summary>
    private static int[] NodesOf(ThermalMesh m, SmallSignalSource s)
    {
        var set = new HashSet<int>();
        var tags = s.SurfaceTags.ToHashSet();
        int nf = m.NodesPerTriangle, nn = m.NodesPerTet;
        for (int t = 0; t < m.TriangleCount; t++)
            if (tags.Contains(m.TriangleTag[t])) for (int k = 0; k < nf; k++) set.Add(m.Triangles[nf * t + k]);
        var regions = s.Regions.ToHashSet();
        for (int e = 0; e < m.TetCount; e++)
            if (regions.Contains(m.TetRegion[e])) for (int k = 0; k < nn; k++) set.Add(m.Tets[nn * e + k]);
        return [.. set.Order()];
    }
}
