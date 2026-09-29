// brief-em3d-77 — the thermal run's conductive-balance half: a setup with bond wires or port currents is solved by Newton over
// temperature and potential (src/Thermal/Nonlinear), point by point from the last converged point, and a point past the
// steady-state limit is a RUNAWAY — a result, not a failure (R-em3d77-5).
//
// WHAT IT ADDS TO THE RESULT (R-em3d77-6), per sweep point:
//   * the "wires" group: T(s) of every wire, over the sweep's axes then s (metres, 0 at the start heel) — the table the viewer
//     colours each wire from, named Twire:<name>(s);
//   * the thermal group: per wire its hottest temperature and where (Twire:<name>:max, :smax), its DC current (Iwire:), its
//     dissipated power (Pwire:) and its resistance at its solved temperature (Rwire:); per port its voltage (Vport:<n>); per
//     conducting solid its Joule heat (Pjoule:<solid>); and Runaway, 1 at a point with no steady state.
//   * brief-em3d-78 R-em3d78-4e: per wire carrying harmonics, its RF heat in all (Prf:<name>) and per harmonic (Prf:<name>:h2), and
//     its peak current per harmonic (Irf:<name>:h2) — the share of its array's current, whatever the point's temperatures.
// A runaway point's temperatures are NaN, every cube's included, and its fields are NaN too: it has none.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Thermal;
using CircuitRF.Thermal.Electrothermal;
using CircuitRF.Thermal.Nonlinear;
using RfCore.Data;

namespace CircuitRF.Design.Thermal;

public static partial class ThermalRunService
{
    /// <summary>R-em3d77-5 — the sentence a runaway puts in the run's messages.</summary>
    public static string RunawaySentence(double aboveA, double lastA, string wire, double tempC)
        => string.Create(CultureInfo.InvariantCulture,
            $"No steady state above about {aboveA:G4} A: thermal runaway. The last converged point is {lastA:G4} A (wire {wire} at {tempC:F0} °C).");

    /// <summary>What conductive balance hands back for one point.</summary>
    private sealed record ElectroPoint(ThermalSolution Thermal, ElectrothermalSolution? Solution, bool Runaway, string? Failure);

    /// <summary>One run's conductive-balance state: the system, the last converged point, and each point's answer.</summary>
    private sealed class ElectroRun
    {
        public required ElectrothermalSystem System { get; init; }
        public required Func<IReadOnlyList<(string Var, double Value)>, double, ElectrothermalProblem> At { get; init; }
        public required ThermalSolveOptions Options { get; init; }
        public required ThermalMesh Mesh { get; init; }
        public required IReadOnlyList<ThermalWirePlan> Wires { get; init; }
        public required ThermalRfPlan Rf { get; init; }
        /// <summary>brief-em3d-79 — a circuit-driven run's sweep coordinates of a drive vector (<c>Pin ≈ 31.2 dBm</c>), so a
        /// runaway is placed in the sweep's own unit rather than in amperes; null otherwise.</summary>
        public Func<IReadOnlyList<(string Var, double Value)>, string>? Say { get; init; }
        /// <summary>brief-em3d-78 — per point, per wire, each harmonic's peak current in the wire (the plan's labels' order).</summary>
        public readonly List<double[][]> RfPeaks = [];
        public ElectrothermalSolution? Last;
        public IReadOnlyList<(string Var, double Value)>? LastPoint;
        public ElectrothermalSolution? RowFirst;
        public IReadOnlyList<(string Var, double Value)>? RowFirstPoint;
        public bool RowRunaway;
        /// <summary>The point this row ran away at: a later point of the row is passed over only if it lies at or beyond it, seen
        /// from the last converged point — a sweep running DOWN in current (or up in cooling) reaches points that do solve.</summary>
        private IReadOnlyList<(string Var, double Value)>? _runawayAt;
        /// <summary>This row's first point was skipped: its first point that solves is the one the next row starts from.</summary>
        private bool _rowFirstPending;
        public readonly List<ElectrothermalSolution?> Solutions = [];

