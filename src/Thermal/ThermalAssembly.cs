// brief-em3d-74 R-em3d74-2b — assembly: stiffness ∫k∇Nᵢ·∇Nⱼ, volumetric source ∫qNᵢ, surface source ∫q″Nᵢ, Robin ∫hNᵢNⱼ and
// ∫hT∞Nᵢ, and — for a Newton step over k(T) — the tangent's ∫(dk/dT)Nⱼ ∇Nᵢ·∇T. Quadrature: the 14-point degree-5 rule in
// the volume, the 6-point degree-4 rule on a surface (ReferenceElement says why).
//
// PARALLEL AND BIT-FOR-BIT DETERMINISTIC. The elements are coloured greedily, in element order, so that no two elements
// of one colour share a node; the colours are assembled one after another and the elements of a colour in parallel. Each
// matrix slot then receives its contributions in colour order and at most one per colour — the same additions in the
// same order on every run and at every thread count, so the same mesh gives the same matrix bits (gate 6 hashes it).
// Two elements sharing a mid-edge node share that edge's two corners, so colouring on the corners is enough.
//
// Dirichlet conditions are applied later, by elimination (ThermalSolver), not by penalty.

using CircuitRF.Thermal.Solvers;

namespace CircuitRF.Thermal;

/// <summary>What one assembly produced: the matrix values on <see cref="ThermalAssembly.Pattern"/>, the load, and the
/// source power the load carries.</summary>
public sealed class AssembledSystem
{
    /// <summary>K(T): the secant matrix — stiffness at the evaluated conductivities plus the Robin mass.</summary>
    public required double[] Secant { get; init; }
    /// <summary>The Newton tangent (Secant plus the dk/dT term), or null when not asked for or k is constant.</summary>
    public double[]? Tangent { get; init; }
    /// <summary>Sources plus the Robin term hT∞.</summary>
    public required double[] Load { get; init; }
    /// <summary>∫q + ∫q″ — the heat the sources put in, W.</summary>
    public required double SourcePowerW { get; init; }
    /// <summary>A conductivity held at its table's end, say (reported by the caller, never silently).</summary>
    public bool AnyNonFinite { get; init; }
}

/// <summary>The sparsity pattern and colouring of one mesh, and its assembly.</summary>
public sealed class ThermalAssembly
{
    private readonly ThermalMesh _mesh;
    private readonly int[][] _colours;
    private readonly int? _dop;

    public ThermalAssembly(ThermalMesh mesh, int? maxDegreeOfParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _mesh = mesh;
        _dop = maxDegreeOfParallelism;
        Pattern = BuildPattern(mesh, _dop);
        _colours = Colour(mesh);
    }

    /// <summary>The matrix's structure (values zero): every pair of nodes sharing a tetrahedron.</summary>
    public SparseRows Pattern { get; }

    /// <summary>How many colours the elements took.</summary>
    public int ColourCount => _colours.Length;

    /// <summary>A matrix with this pattern and <paramref name="values"/>.</summary>
    public SparseRows Matrix(double[] values) => new(Pattern.Rows, Pattern.Cols, Pattern.Ptr, Pattern.Idx, values);

    /// <summary>
    /// Assembles <paramref name="problem"/>. With <paramref name="temperature"/> null every region takes its nominal k;
    /// otherwise a region with k(T) is evaluated at each quadrature point's interpolated temperature (when
    /// <paramref name="kOfT"/>), and <paramref name="tangent"/> adds the Newton term.
    /// </summary>
    public AssembledSystem Assemble(ThermalProblem problem, double[]? temperature, bool kOfT, bool tangent)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var m = _mesh;
        int n = m.NodeCount, order = m.Order, nn = m.NodesPerTet;
        var cond = problem.Conductivity;
        bool varying = kOfT && temperature is not null && cond.Any(c => !c.IsConstant);
        bool withTangent = tangent && varying;
        var secant = new double[Pattern.Nnz];
        var tan = withTangent ? new double[Pattern.Nnz] : null;
        var load = new double[n];
        var qByRegion = new double[cond.Count];
        foreach (var s in problem.VolumeSources)
            if ((uint)s.Region < (uint)qByRegion.Length) qByRegion[s.Region] += s.PowerDensityWm3;
        var shapes = TetShapes.For(order);
        int nq = ReferenceElement.TetQuadraturePoints;
        bool nonFinite = false;
        var po = new ParallelOptions { MaxDegreeOfParallelism = _dop ?? -1 };

