// brief-em3d-74 R-em3d74-2c/-2d/-3 — one steady solve: assemble, eliminate the fixed temperatures, solve; Newton over
// k(T) when a region's conductivity follows temperature and k(T) is on; then the heat that left through each fixed face
// and by convection, and the energy balance they must close.
//
// NEWTON (R-em3d74-2d). From the constant-k solution at every region's nominal k (or the previous sweep point's field — a
// warm start), each step solves J·δ = −r with the TANGENT J = K(T) + ∫(dk/dT)Nⱼ∇Nᵢ·∇T, which is nonsymmetric: LU on the
// direct path, BiCGStab with the AMG of its symmetric part on the iterative one. A step whose residual does not fall is
// halved (up to ten times). Converged when the largest nodal update is below Tolerance × the temperature span AND the
// residual has fallen by 1e-8 (relative to the larger of the starting residual and the load, so a warm start already at the
// answer is not asked to fall by 1e-8 of nothing). A point that does not converge is reported with its last residual; it is
// not a runaway (that is brief 77's result, for σ(T)).
//
// THE BALANCE (R-em3d74-3). Σ source power = heat out through the fixed faces (the reactions (F − K·T) at their nodes) +
// heat out by convection (∫h(T − T∞)). It closes to the solve's own residual, so a failure means the solve, the reactions
// or the sources are wrong — the cheapest correctness check there is, and it is always reported.

using CircuitRF.Thermal.Solvers;

namespace CircuitRF.Thermal;

