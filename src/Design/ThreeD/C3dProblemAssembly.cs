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
// air. Ports and face boundaries are brief 49's; until then a driven setup with no port is refused where it
// always was, by the backend ("nothing to excite"). A static setup's terminals name nets — a layout
// instance's nets, or a drawn conductor's own name.
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
        var ports = new List<Em3dPort>();

        // ── Terminals and ground (R-em3d22-2), by net ───────────────────────────────────────────
        List<Em3dTerminal> terminals = [];
        List<string> ground = [];
        if (setup.IsStatic3D &&
            Em3dGenerator.Terminals(setup, solids, sheets, ports, pecFloor: false, e.ObjectNets.ToDictionary(kv => kv.Key, kv => kv.Value),
                                    [.. e.GroundBandObjects], out terminals, out ground) is { } terminalRefusal)
            return No(terminalRefusal, e.Warnings);

        var problem = new Em3dProblem(solids, sheets, e.Materials, ports, box, frequency, tempC)
        {
            Type = setup.Problem3D,
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
