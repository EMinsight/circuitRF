// brief-emdc-1 R-emdc1-2 — 0 Hz of a 3D problem, solved as steady conduction on a conductor-only mesh.
//
// THE PHYSICS. At ω = 0 the field inside a conductor is ∇·(σ∇φ) = 0 and the dielectrics and the air carry nothing, so what is
// left is the operator the thermal solver assembles for k, with σ in its place: the σ-weighted Laplacian on the tetrahedra,
// and the σt-weighted surface Laplacian on a conductor sheet's triangles. Nothing here knows what a port, a via or a lumped
// element is; the lowering (src/Design/Em3d/Em3dDcLowering.cs) turns those into node sets, ties and resistors.
//
// TIES MERGE UNKNOWNS. A PEC body, a PEC sheet, a lumped L or an R = 0 is one potential: its nodes are one item before
// assembly, exactly as the electrothermal system makes a contact one φ item. NEVER A PENALTY CONDUCTANCE — brief-em3d-85 found
// that no Krylov solve converged with one. Each contact (one side of a port, one end of a resistor) is one item too: an
// equipotential carrying a total current, never a uniform current density.
//
// THE PORT MODEL (brief-emdc-0 §3, and PlanarDcSolve's header — the 2026-09-14 defect). A port is a terminal PAIR, neither
// terminal assumed to be ground: a branch of conductance 1/Z₀ₖ between its two contact items. Port j is driven by an EMF of
// 2√Z₀ⱼ in series with its branch, which is the Norton current 2√Z₀ⱼ/Z₀ⱼ across the pair; I_k is the current into port k's
// positive contact, I_k = (E_k − (V⁺ − V⁻))/Z₀ₖ, and S_kj = δ_kj − √Z₀ₖ·I_k. That is finite for an ideal short, an ideal
// open and a zero-ohm tie alike, so there is never a Y or a Z to invert at a singular point.
//
// REFERENCES, AND WHY THE PORT BRANCHES ARE CLOSED ON THE CONTACTS. A component no port's contact lies in is dropped: a
// floating conductor carries no current, and solving it would be a singular block. Every other CONDUCTION group (elements,
// ties, resistors — not the port branches) is grounded at one of its contacts, and the sparse solve gives each group's
// resistances between its contacts: one factorisation, one solve per contact. The port branches are then closed on those
// contacts in a small dense system — the ports' currents, and one potential offset per group, one group of every set the
// branches join held at 0. That is the same network as stamping each branch into the sparse matrix, and it is how it is solved
// because the stamped form is not exact where it has to be: a conductor joined to the rest by one port's 1/Z₀ alone is a
// near-null block, and brief-emdc-1 measured an open coming back as S₁₁ = 1 − 1.8e-7 and a through 4.7e-8 off. Here a
// group joined by one branch alone has that port's current as its own balance, so an open is exactly an open.
//
// CHECKS, AS NOTES, NEVER SILENT. Reciprocity ‖S − Sᵀ‖ ≤ 1e-9 and passivity σ_max(S) ≤ 1 + 1e-9. A failure of either is a
// refusal: a DC row that fails them is a solver defect, and must not be written into anyone's file.

using System.Diagnostics;
using System.Globalization;
using CircuitRF.Thermal.Solvers;

namespace CircuitRF.Thermal.Conduction;

/// <summary>One port at DC: its two contacts (node sets, each one equipotential) and its real reference impedance, Ω.</summary>
public sealed record DcPort(int Number, IReadOnlyList<int> Positive, IReadOnlyList<int> Negative, double Z0);

/// <summary>A lumped resistor between two node sets, each one equipotential.</summary>
public sealed record DcResistor(string Name, IReadOnlyList<int> A, IReadOnlyList<int> B, double Ohms);

/// <summary>What a lumped R, L and C are at DC.</summary>
public enum DcLumpedKind
{
    /// <summary>One potential: a short.</summary>
    Tie,
    /// <summary>A resistor of the element's R.</summary>
    Resistor,
    /// <summary>An open: it is not in the network at all.</summary>
    Nothing,
}