        foreach (var colour in _colours)
        {
            Parallel.For(0, colour.Length, po, ci =>
            {
                int e = colour[ci];
                Span<double> xyz = stackalloc double[30];
                Span<double> gx = stackalloc double[10], gy = stackalloc double[10], gz = stackalloc double[10];
                Span<double> ke = stackalloc double[100];
                Span<double> je = stackalloc double[100];
                Span<double> fe = stackalloc double[10];
                Span<int> nodes = stackalloc int[10];
                ke.Clear(); je.Clear(); fe.Clear();
                for (int i = 0; i < nn; i++)
                {
                    int v = m.Tets[nn * e + i];
                    nodes[i] = v;
                    xyz[3 * i] = m.Nodes[3 * v]; xyz[3 * i + 1] = m.Nodes[3 * v + 1]; xyz[3 * i + 2] = m.Nodes[3 * v + 2];
                }
                int region = m.TetRegion[e];
                var c = cond[region];
                double q = qByRegion[region];
                for (int qp = 0; qp < nq; qp++)
                {
                    var nq_ = shapes.N(qp);
                    if (!ReferenceElement.TetGradients(xyz, nn, shapes.Dx(qp), shapes.Dy(qp), shapes.Dz(qp), gx, gy, gz, out double det))
                    { nonFinite = true; continue; }
                    double w = ReferenceElement.TetW[qp] * det;
                    double k = c.Nominal, dk = 0, tx = 0, ty = 0, tz = 0;
                    if (varying && !c.IsConstant)
                    {
                        double t = 0;
                        for (int i = 0; i < nn; i++)
                        {
                            double ti = temperature![nodes[i]];
                            t += nq_[i] * ti; tx += gx[i] * ti; ty += gy[i] * ti; tz += gz[i] * ti;
                        }
                        (k, dk) = c.OfT!(t);
                        if (!double.IsFinite(k) || !double.IsFinite(dk)) nonFinite = true;
                    }
                    double wk = w * k;
                    for (int i = 0; i < nn; i++)
                    {
                        for (int j = 0; j < nn; j++)
                            ke[10 * i + j] += wk * (gx[i] * gx[j] + gy[i] * gy[j] + gz[i] * gz[j]);
                        if (q != 0) fe[i] += w * q * nq_[i];
                        if (withTangent && dk != 0)
                        {
                            double gi = w * dk * (gx[i] * tx + gy[i] * ty + gz[i] * tz);
                            for (int j = 0; j < nn; j++) je[10 * i + j] += gi * nq_[j];
                        }
                    }
                }
                for (int i = 0; i < nn; i++)
                {
                    int row = nodes[i];
                    load[row] += fe[i];
                    for (int j = 0; j < nn; j++)
                    {
                        int s = Pattern.Slot(row, nodes[j]);
                        secant[s] += ke[10 * i + j];
                        if (tan is not null) tan[s] += ke[10 * i + j] + je[10 * i + j];
                    }
                }
            });
        }

