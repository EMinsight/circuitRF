using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Cli;

/// <summary>
/// A <c>.cem</c> read, resolved and — for a 3D setup — generated, the way every verb that looks at a
/// 3D setup reads one (brief-em3d-5). <c>render</c> and <c>explain</c> both come through here, so
/// the picture and the report are of the same problem: the same two walk-ups
/// (<see cref="EmSetupResolver"/>), the same generator, the same stem-paired <c>.wBond</c>.
/// </summary>
internal sealed record Em3dSetupSource(
    string                 Path,
    EmSetup                Setup,
    EmSetupResolution      Resolution,
    Em3dGenerationResult?  Generated,
    string?                Refusal)
{
    /// <summary>brief-em3d-42 — the elaboration a 3D view's problem was assembled from (a <c>.c3d</c>, or a
    /// <c>.cem</c> naming one); null for a layout's.</summary>
    public C3dElaboration? Elaboration { get; init; }

    /// <summary>Reads <paramref name="path"/>. Throws what the <c>.cem</c> reader throws on a file
    /// that will not parse; every other failure is <see cref="Refusal"/>.</summary>
    public static Em3dSetupSource Load(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (DocumentKinds.Classify(full) == DocumentKind.ThreeD) return ForThreeDView(full, null);
        var setup = EmSetupPersistence.LoadFromFile(full);
        string? cws = DocumentKinds.AncestorCws(full);
        // brief-em3d-42 R-em3d42-5b — one field, two kinds of geometry: a LayoutRef naming a .c3d elaborates it.
        if (C3dSetups.IsThreeDView(EmSetupResolver.ResolveLayoutPath(full, setup.LayoutRef, cws)))
            return FromCemOnThreeDView(full, setup, cws);
        var resolution = EmSetupResolver.Resolve(full, setup.LayoutRef, cws, new TechnologyCache());
        return From(full, setup, resolution);
    }

    /// <summary>
    /// brief-em3d-42 — a <c>.c3d</c> drawn or explained: through its embedded setup when it has exactly one (or
    /// <paramref name="setupName"/> names one), so the picture is the problem a run would receive; otherwise
    /// its elaboration alone, in an air box at its own extent, because there is no setup to pad by.
    /// </summary>
    public static Em3dSetupSource ForThreeDView(string full, string? setupName)
    {
        var doc = C3dPersistence.LoadFromFile(full);
        string? cws = DocumentKinds.AncestorCws(full);
        var embedded = C3dSetups.Read(doc);
        var elaborator = new C3dElaborator();
        bool one = setupName is not null || embedded.Count(s => s.Setup is not null) == 1 && embedded.Count == 1;
        if (one)
        {
            var (chosen, why) = C3dSetups.Select(doc, setupName);
            if (chosen is null)
                return new Em3dSetupSource(full, new EmSetup { Name = setupName ?? "" }, NoResolution(full), null, why);
            var setup = C3dSetups.ForRun(chosen, full);
            var generated = C3dProblemAssembly.Assemble(setup, doc, full, cws, elaborator);
            var e = elaborator.Elaborate(doc, full, cws, new C3dElaborationOptions(generated.Problem?.Frequency.StopHz,
                                                                                   setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC));
            return new Em3dSetupSource(full, setup, ResolutionOf(full, e), generated,
                generated.Problem is null ? generated.Refusal ?? "the 3D problem could not be built." : null) { Elaboration = e };
        }

        var elaboration = elaborator.Elaborate(doc, full, cws);
        var drawing = new EmSetup { Name = System.IO.Path.GetFileNameWithoutExtension(full), Solver3D = Em3dSolver.Palace };
        if (!elaboration.Ok || elaboration.Extent() is not { } x)
            return new Em3dSetupSource(full, drawing, ResolutionOf(full, elaboration), null,
                elaboration.Ok ? C3dProblemAssembly.NothingToSolve(elaboration) : string.Join(" ", elaboration.Refusals)) { Elaboration = elaboration };
        var box = new Em3dAirBox(new Point3(x.X0, x.Y0, x.Z0), new Point3(x.X1, x.Y1, x.Z1),
                                 new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                               Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing));
        var problem = new Em3dProblem(elaboration.Solids, elaboration.Sheets, elaboration.Materials, [], box,
                                      new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), EmSetup.DefaultOperatingTempC);
        var notes = elaboration.Notes.Append(embedded.Count == 0
            ? "Drawn from the 3D view's elaboration in a box at its own extent: it embeds no setup, so there is no air box to show."
            : $"Drawn from the 3D view's elaboration in a box at its own extent: it embeds {embedded.Count} setups; name one with --setup to see its air box.").ToList();
        return new Em3dSetupSource(full, drawing, ResolutionOf(full, elaboration),
            new Em3dGenerationResult(problem, null, notes)
            {
                Warnings = elaboration.Warnings, Origins = elaboration.Origins, MaterialSources = elaboration.MaterialSources,
                Wires = elaboration.Wires,
            }, null) { Elaboration = elaboration };
    }

    /// <summary>brief-em3d-42 R-em3d42-5b — a <c>.cem</c> whose LayoutRef names a <c>.c3d</c>: that document,
    /// elaborated and assembled with this setup; its own embedded setups are not consulted.</summary>
    public static Em3dSetupSource FromCemOnThreeDView(string full, EmSetup setup, string? cws)
    {
        string c3d = EmSetupResolver.ResolveLayoutPath(full, setup.LayoutRef, cws)!;
        if (!File.Exists(c3d))
            return new Em3dSetupSource(full, setup, new EmSetupResolution(null, c3d, null, [$"3D view not found: {c3d}"]), null,
                                       $"its LayoutRef names the 3D view {c3d}, which does not exist.");
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(c3d); }
        catch (Exception e)
        {
            return new Em3dSetupSource(full, setup, new EmSetupResolution(null, c3d, null, [e.Message]), null,
                                       $"its 3D view {c3d} cannot be read: {e.Message}");
        }
        if (!setup.Is3D)
            return new Em3dSetupSource(full, setup, new EmSetupResolution(null, c3d, null, []), null,
                                       $"this setup is a planar analysis, and {C3dSetups.PlanarRefusal}.");
        var elaborator = new C3dElaborator();
        var generated = C3dProblemAssembly.Assemble(setup, doc, c3d, DocumentKinds.AncestorCws(c3d), elaborator, fromCem: true);
        var e2 = elaborator.Elaborate(doc, c3d, DocumentKinds.AncestorCws(c3d),
                                      new C3dElaborationOptions(generated.Problem?.Frequency.StopHz, setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC));
        return new Em3dSetupSource(full, setup, ResolutionOf(c3d, e2), generated,
            generated.Problem is null ? generated.Refusal ?? "the 3D problem could not be built." : null) { Elaboration = e2 };
    }

    private static EmSetupResolution NoResolution(string c3d) => new(null, c3d, null, []);

    private static EmSetupResolution ResolutionOf(string c3d, C3dElaboration e) => new(null, c3d, e.TechnologyPath, []);

    /// <summary>A setup already read and resolved — <c>explain</c>'s case, which has done both for its
    /// walks — generated exactly as <see cref="Load"/> generates it.</summary>
    public static Em3dSetupSource From(string full, EmSetup setup, EmSetupResolution resolution)
    {
        if (!setup.Is3D) return new Em3dSetupSource(full, setup, resolution, null, null);

        if (resolution.Source is not { } source)
            return new Em3dSetupSource(full, setup, resolution, null,
                resolution.Diagnostics.Count > 0 ? string.Join(" ", resolution.Diagnostics)
                                                 : "its layout reference resolves to nothing.");
        if (source.Technology is not { } tech)
            return new Em3dSetupSource(full, setup, resolution, null,
                EmDiagnostics.NoTechnology(setup.LayoutRef).Render());

        var generated = Em3dGenerator.Generate(setup, source, tech);
        return new Em3dSetupSource(full, setup, resolution, generated,
            generated.Problem is null ? generated.Refusal ?? "the 3D problem could not be built." : null);
    }
}