/// <summary>
/// The conduction problem: a mesh, σ per tetrahedron region (null: not assembled — an insulator, or a PEC body whose nodes are
/// a tie), σt per sheet triangle tag, the ties, the resistors and the ports. Node indices at or above the mesh's node count
/// are <see cref="VirtualNodes"/>: items with no element (a ground that no conductor touches).
/// </summary>
public sealed record DcConductionProblem(ThermalMesh Mesh, IReadOnlyList<double?> RegionSigma, IReadOnlyDictionary<int, double> SheetConductance,
                                         IReadOnlyList<DcPort> Ports)
{
    public IReadOnlyList<IReadOnlyList<int>> Ties { get; init; } = [];
    public IReadOnlyList<DcResistor> Resistors { get; init; } = [];
    public int VirtualNodes { get; init; }
}

/// <summary>The DC scattering matrix (port order as given, real, referred to each port's Z₀), or the refusal; what was solved.</summary>
public sealed record DcConductionResult(double[,]? S, string? Refusal, IReadOnlyList<string> Notes, int Unknowns, int Elements, double ElapsedMs);

public static class DcConduction
{
    /// <summary>How far S may be from Sᵀ, and σ_max(S) above 1, before the answer is a solver defect.</summary>
    public const double Tolerance = 1e-9;

    /// <summary>
    /// What a lumped element is at DC (brief-emdc-1 §3). Parallel: any L, or R = 0, is a tie; otherwise an R is a resistor and a
    /// C alone is nothing. Series: any C is nothing; otherwise a non-zero R is a resistor, and an L alone or R = 0 is a tie.
    /// </summary>
    public static DcLumpedKind LumpedAtDc(double? r, double? l, double? c, bool series)
    {
        if (!series)
        {
            if (l is not null || r == 0) return DcLumpedKind.Tie;
            return r is not null ? DcLumpedKind.Resistor : DcLumpedKind.Nothing;
        }
        if (c is not null) return DcLumpedKind.Nothing;
        return r is > 0 ? DcLumpedKind.Resistor : DcLumpedKind.Tie;
    }

