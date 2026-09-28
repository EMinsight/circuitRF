// brief-em3d-73 R-em3d73-6b — `explain x.c3d --analysis [name]`: what a thermal setup would solve, resolved, and nothing
// solved. Every row comes from the code a run reads — the document's resolved scope (C3dResolver), the elaboration
// (C3dElaborator), the technology's interfaces and the one σ(T)/k(T) resolver (ThermalProperties) — so what explain says
// is what brief 74's run will use.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore.Export;

namespace CircuitRF.Cli;

internal static class ExplainThermal
{
    /// <summary>One row per fact of each thermal setup (or of the one <paramref name="name"/> names). Returns the exit code:
    /// 1 when the name matches no thermal setup, or the document has none.</summary>
    public static int Walk(string path, string? name, IReadOnlyList<(string Name, string Expr)> sets, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }

        var thermal = C3dSetups.Read(doc).Where(s => s.Setup is { IsThermal: true }).ToList();
        if (thermal.Count == 0)
            return JsonRun.Fail(CliDiagnostics.ExplainNoRunnableAnalysis("This 3D view embeds no thermal setup (Problem3D: Thermal)."));
        if (name is not null)
        {
            thermal = [.. thermal.Where(s => s.Name == name)];
            if (thermal.Count == 0)
                return JsonRun.Fail(CliDiagnostics.ExplainNoRunnableAnalysis(
                    $"This 3D view has no thermal setup named '{name}'; it has {string.Join(", ", C3dSetups.Read(doc).Where(s => s.Setup is { IsThermal: true }).Select(s => $"'{s.Name}'"))}."));
        }

