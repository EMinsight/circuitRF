// brief-em3d-77 R-em3d77-2..-4 — the coupled system of conductive balance: its unknowns, its sparsity, the geometry of every
// wire coupling (found once per mesh), and the assembly of its residual and full Newton Jacobian at a state.
//
// UNKNOWNS. x = [T on every mesh node, T on every wire node | φ]. φ lives on the conductor sub-mesh (the nodes of tetrahedra
// whose region conducts) and on the wires; a CONTACT's nodes are one φ unknown (a floating equipotential), and a node the
// interface split duplicated (brief 76) is one φ unknown on both sides — the split is a thermal resistance, not an electrical
// gap. A conductor no port's contact reaches carries no current, and has no φ at all: solving it would be a singular block.
// Each connected conductor carrying a port's contacts is referenced at the negative contact of its lowest-numbered port.
//
// RESIDUALS (R-em3d77-4a). r = S(x)·x − L(x):
//   φ rows: S = ∫σ(T)∇Nᵢ·∇Nⱼ (tetrahedra, and σA along the wires) + the contacts' G″; L = the ports' currents;
//   T rows: S = ThermalAssembly's K(T) + the wires' kA + the contacts' h″ + the well couplings + wire convection;
//           L = the sources, Robin's hT∞ + the Joule heat σ|∇φ|² (and σA φ′², and a contact's G″Δφ², half to each side)
//               + brief-em3d-78's RF heat per unit length along each wire's span, Σₙ ½|Iₙ|²R′_ac(fₙ, σ(T)).
// THE JACOBIAN is S plus every dependence S and L have on x: dk/dT, dσ/dT in the φ rows (∫σ′Nⱼ∇Nᵢ·∇φ), and the Joule heat's
// dependence on T (σ′|∇φ|²NᵢNⱼ) and on φ (2σ∇φ·∇NⱼNᵢ), and the RF heat's on T (dq′/dT NᵢNⱼ, through σ(T) and the skin depth).
//
// THE WIRE'S COUPLINGS (R-em3d77-3d/-3e):
//   * a CONTACT PATCH: at each quadrature point of each patch triangle, h″·(T_wire − T_pad) — T_wire at the point's projection
//     onto the foot (or the ball's one node). Symmetric; so is its electrical twin G″·(φ_wire − φ_pad).
//   * the WELL: a span element inside a solid loses g′·(T_wire − T̄) per unit length, g′ = 2πk / ln(r_e/r_w). The heat enters
//     the solid AT THE CENTRELINE (through the host element's shape functions there), and T̄ is the host's temperature averaged
//     over a RING of radius r_e round the centreline. Brief 72 Q1 found that a mesh's own temperature AT a line source is the
//     field at ~0.12 h for second-order elements — under a 1 mil wire's radius at every mesh size it tried, so no positive
//     correction there can exist. At a ring a few elements out the discrete field IS the continuous one (the line source's
//     error is local), so r_e = RingSizes · h with h the host element's size, and the conductance from the wire surface to
//     that ring is exactly the well's. The same formula the brief states, read where the mesh can answer it.
//   * CONVECTION in air, when the setup asks: h·πd·(T − T∞) per unit length.
//
// DETERMINISM. Element blocks are computed in parallel and scattered sequentially, in element order.

using CircuitRF.Thermal.Solvers;

namespace CircuitRF.Thermal.Electrothermal;

/// <summary>A problem the conductive-balance solve cannot be set up for, in a sentence.</summary>
public sealed class ElectrothermalException(string message) : Exception(message);

/// <summary>What one assembly produced, on <see cref="ElectrothermalSystem.Pattern"/>.</summary>
public sealed class ElectrothermalAssembled
{
    public required double[] Secant { get; init; }
    public required double[] Tangent { get; init; }
    public required double[] Load { get; init; }
    /// <summary>Heat the thermal sources put in, W.</summary>
    public required double SourceW { get; init; }
    /// <summary>Joule heat, W: per region, per wire, and in the contacts.</summary>
    public required double[] JouleByRegion { get; init; }
    public required double[] JouleByWire { get; init; }
    public required double JouleContactsW { get; init; }
    public double JouleW => JouleByRegion.Sum() + JouleByWire.Sum() + JouleContactsW;
    /// <summary>A conductivity at or below zero, or not finite, somewhere: the state is not physical (a linear ρ past its pole).</summary>
    public required bool Invalid { get; init; }
    /// <summary>Heat lost by the wires to convection, W.</summary>
    public required double WireConvectionW { get; init; }
    /// <summary>brief-em3d-78 — RF heat, W: per wire, and per wire per harmonic (in <see cref="ThermalWire.Harmonics"/>' order).</summary>
    public double[] RfByWire { get; init; } = [];
    public double[][] RfByHarmonic { get; init; } = [];
    public double RfW => RfByWire.Sum();
}

/// <summary>R-em3d77-4a — the coupled system of one mesh and its wires.</summary>
public sealed class ElectrothermalSystem
{
    /// <summary>A ring's radius, in host element sizes (<see cref="MeshLocator.Size"/>). RESOLVED.md records how it was chosen.</summary>
    public const double RingSizes = 1.5;

    /// <summary>Points on a ring.</summary>
    public const int RingPoints = 8;

    /// <summary>A perfect bond's contact conductance per unit area: this × the pad's k / √(patch area) for heat, and
    /// <see cref="PerfectBondElectrical"/> × its σ / √(patch area) for current. RESOLVED.md says how they were chosen.</summary>
    public const double PerfectBond = 1e6;

    public const double PerfectBondElectrical = 1e4;

    private static readonly double[] GaussX = [-0.8611363115940526, -0.3399810435848563, 0.3399810435848563, 0.8611363115940526];
    private static readonly double[] GaussW = [0.3478548451374538, 0.6521451548625461, 0.6521451548625461, 0.3478548451374538];

    private readonly ThermalMesh _mesh;
    private readonly ThermalAssembly _thermal;
    private readonly int[] _thermalSlot;
    private readonly int _dop;