        /// <summary>Point <paramref name="point"/> (index <paramref name="index"/>; <paramref name="row"/> points per innermost row).</summary>
        public ElectroPoint Solve(IReadOnlyList<(string Var, double Value)> point, int index, int row)
        {
            NewRow(index, row);
            var target = At(point, 1);
            RfPeaks.Add([.. target.Wires.Select(w => w.Harmonics.Select(h => h.PeakA).ToArray())]);
            if (RowRunaway && Beyond(point)) return Record(point, index, null, true, null);
            ContinuationResult r;
            if (target.Currents.Count == 0 && target.Wires.All(w => w.Harmonics.All(h => h.PeakA == 0)))
            {
                var s = ConductiveBalance.Solve(target, Options, System, Last?.State);
                r = new ContinuationResult(s.Converged ? s : null, false, s.Converged ? 1 : 0, 1, s.Converged ? s : Last, 1);
                if (!s.Converged) return Record(point, index, null, false, string.Join(" ", s.Thermal.Notes));
            }
            else if (Last is null || LastPoint is null)
            {
                r = Continuation.Advance(l => At(point, l), System, null, Options, (lo, hi) => hi > 0 ? (hi - lo) / hi : 1);
                if (r.Solution is null && !r.Runaway)
                    return Record(point, index, null, false, "it did not converge even at zero current — " +
                                  string.Join(" ", r.LastFailed?.Thermal.Notes ?? []));
            }
            else
            {
                var from = LastPoint;
                List<(string, double)> Along(double l) => [.. point.Select((p, k) => (p.Var, from[k].Value + l * (p.Value - from[k].Value)))];
                double Width(double lo, double hi) => point.Select((p, k) =>
                {
                    double d = p.Value - from[k].Value, v = from[k].Value + hi * d;
                    return d == 0 ? 0 : Math.Abs((hi - lo) * d) / Math.Max(Math.Abs(v), 1e-300);
                }).DefaultIfEmpty(0).Max();
                r = Continuation.Advance(l => At(Along(l), 1), System, Last, Options, Width);
            }
            if (r.Solution is null && !r.Runaway)
                return Record(point, index, null, false, $"it did not converge in {r.Solves} solves and no runaway bracket closed — " +
                              string.Join(" ", r.LastFailed?.Thermal.Notes ?? []));
            if (r.Runaway)
            {
                RowRunaway = true;
                _runawayAt = point;
                return Record(point, index, r, true, null);
            }
            Last = r.Solution;
            LastPoint = point;
            if (row <= 0 || index % row == 0 || _rowFirstPending) (RowFirst, RowFirstPoint, _rowFirstPending) = (Last, point, false);
            return Record(point, index, r, false, null);
        }

        /// <summary>brief-em3d-79 R-em3d79-3 — a point the circuit's HB did not converge at: no solve, NaN everywhere, and the
        /// continuation carries on from the last point that did solve.</summary>
        public ElectroPoint Skip(int index, int row)
        {
            // a skipped point can still be a row's first: the row's reset must not wait for a point that solves
            NewRow(index, row);
            if (row > 0 && index % row == 0) _rowFirstPending = true;
            RfPeaks.Add([.. Rf.WireLabels.Select(l => Enumerable.Repeat(double.NaN, l.Length).ToArray())]);
            Solutions.Add(null);
            return new ElectroPoint(NanSolution(Mesh), null, false, null);
        }

        /// <summary>Whether <paramref name="point"/> lies at or past the runaway point along the line from the last converged point
        /// to it (each coordinate relative to the step, so a current and a temperature weigh alike). With no converged point to
        /// measure from, every later point of the row is past it.</summary>
        private bool Beyond(IReadOnlyList<(string Var, double Value)> point)
        {
            if (_runawayAt is not { } fail || LastPoint is not { } from) return true;
            double dot = 0, dd = 0;
            for (int k = 0; k < point.Count && k < fail.Count && k < from.Count; k++)
            {
                double d = fail[k].Value - from[k].Value;
                if (d == 0) continue;
                double scale = Math.Abs(d);
                dot += (point[k].Value - from[k].Value) / scale * (d / scale);
                dd += 1;
            }
            return dd == 0 || dot >= dd;
        }

        /// <summary>A new row starts from the last row's first point, not from where the last row ran away.</summary>
        private void NewRow(int index, int row)
        {
            if (row > 0 && index % row == 0 && index > 0)
                (Last, LastPoint, RowRunaway, _runawayAt) = (RowFirst, RowFirstPoint, false, null);
        }

        private string? _runaway;

