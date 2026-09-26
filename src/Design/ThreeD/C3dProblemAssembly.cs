// brief-em3d-42 R-em3d42-5 — a .c3d's setups, and the problem a setup makes of its elaboration.
//
// SETUPS ARE IN TWO PLACES AND ONE SCHEMA (owner decision D5). A .c3d embeds EmSetup objects in its Setups
// list, written and read by EmSetupPersistence's own serializer less LayoutRef (the geometry is the
// document). And a .cem whose LayoutRef names a .c3d elaborates that document; then the .c3d's embedded
// setups are NOT consulted — the .cem is the setup. Either way a planar analysis is refused: a .c3d has
// no stackup for a planar kernel to read, only solids.
//
// THE PROBLEM is the elaboration plus the setup: the sweep, the temperature, the problem type, and the air
// box padded around the elaborated content by the generator's own padding code (Em3dGenerator.PaddedAirBox
// — shared, so a .cem and a .c3d cannot disagree about a box). There is no air solid: the background is
// air. brief-em3d-49: the document's ports (C3dPorts — polarity by contact, measured against THIS box, so a
// wave port is checked against the setup being run) and its face boundaries join it. A static setup's terminals
// name nets — a layout instance's nets, or a drawn conductor's own name — or, from brief 49, objects by name.
//
// WHERE RESULTS LAND (R-em3d42-5d). EmRunService.ResolveSnpPath is predictable by design. An embedded
// setup's result is named as if a .cem called "<c3d stem> <setup name>.cem" stood beside the .c3d — so
// the run's setup is named "<c3d stem> <setup name>", which is the key that path derives from.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>One embedded setup, read — or why it could not be.</summary>
public sealed record C3dEmbeddedSetup(int Index, string Name, EmSetup? Setup, string? Refusal);

public static class C3dSetups
{
    /// <summary>The sentence a planar setup gets, from either container.</summary>
    public const string PlanarRefusal = "a .c3d is solved by the 3D solvers; set Solver3D to Palace or openEMS";

    /// <summary>Every embedded setup, in file order, each read by the .cem reader and checked: a name,
    /// unique, and a 3D solver.</summary>
    public static IReadOnlyList<C3dEmbeddedSetup> Read(C3dDocument doc)
    {
        var read = new List<C3dEmbeddedSetup>();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Setups.Count; i++)
        {
            EmSetup setup;
            try { setup = EmSetupPersistence.FromEmbedded(doc.Setups[i]); }
            catch (Exception e)
            {
                read.Add(new C3dEmbeddedSetup(i, $"#{i + 1}", null, $"Embedded setup #{i + 1} cannot be read: {e.Message}"));
                continue;
            }
            string name = setup.Name;
            if (name.Length == 0)
                read.Add(new C3dEmbeddedSetup(i, $"#{i + 1}", null,
                    $"Embedded setup #{i + 1} has no Name; a .c3d's setups are chosen by name."));
            else if (names.TryGetValue(name, out int first))
                read.Add(new C3dEmbeddedSetup(i, name, null,
                    $"Embedded setups #{first + 1} and #{i + 1} are both named '{name}'; a setup's name is unique in its document."));
            else if (!setup.Is3D)
                read.Add(new C3dEmbeddedSetup(i, name, null, $"Embedded setup '{name}' is a planar analysis: {PlanarRefusal}."));
            else
                read.Add(new C3dEmbeddedSetup(i, name, setup, null));
            names.TryAdd(name, i);
        }
        return read;
    }

    /// <summary>
    /// The setup <c>em x.c3d</c> runs: the one there is, or the one <paramref name="name"/> names. Several and
    /// no name is a refusal LISTING them (render --view's pattern), answered by <c>--setup</c>.
    /// </summary>
    public static (EmSetup? Setup, string? Refusal) Select(C3dDocument doc, string? name)
    {
        var all = Read(doc);
        string List() => string.Join(", ", all.Select(s => $"'{s.Name}'"));
        if (all.Count == 0)
            return (null, "This 3D view embeds no EM setup. Add one to its Setups, or point a .cem's LayoutRef at it.");
        C3dEmbeddedSetup chosen;
        if (name is not null)
        {
            if (all.FirstOrDefault(s => s.Name == name) is not { } hit)
                return (null, $"This 3D view has no setup named '{name}'; it has {List()}.");
            chosen = hit;
        }
        else if (all.Count > 1)
            return (null, $"This 3D view embeds {all.Count} setups ({List()}); name one with --setup <name>.");
        else chosen = all[0];
        return chosen.Refusal is { } why ? (null, why) : (chosen.Setup, null);
    }

    /// <summary>R-em3d42-5d — the embedded setup as its run uses it: named "<c>&lt;c3d stem&gt; &lt;setup name&gt;</c>",
    /// as if a <c>.cem</c> of that name stood beside the <c>.c3d</c>, so its result path is predictable.</summary>
    public static EmSetup ForRun(EmSetup embedded, string c3dPath)
    {
        var s = embedded.Clone();
        s.Name = $"{Path.GetFileNameWithoutExtension(c3dPath)} {embedded.Name}";
        s.LayoutRef = Path.GetFileName(c3dPath);
        return s;
    }

    /// <summary>True when a <c>.cem</c>'s resolved geometry is a 3D view rather than a layout.</summary>
    public static bool IsThreeDView(string? geometryPath)
        => geometryPath is not null && geometryPath.EndsWith(C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase);
}

