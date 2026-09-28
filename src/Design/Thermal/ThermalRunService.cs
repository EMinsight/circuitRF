// brief-em3d-74 R-em3d74-5 — a thermal setup's run: THE function the GUI's Simulate and `circuitrf em` both reach (through
// EmRunService.RunThreeDView — CLAUDE.md: an operation that lives in a view model is not a capability).
//
//   1. resolve the document and the setup, and refuse on exactly what `check` reports (C3dThermal's one validator);
//   2. find Gmsh — the refusal a Palace run gets, with the same install offer;
//   3. lower and mesh ONCE: a sweep variable never touches geometry (brief 73 §5b refused that already);
//   4. per sweep point: resolve the values, build sources and boundaries, solve — the previous point's field seeds Newton —
//      and read the probes and measures;
//   5. write the result: the DataSet as <results>/<key>.thermal.npy, and the run directory <results>/<key>.thermal/ KEPT
//      with the .geo, the .msh and postpro/paraview/thermal/thermal.pvd, one step per sweep point, the layout Palace writes.
//
// A cancelled run writes no .npy. The energy balance of every point is in the notes; a point that does not close it is a
// warning, since it means the solve is wrong, not the design.

using System.Diagnostics;
using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Results;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Thermal;

public static class ThermalRunService
{
    /// <summary>The token a thermal result's name carries, as <c>palace</c> and <c>openems</c> do.</summary>
    public const string SolverToken = "thermal";

    /// <summary>The DataSet's group of probe, energy and limit cubes.</summary>
    public const string Group = "thermal";

    /// <summary>The group the run's notes are carried in, as labels.</summary>
    public const string NotesGroup = "Notes";

    /// <summary>Samples along a line probe.</summary>
    public const int LineSamples = 101;

    /// <summary>R-em3d74-5e — every element size multiplied by this for the mesh-convergence check.</summary>
    public const double CheckScale = 0.7;

    /// <summary>A thermal result's stem: the planar stem and the token.</summary>
    public static string ResultKey(EmSetup setup) => EmRunService.ResolveResultKey(setup) + "." + SolverToken;

    /// <summary>The run directory, kept after the run.</summary>
    public static string RunDirectory(string resultsRoot, EmSetup setup) => Path.Combine(resultsRoot, ResultKey(setup));

    /// <summary>Where the DataSet lands with no override: <c>&lt;results&gt;/&lt;key&gt;.thermal.npy</c>.</summary>
    public static string NpyPath(string resultsRoot, EmSetup setup) => Path.Combine(resultsRoot, ResultKey(setup) + ".npy");