    /// <summary>Solves <paramref name="p"/>: one factorisation, one solve per contact, then the ports closed on the contacts. Cancellation throws.</summary>
    public static DcConductionResult Solve(DcConductionProblem p, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(p);
        var clock = Stopwatch.StartNew();
        var ci = CultureInfo.InvariantCulture;
        var notes = new List<string>();
        var m = p.Mesh;
        int nMesh = m.NodeCount, n = nMesh + p.VirtualNodes, nn = m.NodesPerTet, nf = m.NodesPerTriangle;
        var ports = p.Ports.OrderBy(x => x.Number).ToList();
        DcConductionResult Refuse(string why) => new(null, why, notes, 0, 0, clock.Elapsed.TotalMilliseconds);
        if (ports.Count == 0) return Refuse("There is no port, so there is no DC row to solve.");
        foreach (var port in ports)
        {
            if (!(port.Z0 > 0) || !double.IsFinite(port.Z0))
                return Refuse($"Port {port.Number}'s reference impedance has a real part of {port.Z0.ToString("G4", ci)} Ω: a DC row is referred to a positive resistance.");
            if (port.Positive.Count == 0 || port.Negative.Count == 0)
                return Refuse($"Port {port.Number}'s {(port.Positive.Count == 0 ? "positive" : "negative")} contact touches no conductor, so it has no DC meaning.");
        }
        foreach (var r in p.Resistors)
            if (!(r.Ohms > 0) || !double.IsFinite(r.Ohms))
                return Refuse($"Resistor '{r.Name}' is {r.Ohms.ToString("G4", ci)} Ω: a resistor at DC is a finite positive resistance (a zero one is a tie).");

        // ── which elements conduct ──
        var tets = new List<int>();
        for (int e = 0; e < m.TetCount; e++)
            if (m.TetRegion[e] < p.RegionSigma.Count && p.RegionSigma[m.TetRegion[e]] is { } sg && sg > 0 && double.IsFinite(sg)) tets.Add(e);
        var tris = new List<int>();
        for (int t = 0; t < m.TriangleCount; t++)
            if (p.SheetConductance.TryGetValue(m.TriangleTag[t], out double g) && g > 0 && double.IsFinite(g)) tris.Add(t);
        int elements = tets.Count + tris.Count;

        // ── items: ties and contacts merge nodes ──
        var electrical = new bool[n];
        foreach (int e in tets) for (int k = 0; k < nn; k++) electrical[m.Tets[nn * e + k]] = true;
        foreach (int t in tris) for (int k = 0; k < nf; k++) electrical[m.Triangles[nf * t + k]] = true;
        var same = new UnionFind(n);
        void Merge(IReadOnlyList<int> set)
        {
            foreach (int v in set)
            {
                if ((uint)v >= (uint)n) throw new ArgumentException($"node {v} does not exist", nameof(p));
                electrical[v] = true;
                same.Union(set[0], v);
            }
        }
        int tiedNodes = 0;
        foreach (var tie in p.Ties) if (tie.Count > 0) { Merge(tie); tiedNodes += tie.Count; }
        foreach (var port in ports) { Merge(port.Positive); Merge(port.Negative); }
        foreach (var r in p.Resistors)
        {
            if (r.A.Count == 0 || r.B.Count == 0) continue;
            Merge(r.A); Merge(r.B);
        }

        // ── conduction groups: elements, ties and resistors; a port's branch is not conduction ──
        var path = new UnionFind(n);
        foreach (int e in tets) for (int k = 1; k < nn; k++) path.Union(same.Find(m.Tets[nn * e]), same.Find(m.Tets[nn * e + k]));
        foreach (int t in tris) for (int k = 1; k < nf; k++) path.Union(same.Find(m.Triangles[nf * t]), same.Find(m.Triangles[nf * t + k]));
        foreach (var r in p.Resistors) if (r.A.Count > 0 && r.B.Count > 0) path.Union(same.Find(r.A[0]), same.Find(r.B[0]));
        for (int i = 0; i < n; i++) if (electrical[i]) path.Union(i, same.Find(i));

        // each group a port's contact lies in is grounded at one of its contacts (the lowest-numbered port's, negative first);
        // a group no contact lies in carries no current and is dropped
        var reference = new Dictionary<int, int>();
        foreach (var port in ports)
            foreach (int t in new[] { port.Negative[0], port.Positive[0] })
                if (!reference.ContainsKey(path.Find(t))) reference[path.Find(t)] = same.Find(t);
        var unknownOf = new int[n];
        Array.Fill(unknownOf, -1);
        var itemUnknown = new Dictionary<int, int>();
        var dropped = new HashSet<int>();
        int droppedNodes = 0;
        for (int i = 0; i < n; i++)
        {
            if (!electrical[i]) continue;
            int comp = path.Find(i);
            if (!reference.TryGetValue(comp, out int refItem)) { dropped.Add(comp); droppedNodes++; continue; }
            int item = same.Find(i);
            if (item == refItem) continue;
            if (!itemUnknown.TryGetValue(item, out int u)) itemUnknown[item] = u = itemUnknown.Count;
            unknownOf[i] = u;
        }
        int size = itemUnknown.Count;
        int U(int node) => unknownOf[same.Find(node)] >= 0 ? unknownOf[same.Find(node)] : -1;

        // ── the pattern: elements' unknowns, plus the resistors' pairs ──
        int ne = elements;
        int Count(int el) => el < tets.Count ? nn : nf;
        int NodeOf(int el, int k) => el < tets.Count ? m.Tets[nn * tets[el] + k] : m.Triangles[nf * tris[el - tets.Count] + k];
        var branches = new List<(int A, int B, double G)>();
        foreach (var r in p.Resistors) if (r.A.Count > 0 && r.B.Count > 0) branches.Add((U(r.A[0]), U(r.B[0]), 1 / r.Ohms));
        var elemUnknowns = new int[ne * 10];
        for (int el = 0; el < ne; el++)
            for (int k = 0; k < Count(el); k++) elemUnknowns[10 * el + k] = U(NodeOf(el, k));
        var start = new int[size + 1];
        for (int el = 0; el < ne; el++)
            for (int k = 0; k < Count(el); k++) if (elemUnknowns[10 * el + k] is int u and >= 0) start[u + 1]++;
        for (int i = 0; i < size; i++) start[i + 1] += start[i];
        var fill = (int[])start.Clone();
        var touching = new int[start[size]];
        for (int el = 0; el < ne; el++)
            for (int k = 0; k < Count(el); k++) if (elemUnknowns[10 * el + k] is int u and >= 0) touching[fill[u]++] = el;
        var extra = new List<int>[size];
        foreach (var (ba, bb, _) in branches)
        {
            if (ba < 0 || bb < 0 || ba == bb) continue;
            (extra[ba] ??= []).Add(bb);
            (extra[bb] ??= []).Add(ba);
        }
        var rows = new int[size][];
        Parallel.For(0, size, () => new List<int>(128), (i, _, cols) =>
        {
            cols.Clear();
            cols.Add(i);
            for (int q = start[i]; q < start[i + 1]; q++)
            {
                int el = touching[q];
                for (int k = 0; k < Count(el); k++) if (elemUnknowns[10 * el + k] >= 0) cols.Add(elemUnknowns[10 * el + k]);
            }
            if (extra[i] is { } x) cols.AddRange(x);
            cols.Sort();
            int w = 0;
            for (int k = 0; k < cols.Count; k++) if (w == 0 || cols[k] != cols[w - 1]) cols[w++] = cols[k];
            rows[i] = [.. cols.Take(w)];
            return cols;
        }, _ => { });
        var ptr = new int[size + 1];
        for (int i = 0; i < size; i++) ptr[i + 1] = ptr[i] + rows[i].Length;
        var idx = new int[ptr[size]];
        for (int i = 0; i < size; i++) rows[i].CopyTo(idx, ptr[i]);
        var a = new SparseRows(size, size, ptr, idx, new double[idx.Length]);
        ct.ThrowIfCancellationRequested();

        // ── assembly: element blocks in parallel, scattered in element order ──
        const int block = 4096;
        var ke = new double[block * 100];
        int degenerate = 0;
        for (int b0 = 0; b0 < ne; b0 += block)
        {
            ct.ThrowIfCancellationRequested();
            int b1 = Math.Min(ne, b0 + block);
            int bad = 0;
            Parallel.For(b0, b1, el =>
            {
                var dst = ke.AsSpan(100 * (el - b0), 100);
                bool ok = el < tets.Count ? TetBlock(m, tets[el], p.RegionSigma[m.TetRegion[tets[el]]]!.Value, dst)
                                          : TriBlock(m, tris[el - tets.Count], p.SheetConductance[m.TriangleTag[tris[el - tets.Count]]], dst);
                if (!ok) Interlocked.Increment(ref bad);
            });
            degenerate += bad;
            for (int el = b0; el < b1; el++)
            {
                int c = Count(el);
                var src = ke.AsSpan(100 * (el - b0), 100);
                for (int i = 0; i < c; i++)
                {
                    int ui = elemUnknowns[10 * el + i];
                    if (ui < 0) continue;
                    for (int j = 0; j < c; j++)
                    {
                        int uj = elemUnknowns[10 * el + j];
                        if (uj < 0) continue;
                        a.Val[a.Slot(ui, uj)] += src[10 * i + j];
                    }
                }
            }
        }
        foreach (var (ua, ub, g) in branches)
        {
            if (ua >= 0) a.Val[a.Slot(ua, ua)] += g;
            if (ub >= 0) a.Val[a.Slot(ub, ub)] += g;
            if (ua >= 0 && ub >= 0) { a.Val[a.Slot(ua, ub)] -= g; a.Val[a.Slot(ub, ua)] -= g; }
        }

        // ── each group's terminal resistances: one factorisation, one solve per contact ──
        var terminals = new List<int>();
        var terminalOf = new Dictionary<int, int>();
        foreach (var port in ports)
            foreach (int t in new[] { port.Positive[0], port.Negative[0] })
                if (!terminalOf.ContainsKey(same.Find(t))) { terminalOf[same.Find(t)] = terminals.Count; terminals.Add(same.Find(t)); }
        int nt = terminals.Count;
        var groupOf = terminals.Select(t => path.Find(t)).ToArray();
        var options = new ThermalSolveOptions { DirectBelow = 150_000, RelativeTolerance = 1e-13, MaxIterations = 5000, Cancellation = ct };
        var solve = PreparedSolve.Create(a, symmetric: true, ThermalSolverKind.Auto, options);
        var rt = new double[nt, nt];
        double worstResidual = 0;
        for (int q = 0; q < nt; q++)
        {
            ct.ThrowIfCancellationRequested();
            int uq = unknownOf[terminals[q]];
            if (uq < 0) continue;
            var rhs = new double[size];
            rhs[uq] = 1;
            var v = new double[size];
            worstResidual = Math.Max(worstResidual, solve.Solve(rhs, v).RelativeResidual);
            for (int t = 0; t < nt; t++)
                if (groupOf[t] == groupOf[q] && unknownOf[terminals[t]] is int ut and >= 0) rt[t, q] = v[ut];
        }
        for (int i = 0; i < nt; i++)
            for (int j = i + 1; j < nt; j++) rt[i, j] = rt[j, i] = (rt[i, j] + rt[j, i]) / 2;

        // ── the port branches closed on the terminals ──
        // unknowns: each port's current I_k, and each group's potential offset u_g (one group of every set the port branches join
        // is the gauge, u = 0, and its current balance — the sum of the others' — is not written). Port k: Z₀ₖI_k + V⁺ − V⁻ = E_k,
        // V_t = u_g(t) + Σ (r_t,p⁺ − r_t,p⁻)·I_p; group g: Σ_k ([g(k⁺) = g] − [g(k⁻) = g])·I_k = 0. A group joined to the rest by one
        // port's branch alone has that port's current as its balance: an open is decided by the topology, exactly.
        int np = ports.Count;
        var groups = reference.Keys.ToList();
        var joined = new UnionFind(groups.Count);
        int Gi(int node) => groups.IndexOf(path.Find(node));
        foreach (var port in ports) joined.Union(Gi(port.Positive[0]), Gi(port.Negative[0]));
        var gauge = new HashSet<int>();
        var gaugeOfSet = new HashSet<int>();
        foreach (var port in ports)
            if (gaugeOfSet.Add(joined.Find(Gi(port.Negative[0])))) gauge.Add(Gi(port.Negative[0]));
        var offsetOf = new int[groups.Count];
        int offsets = 0;
        for (int g = 0; g < groups.Count; g++) offsetOf[g] = gauge.Contains(g) ? -1 : np + offsets++;
        int dim = np + offsets;
        var mat = new double[dim, dim];
        int Ti(int node) => terminalOf[same.Find(node)];
        for (int k = 0; k < np; k++)
        {
            int pk = Ti(ports[k].Positive[0]), nk = Ti(ports[k].Negative[0]);
            for (int j = 0; j < np; j++)
            {
                int pj = Ti(ports[j].Positive[0]), nj = Ti(ports[j].Negative[0]);
                mat[k, j] = rt[pk, pj] - rt[pk, nj] - rt[nk, pj] + rt[nk, nj];
            }
            mat[k, k] += ports[k].Z0;
            int gp = Gi(ports[k].Positive[0]), gn = Gi(ports[k].Negative[0]);
            if (gp == gn) continue;
            if (offsetOf[gp] >= 0) { mat[k, offsetOf[gp]] += 1; mat[offsetOf[gp], k] += 1; }
            if (offsetOf[gn] >= 0) { mat[k, offsetOf[gn]] -= 1; mat[offsetOf[gn], k] -= 1; }
        }
        var lu = DenseLu.Factor(mat);
        var s = new double[np, np];
        for (int j = 0; j < np; j++)
        {
            double emf = 2 * Math.Sqrt(ports[j].Z0);
            var rhs = new double[dim];
            rhs[j] = emf;
            var x = lu.Solve(rhs);
            for (int k = 0; k < np; k++) s[k, j] = (k == j ? 1 : 0) - Math.Sqrt(ports[k].Z0) * x[k];
        }

        // ── the checks ──
        double asym = 0;
        for (int i = 0; i < np; i++) for (int j = 0; j < np; j++) asym = Math.Max(asym, Math.Abs(s[i, j] - s[j, i]));
        double sigmaMax = LargestSingularValue(s);
        string G(double x) => x.ToString("G3", ci);
        int merged = p.Ties.Count(t => t.Count > 0);
        notes.Add($"DC conduction: {elements:N0} element(s) ({tets.Count:N0} tetrahedra, {tris.Count:N0} sheet triangles), " +
                  $"{size:N0} unknown(s) after merging" +
                  (merged > 0 ? $" ({tiedNodes:N0} node(s) in {merged} tie(s), each one potential)" : "") +
                  $", in {reference.Count} conduction group(s), each grounded at one of its contacts; {nt} contact(s) solved for " +
                  $"({(solve.Kind == ThermalSolverKind.Direct ? "direct" : "iterative")}, relative residual {G(worstResidual)}).");
        if (dropped.Count > 0)
            notes.Add($"{dropped.Count} conductor group(s) ({droppedNodes:N0} node(s)) touch no port's contact, so no current flows in them; they are not solved.");
        if (degenerate > 0) notes.Add($"{degenerate} degenerate element(s) were skipped.");
        if (solve.FallbackNote is { } fb) notes.Add(fb);
        notes.Add($"Reciprocity: max |S − Sᵀ| = {G(asym)}; passivity: σ_max(S) = {sigmaMax.ToString("R", ci)}.");
        double ms = clock.Elapsed.TotalMilliseconds;
        if (!(asym <= Tolerance))
            return new(null, $"The DC solve is not reciprocal (max |S − Sᵀ| = {G(asym)}, above {G(Tolerance)}): that is a solver defect, so no DC row is written.",
                       notes, size, elements, ms);
        if (!(sigmaMax <= 1 + Tolerance))
            return new(null, $"The DC solve is not passive (σ_max(S) = {sigmaMax.ToString("R", ci)}, above 1 + {G(Tolerance)}): that is a solver defect, so no DC row is written.",
                       notes, size, elements, ms);
        return new(s, null, notes, size, elements, ms);
    }