        var e = C3dElaborator.ElaborateOnce(doc, full, null);
        foreach (var embedded in thermal)
            Setup(doc, full, embedded.Name, embedded.Setup!, e, sets, walks);
        return 0;
    }

    private static void Setup(C3dDocument doc, string full, string name, EmSetup setup, C3dElaboration e,
                              IReadOnlyList<(string Name, string Expr)> sets, List<ResolutionStepJson> walks)
    {
        var t = setup.Thermal ?? new CemThermal();
        string at = $"thermal setup '{name}'";
        var sweep = t.Sweep ?? [];

        // The first sweep point, then --set over it: what "resolved at the first sweep point" means.
        var first = sweep.Select(s => (s.Var, s.Start)).Concat(sets).ToList();
        var res = C3dResolver.Resolve(doc, C3dCell.Of(full), null, first);

        walks.Add(new ResolutionStepJson(at, full,
            $"{doc.HeatSources.Count} heat source(s), {(t.Boundaries ?? []).Count} boundary(ies), {doc.Probes.Count} probe(s), " +
            $"{sweep.Count} sweep axis(es), {(t.Measures ?? []).Count} measure(s); solved by circuitRF's thermal solver",
            "an embedded setup with Problem3D: Thermal; its places are the document's, its values the setup's"));

        // ── brief-em3d-74 R-em3d74-6 — the size: from the lowering a run would write, before any mesher runs ──
        if (e.Ok)
        {
            var low = CircuitRF.Design.Thermal.ThermalLowerings.Build(doc, e, t, 1, out string? lowWhy);
            if (low is null)
                walks.Add(new ResolutionStepJson($"{at}: size", null, $"not estimated: {lowWhy}", "the run refuses the same"));
            else
            {
                var options = new CircuitRF.Thermal.ThermalSolveOptions
                {
                    Solver = t.Mesh?.Solver switch
                    {
                        ThermalMeshSolver.Direct => CircuitRF.Thermal.ThermalSolverKind.Direct,
                        ThermalMeshSolver.Iterative => CircuitRF.Thermal.ThermalSolverKind.Iterative,
                        _ => CircuitRF.Thermal.ThermalSolverKind.Auto,
                    },
                };
                var size = CircuitRF.Design.Thermal.ThermalSizeEstimate.Of(low.Input, options);
                walks.Add(new ResolutionStepJson($"{at}: size", null,
                    $"about {size.Elements.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra of order {low.Input.Sizing.Order}, " +
                    $"{size.Unknowns.ToString("N0", CultureInfo.InvariantCulture)} unknowns; the " +
                    $"{(size.Solver == CircuitRF.Thermal.ThermalSolverKind.Direct ? "direct (Cholesky)" : "iterative (PCG + AMG)")} solver; " +
                    $"about {size.MemoryBytes / 1048576.0:F0} MB for the solve",
                    "an estimate from the sizing rules (each solid at its own element size, each heat source's graded shell, each " +
                    "mesh region's box); the direct solver below 5,000 unknowns (brief 72's crossover), unless the setup's " +
                    "Mesh.Solver says otherwise"));
            }
        }

        // ── the sweep: base SI, with the variable's unit and scale (CLAUDE.md: a mark read without its scale ran at 2 Hz) ──
        foreach (var s in sweep)
        {
            string unit = res.Names.TryGetValue(s.Var, out var n) ? n.Unit ?? "(none)" : "(not a variable)";
            double scale = n?.Unit is { } u ? CircuitRF.Core.Expressions.Units.Scale(u) ?? 1 : 1;
            walks.Add(new ResolutionStepJson($"{at}: sweep {s.Var}", null,
                $"{Eval(res, s.Start)} to {Eval(res, s.Stop)} in {s.Points} point(s), base SI; unit {unit}, scale {G(scale)}",
                "linear from Start to Stop; two axes make a product"));
        }

        // ── sources, with the power each resolves to at the first sweep point ──
        foreach (var h in doc.HeatSources)
        {
            var over = (t.Sources ?? []).FirstOrDefault(s => s.Name == h.Name);
            string? text = over?.Power ?? h.Power;
            string unit = h.Density switch { C3dHeatDensity.PerArea => "W/m²", C3dHeatDensity.PerVolume => "W/m³", _ => "W" };
            string where = h.Solid is { } solid ? $"through the solid '{solid}'" : $"on a {h.Sheet?.Plane} sheet";
            walks.Add(new ResolutionStepJson($"{at}: source {h.Name}", null,
                text is null ? $"{where}; no power (check refuses this)" : $"{where}; {text} = {Eval(res, text, "")} {unit}",
                over is not null ? "the setup's Sources override" : "the heat source's own default Power"));
        }

        // ── boundaries ──
        foreach (var b in t.Boundaries ?? [])
            walks.Add(new ResolutionStepJson($"{at}: boundary {b.Face}", null,
                b.Kind == ThermalBoundaryKind.FixedT
                    ? $"FixedT at {Eval(res, b.TempC ?? "", "")} °C"
                    : $"Convection, h = {Eval(res, b.H ?? "", "")} W/(m²·K) to {Eval(res, b.AmbientC ?? "", "")} °C",
                b.Face == C3dThermal.ExposedFaces ? "every face touching no other solid and not otherwise conditioned" : "a named face; every other face is insulated"));

        // ── interfaces in force: every touching pair of solids with a material-pair value or a contact override ──
        if (e.Ok)
        {
            var solids = e.Solids.Where(s => s.Role != Em3dRole.Air).Take(400).ToList();
            var tech = e.Technology;
            for (int i = 0; i < solids.Count; i++)
                for (int j = i + 1; j < solids.Count; j++)
                {
                    var a = solids[i];
                    var b = solids[j];
                    if (!Touch(Em3dProblem.Bounds(a.Primitive), Em3dProblem.Bounds(b.Primitive))) continue;
                    var over = doc.ContactResistances.FirstOrDefault(c => c.Between.Count == 2 &&
                        (c.Between[0] == a.Name && c.Between[1] == b.Name || c.Between[0] == b.Name && c.Between[1] == a.Name));
                    string ma = a.Material.Split('@')[0], mb = b.Material.Split('@')[0];
                    var pair = tech?.FindThermalInterface(ma, mb);
                    if (over is null && pair is null) continue;
                    walks.Add(new ResolutionStepJson($"{at}: interface '{a.Name}' | '{b.Name}'", null,
                        over is not null ? $"{G(over.ResistanceM2KW)} m²·K/W, the document's ContactResistances override"
                                         : $"{G(pair!.ResistanceM2KW)} m²·K/W, the pair '{pair.MaterialA}' / '{pair.MaterialB}'",
                        "a contact override wins over the technology's material pair, for that contact only (bounding boxes that touch)"));
                }

            // ── each meshed material's k at 25 °C ──
            foreach (string material in solids.Select(s => s.Material.Split('@')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tm = tech?.FindMaterial(material);
                var k = tm is null ? null : ThermalProperties.ThermalKAt(tm, 25);
                walks.Add(new ResolutionStepJson($"{at}: material {material}", e.TechnologyPath,
                    k is { } kv ? $"k(25 °C) = {G(kv.Value)} W/(m·K)" + (tm!.ThermalKVsTemp is { Count: > 0 } ? " from its ThermalKVsTemp table" : " (ThermalK, constant)")
                                : "states no thermal conductivity (check refuses this)",
                    "the table wins over the constant when both are stated"));
            }
        }

        // ── probes and measures ──
        foreach (var p in doc.Probes)
            walks.Add(new ResolutionStepJson($"{at}: probe {p.Name}", null,
                $"{string.Join("/", p.Kinds())}{(p.Stat is { } st ? $", {st}" : "")}{(p.LimitC is { } lim ? $", limit {G(lim)} °C" : "")}",
                "read after the solve; a measure calls Tmax, Tmin, Tavg or T on it"));
        foreach (string m in t.Measures ?? [])
            walks.Add(new ResolutionStepJson($"{at}: measure", null, m, "evaluated after the solve, at every sweep point"));
    }

    private static string Eval(C3dResolution res, string text, string? _ = null)
    {
        return C3dThermal.Evaluate(res, text, out string? error) is { } v ? G(v) : $"'{text}' (does not resolve: {error})";
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static bool Touch((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a,
                              (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
    {
        const double tol = 1e-12;
        return a.X0 <= b.X1 + tol && b.X0 <= a.X1 + tol && a.Y0 <= b.Y1 + tol && b.Y0 <= a.Y1 + tol && a.Z0 <= b.Z1 + tol && b.Z0 <= a.Z1 + tol;
    }
}