/// <summary>One solve's answer and what it took.</summary>
public sealed class ThermalSolution
{
    /// <summary>Temperature per node, °C.</summary>
    public required double[] Temperature { get; init; }
    public required int Unknowns { get; init; }
    public required ThermalSolverKind Solver { get; init; }
    /// <summary>The last linear solve's iterations (0 for a direct solve).</summary>
    public required int LinearIterations { get; init; }
    public required double LinearResidual { get; init; }
    /// <summary>Newton steps taken; 0 for a linear problem.</summary>
    public int NewtonIterations { get; init; }
    public bool Converged { get; init; } = true;
    /// <summary>The last Newton residual norm relative to its reference, and the last largest update, K.</summary>
    public double NewtonResidual { get; init; }
    public double NewtonUpdateK { get; init; }
    public required double SourcePowerW { get; init; }
    public required double FixedHeatOutW { get; init; }
    public required double ConvectionHeatOutW { get; init; }
    /// <summary>Heat out through each fixed-temperature tag, W.</summary>
    public required IReadOnlyDictionary<int, double> FixedHeatOutByTag { get; init; }
    /// <summary>|in − out| / the larger of in and the total flow.</summary>
    public required double BalanceRelative { get; init; }
    public required int AmgLevels { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>A body with no fixed-temperature face and no convection touches nothing that sets its temperature: no steady
/// state. <see cref="Regions"/> are the mesh regions of every such body.</summary>
public sealed class FloatingRegionsException(int[] regions)
    : InvalidOperationException($"{regions.Length} region(s) ({string.Join(", ", regions)}) touch no fixed-temperature or convection " +
                                "face through any solid: their temperature has no steady state")
{
    public int[] Regions { get; } = regions;
}

public static class ThermalSolver
{
    /// <summary>The energy balance a solve must close to.</summary>
    public const double BalanceTolerance = 1e-6;

    /// <summary>The smallest temperature span Newton's update test is measured against, K.</summary>
    private const double SpanFloorK = 1e-3;

    /// <summary>Solves <paramref name="problem"/>. <paramref name="assembly"/> may be passed to reuse one mesh's pattern and
    /// colouring across sweep points.</summary>
    public static ThermalSolution Solve(ThermalProblem problem, ThermalSolveOptions options, ThermalAssembly? assembly = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(options);
        var m = problem.Mesh;
        if (problem.Fixed.Count == 0 && problem.FixedFields.Count == 0 && problem.Convection.Count == 0)
            throw new InvalidOperationException("no fixed-temperature or convection condition: no steady state");
        if (m.TetRegion.Length > 0 && m.TetRegion.Max() >= problem.Conductivity.Count)
            throw new ArgumentException("a region has no conductivity", nameof(problem));
        assembly ??= new ThermalAssembly(m, options.MaxDegreeOfParallelism);
        var notes = new List<string>();
        int n = m.NodeCount;

        // ── the fixed nodes: the first condition naming a node gives its temperature ──
        var (fixedT, fixedTag, conflicts) = FixedNodes(problem);
        if (conflicts > 0)
            notes.Add($"{conflicts} node(s) lie on two fixed-temperature faces at different temperatures; each takes the first face's.");
        var free = new int[n];
        int nFree = 0;
        for (int i = 0; i < n; i++) free[i] = double.IsNaN(fixedT[i]) ? nFree++ : -1;
        if (nFree == n && problem.Convection.Count == 0)
            throw new InvalidOperationException("the fixed-temperature faces select no triangle: no steady state");
        if (FloatingRegions(problem, fixedT) is { Length: > 0 } floating)
            throw new FloatingRegionsException(floating);

        bool nonlinear = options.KOfT && problem.Conductivity.Any(c => !c.IsConstant);
        var kind = options.Solver == ThermalSolverKind.Auto
            ? nFree < options.DirectBelow ? ThermalSolverKind.Direct : ThermalSolverKind.Iterative
            : options.Solver;

        double[] T = new double[n];
        LinearSolveReport last;
        AssembledSystem sys;
        int newtonIts = 0;
        bool converged = true;
        double newtonRes = 0, newtonUpd = 0;

        if (!nonlinear || options.InitialGuess is null)
        {
            // the constant-k solve: the answer for a linear problem, Newton's start otherwise
            sys = assembly.Assemble(problem, null, kOfT: false, tangent: false);
            last = SolveReduced(assembly.Matrix(sys.Secant), sys.Load, fixedT, free, nFree, T, symmetric: true, kind, options);
            if (last.FallbackNote is { } fb) notes.Add(fb);
        }
        else
        {
            if (options.InitialGuess.Length != n) throw new ArgumentException("initial guess length", nameof(options));
            Array.Copy(options.InitialGuess, T, n);
            for (int i = 0; i < n; i++) if (!double.IsNaN(fixedT[i])) T[i] = fixedT[i];
            if (T.All(double.IsFinite))
            {
                sys = assembly.Assemble(problem, T, kOfT: true, tangent: false);
                last = new LinearSolveReport(kind, 0, 0, true, null);
            }
            else
            {
                // a warm start that is not a field (a previous point that failed) is no start: the constant-k solve is
                sys = assembly.Assemble(problem, null, kOfT: false, tangent: false);
                last = SolveReduced(assembly.Matrix(sys.Secant), sys.Load, fixedT, free, nFree, T, symmetric: true, kind, options);
                if (last.FallbackNote is { } fb) notes.Add(fb);
            }
        }

        if (nonlinear)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            sys = assembly.Assemble(problem, T, kOfT: true, tangent: true);
            var r = Residual(assembly, sys, T, free, nFree);
            double rNorm = SparseRows.Norm(r);
            double reference = Math.Max(rNorm, ReducedLoadNorm(assembly, sys, fixedT, free, nFree));
            if (reference == 0) reference = 1;
            converged = false;
            var delta = new double[nFree];
            var trial = new double[n];
            for (newtonIts = 1; newtonIts <= options.NewtonMaxIterations; newtonIts++)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var j = Reduce(assembly.Matrix(sys.Tangent ?? sys.Secant), free, nFree);
                var rhs = new double[nFree];
                for (int i = 0; i < nFree; i++) rhs[i] = -r[i];
                Array.Clear(delta);
                last = LinearSolver.Solve(j, rhs, delta, symmetric: false, kind, options);
                if (last.FallbackNote is { } fb) notes.Add(fb);
                if (!delta.All(double.IsFinite))
                {
                    // a singular tangent: T keeps its last finite iterate, and the point says it did not converge
                    notes.Add("A Newton step over k(T) was not finite (a singular tangent); the last finite iterate is kept.");
                    break;
                }
                double step = 2;
                AssembledSystem? next = null;
                double[]? rNext = null;
                double rNextNorm = double.PositiveInfinity;
                for (int halving = 0; halving <= 10; halving++)
                {
                    step /= 2;
                    Array.Copy(T, trial, n);
                    for (int i = 0; i < n; i++) if (free[i] >= 0) trial[i] += step * delta[free[i]];
                    next = assembly.Assemble(problem, trial, kOfT: true, tangent: true);
                    rNext = Residual(assembly, next, trial, free, nFree);
                    rNextNorm = SparseRows.Norm(rNext);
                    if (rNextNorm < rNorm || rNorm <= 1e-8 * reference) break;
                }
                if (!double.IsFinite(rNextNorm))
                {
                    notes.Add("Every trial of a Newton step over k(T) gave a non-finite residual; the last finite iterate is kept.");
                    break;
                }
                double maxDelta = 0;
                foreach (double d in delta) maxDelta = Math.Max(maxDelta, Math.Abs(d));
                newtonUpd = step * maxDelta;
                Array.Copy(trial, T, n);
                sys = next!;
                r = rNext!;
                rNorm = rNextNorm;
                newtonRes = rNorm / reference;
                // the span is floored at 1 mK: at an isothermal point (no power, every boundary at one temperature) the span is
                // round-off, and so is every update, which a relative test against the span would never accept
                double span = Span(T);
                if (newtonUpd <= options.NewtonTolerance * Math.Max(span, SpanFloorK) && newtonRes <= 1e-8)
                {
                    converged = true;
                    break;
                }
            }
            if (newtonIts > options.NewtonMaxIterations) newtonIts = options.NewtonMaxIterations;
            notes.Add(converged
                ? $"Newton over k(T) converged in {newtonIts} step(s): last update {newtonUpd:G3} K, residual {newtonRes:G3} of its start."
                : $"Newton over k(T) did not converge in {options.NewtonMaxIterations} steps: last update {newtonUpd:G3} K, " +
                  $"residual {newtonRes:G3} of its start.");
        }

        // ── the heat that left, and the balance it must close ──
        var ks = assembly.Matrix(sys.Secant);
        var kt = new double[n];
        ks.Multiply(T, kt);
        double fixedOut = 0;
        var byTag = new Dictionary<int, double>();
        for (int i = 0; i < n; i++)
        {
            if (free[i] >= 0) continue;
            double q = sys.Load[i] - kt[i];
            fixedOut += q;
            byTag[fixedTag[i]] = byTag.GetValueOrDefault(fixedTag[i]) + q;
        }
        double convOut = assembly.ConvectionOut(problem, T);
        double pin = sys.SourcePowerW;
        double flow = Math.Max(Math.Abs(pin), Math.Abs(fixedOut) + Math.Abs(convOut));
        double balance = flow > 0 ? Math.Abs(pin - fixedOut - convOut) / flow : 0;
        if (sys.AnyNonFinite) notes.Add("An element was degenerate or a conductivity was not finite somewhere in the mesh.");

        return new ThermalSolution
        {
            Temperature = T, Unknowns = nFree, Solver = last.Solver, LinearIterations = last.Iterations,
            LinearResidual = last.RelativeResidual, NewtonIterations = nonlinear ? newtonIts : 0, Converged = converged,
            NewtonResidual = newtonRes, NewtonUpdateK = newtonUpd, SourcePowerW = pin, FixedHeatOutW = fixedOut,
            ConvectionHeatOutW = convOut, FixedHeatOutByTag = byTag, BalanceRelative = balance, AmgLevels = last.AmgLevels,
            Notes = notes,
        };
    }