        /// <summary>The first runaway's sentence, once.</summary>
        public string? RunawayLine { get => _runaway; private set => _runaway ??= value; }

        private ElectroPoint Record(IReadOnlyList<(string Var, double Value)> point, int index, ContinuationResult? r, bool runaway, string? failure)
        {
            if (runaway && r is { LastConverged: { } lc })
            {
                // the ports' current at each end of the bracket: the path's λ₀ and λ₁
                var from = LastPoint ?? point;
                double I(double l)
                {
                    // a cold start's λ scales the target's currents; a warm one's runs along the path from the last point
                    var p = LastPoint is null ? At(point, l) :
                            At([.. point.Select((q, k) => (q.Var, from[k].Value + l * (q.Value - from[k].Value)))], 1);
                    // brief-em3d-78: a run with harmonic currents only reports its largest wire current (peak)
                    return p.Currents.Count > 0 ? p.Currents[0].CurrentA
                        : p.Wires.SelectMany(w => w.Harmonics).Select(h => h.PeakA).DefaultIfEmpty(double.NaN).Max();
                }
                int hot = Enumerable.Range(0, lc.WireTemperature.Length).DefaultIfEmpty(-1).MaxBy(w => w < 0 ? 0 : lc.WireTemperature[w].Max());
                string wire = hot >= 0 ? Wires[hot].Name : "(none)";
                double tempC = hot >= 0 ? lc.WireTemperature[hot].Max() : lc.Thermal.Temperature.Max();
                // brief-em3d-79: a circuit-driven run between two of its points says where in the HB's sweep, as brief 77 says it
                if (Say is not null && LastPoint is not null)
                {
                    List<(string, double)> Along(double l) => [.. point.Select((q, k) => (q.Var, from[k].Value + l * (q.Value - from[k].Value)))];
                    RunawayLine = string.Create(CultureInfo.InvariantCulture,
                        $"No steady state above about {Say(Along(r.FailedAt))}: thermal runaway. The last converged point is {Say(Along(r.ConvergedAt))} " +
                        $"(wire {wire} at {tempC:F0} °C).");
                }
                else RunawayLine = RunawaySentence(I(r.FailedAt), I(r.ConvergedAt), wire, tempC);
            }
            var sol = runaway || failure is not null ? null : r?.Solution;
            Solutions.Add(sol);
            if (sol is not null) return new ElectroPoint(sol.Thermal, sol, false, null);
            return new ElectroPoint(NanSolution(Mesh), null, runaway, failure);
        }
    }

    /// <summary>A point with no temperature (a runaway, or a circuit point skipped): NaN at every node.</summary>
    private static ThermalSolution NanSolution(ThermalMesh mesh)
    {
        var nan = new double[mesh.NodeCount];
        Array.Fill(nan, double.NaN);
        return new ThermalSolution
        {
            Temperature = nan, Unknowns = 0, Solver = ThermalSolverKind.Auto, LinearIterations = 0, LinearResidual = 0, Converged = false,
            SourcePowerW = double.NaN, FixedHeatOutW = double.NaN, ConvectionHeatOutW = double.NaN, FixedHeatOutByTag = new Dictionary<int, double>(),
            BalanceRelative = 0, AmgLevels = 0, Notes = [],
        };
    }