    /// <summary>σ ∫ ∇Nᵢ·∇Nⱼ over tetrahedron <paramref name="e"/>, into a 10 × 10 block.</summary>
    private static bool TetBlock(ThermalMesh m, int e, double sigma, Span<double> ke)
    {
        int nn = m.NodesPerTet;
        ke.Clear();
        Span<double> xyz = stackalloc double[30];
        Span<double> gx = stackalloc double[10], gy = stackalloc double[10], gz = stackalloc double[10];
        for (int i = 0; i < nn; i++)
        {
            int v = m.Tets[nn * e + i];
            xyz[3 * i] = m.Nodes[3 * v]; xyz[3 * i + 1] = m.Nodes[3 * v + 1]; xyz[3 * i + 2] = m.Nodes[3 * v + 2];
        }
        var shapes = TetShapes.For(m.Order);
        bool ok = true;
        for (int q = 0; q < ReferenceElement.TetQuadraturePoints; q++)
        {
            if (!ReferenceElement.TetGradients(xyz, nn, shapes.Dx(q), shapes.Dy(q), shapes.Dz(q), gx, gy, gz, out double det)) { ok = false; continue; }
            double w = ReferenceElement.TetW[q] * det * sigma;
            for (int i = 0; i < nn; i++)
                for (int j = 0; j < nn; j++) ke[10 * i + j] += w * (gx[i] * gx[j] + gy[i] * gy[j] + gz[i] * gz[j]);
        }
        return ok;
    }