    /// <summary>Mesh nodes; wire nodes follow them in the T block.</summary>
    public int HostNodes { get; }
    /// <summary>The first T unknown of each wire's node 0.</summary>
    public int[] WireOffset { get; }
    public int TUnknowns { get; }
    public int PhiUnknowns { get; }
    public int Size => TUnknowns + PhiUnknowns;

    /// <summary>Per mesh node, its φ unknown (0-based within φ) or −1.</summary>
    public int[] HostPhi { get; }
    /// <summary>Per wire, per node, its φ unknown or −1.</summary>
    public int[][] WirePhi { get; }
    /// <summary>The φ unknowns held at 0: one per conductor carrying a port's current.</summary>
    public int[] Reference { get; }
    /// <summary>Per terminal, its positive and negative φ unknowns.</summary>
    public (int Positive, int Negative)[] TerminalPhi { get; }

    /// <summary>The structure (values zero), structurally symmetric.</summary>
    public SparseRows Pattern { get; }

    public ThermalAssembly Thermal => _thermal;
    public MeshLocator Locator { get; }

    /// <summary>Per wire: its elements lying in a solid, in air, and on a pad.</summary>
    public (int InSolid, int InAir, int OnPad)[] WireHosting { get; }

    /// <summary>Per wire: the smallest and largest ring radius r_e its well coupling used, metres (0, 0 in air).</summary>
    public (double Min, double Max)[] WellRadius { get; private set; } = [];

    /// <summary>brief-em3d-78 R-em3d78-4d — per wire, the regions its span runs through (none: all in air).</summary>
    public int[][] WireHostRegions { get; private set; } = [];

    /// <summary>Notes the set-up makes (conductors with no current, rings a boundary cut).</summary>
    public IReadOnlyList<string> Notes => _notes;
    private readonly List<string> _notes = [];

    private readonly int[] _conductorTets;
    private readonly List<PatchSample> _patch = [];
    private readonly List<WellSample> _well = [];
    private readonly List<AirSample> _air = [];

    private sealed record PatchSample(int Wire, int Contact, int[] Host, double[] HostN, int[] WireNodes, double[] WireN, double W);
    private sealed record WellSample(int Wire, int[] WireNodes, double[] WireN, double W, int[] Line, double[] LineN, int[] Ring, double[] RingW, double G);
    private sealed record AirSample(int Wire, int[] WireNodes, double[] WireN, double W);

    public ElectrothermalSystem(ElectrothermalProblem problem, int? maxDegreeOfParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var m = _mesh = problem.Thermal.Mesh;
        _dop = maxDegreeOfParallelism ?? -1;
        _thermal = new ThermalAssembly(m, maxDegreeOfParallelism);
        Locator = new MeshLocator(m);
        int n = HostNodes = m.NodeCount;
        var wires = problem.Wires;
        WireOffset = new int[wires.Count];
        int nT = n;
        for (int w = 0; w < wires.Count; w++)
        {
            var wire = wires[w];
            if (wire.NodeCount < 3 || wire.NodeCount % 2 == 0) throw new ElectrothermalException($"Wire '{wire.Name}' has no second-order chain.");
            if (wire.S.Length != wire.NodeCount) throw new ElectrothermalException($"Wire '{wire.Name}' has an arc length per node missing.");
            WireOffset[w] = nT;
            nT += wire.NodeCount;
        }
        TUnknowns = nT;
        int nn = m.NodesPerTet;
        _conductorTets = [.. Enumerable.Range(0, m.TetCount).Where(e => problem.SigmaOf(m.TetRegion[e]) is not null)];

        // ── φ: which nodes conduct, which are one equipotential, which conductors carry a current ──
        int items = nT;
        var electrical = new bool[items];
        foreach (int e in _conductorTets) for (int k = 0; k < nn; k++) electrical[m.Tets[nn * e + k]] = true;
        for (int i = n; i < nT; i++) electrical[i] = true;
        var same = new UnionFind(items);
        if (m.Interfaces is { } faces)
        {
            int nf = faces.NodesPerSide;
            for (int t = 0; t < faces.Count; t++)
                for (int k = 0; k < nf; k++)
                {
                    int a = faces.Triangles[2 * nf * t + k], b = faces.Triangles[2 * nf * t + nf + k];
                    if (electrical[a] && electrical[b]) same.Union(a, b);
                }
        }
        var terminalItems = new (int Pos, int Neg)[problem.Currents.Count];
        int SideItem(IReadOnlyList<int> tags, int port, string side)
        {
            int first = -1;
            int nf = m.NodesPerTriangle;
            var set = new HashSet<int>(tags);
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (!set.Contains(m.TriangleTag[t])) continue;
                for (int k = 0; k < nf; k++)
                {
                    int v = m.Triangles[nf * t + k];
                    if (!electrical[v]) continue;
                    if (first < 0) first = v; else same.Union(first, v);
                }
            }
            if (first < 0)
                throw new ElectrothermalException($"Port {port}'s {side} contact touches no conductor a current can flow in: its face lies on no " +
                                                  "solid whose material conducts.");
            return first;
        }
        for (int i = 0; i < problem.Currents.Count; i++)
        {
            var c = problem.Currents[i];
            terminalItems[i] = (SideItem(c.PositiveTags, c.Port, "positive"), SideItem(c.NegativeTags, c.Port, "negative"));
        }

        // ── the patches' and the wells' geometry ──
        WireHosting = new (int, int, int)[wires.Count];
        WellRadius = new (double, double)[wires.Count];
        WireHostRegions = new int[wires.Count][];
        for (int w = 0; w < wires.Count; w++) Samples(problem, w);