    /// <summary>
    /// The conductive-balance run of <paramref name="lowering"/>: each conducting region's σ(T), the wires as the solver takes them,
    /// and the function that builds the problem at a point (<paramref name="thermal"/> gives its thermal half) with its currents
    /// scaled by the second argument. Null with the refusal.
    /// </summary>
    private static ElectroRun? Electro(ThermalLowering lowering, C3dElaboration e, CemThermal t, ThermalMesh mesh,
                                       Func<IReadOnlyList<(string Var, double Value)>, (ThermalProblem? Problem, C3dResolution? Res, string? Error)> thermal,
                                       ThermalSolveOptions options, List<string> notes, IReadOnlyList<(string Var, double Value)> first,
                                       out string? refusal, Func<IReadOnlyList<(string Var, double Value)>, string>? say = null)
    {
        refusal = null;
        bool currents = lowering.PortContacts.Count > 0;
        var sigma = new List<ElectricalConductivity?>();
        for (int i = 0; i < lowering.Regions.Count; i++)
        {
            if (i >= lowering.Conductors.Count || !lowering.Conductors[i]) { sigma.Add(null); continue; }
            var rec = ThermalMaterials.For(e, lowering.Regions[i].Solid, lowering.Regions[i].Material);
            var m = rec?.Material;
            if (m is null || ThermalProperties.SigmaAt(m, 20) is not { } s20)
            {
                if (currents)
                {
                    refusal = $"'{lowering.Regions[i].Solid}' is a conductor and its material states no electrical conductivity (Sigma20 or " +
                              "SigmaVsTemp), so the current's path through it cannot be solved. State it in the material.";
                    return null;
                }
                sigma.Add(null);
                continue;
            }
            sigma.Add(Conductivity(m, s20.Value));
        }

        // the wires: their chains, their materials, their contacts (the geometry is the lowering's; values come per point)
        var wireLaws = new List<(ThermalConductivity K, ElectricalConductivity S)>();
        foreach (var w in lowering.Wires)
        {
            var rec = ThermalMaterials.ForWire(e, w.Name, w.Material, out string? lookNote);
            if (lookNote is not null && !notes.Contains(lookNote)) notes.Add(lookNote);
            if (rec is null || ThermalProperties.ThermalKAt(rec.Material, 25) is not { } k25 || ThermalProperties.SigmaAt(rec.Material, 20) is not { } s20)
            {
                refusal = $"Wire '{w.Name}' is made of '{ThermalMaterials.BaseName(w.Material)}', which states no thermal conductivity or no " +
                          "electrical conductivity anywhere circuitRF looks (the technology, its libraries, the shipped materials).";
                return null;
            }
            var m = rec.Material;
            wireLaws.Add((m.ThermalKVsTemp is { Count: > 0 }
                ? ThermalConductivity.Varying(k25.Value, T => { var v = ThermalProperties.ThermalKAt(m, T)!.Value; return (v.Value, v.Slope); })
                : ThermalConductivity.Constant(k25.Value), Conductivity(m, s20.Value)));
        }

        // brief-em3d-78 R-em3d78-2/-3: which array carries each port's harmonics, and how its wires share them (once: geometry)
        var rf = ThermalRfPlan.Build(e, lowering, t, out string? rfWhy, fromCircuit: say is not null);
        if (rf is null) { refusal = rfWhy; return null; }
        List<string>? collect = null;

        ElectrothermalProblem At(IReadOnlyList<(string Var, double Value)> point, double scale)
        {
            var (tp, res, error) = thermal(point);
            string? bad = tp is null || res is null ? error ?? "the point does not resolve." : null;
            // brief-em3d-79 — a circuit-driven run's currents are names in the reserved space, bound by the point itself
            Dictionary<string, double>? drive = null;
            foreach (var q in point)
                if (ThermalCircuitLink.IsReserved(q.Var)) (drive ??= new(StringComparer.Ordinal))[q.Var] = q.Value;
            double Opt(string? text, string what, ThermalQuantity q)
            {
                if (bad is not null || string.IsNullOrWhiteSpace(text)) return double.NaN;
                if (drive is not null && drive.TryGetValue(text.Trim(), out double bound)) return bound;
                double v = C3dThermal.Evaluate(res!, text, out string? err, q) ?? double.NaN;
                if (err is not null || !double.IsFinite(v)) bad = $"{what} '{text}' does not resolve{(err is null ? "" : ": " + err)}.";
                return v;
            }
            double convH = Opt(t.WireConvectionH, "WireConvectionH", ThermalQuantity.Plain), convT = Opt(t.WireAmbientC, "WireAmbientC", ThermalQuantity.Temperature);
            double bondT = Opt(t.BondThermalResistance, "BondThermalResistance", ThermalQuantity.Plain);
            double bondE = Opt(t.BondElectricalResistance, "BondElectricalResistance", ThermalQuantity.Plain);
            double Stated(string? text, string what, ThermalQuantity q)
            {
                if (string.IsNullOrWhiteSpace(text)) { bad ??= $"{what} is not stated."; return double.NaN; }
                return Opt(text, what, q);
            }
            var harmonics = rf.At(Stated, scale, collect);
            var wires = new List<ThermalWire>();
            for (int i = 0; i < lowering.Wires.Count; i++)
            {
                var w = lowering.Wires[i];
                var contacts = new List<WireContact>();
                foreach (var end in new[] { w.Start, w.End })
                {
                    if (!lowering.PatchTags.TryGetValue(end.Patch, out int tag) || lowering.RegionOf(end.Pad) is not (var pad and >= 0)) continue;
                    bool distributed = end.FootFrom >= 0 && (double.IsFinite(bondT) || double.IsFinite(bondE));
                    int a = distributed ? Math.Min(end.FootFrom, end.Node) : end.Node, b = distributed ? Math.Max(end.FootFrom, end.Node) : end.Node;
                    contacts.Add(new WireContact(tag, a, b, pad, double.IsFinite(bondT) && bondT > 0 ? 1 / bondT : null,
                                                 double.IsFinite(bondE) && bondE > 0 ? 1 / bondE : null)
                    {
                        SeriesThermalM2KW = end.BallHeightM > 0 ? end.BallHeightM / wireLaws[i].K.Nominal : 0,
                        SeriesElectricalOhmM2 = end.BallHeightM > 0 ? end.BallHeightM / wireLaws[i].S.At20 : 0,
                    });
                }
                wires.Add(new ThermalWire
                {
                    Name = w.Name, Points = w.Points, S = w.S, Area = Math.PI * w.DiameterM * w.DiameterM / 4, Diameter = w.DiameterM,
                    K = wireLaws[i].K, Sigma = wireLaws[i].S, Contacts = contacts, OnPad = w.OnPad,
                    ConvectionH = double.IsFinite(convH) ? convH : null, AmbientC = double.IsFinite(convT) ? convT : 0,
                    Harmonics = harmonics[i], AcResistance = harmonics[i].Count > 0 ? ThermalRfPlan.Bessel(w.DiameterM) : null,
                });
            }
            var terminals = new List<CurrentTerminal>();
            foreach (var pc in lowering.PortContacts)
            {
                var c = (t.Currents ?? []).First(x => x.Port == pc.Port);
                double amps = string.IsNullOrWhiteSpace(c.Dc) ? 0 : Opt(c.Dc, $"Port {pc.Port}'s Dc", ThermalQuantity.Current);
                var pos = pc.Positive.Select(f => lowering.FaceTag(f, false)).OfType<int>().ToList();
                var neg = pc.Negative.Select(f => lowering.FaceTag(f, false)).OfType<int>().ToList();
                terminals.Add(new CurrentTerminal(pc.Port, pos, neg, scale * amps));
            }
            // the refusal is a value, like Problem's; the continuation that asked for this point is left through the exception
            if (bad is not null) throw new ElectrothermalException(bad);
            return new ElectrothermalProblem { Thermal = tp!, Wires = wires, Sigma = sigma, Currents = terminals };
        }

        ElectrothermalSystem system;
        try { system = new ElectrothermalSystem(At(first, 0), options.MaxDegreeOfParallelism); }
        catch (ElectrothermalException x) { refusal = x.Message; return null; }
        if (rf.Any)
        {
            // the first point's harmonics: both ends' comparison, and which wires are electrically long (R-em3d78-2, -4d)
            collect = [];
            ElectrothermalProblem p1;
            try { p1 = At(first, 1); }
            catch (ElectrothermalException x) { refusal = x.Message; return null; }
            notes.AddRange(rf.Notes);
            notes.AddRange(collect);
            collect = null;
            var epsr = system.WireHostRegions.Select(regions => regions.Select(r =>
                ThermalMaterials.For(e, lowering.Regions[r].Solid, lowering.Regions[r].Material)?.Material.Epsr ?? 1).DefaultIfEmpty(1).Max()).ToList();
            notes.AddRange(ThermalRfPlan.ElectricallyLong(lowering.Wires, [.. p1.Wires.Select(w => w.Harmonics)], epsr));
        }
        return new ElectroRun { System = system, At = At, Options = options, Mesh = mesh, Wires = lowering.Wires, Rf = rf, Say = say };
    }