    /// <summary>σt ∫ ∇ₛNᵢ·∇ₛNⱼ over sheet triangle <paramref name="t"/> (the surface gradient, isoparametric), into a block.</summary>
    private static bool TriBlock(ThermalMesh m, int t, double conductance, Span<double> ke)
    {
        int nf = m.NodesPerTriangle;
        ke.Clear();
        var shapes = TriShapes.For(m.Order);
        bool ok = true;
        for (int q = 0; q < ReferenceElement.TriQuadraturePoints; q++)
        {
            var du = shapes.Du(q);
            var dv = shapes.Dv(q);
            double ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
            for (int i = 0; i < nf; i++)
            {
                int v = m.Triangles[nf * t + i];
                double x = m.Nodes[3 * v], y = m.Nodes[3 * v + 1], z = m.Nodes[3 * v + 2];
                ax += du[i] * x; ay += du[i] * y; az += du[i] * z;
                bx += dv[i] * x; by += dv[i] * y; bz += dv[i] * z;
            }
            double g11 = ax * ax + ay * ay + az * az, g12 = ax * bx + ay * by + az * bz, g22 = bx * bx + by * by + bz * bz;
            double det = g11 * g22 - g12 * g12;
            if (!(det > 0)) { ok = false; continue; }
            double jac = Math.Sqrt(det), w = ReferenceElement.TriW[q] * jac * conductance;
            // ∇ₛNᵢ·∇ₛNⱼ = [dNᵢ/du dNᵢ/dv] G⁻¹ [dNⱼ/du dNⱼ/dv]ᵀ
            double i11 = g22 / det, i12 = -g12 / det, i22 = g11 / det;
            for (int i = 0; i < nf; i++)
                for (int j = 0; j < nf; j++)
                    ke[10 * i + j] += w * (du[i] * (i11 * du[j] + i12 * dv[j]) + dv[i] * (i12 * du[j] + i22 * dv[j]));
        }
        return ok;
    }