public static class C3dProblemAssembly
{
    /// <summary>
    /// R-em3d49-2b — the setup's ground set: the conductors on its Ground3D net (a drawn conductor's net is its own name)
    /// and a layout instance's ground-reference conductors. A PEC air-box face joins it in <see cref="C3dPortContext"/>.
    /// </summary>
    public static IReadOnlyList<string> GroundSet(EmSetup? setup, C3dElaboration e)
    {
        var set = new List<string>(e.GroundBandObjects);
        if (setup?.Ground3D is { Length: > 0 } net)
            set.AddRange(e.ObjectNets.Where(kv => kv.Value.Contains(net)).Select(kv => kv.Key));
        return [.. set.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The port context <paramref name="setup"/>'s run measures ports against: this elaboration, this box.</summary>
    public static C3dPortContext PortContext(EmSetup? setup, C3dDocument document, C3dElaboration e, Em3dAirBox? box)
        => new(e, document.DbuPerMicron, box, GroundSet(setup, e));

    /// <summary>
    /// The air box <paramref name="setup"/> puts around <paramref name="e"/> — the one a run of it solves in, which is also
    /// the one the editor draws (R-em3d49-3a) and a wave port must lie on. Null when there is no content.
    /// </summary>
    public static Em3dAirBox? AirBox(EmSetup setup, C3dElaboration e, out string? refusal)
    {
        refusal = null;
        if (e.Extent() is not { } extent) return null;
        double fMin;
        try { fMin = setup.Frequency.Expand().Where(f => f > 0).DefaultIfEmpty(1e9).Min(); }
        catch (Exception) { fMin = 1e9; }
        return Em3dGenerator.PaddedAirBox(setup, (extent.X0, extent.Y0, extent.X1, extent.Y1, extent.Z0, extent.Z1),
                                          fMin, floorZ: null, waves: [], [], out refusal);
    }

    /// <summary>
    /// brief-em3d-49 R-em3d49-4d — each face boundary on its own, for drawing: its pieces in the world, or why it cannot
    /// be placed. The same resolution <see cref="FaceBoundaries"/> makes, one boundary at a time, so one refusal does not
    /// hide the others.
    /// </summary>
    public static IReadOnlyList<(C3dFaceBoundary Boundary, IReadOnlyList<Em3dFacePolygon> Pieces, string? Refusal)> FaceBoundaryPreview(
        C3dDocument document, C3dElaboration e)
    {
        var list = new List<(C3dFaceBoundary, IReadOnlyList<Em3dFacePolygon>, string?)>();
        foreach (var b in document.FaceBoundaries)
        {
            var one = new C3dDocument { Objects = document.Objects, FaceBoundaries = [b] };
            var materials = e.Materials.ToList();
            if (FaceBoundaries(one, e, materials, EmSetup.DefaultOperatingTempC, out var resolved) is { } why)
            {
                list.Add((b, [], why));
                continue;
            }
            var solid = e.Solids.First(s => s.Name == b.Object);
            list.Add((b, [.. resolved.SelectMany(r => Em3dFaceGeometry.Pieces(solid.Primitive, r.Face, out _) ?? [])], null));
        }
        return list;
    }

    /// <summary>
    /// R-em3d49-4 — the document's face boundaries as the neutral problem states them: each on its object's elaborated
    /// solid, the document's face name carried to the primitive's own through the provenance map (a box turned a quarter
    /// turn has its document xmin on its world ymin). A conductive face's metal resolves in the document's technology.
    /// A refusal names the object and the face — never a guess (§6.4).
    /// </summary>
    public static string? FaceBoundaries(C3dDocument document, C3dElaboration e, List<Em3dMaterial> materials, double tempC,
                                         out List<Em3dFaceBoundary> boundaries)
    {
        boundaries = [];
        foreach (var b in document.FaceBoundaries)
        {
            string where = $"The boundary on face '{b.Face}' of '{b.Object}'";
            if (b.Kind is Em3dFaceBoundaryKind.Absorbing or Em3dFaceBoundaryKind.Pmc or Em3dFaceBoundaryKind.Symmetry)
                return $"{where} is {b.Kind}, which both 3D solvers state only on the problem's outer boundary: set it on a face " +
                       "of the setup's air box. A solid's face may be Pec or Conductive.";
            var obj = document.Objects.FirstOrDefault(o => o.Name == b.Object);
            if (obj is null) return $"{where} names no object of this 3D view.";
            if (obj is C3dSheet or C3dPolyline)
                return $"{where} is on a {(obj is C3dSheet ? "sheet, which is already a conductor's surface" : "polyline, which is construction geometry")}: " +
                       "a boundary goes on a face of a dielectric or air solid.";
            if (e.Solids.FirstOrDefault(s => s.Name == b.Object) is not { } solid || !e.Provenance.TryGetValue(b.Object, out var prov))
                return $"{where} is on an object the elaboration did not make a solid of; its own refusal says why.";
            if (solid.Role == Em3dRole.Conductor)
                return $"{where} is on a conductor, which is a void bounded by its own metal: a boundary on it has nothing to add. " +
                       "Put boundaries on dielectric and air objects.";
            var primNames = Em3dFaceGeometry.FaceNames(solid.Primitive);
            var mine = Enumerable.Range(0, Math.Min(prov.FaceNames.Count, primNames.Count))
                                 .Where(i => prov.FaceNames[i] == b.Face).Select(i => primNames[i]).Distinct().ToList();
            if (mine.Count == 0)
                return $"{where} names a face '{b.Object}' does not have (it has {string.Join(", ", obj.FaceNames().Distinct())}). " +
                       "A face's name is kept through every edit; one that no longer exists is refused, never guessed.";
            string? material = null;
            if (b.Kind == Em3dFaceBoundaryKind.Conductive)
            {
                if (b.Material is not { Length: > 0 } m) return $"{where} is Conductive and names no Material.";
                if (e.Technology?.FindMaterial(m) is not { } tm)
                    return $"{where} is made of '{m}', which the 3D view's technology does not define.";
                var values = C3dElaborator.MaterialValues(tm, tempC);
                if (!(values.SigmaSm > 0)) return $"{where} is made of '{m}', which states no conductivity.";
                material = materials.FirstOrDefault(x => x.Name == tm.Name && x with { Name = "" } == values with { Name = "" })?.Name
                           ?? materials.FirstOrDefault(x => x.Name.StartsWith(tm.Name + "@", StringComparison.Ordinal) &&
                                                            x with { Name = "" } == values with { Name = "" })?.Name;
                if (material is null)
                {
                    material = materials.Any(x => x.Name == tm.Name) ? $"{tm.Name}@face" : tm.Name;
                    materials.Add(values with { Name = material });
                }
            }
            foreach (string face in mine)
            {
                if (Em3dFaceGeometry.Pieces(solid.Primitive, face, out string? why) is null)
                    return $"{where} cannot be placed: {why}.";
                boundaries.Add(new Em3dFaceBoundary(b.Object, face, b.Kind, material));
            }
        }
        return null;
    }

    /// <summary>
    /// The 3D problem <paramref name="setup"/> makes of <paramref name="document"/>: elaborated at the setup's
    /// top frequency and temperature, then the air box, sweep and type. A refusal names what is missing.
    /// </summary>
    /// <param name="fromCem">True when the setup is a <c>.cem</c> pointing at the document (its embedded setups
    /// are then not consulted, and a note says so).</param>
    public static Em3dGenerationResult Assemble(EmSetup setup, C3dDocument document, string path, string? workspaceCws,
                                                C3dElaborator? elaborator = null, bool fromCem = false)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(document);
        var notes = new List<string>();
        Em3dGenerationResult No(string why, IReadOnlyList<string>? warnings = null)
            => new(null, why, notes) { Warnings = warnings ?? [] };

        if (!setup.Is3D) return No($"This setup is a planar analysis, and {C3dSetups.PlanarRefusal}.");

        // ── The sweep and the temperature, as the generator reads them ──────────────────────────
        double[] freqs;
        try { freqs = setup.Frequency.Expand(); }
        catch (Exception ex) { return No(EmDiagnostics.FrequencySweepUnresolvable(ex.Message).Render()); }
        var positive = freqs.Where(f => f > 0).ToArray();
        if (positive.Length == 0) return No(EmDiagnostics.FrequencySweepEmpty().Render());
        double fMin = positive.Min(), fMax = positive.Max();
        if (positive.Length < freqs.Length)
            notes.Add("The sweep's 0 Hz point is not in the 3D problem: a full-wave solver has no DC solution to give it.");
        var frequency = new Em3dFrequency(fMin, fMax, positive.Length,
            setup.Frequency.Kind == CircuitRF.Core.Design.SweepKind.Log ? Em3dSweepKind.Log : Em3dSweepKind.Linear);
        double tempC = setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC;
        if (fromCem && document.Setups.Count > 0)
            notes.Add($"The 3D view's own {document.Setups.Count} embedded setup(s) are not consulted: this .cem is the setup.");

        // ── The geometry: the elaboration the editor draws ──────────────────────────────────────
        var e = (elaborator ?? new C3dElaborator()).Elaborate(document, path, workspaceCws, new C3dElaborationOptions(fMax, tempC));
        notes.AddRange(e.Notes);
        if (!e.Ok) return No(string.Join(" ", e.Refusals), e.Warnings);
        if (e.Extent() is not { } extent)
            return No("This 3D view holds no solid or sheet, so there is nothing to solve.", e.Warnings);

        // ── The air box, padded by the generator's own rule ─────────────────────────────────────
        var box = Em3dGenerator.PaddedAirBox(setup, (extent.X0, extent.Y0, extent.X1, extent.Y1, extent.Z0, extent.Z1),
                                             fMin, floorZ: null, waves: [], notes, out string? boxRefusal);
        if (boxRefusal is not null || box is null) return No(boxRefusal ?? "The air box could not be built.", e.Warnings);
        notes.Add("The air box's floor is below the geometry: a 3D view states no floor of its own, so every face is " +
                  "the setup's (absorbing unless its AirBox says otherwise).");

        var solids = e.Solids.ToList();
        var sheets = e.Sheets.ToList();
        var materials = e.Materials.ToList();

        // ── Ports (R-em3d49-2): the document's, polarity by contact ─────────────────────────────
        // The generator's rule by problem (R-em3d22-1c): an electrostatic solve has none, and a magnetostatic one only
        // the ports its terminals are driven through — so a port no solve reads can never refuse the run.
        var context = PortContext(setup, document, e, box);
        var ports = new List<Em3dPort>();
        if (setup.Problem3D == Em3dProblemType.Electrostatic)
        {
            if (document.Ports.Count > 0)
                notes.Add($"An electrostatic solve has no ports: the 3D view's {document.Ports.Count} port(s) are not in the problem.");
        }
        else
        {
            var sources = setup.Problem3D == Em3dProblemType.Magnetostatic
                ? setup.Terminals3D.Select(t => t.Source?.Trim()).OfType<string>()
                       .Select(src => src.StartsWith("port/", StringComparison.Ordinal) ? src : "port/" + src).ToHashSet(StringComparer.Ordinal)
                : null;
            foreach (var r in C3dPorts.Resolve(document, context))
            {
                if (sources is not null && !sources.Contains(C3dPorts.ProblemName(r.Port.Number))) continue;
                if (r.Resolved is null) return No(r.Refusal!, e.Warnings);
                ports.Add(r.Resolved);
            }
            if (sources is not null && document.Ports.Count > ports.Count)
                notes.Add($"{document.Ports.Count - ports.Count} port(s) drive no terminal and are not in the magnetostatic problem.");
        }

        // ── Face boundaries (R-em3d49-4) ────────────────────────────────────────────────────────
        if (FaceBoundaries(document, e, materials, tempC, out var faceBoundaries) is { } boundaryRefusal)
            return No(boundaryRefusal, e.Warnings);

        // ── Terminals and ground (R-em3d22-2), by net ───────────────────────────────────────────
        List<Em3dTerminal> terminals = [];
        List<string> ground = [];
        if (setup.IsStatic3D &&
            Em3dGenerator.Terminals(setup, solids, sheets, ports, pecFloor: false, e.ObjectNets.ToDictionary(kv => kv.Key, kv => kv.Value),
                                    [.. e.GroundBandObjects], out terminals, out ground) is { } terminalRefusal)
            return No(terminalRefusal, e.Warnings);

        var problem = new Em3dProblem(solids, sheets, materials, ports, box, frequency, tempC)
        {
            Type = setup.Problem3D,
            FaceBoundaries = faceBoundaries,
            Terminals = terminals,
            GroundObjects = ground,
            EigenmodeCount = setup.Eigenmode?.Count ?? EmEigenmode3D.DefaultCount,
            EigenmodeTargetHz = setup.Eigenmode?.TargetGHz is { } target ? target * 1e9 : fMin,
        };
        return new Em3dGenerationResult(problem, null, notes)
        {
            Warnings = e.Warnings,
            Wires = e.Wires,
            Origins = e.Origins,
            MaterialSources = e.MaterialSources,
        };
    }
}

/// <summary>brief-em3d-49 R-em3d49-5c — one port as a setup's run resolves it; <see cref="Setup"/> is null when the
/// document embeds no setup (lumped ports are still inferred; a wave port needs a box).</summary>
public sealed record C3dPortReport(string? Setup, C3dPortResult Result)
{
    /// <summary>What <c>check</c> prints: the polarity and why, or the refusal.</summary>
    public string Text => (Setup is { } s ? $"setup '{s}': " : "") + (Result.Refusal ?? C3dPortReports.Describe(Result));
}

/// <summary>
/// brief-em3d-49 R-em3d49-5c — what <c>check</c> and <c>explain</c> say about a 3D view's ports and face boundaries: the
/// SAME resolution a run makes (C3dPorts through the setup's own box and ground set, C3dProblemAssembly.FaceBoundaries),
/// so a port that checks clean is the port the solver gets.
/// </summary>
public static class C3dPortReports
{
    /// <summary>Every port under every readable embedded setup — each setup's box and ground set can change a port's
    /// polarity or, for a wave port, whether it lies on the box at all. Identical lines from two setups are said once.</summary>
    public static IReadOnlyList<C3dPortReport> For(C3dDocument doc, C3dElaboration e)
    {
        var reports = new List<C3dPortReport>();
        if (doc.Ports.Count == 0 || !e.Ok) return reports;
        var setups = C3dSetups.Read(doc).Where(s => s.Setup is not null).ToList();
        if (setups.Count == 0)
        {
            var ctx = C3dProblemAssembly.PortContext(null, doc, e, null);
            reports.AddRange(C3dPorts.Resolve(doc, ctx).Select(r => new C3dPortReport(null, r)));
            return reports;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in setups)
        {
            var box = C3dProblemAssembly.AirBox(s.Setup!, e, out _);
            var ctx = C3dProblemAssembly.PortContext(s.Setup, doc, e, box);
            foreach (var r in C3dPorts.Resolve(doc, ctx))
                if (seen.Add(r.Refusal ?? Describe(r))) reports.Add(new C3dPortReport(setups.Count == 1 ? null : s.Name, r));
        }
        return reports;
    }