        // ── which φ items conduct into which: tetrahedra, wire elements, electrical contacts ──
        var path = new UnionFind(items);
        foreach (int e in _conductorTets)
            for (int k = 1; k < nn; k++) path.Union(same.Find(m.Tets[nn * e]), same.Find(m.Tets[nn * e + k]));
        for (int w = 0; w < wires.Count; w++)
            for (int i = 1; i < wires[w].NodeCount; i++) path.Union(WireOffset[w], WireOffset[w] + i);
        foreach (var s in _patch)
        {
            if (!s.Host.All(v => electrical[v])) continue;
            int a = same.Find(WireOffset[s.Wire] + s.WireNodes[0]);
            foreach (int v in s.Host) path.Union(a, same.Find(v));
        }
        for (int i = 0; i < items; i++) if (electrical[i]) path.Union(i, same.Find(i));
        var carrying = new HashSet<int>();
        for (int i = 0; i < terminalItems.Length; i++)
        {
            var (p, q) = terminalItems[i];
            int port = problem.Currents[i].Port;
            if (path.Find(p) != path.Find(q))
                throw new ElectrothermalException($"Port {port}'s positive and negative contacts are not joined by any conductor or wire, so its current " +
                                                  "has no path: connect them, or remove the port's current.");
            if (same.Find(p) == same.Find(q))
                throw new ElectrothermalException($"Port {port}'s positive and negative contacts are one equipotential: its current would flow nowhere.");
            carrying.Add(path.Find(p));
        }

        // ── the φ unknowns, in node order, of the conductors that carry a current ──
        var phiOf = new Dictionary<int, int>();
        HostPhi = new int[n];
        Array.Fill(HostPhi, -1);
        WirePhi = [.. wires.Select(x => Enumerable.Repeat(-1, x.NodeCount).ToArray())];
        int idle = 0;
        var idleRoots = new HashSet<int>();
        for (int i = 0; i < items; i++)
        {
            if (!electrical[i]) continue;
            if (!carrying.Contains(path.Find(i))) { idleRoots.Add(path.Find(i)); continue; }
            int root = same.Find(i);
            if (!phiOf.TryGetValue(root, out int d)) phiOf[root] = d = phiOf.Count;
            if (i < n) HostPhi[i] = d;
            else
            {
                int w = Array.FindLastIndex(WireOffset, o => o <= i);
                WirePhi[w][i - WireOffset[w]] = d;
            }
        }
        idle = idleRoots.Count;
        PhiUnknowns = phiOf.Count;
        TerminalPhi = [.. terminalItems.Select(t => (phiOf.GetValueOrDefault(same.Find(t.Pos), -1), phiOf.GetValueOrDefault(same.Find(t.Neg), -1)))];
        var refs = new Dictionary<int, (int Port, int Dof)>();
        for (int i = 0; i < terminalItems.Length; i++)
        {
            int comp = path.Find(terminalItems[i].Pos);
            int port = problem.Currents[i].Port;
            if (!refs.TryGetValue(comp, out var r) || port < r.Port) refs[comp] = (port, TerminalPhi[i].Negative);
        }
        Reference = [.. refs.Values.Select(r => r.Dof).Distinct().Order()];
        if (problem.Currents.Count > 0 && idle > 0)
            _notes.Add($"{idle} conductor group(s) touch no port's contact, so no current flows in them; they conduct heat only.");