    /// <summary>The largest singular value of a small real matrix: √ of the largest eigenvalue of SᵀS, by Jacobi rotations.</summary>
    public static double LargestSingularValue(double[,] s)
    {
        int n = s.GetLength(0);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double x = 0;
                for (int k = 0; k < n; k++) x += s[k, i] * s[k, j];
                a[i, j] = x;
            }
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            if (off < 1e-30) break;
            for (int pp = 0; pp < n; pp++)
                for (int q = pp + 1; q < n; q++)
                {
                    if (a[pp, q] == 0) continue;
                    double theta = (a[q, q] - a[pp, pp]) / (2 * a[pp, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, pp], akq = a[k, q];
                        a[k, pp] = c * akp - sn * akq;
                        a[k, q] = sn * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[pp, k], aqk = a[q, k];
                        a[pp, k] = c * apk - sn * aqk;
                        a[q, k] = sn * apk + c * aqk;
                    }
                }
        }
        double max = 0;
        for (int i = 0; i < n; i++) max = Math.Max(max, a[i, i]);
        return Math.Sqrt(max);
    }

    /// <summary>A small dense LU with partial pivoting: the port system, factorised once for every right-hand side.</summary>
    private sealed class DenseLu
    {
        private readonly double[,] _a;
        private readonly int[] _perm;

        private DenseLu(double[,] a, int[] perm) { _a = a; _perm = perm; }

        public static DenseLu Factor(double[,] m)
        {
            int n = m.GetLength(0);
            var a = (double[,])m.Clone();
            var perm = Enumerable.Range(0, n).ToArray();
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
                if (a[piv, c] == 0) throw new InvalidOperationException("the DC port system is singular");
                if (piv != c)
                {
                    for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]);
                    (perm[c], perm[piv]) = (perm[piv], perm[c]);
                }
                for (int r = c + 1; r < n; r++)
                {
                    double f = a[r, c] / a[c, c];
                    a[r, c] = f;
                    if (f == 0) continue;
                    for (int k = c + 1; k < n; k++) a[r, k] -= f * a[c, k];
                }
            }
            return new DenseLu(a, perm);
        }

        public double[] Solve(double[] b)
        {
            int n = b.Length;
            var x = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v = b[_perm[i]];
                for (int k = 0; k < i; k++) v -= _a[i, k] * x[k];
                x[i] = v;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double v = x[i];
                for (int k = i + 1; k < n; k++) v -= _a[i, k] * x[k];
                x[i] = v / _a[i, i];
            }
            return x;
        }
    }

    private sealed class UnionFind
    {
        private readonly int[] _p;
        public UnionFind(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
        public int Find(int a)
        {
            while (_p[a] != a) { _p[a] = _p[_p[a]]; a = _p[a]; }
            return a;
        }
        public void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (a < b) _p[b] = a; else _p[a] = b;
        }
    }
}