    /// <summary>The face boundaries' refusals, in the document's technology at the default temperature.</summary>
    public static IReadOnlyList<string> FaceBoundaryRefusals(C3dDocument doc, C3dElaboration e)
    {
        if (doc.FaceBoundaries.Count == 0 || !e.Ok) return [];
        var materials = e.Materials.ToList();
        return C3dProblemAssembly.FaceBoundaries(doc, e, materials, EmSetup.DefaultOperatingTempC, out _) is { } why ? [why] : [];
    }

    /// <summary>A resolved port in a sentence: <c>P1 (lumped) runs from 'ground' to 'trace' along +z: 'ground' is in the
    /// ground set.</c></summary>
    public static string Describe(C3dPortResult r)
    {
        if (r.Resolved is not { } p) return r.Refusal ?? "";
        var d = p.Kind == Em3dPortKind.Wave && p.VoltagePath is { } v
            ? new Point3(v.To.X - v.From.X, v.To.Y - v.From.Y, v.To.Z - v.From.Z) : p.Direction;
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
        string along = ax >= ay && ax >= az ? (d.X >= 0 ? "+x" : "-x") : ay >= az ? (d.Y >= 0 ? "+y" : "-y") : (d.Z >= 0 ? "+z" : "-z");
        return $"{r.Label} ({(p.Kind == Em3dPortKind.Wave ? "wave" : "lumped")}) runs from '{p.NegativeObject}' to '{p.PositiveObject}' " +
               $"along {along}: {r.Reason}.";
    }
}