    private static ElectricalConductivity Conductivity(TechMaterial m, double at20)
        => m.SigmaVsTemp is { Count: > 0 } || m.Alpha20 is { } a && a != 0
            ? ElectricalConductivity.Varying(at20, T => { var v = ThermalProperties.SigmaAt(m, T)!.Value; return (v.Value, v.Slope); })
            : ElectricalConductivity.Constant(at20);

    /// <summary>R-em3d77-6 — the wire table, the port voltages, the Joule heat per conductor and the runaway flags, added to
    /// <paramref name="ds"/>.</summary>
    private static void AddElectro(DataSet ds, List<(string Var, double[] Values, string Unit)> axes, ElectroRun run, ThermalLowering lowering,
                                   IReadOnlyList<bool> runaway)
    {
        Axis[] sweep = [.. axes.Select(a => new Axis(a.Var, a.Values, a.Unit))];
        int points = run.Solutions.Count;
        DataCube Cube(Func<int, double> at, string unit)
        {
            var v = new double[points];
            for (int i = 0; i < points; i++) v[i] = at(i);
            return sweep.Length == 0 ? new DataCube([], v) { Unit = unit } : new DataCube(sweep, v) { Unit = unit };
        }
        for (int w = 0; w < lowering.Wires.Count; w++)
        {
            var plan = lowering.Wires[w];
            string name = plan.Name;
            var s = new Axis("s", plan.S, "m");
            var along = new double[points * plan.NodeCount];
            for (int i = 0; i < points; i++)
                for (int k = 0; k < plan.NodeCount; k++)
                    along[i * plan.NodeCount + k] = run.Solutions[i] is { } sol ? sol.WireTemperature[w][k] : double.NaN;
            ds.AddToGroup(WireGroup, $"Twire:{name}(s)", new DataCube([.. sweep, s], along) { Unit = "°C" });
            int w0 = w;
            double Max(int i) => run.Solutions[i] is { } sol ? sol.WireTemperature[w0].Max() : double.NaN;
            double Where(int i)
            {
                if (run.Solutions[i] is not { } sol) return double.NaN;
                var t = sol.WireTemperature[w0];
                return plan.S[Array.IndexOf(t, t.Max())];
            }
            ds.AddToGroup(Group, $"Twire:{name}:max", Cube(Max, "°C"));
            ds.AddToGroup(Group, $"Twire:{name}:smax", Cube(Where, "m"));
            ds.AddToGroup(Group, $"Iwire:{name}", Cube(i => run.Solutions[i]?.WireCurrentA[w0] ?? double.NaN, "A"));
            ds.AddToGroup(Group, $"Pwire:{name}", Cube(i => run.Solutions[i]?.JouleByWire[w0] ?? double.NaN, "W"));
            ds.AddToGroup(Group, $"Rwire:{name}", Cube(i => run.Solutions[i]?.WireResistanceOhm[w0] ?? double.NaN, "Ω"));
            // R-em3d78-4e — the RF heat, in all and per harmonic, and the peak current per harmonic
            var labels = run.Rf.WireLabels[w];
            if (labels.Length == 0) continue;
            ds.AddToGroup(Group, $"Prf:{name}", Cube(i => run.Solutions[i] is { } sol && sol.RfByWire.Length > w0 ? sol.RfByWire[w0] : double.NaN, "W"));
            for (int h = 0; h < labels.Length; h++)
            {
                int h0 = h;
                ds.AddToGroup(Group, $"Irf:{name}:{labels[h]}", Cube(i => i < run.RfPeaks.Count ? run.RfPeaks[i][w0][h0] : double.NaN, "A"));
                ds.AddToGroup(Group, $"Prf:{name}:{labels[h]}",
                              Cube(i => run.Solutions[i] is { } sol && sol.RfByHarmonic.Length > w0 ? sol.RfByHarmonic[w0][h0] : double.NaN, "W"));
            }
        }
        for (int p = 0; p < lowering.PortContacts.Count; p++)
        {
            int p0 = p;
            ds.AddToGroup(Group, $"Vport:{lowering.PortContacts[p].Port}", Cube(i => run.Solutions[i]?.PortVoltage[p0] ?? double.NaN, "V"));
        }
        if (lowering.PortContacts.Count > 0)
            for (int r = 0; r < lowering.Regions.Count; r++)
            {
                if (r >= lowering.Conductors.Count || !lowering.Conductors[r]) continue;
                int r0 = r;
                ds.AddToGroup(Group, $"Pjoule:{lowering.Regions[r].Solid}", Cube(i => run.Solutions[i]?.JouleByRegion[r0] ?? double.NaN, "W"));
            }
        ds.AddToGroup(Group, "Runaway", Cube(i => runaway[i] ? 1 : 0, "1"));
    }
}