        // ── the pattern ──
        Pattern = BuildPattern(problem, out _thermalSlot);
    }

    // ── geometry of the couplings ────────────────────────────────────────────────────────────────────

    private void Samples(ElectrothermalProblem problem, int w)
    {
        var m = _mesh;
        var wire = problem.Wires[w];
        int ne = wire.Elements;
        var onPad = new bool[ne];
        if (wire.OnPad is { } op) for (int e = 0; e < Math.Min(ne, op.Length); e++) onPad[e] = op[e];

        // contact patches: every quadrature point of every triangle of each contact's tag
        var tri = TriShapes.For(m.Order);
        int nf = m.NodesPerTriangle;
        var xyz = new double[18];
        for (int c = 0; c < wire.Contacts.Count; c++)
        {
            var ct = wire.Contacts[c];
            if (ct.FirstNode < 0 || ct.LastNode >= wire.NodeCount || ct.FirstNode > ct.LastNode || ct.FirstNode % 2 != 0 || ct.LastNode % 2 != 0)
                throw new ElectrothermalException($"Wire '{wire.Name}''s contact names nodes {ct.FirstNode}..{ct.LastNode}, which are not element ends of its chain.");
            int found = 0;
            for (int t = 0; t < m.TriangleCount; t++)
            {
                if (m.TriangleTag[t] != ct.Tag) continue;
                found++;
                var host = new int[nf];
                for (int i = 0; i < nf; i++)
                {
                    int v = host[i] = m.Triangles[nf * t + i];
                    xyz[3 * i] = m.Nodes[3 * v]; xyz[3 * i + 1] = m.Nodes[3 * v + 1]; xyz[3 * i + 2] = m.Nodes[3 * v + 2];
                }
                for (int q = 0; q < ReferenceElement.TriQuadraturePoints; q++)
                {
                    var nq = tri.N(q);
                    double wq = ReferenceElement.TriW[q] * ReferenceElement.TriJacobian(xyz, nf, tri.Du(q), tri.Dv(q));
                    double px = 0, py = 0, pz = 0;
                    for (int i = 0; i < nf; i++) { px += nq[i] * xyz[3 * i]; py += nq[i] * xyz[3 * i + 1]; pz += nq[i] * xyz[3 * i + 2]; }
                    var (wn, wv) = WireAt(wire, ct, px, py, pz);
                    _patch.Add(new PatchSample(w, c, host, nq.ToArray(), wn, wv, wq));
                }
            }
            if (found == 0) _notes.Add($"Wire '{wire.Name}''s contact patch (tag {ct.Tag}) has no triangle in the mesh: that end touches nothing.");
        }

        // the span: a solid round it (the well), or air
        int inSolid = 0, inAir = 0, pads = 0;
        var hosts = new SortedSet<int>();
        for (int e = 0; e < ne; e++)
        {
            if (onPad[e]) { pads++; continue; }
            bool anySolid = false;
            for (int q = 0; q < GaussX.Length; q++)
            {
                var (x, tangent, jac) = Geometry(wire, e, GaussX[q]);
                var wn = Line3(GaussX[q]);
                int[] nodes = [2 * e, 2 * e + 1, 2 * e + 2];
                double wq = GaussW[q] * jac;
                var at = Locator.Locate(x.X, x.Y, x.Z);
                if (at is not { } host) { _air.Add(new AirSample(w, nodes, wn, wq)); continue; }
                anySolid = true;
                hosts.Add(host.Region);
                double k = PerpendicularK(problem.Thermal.Conductivity[host.Region], tangent);
                double h = Locator.Size(host.Element);
                double r = Math.Max(RingSizes * h, 2 * wire.Radius);
                var (u, v) = Perpendiculars(tangent);
                var ring = new Dictionary<int, double>();
                int inside = 0;
                for (int j = 0; j < RingPoints; j++)
                {
                    double a = 2 * Math.PI * j / RingPoints, ca = Math.Cos(a), sa = Math.Sin(a);
                    if (Locator.Locate(x.X + r * (ca * u.X + sa * v.X), x.Y + r * (ca * u.Y + sa * v.Y), x.Z + r * (ca * u.Z + sa * v.Z)) is not { } p) continue;
                    inside++;
                    for (int i = 0; i < p.N.Length; i++)
                    {
                        int node = m.Tets[m.NodesPerTet * p.Element + i];
                        ring[node] = ring.GetValueOrDefault(node) + p.N[i];
                    }
                }
                if (inside == 0) { _air.Add(new AirSample(w, nodes, wn, wq)); continue; }
                var ringNodes = ring.Keys.Order().ToArray();
                var ringW = ringNodes.Select(i => ring[i] / inside).ToArray();
                var line = Enumerable.Range(0, m.NodesPerTet).Select(i => m.Tets[m.NodesPerTet * host.Element + i]).ToArray();
                double g = 2 * Math.PI * k / Math.Log(r / wire.Radius);
                var (r0, r1) = WellRadius[w];
                WellRadius[w] = (r0 == 0 ? r : Math.Min(r0, r), Math.Max(r1, r));
                _well.Add(new WellSample(w, nodes, wn, wq, line, host.N, ringNodes, ringW, g));
            }
            if (anySolid) inSolid++; else inAir++;
        }
        WireHosting[w] = (inSolid, inAir, pads);
        WireHostRegions[w] = [.. hosts];
    }

    /// <summary>The wire's chain nodes and shape values at a patch point.</summary>
    private static (int[] Nodes, double[] N) WireAt(ThermalWire wire, WireContact ct, double px, double py, double pz)
    {
        if (ct.FirstNode == ct.LastNode) return ([ct.FirstNode], [1.0]);
        var p = wire.Points;
        int a = ct.FirstNode, b = ct.LastNode;
        double ax = p[3 * a], ay = p[3 * a + 1], az = p[3 * a + 2];
        double dx = p[3 * b] - ax, dy = p[3 * b + 1] - ay, dz = p[3 * b + 2] - az;
        double l2 = dx * dx + dy * dy + dz * dz;
        double t = l2 > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy + (pz - az) * dz) / l2, 0, 1) : 0;
        double s = wire.S[a] + t * (wire.S[b] - wire.S[a]);
        int e = a / 2;
        while (e < b / 2 - 1 && (wire.S[2 * e + 2] - s) * Math.Sign(wire.S[b] - wire.S[a]) < 0) e++;
        double sa = wire.S[2 * e], sb = wire.S[2 * e + 2];
        double xi = sb != sa ? Math.Clamp(2 * (s - sa) / (sb - sa) - 1, -1, 1) : 0;
        return ([2 * e, 2 * e + 1, 2 * e + 2], Line3(xi));
    }

    /// <summary>A three-node line element's shape values at ξ, nodes (start, middle, end).</summary>
    private static double[] Line3(double xi) => [xi * (xi - 1) / 2, 1 - xi * xi, xi * (xi + 1) / 2];

    private static (double A, double M, double B) Line3D(double xi) => (xi - 0.5, -2 * xi, xi + 0.5);

    /// <summary>Position, unit tangent and |dx/dξ| of wire element <paramref name="e"/> at ξ.</summary>
    private static ((double X, double Y, double Z) X, (double X, double Y, double Z) T, double J) Geometry(ThermalWire wire, int e, double xi)
    {
        var n = Line3(xi);
        var (da, dm, db) = Line3D(xi);
        var p = wire.Points;
        int a = 2 * e, mid = a + 1, b = a + 2;
        double X(int i, int c) => p[3 * i + c];
        var x = (n[0] * X(a, 0) + n[1] * X(mid, 0) + n[2] * X(b, 0), n[0] * X(a, 1) + n[1] * X(mid, 1) + n[2] * X(b, 1),
                 n[0] * X(a, 2) + n[1] * X(mid, 2) + n[2] * X(b, 2));
        double tx = da * X(a, 0) + dm * X(mid, 0) + db * X(b, 0);
        double ty = da * X(a, 1) + dm * X(mid, 1) + db * X(b, 1);
        double tz = da * X(a, 2) + dm * X(mid, 2) + db * X(b, 2);
        double j = Math.Sqrt(tx * tx + ty * ty + tz * tz);
        return (x, j > 0 ? (tx / j, ty / j, tz / j) : (0, 0, 1), j);
    }

    /// <summary>The conductivity across a line of direction <paramref name="t"/>: the region's scalar for an isotropic region,
    /// else √(det K / tᵀKt) of its diagonal tensor.</summary>
    private static double PerpendicularK(ThermalConductivity c, (double X, double Y, double Z) t)
    {
        if (c.IsIsotropic) return c.Nominal;
        double kx = c.Nominal * c.Axes.X, ky = c.Nominal * c.Axes.Y, kz = c.Nominal * c.Axes.Z;
        return Math.Sqrt(kx * ky * kz / (kx * t.X * t.X + ky * t.Y * t.Y + kz * t.Z * t.Z));
    }

    private static ((double X, double Y, double Z), (double X, double Y, double Z)) Perpendiculars((double X, double Y, double Z) t)
    {
        var a = Math.Abs(t.Z) < 0.9 ? (0.0, 0.0, 1.0) : (1.0, 0.0, 0.0);
        var u = (t.Y * a.Item3 - t.Z * a.Item2, t.Z * a.Item1 - t.X * a.Item3, t.X * a.Item2 - t.Y * a.Item1);
        double l = Math.Sqrt(u.Item1 * u.Item1 + u.Item2 * u.Item2 + u.Item3 * u.Item3);
        u = (u.Item1 / l, u.Item2 / l, u.Item3 / l);
        var v = (t.Y * u.Item3 - t.Z * u.Item2, t.Z * u.Item1 - t.X * u.Item3, t.X * u.Item2 - t.Y * u.Item1);
        return (u, v);
    }

    // ── structure ────────────────────────────────────────────────────────────────────────────────────

    private SparseRows BuildPattern(ElectrothermalProblem problem, out int[] thermalSlot)
    {
        var m = _mesh;
        int size = Size, nT = TUnknowns;
        var rows = new List<int>[size];
        for (int i = 0; i < size; i++) rows[i] = [];
        void Pair(int a, int b) { rows[a].Add(b); rows[b].Add(a); }
        void All(IReadOnlyList<int> ids) { foreach (int a in ids) foreach (int b in ids) rows[a].Add(b); }
        var tp = _thermal.Pattern;
        for (int i = 0; i < tp.Rows; i++)
            for (int k = tp.Ptr[i]; k < tp.Ptr[i + 1]; k++) rows[i].Add(tp.Idx[k]);
        int nn = m.NodesPerTet;
        foreach (int e in _conductorTets)
        {
            var ids = new List<int>(2 * nn);
            for (int k = 0; k < nn; k++)
            {
                int v = m.Tets[nn * e + k];
                ids.Add(v);
                if (HostPhi[v] >= 0) ids.Add(nT + HostPhi[v]);
            }
            All(ids);
        }
        for (int w = 0; w < problem.Wires.Count; w++)
        {
            var wire = problem.Wires[w];
            for (int e = 0; e < wire.Elements; e++)
            {
                var ids = new List<int>(6);
                for (int k = 0; k < 3; k++)
                {
                    int local = 2 * e + k;
                    ids.Add(WireOffset[w] + local);
                    if (WirePhi[w][local] >= 0) ids.Add(nT + WirePhi[w][local]);
                }
                All(ids);
            }
        }
        foreach (var s in _patch)
        {
            var ids = new List<int>();
            foreach (int k in s.WireNodes)
            {
                ids.Add(WireOffset[s.Wire] + k);
                if (WirePhi[s.Wire][k] >= 0) ids.Add(nT + WirePhi[s.Wire][k]);
            }
            foreach (int v in s.Host)
            {
                ids.Add(v);
                if (HostPhi[v] >= 0) ids.Add(nT + HostPhi[v]);
            }
            All(ids);
        }
        foreach (var s in _well)
        {
            var a = s.WireNodes.Select(k => WireOffset[s.Wire] + k).Concat(s.Line).ToList();
            var b = s.WireNodes.Select(k => WireOffset[s.Wire] + k).Concat(s.Ring).ToList();
            foreach (int i in a) foreach (int j in b) Pair(i, j);
        }
        var ptr = new int[size + 1];
        var cols = new int[size][];
        Parallel.For(0, size, new ParallelOptions { MaxDegreeOfParallelism = _dop }, i =>
        {
            var r = rows[i];
            r.Add(i);
            r.Sort();
            int u = 0;
            for (int k = 0; k < r.Count; k++) if (u == 0 || r[k] != r[u - 1]) r[u++] = r[k];
            cols[i] = [.. r.Take(u)];
        });
        for (int i = 0; i < size; i++) ptr[i + 1] = ptr[i] + cols[i].Length;
        var idx = new int[ptr[size]];
        for (int i = 0; i < size; i++) cols[i].CopyTo(idx, ptr[i]);
        var pattern = new SparseRows(size, size, ptr, idx, new double[idx.Length]);
        thermalSlot = new int[tp.Nnz];
        for (int i = 0; i < tp.Rows; i++)
            for (int k = tp.Ptr[i]; k < tp.Ptr[i + 1]; k++) thermalSlot[k] = pattern.Slot(i, tp.Idx[k]);
        return pattern;
    }

    public SparseRows Matrix(double[] values) => new(Pattern.Rows, Pattern.Cols, Pattern.Ptr, Pattern.Idx, values);

    // ── assembly ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The secant, the full Jacobian and the load at state <paramref name="x"/> (length <see cref="Size"/>). With
    /// <paramref name="kOfT"/> off every k is nominal; with <paramref name="sigmaOfT"/> off every σ is its 20 °C value.
    /// </summary>
    public ElectrothermalAssembled Assemble(ElectrothermalProblem problem, double[] x, bool kOfT, bool sigmaOfT)
    {
        var m = _mesh;
        int n = HostNodes, nT = TUnknowns, size = Size;
        var secant = new double[Pattern.Nnz];
        var tangent = new double[Pattern.Nnz];
        var load = new double[size];
        bool invalid = false;

        // ── the mesh's own conduction, sources, Robin and interfaces ──
        var th = _thermal.Assemble(problem.Thermal, x[..n], kOfT, tangent: true);
        var thT = th.Tangent ?? th.Secant;
        for (int k = 0; k < th.Secant.Length; k++)
        {
            secant[_thermalSlot[k]] += th.Secant[k];
            tangent[_thermalSlot[k]] += thT[k];
        }
        for (int i = 0; i < n; i++) load[i] += th.Load[i];
        if (th.AnyNonFinite) invalid = true;
        void Add(int r, int c, double s, double t)
        {
            int slot = Pattern.Slot(r, c);
            if (slot < 0) throw new InvalidOperationException("an entry outside the pattern");
            secant[slot] += s;
            tangent[slot] += t;
        }

        // ── conductor tetrahedra: σ(T), Joule ──
        var jouleRegion = new double[problem.Thermal.Conductivity.Count];
        int nn = m.NodesPerTet;
        if (PhiUnknowns > 0 && _conductorTets.Length > 0)
        {
            var blocks = new TetBlock?[_conductorTets.Length];
            var bad = new bool[_conductorTets.Length];
            Parallel.For(0, _conductorTets.Length, new ParallelOptions { MaxDegreeOfParallelism = _dop }, i =>
            {
                blocks[i] = TetElectrical(problem, _conductorTets[i], x, sigmaOfT, out bad[i]);
            });
            for (int i = 0; i < blocks.Length; i++)
            {
                int e = _conductorTets[i];
                var b = blocks[i];
                if (bad[i]) invalid = true;
                if (b is null) continue;
                jouleRegion[m.TetRegion[e]] += b.Joule;
                for (int a = 0; a < nn; a++)
                {
                    int va = m.Tets[nn * e + a], pa = HostPhi[va];
                    load[va] += b.Q[a];
                    for (int c = 0; c < nn; c++)
                    {
                        int vc = m.Tets[nn * e + c], pc = HostPhi[vc];
                        Add(nT + pa, nT + pc, b.E[10 * a + c], b.E[10 * a + c]);
                        Add(nT + pa, vc, 0, b.EdT[10 * a + c]);
                        Add(va, nT + pc, 0, -b.QdPhi[10 * a + c]);
                        Add(va, vc, 0, -b.QdT[10 * a + c]);
                    }
                }
            }
        }

        // ── wires: kA, σA, Joule ──
        var jouleWire = new double[problem.Wires.Count];
        var rfWire = new double[problem.Wires.Count];
        var rfHarmonic = new double[problem.Wires.Count][];
        for (int w = 0; w < problem.Wires.Count; w++)
        {
            var wire = problem.Wires[w];
            var rfPart = rfHarmonic[w] = new double[wire.Harmonics.Count];
            var rfq = new double[wire.Harmonics.Count];
            var rf3 = new double[3];
            var rfdt = new double[9];
            int o = WireOffset[w];
            var ph = WirePhi[w];
            var t3 = new double[3];
            var f3 = new double[3];
            var ke = new double[9];
            var je = new double[9];
            var ee = new double[9];
            var edt = new double[9];
            var q = new double[3];
            var qdf = new double[9];
            var qdt = new double[9];
            for (int e = 0; e < wire.Elements; e++)
            {
                int a0 = 2 * e;
                bool conducts = ph[a0] >= 0;
                for (int k = 0; k < 3; k++) { t3[k] = x[o + a0 + k]; f3[k] = conducts ? x[nT + ph[a0 + k]] : 0; }
                Array.Clear(ke); Array.Clear(je); Array.Clear(ee); Array.Clear(edt); Array.Clear(q); Array.Clear(qdf); Array.Clear(qdt);
                Array.Clear(rf3); Array.Clear(rfdt);
                // R-em3d78-4a/-4c/-4d: RF heat along the span only (a foot on its pad hands its current to the pad), uniform along it
                bool rf = wire.Harmonics.Count > 0 && !(wire.OnPad is { } pad && e < pad.Length && pad[e]);
                for (int g = 0; g < GaussX.Length; g++)
                {
                    var (_, _, jac) = Geometry(wire, e, GaussX[g]);
                    var nv = Line3(GaussX[g]);
                    var (da, dm, db) = Line3D(GaussX[g]);
                    double[] dn = [da / jac, dm / jac, db / jac];
                    double wq = GaussW[g] * jac;
                    double tq = 0, tp = 0, fp = 0;
                    for (int k = 0; k < 3; k++) { tq += nv[k] * t3[k]; tp += dn[k] * t3[k]; fp += dn[k] * f3[k]; }
                    double kk = wire.K.Nominal, dk = 0;
                    if (kOfT && wire.K.OfT is { } kf) (kk, dk) = kf(tq);
                    var (sg, ds) = wire.Sigma.At(tq, sigmaOfT);
                    if (!(kk > 0) || !double.IsFinite(kk) || !(sg > 0) || !double.IsFinite(sg) || !double.IsFinite(dk) || !double.IsFinite(ds)) invalid = true;
                    double A = wire.Area;
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                        {
                            ke[3 * i + j] += wq * kk * A * dn[i] * dn[j];
                            je[3 * i + j] += wq * dk * A * tp * dn[i] * nv[j];
                            if (!conducts) continue;
                            ee[3 * i + j] += wq * sg * A * dn[i] * dn[j];
                            edt[3 * i + j] += wq * ds * A * fp * dn[i] * nv[j];
                            qdf[3 * i + j] += wq * 2 * sg * A * fp * dn[j] * nv[i];
                            qdt[3 * i + j] += wq * ds * A * fp * fp * nv[i] * nv[j];
                        }
                    if (rf)
                    {
                        var (qr, dqr) = wire.RfHeat(tq, sigmaOfT, rfq);
                        if (!double.IsFinite(qr) || !double.IsFinite(dqr)) invalid = true;
                        rfWire[w] += wq * qr;
                        for (int h = 0; h < rfq.Length; h++) rfPart[h] += wq * rfq[h];
                        for (int i = 0; i < 3; i++)
                        {
                            rf3[i] += wq * qr * nv[i];
                            for (int j = 0; j < 3; j++) rfdt[3 * i + j] += wq * dqr * nv[i] * nv[j];
                        }
                    }
                    if (conducts)
                    {
                        double qq = wq * sg * A * fp * fp;
                        jouleWire[w] += qq;
                        for (int i = 0; i < 3; i++) q[i] += qq * nv[i];
                    }
                }
                for (int i = 0; i < 3; i++)
                {
                    int ri = o + a0 + i;
                    load[ri] += q[i] + rf3[i];
                    for (int j = 0; j < 3; j++)
                    {
                        int cj = o + a0 + j;
                        Add(ri, cj, ke[3 * i + j], ke[3 * i + j] + (kOfT ? je[3 * i + j] : 0) - qdt[3 * i + j] - rfdt[3 * i + j]);
                        if (!conducts) continue;
                        int pi = nT + ph[a0 + i], pj = nT + ph[a0 + j];
                        Add(pi, pj, ee[3 * i + j], ee[3 * i + j]);
                        Add(pi, cj, 0, sigmaOfT ? edt[3 * i + j] : 0);
                        Add(ri, pj, 0, -qdf[3 * i + j]);
                    }
                }
            }
        }

        // ── contact patches: h″ and G″ between the wire and the pad, and the contact's own Joule heat ──
        double jouleContacts = 0;
        foreach (var s in _patch)
        {
            var wire = problem.Wires[s.Wire];
            var ct = wire.Contacts[s.Contact];
            var (h, g) = ContactConductance(problem, s.Wire, s.Contact);
            int o = WireOffset[s.Wire];
            int nw = s.WireNodes.Length, nh = s.Host.Length;
            var ids = new int[nw + nh];
            var c = new double[nw + nh];
            var a = new double[nw + nh];
            for (int i = 0; i < nw; i++) { ids[i] = o + s.WireNodes[i]; c[i] = s.WireN[i]; a[i] = s.WireN[i]; }
            for (int i = 0; i < nh; i++) { ids[nw + i] = s.Host[i]; c[nw + i] = -s.HostN[i]; a[nw + i] = s.HostN[i]; }
            for (int i = 0; i < ids.Length; i++)
                for (int j = 0; j < ids.Length; j++)
                {
                    double v = h * s.W * c[i] * c[j];
                    Add(ids[i], ids[j], v, v);
                }
            bool electric = g > 0 && s.Host.All(v => HostPhi[v] >= 0) && s.WireNodes.All(k => WirePhi[s.Wire][k] >= 0);
            if (!electric) continue;
            var pids = new int[ids.Length];
            double dphi = 0;
            for (int i = 0; i < nw; i++) pids[i] = nT + WirePhi[s.Wire][s.WireNodes[i]];
            for (int i = 0; i < nh; i++) pids[nw + i] = nT + HostPhi[s.Host[i]];
            for (int i = 0; i < ids.Length; i++) dphi += c[i] * x[pids[i]];
            double qc = g * s.W * dphi * dphi;
            jouleContacts += qc;
            for (int i = 0; i < ids.Length; i++)
            {
                load[ids[i]] += 0.5 * qc * a[i];
                for (int j = 0; j < ids.Length; j++)
                {
                    double v = g * s.W * c[i] * c[j];
                    Add(pids[i], pids[j], v, v);
                    Add(ids[i], pids[j], 0, -g * s.W * dphi * c[j] * a[i]);
                }
            }
        }

        // ── the well: heat from the span into the solid round it ──
        foreach (var s in _well)
        {
            int o = WireOffset[s.Wire];
            double gw = s.G * s.W;
            // b: where the heat goes (+ the wire, − the solid at the centreline); d: the difference it is driven by
            for (int i = 0; i < 3; i++)
            {
                int ri = o + s.WireNodes[i];
                for (int j = 0; j < 3; j++) { double v = gw * s.WireN[i] * s.WireN[j]; Add(ri, o + s.WireNodes[j], v, v); }
                for (int l = 0; l < s.Ring.Length; l++) { double v = -gw * s.WireN[i] * s.RingW[l]; Add(ri, s.Ring[l], v, v); }
            }
            for (int i = 0; i < s.Line.Length; i++)
            {
                int ri = s.Line[i];
                for (int j = 0; j < 3; j++) { double v = -gw * s.LineN[i] * s.WireN[j]; Add(ri, o + s.WireNodes[j], v, v); }
                for (int l = 0; l < s.Ring.Length; l++) { double v = gw * s.LineN[i] * s.RingW[l]; Add(ri, s.Ring[l], v, v); }
            }
        }

        // ── convection from a span in air, when asked for ──
        double wireConv = 0;
        foreach (var s in _air)
        {
            var wire = problem.Wires[s.Wire];
            if (wire.ConvectionH is not { } hc || hc <= 0) continue;
            int o = WireOffset[s.Wire];
            double hp = hc * Math.PI * wire.Diameter * s.W;
            double tq = 0;
            for (int i = 0; i < 3; i++) tq += s.WireN[i] * x[o + s.WireNodes[i]];
            wireConv += hp * (tq - wire.AmbientC);
            for (int i = 0; i < 3; i++)
            {
                load[o + s.WireNodes[i]] += hp * wire.AmbientC * s.WireN[i];
                for (int j = 0; j < 3; j++) { double v = hp * s.WireN[i] * s.WireN[j]; Add(o + s.WireNodes[i], o + s.WireNodes[j], v, v); }
            }
        }

        // ── the ports' currents ──
        for (int i = 0; i < problem.Currents.Count; i++)
        {
            var (p, q) = TerminalPhi[i];
            double c = problem.Currents[i].CurrentA;
            load[nT + p] += c;
            load[nT + q] -= c;
        }

        return new ElectrothermalAssembled
        {
            Secant = secant, Tangent = tangent, Load = load, SourceW = th.SourcePowerW, JouleByRegion = jouleRegion,
            JouleByWire = jouleWire, JouleContactsW = jouleContacts, Invalid = invalid, WireConvectionW = wireConv,
            RfByWire = rfWire, RfByHarmonic = rfHarmonic,
        };
    }

    /// <summary>A contact's h″ and G″ per unit area: as stated (in series with its lumped part), or a perfect bond.</summary>
    public (double H, double G) ContactConductance(ElectrothermalProblem problem, int wire, int contact)
    {
        var ct = problem.Wires[wire].Contacts[contact];
        double area = _patchArea.GetOrAdd((wire, contact), _ => _patch.Where(s => s.Wire == wire && s.Contact == contact).Sum(s => s.W));
        double scale = area > 0 ? Math.Sqrt(area) : 1;
        double kHost = ct.HostRegion < problem.Thermal.Conductivity.Count ? problem.Thermal.Conductivity[ct.HostRegion].Nominal : 1;
        double sHost = problem.SigmaOf(ct.HostRegion)?.At20 ?? 0;
        double h = ct.HWm2K ?? PerfectBond * kHost / scale;
        double g = ct.GSm2 ?? (sHost > 0 ? PerfectBondElectrical * sHost / scale : 0);
        if (ct.SeriesThermalM2KW > 0) h = 1 / (1 / h + ct.SeriesThermalM2KW);
        if (ct.SeriesElectricalOhmM2 > 0 && g > 0) g = 1 / (1 / g + ct.SeriesElectricalOhmM2);
        return (h, g);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), double> _patchArea = new();

    /// <summary>One conductor tetrahedron's electrical blocks and Joule load.</summary>
    private sealed class TetBlock
    {
        public double[] E = new double[100], EdT = new double[100], QdPhi = new double[100], QdT = new double[100], Q = new double[10];
        public double Joule;
    }

    private TetBlock? TetElectrical(ElectrothermalProblem problem, int e, double[] x, bool sigmaOfT, out bool bad)
    {
        bad = false;
        var m = _mesh;
        int nn = m.NodesPerTet, nT = TUnknowns;
        var c = problem.SigmaOf(m.TetRegion[e])!;
        var b = new TetBlock();
        Span<double> xyz = stackalloc double[30];
        Span<double> gx = stackalloc double[10], gy = stackalloc double[10], gz = stackalloc double[10];
        Span<double> t = stackalloc double[10], f = stackalloc double[10];
        for (int i = 0; i < nn; i++)
        {
            int v = m.Tets[nn * e + i];
            xyz[3 * i] = m.Nodes[3 * v]; xyz[3 * i + 1] = m.Nodes[3 * v + 1]; xyz[3 * i + 2] = m.Nodes[3 * v + 2];
            t[i] = x[v];
            f[i] = HostPhi[v] >= 0 ? x[nT + HostPhi[v]] : 0;
        }
        if (Enumerable.Range(0, nn).Any(i => HostPhi[m.Tets[nn * e + i]] < 0)) return null;
        var shapes = TetShapes.For(m.Order);
        for (int q = 0; q < ReferenceElement.TetQuadraturePoints; q++)
        {
            var nq = shapes.N(q);
            if (!ReferenceElement.TetGradients(xyz, nn, shapes.Dx(q), shapes.Dy(q), shapes.Dz(q), gx, gy, gz, out double det)) { bad = true; continue; }
            double w = ReferenceElement.TetW[q] * det;
            double tq = 0, fx = 0, fy = 0, fz = 0;
            for (int i = 0; i < nn; i++) { tq += nq[i] * t[i]; fx += gx[i] * f[i]; fy += gy[i] * f[i]; fz += gz[i] * f[i]; }
            var (sg, ds) = c.At(tq, sigmaOfT);
            if (!(sg > 0) || !double.IsFinite(sg) || !double.IsFinite(ds)) bad = true;
            double f2 = fx * fx + fy * fy + fz * fz;
            b.Joule += w * sg * f2;
            for (int i = 0; i < nn; i++)
            {
                double gif = gx[i] * fx + gy[i] * fy + gz[i] * fz;
                b.Q[i] += w * sg * f2 * nq[i];
                for (int j = 0; j < nn; j++)
                {
                    b.E[10 * i + j] += w * sg * (gx[i] * gx[j] + gy[i] * gy[j] + gz[i] * gz[j]);
                    b.EdT[10 * i + j] += w * ds * gif * nq[j];
                    b.QdPhi[10 * i + j] += w * 2 * sg * (gx[j] * fx + gy[j] * fy + gz[j] * fz) * nq[i];
                    b.QdT[10 * i + j] += w * ds * f2 * nq[i] * nq[j];
                }
            }
        }
        return b;
    }

    // ── read-outs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A wire's current, A, positive from its start to its end: the conservative flux out of the far end of its
    /// middle span element, −Σⱼ Eᵦⱼφⱼ with E = ∫σ(T)A N′ᵢN′ⱼ ds — the quantity the φ equations balance exactly, which a
    /// derivative of the second-order φ at a point is not.</summary>
    public double WireCurrent(ElectrothermalProblem problem, int w, double[] x, bool sigmaOfT)
    {
        var wire = problem.Wires[w];
        if (WirePhi[w][0] < 0) return 0;
        var span = Enumerable.Range(0, wire.Elements).Where(e => wire.OnPad is null || e >= wire.OnPad.Length || !wire.OnPad[e]).ToList();
        int el = span.Count > 0 ? span[span.Count / 2] : wire.Elements / 2;
        int o = WireOffset[w], nT = TUnknowns;
        double f = 0;
        for (int g = 0; g < GaussX.Length; g++)
        {
            var (_, _, jac) = Geometry(wire, el, GaussX[g]);
            var nv = Line3(GaussX[g]);
            var (da, dm, db) = Line3D(GaussX[g]);
            double[] dn = [da / jac, dm / jac, db / jac];
            double tq = 0, fp = 0;
            for (int k = 0; k < 3; k++) { tq += nv[k] * x[o + 2 * el + k]; fp += dn[k] * x[nT + WirePhi[w][2 * el + k]]; }
            f += GaussW[g] * jac * wire.Sigma.At(tq, sigmaOfT).Sigma * wire.Area * dn[2] * fp;
        }
        return -f;
    }

    /// <summary>A wire's resistance along its span (heel to heel), Ω, at the temperatures of <paramref name="x"/>.</summary>
    public double WireResistance(ElectrothermalProblem problem, int w, double[] x, bool sigmaOfT)
    {
        var wire = problem.Wires[w];
        int o = WireOffset[w];
        double r = 0;
        for (int e = 0; e < wire.Elements; e++)
        {
            if (wire.OnPad is { } op && e < op.Length && op[e]) continue;
            for (int g = 0; g < GaussX.Length; g++)
            {
                var (_, _, jac) = Geometry(wire, e, GaussX[g]);
                var nv = Line3(GaussX[g]);
                double tq = 0;
                for (int k = 0; k < 3; k++) tq += nv[k] * x[o + 2 * e + k];
                r += GaussW[g] * jac / (wire.Sigma.At(tq, sigmaOfT).Sigma * wire.Area);
            }
        }
        return r;
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