    /// <summary>The temperature each fixed node takes (NaN for a free node), the tag that fixed it, and how many nodes two
    /// fixed faces disagreed on: the first condition naming a node wins, fixed fields after fixed faces.</summary>
    internal static (double[] FixedT, int[] FixedTag, int Conflicts) FixedNodes(ThermalProblem problem)
    {
        var m = problem.Mesh;
        int n = m.NodeCount;
        var fixedT = new double[n];
        var fixedTag = new int[n];
        Array.Fill(fixedT, double.NaN);
        var conflicted = new HashSet<int>();
        int nf = m.NodesPerTriangle;
        foreach (var f in problem.Fixed)
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (m.TriangleTag[t] != f.Tag) continue;
                for (int k = 0; k < nf; k++)
                {
                    int v = m.Triangles[nf * t + k];
                    if (double.IsNaN(fixedT[v])) { fixedT[v] = f.TempC; fixedTag[v] = f.Tag; }
                    else if (fixedT[v] != f.TempC) conflicted.Add(v);
                }
            }
        // brief-em3d-76 — fixed FIELDS after the fixed faces: each node at its own position's value
        foreach (var f in problem.FixedFields)
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (m.TriangleTag[t] != f.Tag) continue;
                for (int k = 0; k < nf; k++)
                {
                    int v = m.Triangles[nf * t + k];
                    if (!double.IsNaN(fixedT[v])) continue;
                    double tv = f.TempAt(m.Nodes[3 * v], m.Nodes[3 * v + 1], m.Nodes[3 * v + 2]);
                    if (!double.IsFinite(tv)) throw new InvalidOperationException($"a fixed field on tag {f.Tag} gave a node no temperature");
                    fixedT[v] = tv;
                    fixedTag[v] = f.Tag;
                }
            }
        return (fixedT, fixedTag, conflicted.Count);
    }

    /// <summary>
    /// The regions of every connected body (tetrahedra joined through shared nodes, and across interface elements) that
    /// touches no fixed node and no convection face with h &gt; 0. Such a body's temperature is not determined — its block of
    /// the matrix is singular — so the solve is refused rather than handing the factorisation a zero pivot.
    /// </summary>
    internal static int[] FloatingRegions(ThermalProblem problem, double[] fixedT)
    {
        var m = problem.Mesh;
        int n = m.NodeCount, nt = m.NodesPerTet, nf = m.NodesPerTriangle;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        for (int e = 0; e < m.TetCount; e++)
            for (int k = 1; k < nt; k++) Union(m.Tets[nt * e], m.Tets[nt * e + k]);
        if (m.Interfaces is { } faces)
            for (int f = 0; f < faces.Count; f++)
                if (faces.H[f] > 0)
                    for (int k = 0; k < faces.NodesPerSide; k++)
                        Union(faces.Triangles[2 * faces.NodesPerSide * f + k], faces.Triangles[2 * faces.NodesPerSide * f + faces.NodesPerSide + k]);
        var anchored = new bool[n];
        for (int i = 0; i < n; i++) if (!double.IsNaN(fixedT[i])) anchored[Find(i)] = true;
        var robin = new HashSet<int>();
        foreach (var c in problem.Convection) if (c.HWm2K > 0) robin.Add(c.Tag);
        if (robin.Count > 0)
            for (int t = 0; t < m.TriangleCount; t++)
                if (robin.Contains(m.TriangleTag[t])) anchored[Find(m.Triangles[nf * t])] = true;
        var floating = new SortedSet<int>();
        for (int e = 0; e < m.TetCount; e++)
            if (!anchored[Find(m.Tets[nt * e])]) floating.Add(m.TetRegion[e]);
        return [.. floating];
    }

    private static double Span(double[] t)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (double v in t) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        return hi - lo;
    }

    /// <summary>K(T)·T − F on the free rows.</summary>
    private static double[] Residual(ThermalAssembly a, AssembledSystem s, double[] t, int[] free, int nFree)
    {
        var kt = new double[t.Length];
        a.Matrix(s.Secant).Multiply(t, kt);
        var r = new double[nFree];
        for (int i = 0; i < t.Length; i++) if (free[i] >= 0) r[free[i]] = kt[i] - s.Load[i];
        return r;
    }

    private static double ReducedLoadNorm(ThermalAssembly a, AssembledSystem s, double[] fixedT, int[] free, int nFree)
    {
        var lift = new double[fixedT.Length];
        for (int i = 0; i < lift.Length; i++) lift[i] = double.IsNaN(fixedT[i]) ? 0 : fixedT[i];
        var kl = new double[lift.Length];
        a.Matrix(s.Secant).Multiply(lift, kl);
        var b = new double[nFree];
        for (int i = 0; i < lift.Length; i++) if (free[i] >= 0) b[free[i]] = s.Load[i] - kl[i];
        return SparseRows.Norm(b);
    }

    /// <summary>The free rows and columns of <paramref name="a"/>.</summary>
    internal static SparseRows Reduce(SparseRows a, int[] free, int nFree)
    {
        var ptr = new int[nFree + 1];
        var idx = new List<int>(a.Nnz);
        var val = new List<double>(a.Nnz);
        int row = 0;
        for (int i = 0; i < a.Rows; i++)
        {
            if (free[i] < 0) continue;
            for (int k = a.Ptr[i]; k < a.Ptr[i + 1]; k++)
            {
                int c = free[a.Idx[k]];
                if (c < 0) continue;
                idx.Add(c);
                val.Add(a.Val[k]);
            }
            ptr[++row] = idx.Count;
        }
        return new SparseRows(nFree, nFree, ptr, [.. idx], [.. val]);
    }

    /// <summary>Solves the free block with the fixed values moved to the right-hand side (elimination), writing every node
    /// of <paramref name="t"/>.</summary>
    private static LinearSolveReport SolveReduced(SparseRows k, double[] load, double[] fixedT, int[] free, int nFree, double[] t,
                                                  bool symmetric, ThermalSolverKind kind, ThermalSolveOptions options)
    {
        int n = load.Length;
        var lift = new double[n];
        for (int i = 0; i < n; i++) lift[i] = double.IsNaN(fixedT[i]) ? 0 : fixedT[i];
        var kl = new double[n];
        k.Multiply(lift, kl);
        var b = new double[nFree];
        for (int i = 0; i < n; i++) if (free[i] >= 0) b[free[i]] = load[i] - kl[i];
        var x = new double[nFree];
        var report = LinearSolver.Solve(Reduce(k, free, nFree), b, x, symmetric, kind, options);
        for (int i = 0; i < n; i++) t[i] = free[i] >= 0 ? x[free[i]] : fixedT[i];
        return report;
    }
}