        // Surfaces, in triangle order: sources and Robin terms.
        double sourceW = 0;
        foreach (double f in load) sourceW += f;
        var flux = new Dictionary<int, double>();
        foreach (var s in problem.SurfaceSources) flux[s.Tag] = flux.GetValueOrDefault(s.Tag) + s.FluxWm2;
        var robin = new Dictionary<int, List<ConvectionCondition>>();
        foreach (var r in problem.Convection) (robin.TryGetValue(r.Tag, out var l) ? l : robin[r.Tag] = []).Add(r);
        if (flux.Count > 0 || robin.Count > 0)
        {
            int nf = m.NodesPerTriangle;
            var tri = TriShapes.For(order);
            Span<double> xyz = stackalloc double[18];
            Span<int> nodes = stackalloc int[6];
            for (int t = 0; t < m.TriangleCount; t++)
            {
                int tag = m.TriangleTag[t];
                bool hasFlux = flux.TryGetValue(tag, out double qf);
                bool hasRobin = robin.TryGetValue(tag, out var conds);
                if (!hasFlux && !hasRobin) continue;
                for (int i = 0; i < nf; i++)
                {
                    int v = m.Triangles[nf * t + i];
                    nodes[i] = v;
                    xyz[3 * i] = m.Nodes[3 * v]; xyz[3 * i + 1] = m.Nodes[3 * v + 1]; xyz[3 * i + 2] = m.Nodes[3 * v + 2];
                }
                for (int qp = 0; qp < ReferenceElement.TriQuadraturePoints; qp++)
                {
                    var nqp = tri.N(qp);
                    double w = ReferenceElement.TriW[qp] * ReferenceElement.TriJacobian(xyz, nf, tri.Du(qp), tri.Dv(qp));
                    for (int i = 0; i < nf; i++)
                    {
                        if (hasFlux) { double add = w * qf * nqp[i]; load[nodes[i]] += add; sourceW += add; }
                        if (!hasRobin) continue;
                        foreach (var rc in conds!)
                        {
                            load[nodes[i]] += w * rc.HWm2K * rc.AmbientC * nqp[i];
                            for (int j = 0; j < nf; j++)
                            {
                                int s = Pattern.Slot(nodes[i], nodes[j]);
                                if (s < 0) throw new InvalidOperationException("surface triangle outside the pattern");
                                double add = w * rc.HWm2K * nqp[i] * nqp[j];
                                secant[s] += add;
                                if (tan is not null) tan[s] += add;
                            }
                        }
                    }
                }
            }
        }
        return new AssembledSystem { Secant = secant, Tangent = tan, Load = load, SourcePowerW = sourceW, AnyNonFinite = nonFinite };
    }

    /// <summary>∫h(T − T∞) over each convection condition's triangles: the heat leaving by convection, W.</summary>
    public double ConvectionOut(ThermalProblem problem, double[] temperature)
    {
        var m = _mesh;
        int nf = m.NodesPerTriangle;
        var tri = TriShapes.For(m.Order);
        double total = 0;
        Span<double> xyz = stackalloc double[18];
        foreach (var rc in problem.Convection)
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (m.TriangleTag[t] != rc.Tag) continue;
                for (int i = 0; i < nf; i++)
                {
                    int v = m.Triangles[nf * t + i];
                    xyz[3 * i] = m.Nodes[3 * v]; xyz[3 * i + 1] = m.Nodes[3 * v + 1]; xyz[3 * i + 2] = m.Nodes[3 * v + 2];
                }
                for (int qp = 0; qp < ReferenceElement.TriQuadraturePoints; qp++)
                {
                    var nqp = tri.N(qp);
                    double tq = 0;
                    for (int i = 0; i < nf; i++) tq += nqp[i] * temperature[m.Triangles[nf * t + i]];
                    total += ReferenceElement.TriW[qp] * ReferenceElement.TriJacobian(xyz, nf, tri.Du(qp), tri.Dv(qp)) *
                             rc.HWm2K * (tq - rc.AmbientC);
                }
            }
        return total;
    }

    // ── structure ──────────────────────────────────────────────────────────────────────────────────────────

    private static SparseRows BuildPattern(ThermalMesh m, int? dop)
    {
        int n = m.NodeCount, nn = m.NodesPerTet, ne = m.TetCount;
        var start = new int[n + 1];
        foreach (int v in m.Tets) start[v + 1]++;
        for (int i = 0; i < n; i++) start[i + 1] += start[i];
        var fill = (int[])start.Clone();
        var elems = new int[m.Tets.Length];
        for (int e = 0; e < ne; e++)
            for (int k = 0; k < nn; k++) elems[fill[m.Tets[nn * e + k]]++] = e;

        var rows = new int[n][];
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = dop ?? -1 }, () => new List<int>(128), (i, _, cols) =>
        {
            cols.Clear();
            for (int p = start[i]; p < start[i + 1]; p++)
            {
                int e = elems[p];
                for (int k = 0; k < nn; k++) cols.Add(m.Tets[nn * e + k]);
            }
            cols.Sort();
            int u = 0;
            for (int k = 0; k < cols.Count; k++) if (u == 0 || cols[k] != cols[u - 1]) cols[u++] = cols[k];
            rows[i] = [.. cols.Take(u)];
            return cols;
        }, _ => { });
        var ptr = new int[n + 1];
        for (int i = 0; i < n; i++) ptr[i + 1] = ptr[i] + rows[i].Length;
        var idx = new int[ptr[n]];
        for (int i = 0; i < n; i++) rows[i].CopyTo(idx, ptr[i]);
        return new SparseRows(n, n, ptr, idx, new double[idx.Length]);
    }

    /// <summary>Greedy colouring on the corner nodes, in element order: element e takes the lowest colour none of its corners
    /// has been given yet. Deterministic, sequential, one bitset of W words per node (grown when a node runs out).</summary>
    private static int[][] Colour(ThermalMesh m)
    {
        int n = m.NodeCount, nn = m.NodesPerTet, ne = m.TetCount;
        int words = 2;
        var bits = new ulong[n * words];
        var colourOf = new int[ne];
        int colours = 0;
        for (int e = 0; e < ne; e++)
        {
            int chosen = -1;
            while (chosen < 0)
            {
                for (int w = 0; w < words && chosen < 0; w++)
                {
                    ulong used = 0;
                    for (int k = 0; k < 4; k++) used |= bits[m.Tets[nn * e + k] * words + w];
                    if (used != ulong.MaxValue) chosen = 64 * w + System.Numerics.BitOperations.TrailingZeroCount(~used);
                }
                if (chosen >= 0) break;
                var grown = new ulong[n * words * 2];
                for (int v = 0; v < n; v++) Array.Copy(bits, v * words, grown, v * 2 * words, words);
                bits = grown;
                words *= 2;
            }
            for (int k = 0; k < 4; k++) bits[m.Tets[nn * e + k] * words + chosen / 64] |= 1UL << (chosen % 64);
            colourOf[e] = chosen;
            colours = Math.Max(colours, chosen + 1);
        }
        var count = new int[colours];
        foreach (int c in colourOf) count[c]++;
        var lists = count.Select(c => new int[c]).ToArray();
        var at = new int[colours];
        for (int e = 0; e < ne; e++) lists[colourOf[e]][at[colourOf[e]]++] = e;
        return lists;
    }
}