    /// <summary>Runs thermal setup <paramref name="setup"/> (as <see cref="C3dSetups.ForRun"/> names it) over
    /// <paramref name="document"/>. <c>setup.SnpOutputPathOverride</c>, when set, is where the DataSet goes (<c>-o</c>).</summary>
    public static EmRunResult Run(EmSetup setup, C3dDocument document, string documentPath, string? workspaceCws, string resultsRoot,
                                  CancellationToken ct = default, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(document);
        if (control is { Token: var tk } && tk.CanBeCanceled) ct = tk;
        var notes = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        EmRunResult Stop(EmRunStatus status, Diagnostic d)
            => new(status, null, null, null, null, null, d.Render(), warnings, KernelName: "circuitRF thermal", Notes: notes,
                   Errors: errors, Diagnostic: d);
        EmRunResult Refuse(string text) => Stop(EmRunStatus.Refused, EmDiagnostics.Forwarded("thermal", text));

        try
        {
            var t = setup.Thermal ?? new CemThermal();
            string path = Path.GetFullPath(documentPath);

            // ── 1. the document and the setup: refused on check's own findings ──
            control?.BeginStage("resolving the thermal setup");
            var e = new C3dElaborator().Elaborate(document, path, workspaceCws, new C3dElaborationOptions(null,
                setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC));
            notes.AddRange(e.Notes);
            warnings.AddRange(e.Warnings);
            if (!e.Ok) return Refuse(string.Join(" ", e.Refusals));
            var findings = C3dThermal.Places(document).Concat(C3dThermal.Places(document, e))
                                     .Concat(C3dThermal.Setup(setup.Name, setup, document, e, e.Resolution!))
                                     .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Render()).ToList();
            if (findings.Count > 0) return Refuse(string.Join(" ", findings));
            if (e.Technology is null) return Refuse("This 3D view's technology did not resolve, so no material states a thermal conductivity.");

            // ── 2. Gmsh ──
            var gmsh = SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh));
            if (!gmsh.Proceeds)
                return Stop(EmRunStatus.Refused, EmDiagnostics.SolverUnavailable(gmsh.Name, gmsh.Refusal!, Em3d.Install.SolverInstaller.OfferFor(gmsh)));
            notes.Add($"Solver: circuitRF thermal (steady conduction, second-order tetrahedra by default); mesher: Gmsh " +
                      $"{gmsh.Installation!.DescribeVersion()} at {gmsh.Installation.Path}.");

            // ── 3. lower and mesh, once ──
            var lowering = ThermalLowerings.Build(document, e, t, 1, out string? why);
            if (lowering is null) return Refuse(why!);
            notes.AddRange(lowering.Notes);
            string runDir = RunDirectory(resultsRoot, setup);
            var meshed = Mesh(runDir, lowering, gmsh.Installation.Path, control, ct, notes, out var mesh, out string? meshError);
            if (meshed.Cancelled) return Stop(EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
            if (meshed.Refused) return Refuse(meshed.Message!);
            if (mesh is null) return Stop(EmRunStatus.EngineError, EmDiagnostics.SolveFailed(meshError ?? meshed.Message ?? "the mesh could not be read."));

            // ── the problem's fixed parts: conductivities and the mesh's measures ──
            var tech = e.Technology;
            bool kOfT = t.Balance?.KOfT ?? true;
            var records = new List<ThermalMaterialRecord>();
            var conductivity = new List<ThermalConductivity>();
            foreach (var r in lowering.Regions)
            {
                var rec = ThermalMaterials.Find(tech, ThermalMaterials.BaseName(r.Material))!;
                if (rec.LookThroughNote is { } look && !notes.Contains(look)) notes.Add(look);
                records.Add(rec);
                var m = rec.Material;
                double nominal = ThermalProperties.ThermalKAt(m, 25)!.Value.Value;
                conductivity.Add(m.ThermalKVsTemp is { Count: > 0 }
                    ? ThermalConductivity.Varying(nominal, T => { var v = ThermalProperties.ThermalKAt(m, T)!.Value; return (v.Value, v.Slope); })
                    : ThermalConductivity.Constant(nominal));
            }
            var zero = new ThermalField(mesh, new double[mesh.NodeCount]);
            var probes = document.Probes.ToList();

            // ── the sweep ──
            var baseRes = e.Resolution!;
            var axes = new List<(string Var, double[] Values, string Unit)>();
            foreach (var s in t.Sweep ?? [])
            {
                double a = C3dThermal.Evaluate(baseRes, s.Start, out string? ea) ?? double.NaN;
                double b = C3dThermal.Evaluate(baseRes, s.Stop, out string? eb) ?? double.NaN;
                if (ea is not null || eb is not null) return Refuse($"The sweep of '{s.Var}' does not resolve: {ea ?? eb}.");
                int n = Math.Max(1, s.Points);
                string unit = baseRes.Names.TryGetValue(s.Var, out var nm) && nm.Unit is { } u ? Units.BaseUnit(u) : "";
                axes.Add((s.Var, [.. Enumerable.Range(0, n).Select(i => n == 1 ? a : a + (b - a) * i / (n - 1))], unit));
            }
            var points = Product(axes);

            var options = new ThermalSolveOptions
            {
                Solver = t.Mesh?.Solver switch { ThermalMeshSolver.Direct => ThermalSolverKind.Direct, ThermalMeshSolver.Iterative => ThermalSolverKind.Iterative, _ => ThermalSolverKind.Auto },
                KOfT = kOfT,
                NewtonTolerance = t.Balance?.Tolerance ?? 1e-6,
                NewtonMaxIterations = t.Balance?.MaxIterations ?? 30,
                Cancellation = ct,
            };
            var assembly = new ThermalAssembly(mesh);
            notes.Add($"The mesh: {mesh.TetCount:N0} tetrahedra of order {mesh.Order}, {mesh.NodeCount:N0} nodes, " +
                      $"{lowering.Regions.Count} solid(s).");

            // ── 4. every point ──
            var fields = new List<double[]>();
            var reads = new List<Dictionary<string, ProbeRead>>();
            var measures = new List<Dictionary<string, double>>();
            var balances = new List<(double In, double Balance)>();
            double[]? previous = null;
            var summary = new List<string>();
            for (int pi = 0; pi < points.Count; pi++)
            {
                ct.ThrowIfCancellationRequested();
                control?.BeginStage($"solving point {pi + 1} of {points.Count}");
                var sets = points[pi].Select(p => (p.Var, p.Value.ToString("R", CultureInfo.InvariantCulture))).ToList();
                var res = sets.Count == 0 ? baseRes : C3dResolver.Resolve(document, C3dCell.Of(path), null, sets);
                var problem = Problem(t, document, lowering, conductivity, mesh, zero, res, out string? valueError);
                if (problem is null) return Refuse(At(axes, points[pi]) + valueError);
                var sol = ThermalSolver.Solve(problem, options with { InitialGuess = previous }, assembly);
                previous = sol.Temperature;
                fields.Add(sol.Temperature);
                balances.Add((sol.SourcePowerW, sol.BalanceRelative));
                var field = new ThermalField(mesh, sol.Temperature);
                var read = ReadProbes(probes, lowering, field, document.DbuPerMicron);
                reads.Add(read);
                measures.Add(Measures(t, res, read, errors, At(axes, points[pi])));

                string where = points.Count > 1 ? At(axes, points[pi]) : "";
                summary.Add($"{where}{sol.Unknowns:N0} unknowns, {Describe(sol)}; energy balance {sol.BalanceRelative:G3} " +
                            $"(sources {sol.SourcePowerW:G6} W, out {sol.FixedHeatOutW:G6} W through fixed faces and {sol.ConvectionHeatOutW:G6} W by convection).");
                foreach (string n in sol.Notes) summary.Add(where + n);
                if (sol.BalanceRelative > ThermalSolver.BalanceTolerance)
                    warnings.Add($"{where}The energy balance does not close: {sol.BalanceRelative:G3} of the heat is unaccounted for " +
                                 $"(tolerance {ThermalSolver.BalanceTolerance:G3}). The solve is not to be trusted at this point.");
                if (!sol.Converged)
                    warnings.Add($"{where}Newton over k(T) did not converge; the point's temperatures are its last iterate.");
                foreach (string held in Holds(lowering, records, mesh, sol.Temperature, kOfT)) if (!notes.Contains(held)) notes.Add(held);
            }
            notes.AddRange(summary.Count <= 12 ? summary : [.. summary.Take(6), $"… and {summary.Count - 6} more line(s) in the result's Notes."]);

            // ── limits (D11) ──
            foreach (var p in probes)
            {
                if (p.LimitC is not { } lim) continue;
                int first = reads.FindIndex(r => r.TryGetValue(p.Name, out var v) && v.Max >= lim);
                if (first >= 0)
                    warnings.Add($"Probe '{p.Name}' reaches its limit of {lim:G6} °C first at {At(axes, points[first]).TrimEnd(':', ' ')}" +
                                 $"{(points.Count > 1 ? "" : "the only point")}: {reads[first][p.Name].Max:F3} °C.");
            }

            // ── the mesh-convergence check ──
            if (t.Mesh?.Check == true && points.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                control?.BeginStage("the mesh-convergence check");
                notes.AddRange(ConvergenceCheck(document, e, t, gmsh.Installation.Path, runDir, conductivity, points[0], path, reads[0],
                                                options, control, ct));
            }

            // ── 5. the result ──
            var data = Build(axes, probes, reads, measures, balances, t, summary);
            string pvd = ThermalFieldFiles.Write(runDir, mesh, fields, [.. lowering.Regions.Select(r => r.Tag)]);
            string? npy = WriteNpy(resultsRoot, setup, data, errors);
            ct.ThrowIfCancellationRequested();
            var outputs = new List<EmRunOutput>();
            if (npy is not null) outputs.Add(new EmRunOutput("npy", npy));
            outputs.Add(new EmRunOutput("fields", pvd));
            return new EmRunResult(EmRunStatus.Ok, data, null, null, npy, null, null, warnings,
                KernelName: "circuitRF thermal", Notes: notes, Errors: errors, Outputs: outputs);
        }
        catch (OperationCanceledException)
        {
            return Stop(EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
        }
    }

    /// <summary>A probe's reading at one point: max, min and average (all one number for a point probe), and a line's
    /// samples.</summary>
    public sealed record ProbeRead(double Max, double Min, double Avg, double[]? Line = null);

    private static PalaceStep Mesh(string runDir, ThermalLowering lowering, string gmsh, RunControl? control, CancellationToken ct,
                                   List<string> notes, out ThermalMesh? mesh, out string? error)
    {
        mesh = null;
        error = null;
        control?.BeginStage("meshing with Gmsh");
        PalaceStep step;
        try { step = PalaceRun.Mesh(runDir, lowering.Gmsh, gmsh, control, ct); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            error = $"the mesh could not be staged in '{runDir}' ({x.Message}).";
            return new PalaceStep(false, false, false, error);
        }
        if (!step.Ok) return step;
        notes.Add(step.Reused ? $"The geometry script is unchanged since the last run, so its mesh was reused ({runDir})."
                              : $"Meshed with Gmsh in {runDir}.");
        try { mesh = ReadMesh(Path.Combine(runDir, GmshGeoWriter.MeshFile), lowering, out error); }
        catch (InvalidDataException x) { error = x.Message; }
        return step;
    }

    /// <summary>The solver's mesh from Gmsh's file and the lowering's tag table: tetrahedra with their region, triangles with
    /// their tag, micrometres to metres, and the nodes no tetrahedron uses dropped.</summary>
    public static ThermalMesh? ReadMesh(string mshPath, ThermalLowering lowering, out string? error)
    {
        error = null;
        var raw = MshReader.Read(mshPath);
        int order = lowering.Input.Sizing.Order;
        if (raw.Order != order)
        {
            error = $"Gmsh wrote a mesh of order {raw.Order} where order {order} was asked for.";
            return null;
        }
        var regionOf = new Dictionary<int, int>();
        for (int i = 0; i < lowering.Regions.Count; i++) regionOf[lowering.Regions[i].Tag] = i;
        var region = new int[raw.TetCount];
        for (int k = 0; k < raw.TetCount; k++)
            if (!regionOf.TryGetValue(raw.TetPhysical[k], out region[k]))
            {
                error = $"The mesh holds a tetrahedron of group {raw.TetPhysical[k]}, which is no solid of the problem.";
                return null;
            }
        var nodes = new double[raw.Nodes.Length];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = raw.Nodes[i] * GmshGeoWriter.LengthUnitM;
        return new ThermalMesh(nodes, order, order == 2 ? raw.TetsHigh : raw.Tets, region,
                               order == 2 ? raw.TrianglesHigh : raw.Triangles, raw.TrianglePhysical).Compact(out _);
    }

    /// <summary>The problem at one point: every value resolved in <paramref name="res"/>.</summary>
    private static ThermalProblem? Problem(CemThermal t, C3dDocument doc, ThermalLowering lowering, List<ThermalConductivity> k,
                                           ThermalMesh mesh, ThermalField zero, C3dResolution res, out string? error)
    {
        error = null;
        double Value(string text, string what, out string? err)
        {
            double v = C3dThermal.Evaluate(res, text, out err) ?? double.NaN;
            if (err is not null) err = $"{what} '{text}' does not resolve: {err}.";
            else if (!double.IsFinite(v)) err = $"{what} '{text}' is not a finite number.";
            return v;
        }
        var sheets = new List<SurfaceSource>();
        var volumes = new List<VolumeSource>();
        foreach (var h in doc.HeatSources)
        {
            string text = (t.Sources ?? []).FirstOrDefault(s => s.Name == h.Name)?.Power ?? h.Power ?? "0";
            double p = Value(text, $"Heat source '{h.Name}''s power", out error);
            if (error is not null) return null;
            if (lowering.SheetSourceTags.TryGetValue(h.Name, out int tag))
            {
                double area = zero.Surface(new HashSet<int> { tag })?.Measure ?? 0;
                if (!(area > 0)) { error = $"Heat source '{h.Name}' has no area in the mesh."; return null; }
                sheets.Add(new SurfaceSource(tag, h.Density == C3dHeatDensity.PerArea ? p : p / area));
            }
            else if (lowering.SolidSourceRegions.TryGetValue(h.Name, out int region))
            {
                double volume = zero.Region(region)?.Measure ?? 0;
                if (!(volume > 0)) { error = $"Heat source '{h.Name}' has no volume in the mesh."; return null; }
                volumes.Add(new VolumeSource(region, h.Density == C3dHeatDensity.PerVolume ? p : p / volume));
            }
        }
        var fixedT = new List<FixedTemperature>();
        var conv = new List<ConvectionCondition>();
        foreach (var b in t.Boundaries ?? [])
        {
            int? tag = b.Face == C3dThermal.ExposedFaces ? lowering.ExposedTag : lowering.FaceTag(b.Face, true);
            if (tag is not { } tg) continue;
            if (b.Kind == ThermalBoundaryKind.FixedT)
            {
                double tc = Value(b.TempC ?? "", $"The FixedT boundary on '{b.Face}': TempC", out error);
                if (error is not null) return null;
                fixedT.Add(new FixedTemperature(tg, tc));
            }
            else
            {
                double h = Value(b.H ?? "", $"The Convection boundary on '{b.Face}': H", out error);
                if (error is not null) return null;
                double amb = Value(b.AmbientC ?? "", $"The Convection boundary on '{b.Face}': AmbientC", out error);
                if (error is not null) return null;
                conv.Add(new ConvectionCondition(tg, h, amb));
            }
        }
        return new ThermalProblem { Mesh = mesh, Conductivity = k, SurfaceSources = sheets, VolumeSources = volumes, Fixed = fixedT, Convection = conv };
    }

    /// <summary>Every probe's reading on <paramref name="field"/>. A wire probe waits for brief 77's 1D wires and reads
    /// nothing.</summary>
    private static Dictionary<string, ProbeRead> ReadProbes(IReadOnlyList<C3dProbe> probes, ThermalLowering lowering, ThermalField field,
                                                            int dbuPerMicron)
    {
        double m = 1e-6 / dbuPerMicron;
        var read = new Dictionary<string, ProbeRead>(StringComparer.Ordinal);
        ProbeRead? Stats(ThermalStats? s) => s is { } x ? new ProbeRead(x.MaxC, x.MinC, x.AvgC) : null;
        foreach (var p in probes)
        {
            ProbeRead? r = null;
            if (p.Point is { } pt && field.At(pt.X * m, pt.Y * m, pt.Z * m) is { } v) r = new ProbeRead(v, v, v);
            else if (p.Face is { } face && lowering.FaceTag(face, false) is { } ft) r = Stats(field.Surface(new HashSet<int> { ft }));
            else if (p.Solid is { } solid && lowering.RegionOf(solid) is var ri and >= 0) r = Stats(field.Region(ri));
            else if (p.Spot is { } spot && lowering.FaceTag(spot.Face, false) is { } st)
                r = Stats(field.Spot(new HashSet<int> { st }, (spot.Center.X * m, spot.Center.Y * m, spot.Center.Z * m), spot.Diameter * m / 2));
            else if (p.Line is { } line)
            {
                var samples = field.Line((line.From.X * m, line.From.Y * m, line.From.Z * m), (line.To.X * m, line.To.Y * m, line.To.Z * m), LineSamples);
                var inside = samples.Where(double.IsFinite).ToList();
                if (inside.Count > 0) r = new ProbeRead(inside.Max(), inside.Min(), inside.Average(), samples);
                else r = new ProbeRead(double.NaN, double.NaN, double.NaN, samples);
            }
            if (r is not null) read[p.Name] = r;
        }
        return read;
    }

    /// <summary>R-em3d74-5d — each measure at one point: its probe calls replaced, in the parsed expression, by the probe's
    /// values, then evaluated by the one expression engine in the point's resolved scope. A measure that fails is a named
    /// error for that point.</summary>
    private static Dictionary<string, double> Measures(CemThermal t, C3dResolution res, Dictionary<string, ProbeRead> read,
                                                       List<string> errors, string where)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string measure in t.Measures ?? [])
        {
            int eq = measure.IndexOf('=');
            if (eq <= 0) continue;
            string name = measure[..eq].Trim();
            try
            {
                var ast = Rewrite(Parser.Parse(measure[(eq + 1)..].Trim()), read);
                var v = res.EvaluateParsed(ast);
                values[name] = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
            }
            catch (ExpressionException x)
            {
                values[name] = double.NaN;
                errors.Add($"{where}The measure '{name}' could not be evaluated: {x.Message}");
            }
            catch (ProbeUnread x)
            {
                values[name] = double.NaN;
                errors.Add($"{where}The measure '{name}' could not be evaluated: the probe '{x.Probe}' read nothing (it lies outside " +
                           "every meshed solid, or on nothing a thermal run meshes).");
            }
        }
        return values;
    }

    /// <summary>A measure named a probe that read nothing at this point; the measure's own catch words it.</summary>
    private sealed class ProbeUnread(string probe) : Exception
    {
        public string Probe { get; } = probe;
    }

    private static Expr Rewrite(Expr e, Dictionary<string, ProbeRead> read) => e switch
    {
        CallExpr { Args: [RefExpr { Name: var probe }] } c when C3dThermal.ProbeFunctions.Contains(c.Name, StringComparer.Ordinal)
            => new NumberExpr(read.TryGetValue(probe, out var r)
                ? c.Name switch { "Tmax" => r.Max, "Tmin" => r.Min, _ => r.Avg }
                : throw new ProbeUnread(probe)),
        CallExpr c => c with { Args = [.. c.Args.Select(a => Rewrite(a, read))] },
        UnaryExpr u => u with { Operand = Rewrite(u.Operand, read) },
        BinaryExpr b => b with { Left = Rewrite(b.Left, read), Right = Rewrite(b.Right, read) },
        CompareExpr c => c with { Left = Rewrite(c.Left, read), Right = Rewrite(c.Right, read) },
        LogicExpr l => l with { Left = Rewrite(l.Left, read), Right = Rewrite(l.Right, read) },
        ConditionalExpr d => d with { Condition = Rewrite(d.Condition, read), Then = Rewrite(d.Then, read), Else = Rewrite(d.Else, read) },
        _ => e,
    };

    /// <summary>A table held at its end somewhere in the solved field, per region, as its note.</summary>
    private static IEnumerable<string> Holds(ThermalLowering lowering, List<ThermalMaterialRecord> records, ThermalMesh mesh, double[] t, bool kOfT)
    {
        if (!kOfT) yield break;
        int nn = mesh.NodesPerTet;
        var hi = new double[records.Count];
        var lo = new double[records.Count];
        Array.Fill(hi, double.NegativeInfinity);
        Array.Fill(lo, double.PositiveInfinity);
        for (int e = 0; e < mesh.TetCount; e++)
            for (int k = 0; k < nn; k++)
            {
                double v = t[mesh.Tets[nn * e + k]];
                int r = mesh.TetRegion[e];
                hi[r] = Math.Max(hi[r], v); lo[r] = Math.Min(lo[r], v);
            }
        for (int r = 0; r < records.Count; r++)
        {
            if (records[r].Material.ThermalKVsTemp is not { Count: > 0 } || double.IsInfinity(hi[r])) continue;
            foreach (double at in new[] { hi[r], lo[r] })
                if (ThermalProperties.ThermalKAt(records[r].Material, at)?.HeldNote is { } note)
                    yield return $"In '{lowering.Regions[r].Solid}': {note}";
        }
    }

    private static string Describe(ThermalSolution s)
    {
        string solver = s.Solver == ThermalSolverKind.Direct
            ? $"direct solver (relative residual {s.LinearResidual:G3})"
            : $"PCG + AMG, {s.LinearIterations} iteration(s), {s.AmgLevels} level(s), relative residual {s.LinearResidual:G3}";
        return s.NewtonIterations > 0 ? $"{solver}, {s.NewtonIterations} Newton step(s) over k(T)" : solver;
    }

    private static List<List<(string Var, double Value)>> Product(List<(string Var, double[] Values, string Unit)> axes)
    {
        var points = new List<List<(string, double)>> { new() };
        foreach (var (v, values, _) in axes)
            points = [.. points.SelectMany(p => values.Select(x => new List<(string, double)>(p) { (v, x) }))];
        return points;
    }

    private static string At(List<(string Var, double[] Values, string Unit)> axes, List<(string Var, double Value)> point)
        => point.Count == 0 ? "" : string.Join(", ", point.Select(p => $"{p.Var} = {p.Value.ToString("G6", CultureInfo.InvariantCulture)}" +
                                     (axes.First(a => a.Var == p.Var).Unit is { Length: > 0 } u ? " " + u : ""))) + ": ";

    /// <summary>R-em3d74-5c — the DataSet: per probe statistic, a line's T(s), each measure, the energy balance, the limits,
    /// and the notes. Real, single-kind, °C; over the sweep's axes (a scalar with no sweep).</summary>
    private static DataSet Build(List<(string Var, double[] Values, string Unit)> axes, IReadOnlyList<C3dProbe> probes,
                                 List<Dictionary<string, ProbeRead>> reads, List<Dictionary<string, double>> measures,
                                 List<(double In, double Balance)> balances, CemThermal t, List<string> notes)
    {
        var ds = new DataSet();
        Axis[] sweep = [.. axes.Select(a => new Axis(a.Var, a.Values, a.Unit))];
        DataCube Cube(Func<int, double> at, string unit)
        {
            var v = new double[reads.Count];
            for (int i = 0; i < v.Length; i++) v[i] = at(i);
            return sweep.Length == 0 ? new DataCube([], v) { Unit = unit } : new DataCube(sweep, v) { Unit = unit };
        }
        double Get(int i, string probe, Func<ProbeRead, double> f) => reads[i].TryGetValue(probe, out var r) ? f(r) : double.NaN;
        foreach (var p in probes)
        {
            if (p.Wire is not null) continue;
            if (p.Line is not null)
            {
                var first = reads.Select(r => r.GetValueOrDefault(p.Name)?.Line).FirstOrDefault(l => l is not null);
                if (first is null) continue;
                var s = new Axis("distance", [.. Enumerable.Range(0, first.Length).Select(k => (double)k / (first.Length - 1))], "", null);
                var data = reads.SelectMany(r => r.GetValueOrDefault(p.Name)?.Line ?? new double[first.Length]).ToArray();
                ds.AddToGroup(Group, $"T:{p.Name}(s)", new DataCube([.. sweep, s], data) { Unit = "°C" });
                ds.AddToGroup(Group, $"T:{p.Name}:max", Cube(i => Get(i, p.Name, r => r.Max), "°C"));
                continue;
            }
            if (p.Point is not null) { ds.AddToGroup(Group, $"T:{p.Name}", Cube(i => Get(i, p.Name, r => r.Avg), "°C")); continue; }
            ds.AddToGroup(Group, $"T:{p.Name}:max", Cube(i => Get(i, p.Name, r => r.Max), "°C"));
            ds.AddToGroup(Group, $"T:{p.Name}:min", Cube(i => Get(i, p.Name, r => r.Min), "°C"));
            ds.AddToGroup(Group, $"T:{p.Name}:avg", Cube(i => Get(i, p.Name, r => r.Avg), "°C"));
            if (p.LimitC is { } lim)
                ds.AddToGroup(Group, $"Limit:{p.Name}", Cube(i => Get(i, p.Name, r => r.Max) >= lim ? 1 : 0, "1"));
        }
        ds.AddToGroup(Group, "Energy:balance", Cube(i => balances[i].Balance, "1"));
        ds.AddToGroup(Group, "Energy:in", Cube(i => balances[i].In, "W"));
        foreach (string measure in t.Measures ?? [])
        {
            int eq = measure.IndexOf('=');
            if (eq <= 0) continue;
            string name = measure[..eq].Trim();
            ds.AddToGroup(DataSet.MeasurementsGroup, name, Cube(i => measures[i].GetValueOrDefault(name, double.NaN), ""));
        }
        if (notes.Count > 0)
            ds.AddToGroup(NotesGroup, "Notes", new DataCube(
                [new Axis("note", [.. Enumerable.Range(0, notes.Count).Select(k => (double)k)], "", [.. notes])], new double[notes.Count]));
        return ds;
    }

    private static string? WriteNpy(string resultsRoot, EmSetup setup, DataSet data, List<string> errors)
    {
        try
        {
            if (setup.SnpOutputPathOverride is { Length: > 0 } over)
            {
                string target = Path.ChangeExtension(Path.GetFullPath(over), ".npy");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) File.Delete(target);
                DataSetExporter.Export(data, target, ExportFormat.Npy);
                return target;
            }
            var written = ResultsWriter.WriteRun(Path.GetDirectoryName(Path.GetFullPath(resultsRoot).TrimEnd(Path.DirectorySeparatorChar))!,
                                                 ResultKey(setup), data);
            if (written.Error is { } err) errors.Add($"The thermal result could not be written to results/: {err}");
            return written.Written.Count > 0 ? written.Written[0] : null;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            errors.Add($"The thermal result could not be written: {x.Message}");
            return null;
        }
    }

    /// <summary>R-em3d74-5e — mesh a second time with every size × 0.7, solve the first point, and say how far each probe
    /// moved. A report, not a refinement loop.</summary>
    private static IEnumerable<string> ConvergenceCheck(C3dDocument doc, C3dElaboration e, CemThermal t, string gmsh, string runDir,
                                                        List<ThermalConductivity> k, List<(string Var, double Value)> point, string path,
                                                        Dictionary<string, ProbeRead> coarse, ThermalSolveOptions options,
                                                        RunControl? control, CancellationToken ct)
    {
        var fine = ThermalLowerings.Build(doc, e, t, CheckScale, out string? why);
        if (fine is null) return [$"The mesh-convergence check could not be lowered: {why}"];
        var discard = new List<string>();
        var step = Mesh(Path.Combine(runDir, "check"), fine, gmsh, control, ct, discard, out var mesh, out string? error);
        if (step.Cancelled) throw new OperationCanceledException(ct);
        if (mesh is null) return [$"The mesh-convergence check could not mesh: {error ?? step.Message}"];
        var sets = point.Select(p => (p.Var, p.Value.ToString("R", CultureInfo.InvariantCulture))).ToList();
        var res = sets.Count == 0 ? e.Resolution! : C3dResolver.Resolve(doc, C3dCell.Of(path), null, sets);
        var zero = new ThermalField(mesh, new double[mesh.NodeCount]);
        var problem = Problem(t, doc, fine, k, mesh, zero, res, out string? valueError);
        if (problem is null) return [$"The mesh-convergence check could not be set up: {valueError}"];
        var sol = ThermalSolver.Solve(problem, options with { InitialGuess = null });
        var read = ReadProbes(doc.Probes, fine, new ThermalField(mesh, sol.Temperature), doc.DbuPerMicron);
        var lines = new List<string> { $"Mesh-convergence check: every element size × {CheckScale} ({mesh.TetCount:N0} tetrahedra), the first sweep point." };
        foreach (var (name, c) in coarse)
        {
            if (!read.TryGetValue(name, out var f)) continue;
            double a = c.Max, b = f.Max;
            lines.Add($"  probe '{name}' (max): {a:F4} → {b:F4} °C, changed by {b - a:+0.0000;-0.0000} °C " +
                      $"({(a != 0 ? 100 * (b - a) / Math.Abs(a) : 0):+0.000;-0.000} %).");
        }
        return lines;
    }
}