/// <summary>The tetrahedron's shape functions and reference derivatives at every volume quadrature point, per order.</summary>
internal sealed class TetShapes
{
    private readonly double[] _n, _dx, _dy, _dz;
    private readonly int _nn;
    private static readonly TetShapes P1 = new(1), P2 = new(2);

    private TetShapes(int order)
    {
        _nn = order == 2 ? 10 : 4;
        int nq = ReferenceElement.TetQuadraturePoints;
        _n = new double[nq * _nn]; _dx = new double[nq * _nn]; _dy = new double[nq * _nn]; _dz = new double[nq * _nn];
        for (int q = 0; q < nq; q++)
            ReferenceElement.Tet(order, ReferenceElement.TetX[q], ReferenceElement.TetY[q], ReferenceElement.TetZ[q],
                                 _n.AsSpan(q * _nn, _nn), _dx.AsSpan(q * _nn, _nn), _dy.AsSpan(q * _nn, _nn), _dz.AsSpan(q * _nn, _nn));
    }

    public static TetShapes For(int order) => order == 2 ? P2 : P1;
    public ReadOnlySpan<double> N(int q) => _n.AsSpan(q * _nn, _nn);
    public ReadOnlySpan<double> Dx(int q) => _dx.AsSpan(q * _nn, _nn);
    public ReadOnlySpan<double> Dy(int q) => _dy.AsSpan(q * _nn, _nn);
    public ReadOnlySpan<double> Dz(int q) => _dz.AsSpan(q * _nn, _nn);
}

/// <summary>The triangle's shape functions and reference derivatives at every surface quadrature point, per order.</summary>
internal sealed class TriShapes
{
    private readonly double[] _n, _du, _dv;
    private readonly int _nn;
    private static readonly TriShapes P1 = new(1), P2 = new(2);

    private TriShapes(int order)
    {
        _nn = order == 2 ? 6 : 3;
        int nq = ReferenceElement.TriQuadraturePoints;
        _n = new double[nq * _nn]; _du = new double[nq * _nn]; _dv = new double[nq * _nn];
        for (int q = 0; q < nq; q++)
            ReferenceElement.Tri(order, ReferenceElement.TriU[q], ReferenceElement.TriV[q],
                                 _n.AsSpan(q * _nn, _nn), _du.AsSpan(q * _nn, _nn), _dv.AsSpan(q * _nn, _nn));
    }

    public static TriShapes For(int order) => order == 2 ? P2 : P1;
    public ReadOnlySpan<double> N(int q) => _n.AsSpan(q * _nn, _nn);
    public ReadOnlySpan<double> Du(int q) => _du.AsSpan(q * _nn, _nn);
    public ReadOnlySpan<double> Dv(int q) => _dv.AsSpan(q * _nn, _nn);
}
