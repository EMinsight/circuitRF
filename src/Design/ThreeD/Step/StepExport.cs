// brief-em3d-69 — STEP export: the ONE function File ▸ Export ▸ STEP… and `circuitrf convert … -o x.step`
// both call. Nothing here writes STEP: the worker does (OCCT's writer, with names and colours). This file decides what is IN
// the file, and hands the worker resolved numbers.
//
//   Plan    a .c3d, a .clay or a cell folder → what would be written: the parts, their cuts, the assemblies, the counts
//   Write   a plan → one STEP file, through a temporary file renamed into place, so a cancelled export writes nothing
//   Export  Plan + Write — `convert`'s whole verb, so the CLI holds no export logic
//
// THE ELABORATED MODEL, NEVER A SECOND RENDERING (D8, R-em3d69-1a). A .c3d is elaborated by C3dElaborator and a .clay by
// Em3dLayoutSolids.From — the Tier A path the generator and the .c3d elaborator already share — so the file a colleague
// opens is the geometry the solver gets. Ports, face boundaries, setups and polylines are not in the problem and so not in
// the file; an object with no material is not in the problem either, and the note counts it; a hidden object IS (hiding is
// a view state).
//
// PRECEDENCE APPLIED BY DEFAULT (R-em3d69-1b). Each solid loses what every higher-precedence solid it overlaps takes
// (Em3dPrecedence: metal over dielectric, then construction order) — the rule and the tie-break GmshGeoWriter's cut order
// uses, so a via is a hole through the substrate with the via in it and the receiving tool's mass properties are right.
// As drawn writes every solid whole. Sheets are faces; Thicken sheets makes each a thin solid of its stated thickness, which
// then takes part in precedence as the metal it is.
//
// AS ASSEMBLY (R-em3d69-2c). Every placed instance element is a sub-assembly, named by its cell and instanced with its
// placement; two elements whose contents are the same in their own frames share ONE sub-assembly, so sixteen identical dies
// write the die once. The comparison is of each element's contents carried back into its own frame (C3dInstanceFrame) and
// rounded to 0.1 nm. Precedence applies within a sub-assembly and never across them — a cut across instances would make each
// instance a different shape, which is not an assembly.
//
// UNITS (R-em3d69-3a): nm, µm and mm write millimetres; mil and inch write inches. The numbers change, the geometry does not.
// THE HEADER (R-em3d69-3c) names the output's FILE, never its path, and carries no author and no organisation.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD.Step;

/// <summary>R-em3d69-3b — the application protocol written. AP214 carries everything this export writes.</summary>
public enum StepExportSchema { Ap214, Ap242 }

/// <summary>R-em3d69-5a — which view of a cell folder holding both is exported.</summary>
public enum StepExportView { Auto, ThreeD, Layout }

/// <summary>The Export STEP dialog's options — and <c>convert</c>'s flags, one for one (R-em3d69-5b).</summary>
public sealed record StepExportOptions
{
    /// <summary>Each placed cell a sub-assembly, instanced; off writes one flat assembly.</summary>
    public bool Assembly { get; init; }

    /// <summary>Every solid whole, overlapping where the document draws it; off applies precedence.</summary>
    public bool AsDrawn { get; init; }

    /// <summary>Each sheet a thin solid of its stated thickness; off writes it as a face.</summary>
    public bool ThickenSheets { get; init; }

    /// <summary>The solve's air box as one solid, <c>airbox</c>, uncoloured.</summary>
    public bool IncludeAirBox { get; init; }

    public StepExportSchema Schema { get; init; } = StepExportSchema.Ap214;

    /// <summary>A cell folder holding a 3D view and a layout: which one.</summary>
    public StepExportView View { get; init; } = StepExportView.Auto;

    /// <summary>A layout's technology when its own walk finds none (<c>--tech</c>).</summary>
    public string? TechPath { get; init; }

    /// <summary>The workspace to resolve technologies against when the document's own walk-up finds none.</summary>
    public string? WorkspaceCws { get; init; }

    /// <summary>The editor's document as it stands (unsaved edits included); null reads the file.</summary>
    public C3dDocument? Document { get; init; }

    /// <summary>The layout editor's view as it stands; null reads the file.</summary>
    public LayoutView? Layout { get; init; }
}

public sealed class StepExportException(Diagnostic diagnostic) : Exception(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>brief-em3d-69 R-em3d69-5c — what STEP export refuses, each naming its remedy.</summary>
public static class StepExportDiagnostics
{
    public static Diagnostic NoKernel(string sentence) => Diagnostic.Create(
        "step.export.no-kernel", DiagnosticSeverity.Error, "{sentence}", ("sentence", sentence));

    public static Diagnostic NotASource(string path) => Diagnostic.Create(
        "step.export.source", DiagnosticSeverity.Error,
        "'{path}' is not a .c3d, a .clay or a cell folder, so there is no 3D model to export.", ("path", path));

    public static Diagnostic Unreadable(string path, string why) => Diagnostic.Create(
        "step.export.unreadable", DiagnosticSeverity.Error, "'{path}' cannot be read: {why}", ("path", path), ("why", why));

    public static Diagnostic BothViews(string cell, string threeD, string layout) => Diagnostic.Create(
        "step.export.both-views", DiagnosticSeverity.Error,
        "Cell '{cell}' has a 3D view ({threeD}) and a layout ({layout}); say which with --view 3d|layout.",
        ("cell", cell), ("threeD", threeD), ("layout", layout));

    public static Diagnostic NoView(string cell, string which) => Diagnostic.Create(
        "step.export.no-view", DiagnosticSeverity.Error, "Cell '{cell}' has no {which}, so there is nothing to export.",
        ("cell", cell), ("which", which));

    public static Diagnostic NoTechnology(string path, string why) => Diagnostic.Create(
        "step.export.no-technology", DiagnosticSeverity.Error,
        "The layout '{path}' has no technology{why}, and without its stackup nothing in it has a height. Name one with --tech <path.ctech>.",
        ("path", path), ("why", why));

    public static Diagnostic TechnologyUnreadable(string path, string why) => Diagnostic.Create(
        "step.export.technology-unreadable", DiagnosticSeverity.Error, "The technology '{path}' could not be read: {why}",
        ("path", path), ("why", why));

    /// <summary>Em3dLayoutSolids.From's own refusal, verbatim (R-em3d69-5c).</summary>
    public static Diagnostic LayoutRefused(string refusal) => Diagnostic.Create(
        "step.export.layout", DiagnosticSeverity.Error, "{refusal}", ("refusal", refusal));

    /// <summary>The .c3d does not elaborate: the elaboration's own first refusal, and how many there are.</summary>
    public static Diagnostic DoesNotElaborate(string first, int count) => Diagnostic.Create(
        "step.export.elaboration", DiagnosticSeverity.Error, "{first}{more}",
        ("first", first), ("more", count > 1 ? $" ({count - 1} more refusal{(count == 2 ? "" : "s")}: `check` lists them.)" : ""));

    public static Diagnostic NothingToExport(int ignored) => Diagnostic.Create(
        "step.export.nothing", DiagnosticSeverity.Error, "{text}",
        ("text", ignored == 0
            ? "There is nothing to export: the model holds no solid and no sheet."
            : $"There is nothing to export: none of the {ignored} object{(ignored == 1 ? "" : "s")} has a material, and an object with none is not in the model. Give one a material."));
}

/// <summary>What <see cref="StepExport.Plan"/> decided — everything but the file's bytes.</summary>
public sealed class StepExportPlan
{
    /// <summary>The <c>.c3d</c> or <c>.clay</c> the model came from.</summary>
    public required string SourcePath { get; init; }
    public required bool IsLayout { get; init; }
    public required StepExportOptions Options { get; init; }

    /// <summary><c>mm</c> or <c>in</c> (R-em3d69-3a).</summary>
    public required string Units { get; init; }

    /// <summary>Each part's name, in request order: the name its STEP product carries.</summary>
    public required IReadOnlyList<string> PartNames { get; init; }

    /// <summary>Solids written (thickened sheets and the air box included), faces written (sheets left as surfaces), the
    /// model's sheets, the sub-assemblies written, and the objects left out for having no material.</summary>
    public int Solids { get; init; }
    public int Faces { get; init; }
    public int Sheets { get; init; }
    public int SubAssemblies { get; init; }
    public int Excluded { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>R-em3d69-4a — the dialog's summary line, before anything is written.</summary>
    public string Summary
    {
        get
        {
            string s = $"{Solids} solid{(Solids == 1 ? "" : "s")}, {Faces} face{(Faces == 1 ? "" : "s")}, " +
                       $"{Sheets} sheet{(Sheets == 1 ? "" : "s")}";
            if (Options.ThickenSheets && Sheets > 0) s += " (thickened)";
            if (Options.Assembly) s += $"; {SubAssemblies} sub-assembl{(SubAssemblies == 1 ? "y" : "ies")}";
            s += $" — in {(Units == "in" ? "inches" : "millimetres")}, {(Options.Schema == StepExportSchema.Ap242 ? "AP242" : "AP214")}";
            if (Excluded > 0) s += $"; {Excluded} object{(Excluded == 1 ? "" : "s")} with no material left out";
            return s + ".";
        }
    }

    internal JsonObject Request { get; init; } = new();
    internal IReadOnlyList<GeometryKernelBlob> Blobs { get; init; } = [];
}

/// <summary>What was written: the file, each part's outcome from the worker, and the notes.</summary>
public sealed record StepExportResult(string Path, StepExportPlan Plan, IReadOnlyList<GeometryKernelWrittenPart> Parts,
                                      IReadOnlyList<string> Notes);

public static class StepExport
{
    /// <summary>The extensions a STEP target takes.</summary>
    public static bool IsStepExtension(string path) => StepImport.IsStepExtension(path);

    /// <summary>
    /// R-em3d69-2b — the alpha a dielectric is written with: the 3D view's own (<c>Scene3DBuilder.DielectricAlpha</c>, which
    /// lives in src/Render where nothing in src/Design can see it; a test holds the two equal).
    /// </summary>
    public const byte DielectricAlpha = 90;

    /// <summary>R-em3d69-3a — the unit a document's display unit writes in: millimetres for nm, µm and mm; inches for mil and inch.</summary>
    public static string Units(LayoutUnit display) => display is LayoutUnit.Mil or LayoutUnit.Inch ? "in" : "mm";

    /// <summary>R-em3d69-3c — the header's originating system: circuitRF and the version the repo-root VERSION file gives every
    /// assembly (the attribute AppVersion reads — never a second literal).</summary>
    public static string OriginatingSystem
    {
        get
        {
            var asm = typeof(StepExport).Assembly;
            string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString() ?? "unknown";
            int plus = v.IndexOf('+');
            return "circuitRF " + (plus >= 0 ? v[..plus] : v);
        }
    }

    /// <summary>Plan + Write: what <c>convert</c> calls, and the dialog's OK.</summary>
    /// <exception cref="StepExportException">A refusal, naming its remedy.</exception>
    /// <exception cref="GeometryKernelException">The kernel refused, crashed or timed out.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was written.</exception>
    public static StepExportResult Export(string source, string outputPath, StepExportOptions options, GeometryKernel kernel,
                                          RunControl? control = null, TechnologyCache? cache = null)
        => Write(Plan(source, options, kernel, cache), outputPath, kernel, control);

    // ── Write ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="plan"/> to <paramref name="outputPath"/>: the worker's bytes to a temporary file beside it, then
    /// renamed into place (R-em3d69-4c), so a cancelled or failed export leaves whatever was there before.
    /// </summary>
    public static StepExportResult Write(StepExportPlan plan, string outputPath, GeometryKernel kernel, RunControl? control = null)
    {
        string output = System.IO.Path.GetFullPath(outputPath);
        var request = (JsonObject)plan.Request.DeepClone();
        ((JsonObject)request["header"]!)["name"] = System.IO.Path.GetFileName(output);
        if (control is not null) control.Stage = "Writing the STEP file";
        var file = kernel.WriteStep(request, plan.Blobs, control);
        control?.Token.ThrowIfCancellationRequested();

        string? dir = System.IO.Path.GetDirectoryName(output);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
        string temp = output + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(temp, file.Data);
            control?.Token.ThrowIfCancellationRequested();
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { /* best effort */ }
        }

        var notes = new List<string>(plan.Notes);
        var emptied = file.Parts.Where(p => p.Empty).Select(p => $"'{p.Name}'").ToList();
        if (emptied.Count > 0)
            notes.Add($"{string.Join(", ", emptied)} lie{(emptied.Count == 1 ? "s" : "")} wholly inside higher-precedence solids, so " +
                      $"{(emptied.Count == 1 ? "it has" : "they have")} no volume of {(emptied.Count == 1 ? "its" : "their")} own and " +
                      $"{(emptied.Count == 1 ? "is" : "are")} not in the file.");
        return new StepExportResult(output, plan, file.Parts, notes);
    }

    // ── Plan ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One thing that becomes a STEP product: a solid, a sheet, or the air box.</summary>
    private sealed record Item(string Name, string Material, Em3dRole Role, Em3dPrimitive? Primitive, Em3dSheet? Sheet,
                               string Group, int Priority, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds)
    {
        public bool IsFace(bool thicken) => Sheet is not null && !(thicken && Sheet.ThicknessM > 0);
    }

    /// <summary>
    /// What would be written for <paramref name="source"/> — a <c>.c3d</c>, a <c>.clay</c>, or a cell folder (its 3D view, else
    /// its layout; both is a refusal naming <c>--view</c>). Nothing is written, and the worker is asked nothing but what
    /// elaboration asks of it.
    /// </summary>
    /// <exception cref="StepExportException">A refusal, naming its remedy.</exception>
    public static StepExportPlan Plan(string source, StepExportOptions options, GeometryKernel kernel, TechnologyCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var cap = kernel.Capability;
        if (!cap.Available) throw new StepExportException(StepExportDiagnostics.NoKernel(GeometryKernel.NeedsKernel("Export STEP", cap)));
        var (path, isLayout) = ResolveSource(source, options);
        cache ??= new TechnologyCache();
        return isLayout ? PlanLayout(path, options, cache) : PlanC3d(path, options, kernel, cache);
    }

    /// <summary>The file a source names, and whether it is a layout (R-em3d69-5a).</summary>
    private static (string Path, bool Layout) ResolveSource(string source, StepExportOptions options)
    {
        string full = System.IO.Path.GetFullPath(source);
        if (Directory.Exists(full))
        {
            string cell = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(full));
            string? File3(ViewType t) => CellFolder.ResolvePrimary(full, t).ResolvedName is { } f
                ? System.IO.Path.Combine(CellFolder.SubFolderPath(full, t), f) : null;
            string? threeD = File3(ViewType.ThreeD), layout = File3(ViewType.Layout);
            if (!File.Exists(System.IO.Path.Combine(full, CellFolder.CcellFileName)) && threeD is null && layout is null)
                throw new StepExportException(StepExportDiagnostics.NotASource(source));
            return options.View switch
            {
                StepExportView.ThreeD => threeD is { } t ? (t, false) : throw new StepExportException(StepExportDiagnostics.NoView(cell, "3D view")),
                StepExportView.Layout => layout is { } l ? (l, true) : throw new StepExportException(StepExportDiagnostics.NoView(cell, "layout")),
                _ => (threeD, layout) switch
                {
                    ({ } t, { } l) => throw new StepExportException(StepExportDiagnostics.BothViews(cell,
                                          System.IO.Path.GetRelativePath(full, t), System.IO.Path.GetRelativePath(full, l))),
                    ({ } t, null) => (t, false),
                    (null, { } l) => (l, true),
                    _ => throw new StepExportException(StepExportDiagnostics.NoView(cell, "3D view and no layout")),
                },
            };
        }
        string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
        if (ext == C3dPersistence.Extension) return (full, false);
        if (ext == ".clay") return (full, true);
        throw new StepExportException(StepExportDiagnostics.NotASource(source));
    }

    private static StepExportPlan PlanC3d(string path, StepExportOptions options, GeometryKernel kernel, TechnologyCache cache)
    {
        C3dDocument doc;
        // A copy of the editor's document: resolution writes every bound field in place, and the editor's is not ours to change.
        try { doc = options.Document is { } open ? C3dPersistence.Deserialize(C3dPersistence.Serialize(open)) : C3dPersistence.LoadFromFile(path); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            throw new StepExportException(StepExportDiagnostics.Unreadable(path, x.Message));
        }
        var e = new C3dElaborator(cache, kernel).Elaborate(doc, path, options.WorkspaceCws);
        if (!e.Ok) throw new StepExportException(StepExportDiagnostics.DoesNotElaborate(e.Refusals[0], e.Refusals.Count));
        // brief-em3d-101 R-em3d101-1h — a reference image is not geometry: a not-modelled image sheet (with a material or without) is
        // left out with ONE note, the layout's R-bmp-3. A modelled one is a sheet like any other and is exported as one.
        bool Reference(string name) => e.Images.ContainsKey(name) && (C3dModelled.IsOff(e, name) || e.UnassignedSheets.Any(u => u.Name == name));
        int images = e.Images.Keys.Count(Reference);
        var sheets = images == 0 ? e.Sheets : [.. e.Sheets.Where(sh => !Reference(sh.Name))];
        var unassignedSheets = e.UnassignedSheets.Where(sh => !Reference(sh.Name)).ToList();
        int excluded = e.UnassignedSolids.Count + unassignedSheets.Count;
        if (e.Solids.Count + sheets.Count == 0) throw new StepExportException(StepExportDiagnostics.NothingToExport(excluded));

        var notes = new List<string>(e.Notes);
        if (images > 0) notes.Add($"{images} image sheet{(images == 1 ? "" : "s")} skipped: reference images are not geometry.");
        if (excluded > 0)
        {
            var names = e.UnassignedSolids.Select(s => s.Name).Concat(unassignedSheets.Select(s => s.Name)).Select(n => $"'{n}'");
            notes.Add($"{excluded} object{(excluded == 1 ? " has" : "s have")} no material, so {(excluded == 1 ? "it is" : "they are")} " +
                      $"not in the model and not in the file: {string.Join(", ", names)}.");
        }
        var disabled = doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dOperation>().Where(o => !o.Enabled).ToList();
        if (disabled.Count > 0)
            notes.Add($"{string.Join(", ", disabled.Select(o => $"'{o.Name}' ({C3dOperands.Article(o)})"))} " +
                      $"{(disabled.Count == 1 ? "is" : "are")} disabled, so the file holds what elaboration gives: the operands as they are.");

        Em3dAirBox? box = null;
        if (options.IncludeAirBox)
        {
            var setup = C3dSetups.Read(doc).FirstOrDefault(s => s.Setup is not null)?.Setup ?? new EmSetup { Solver3D = Em3dSolver.Palace };
            box = C3dProblemAssembly.AirBox(setup, e, out string? why);
            if (box is null) notes.Add("The air box was asked for, but none could be sized" + (why is { } w ? $": {w}" : ".") + " It is not in the file.");
        }

        string Group(string name) => e.Provenance.TryGetValue(name, out var p) ? p.InstancePath : "";
        return Compose(path, isLayout: false, options, Units(doc.DisplayUnit), e.Solids, sheets, Group, e.Technology, box,
                       options.Assembly ? e.Instances : [], excluded, notes, System.IO.Path.GetFileNameWithoutExtension(path));
    }

    private static StepExportPlan PlanLayout(string path, StepExportOptions options, TechnologyCache cache)
    {
        LayoutView view;
        try { view = options.Layout ?? LayoutPersistence.LoadFromFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            throw new StepExportException(StepExportDiagnostics.Unreadable(path, e.Message));
        }

        // R-em3d69-5c — never an empty technology here: with no stackup nothing in a layout has a height.
        Technology? tech;
        if (options.TechPath is { } tp)
        {
            try { tech = TechPersistence.LoadFromFile(tp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                throw new StepExportException(StepExportDiagnostics.TechnologyUnreadable(tp, e.Message));
            }
        }
        else
        {
            var (res, _) = TechnologyResolver.ResolveForDocument(view.TechRef, path, options.WorkspaceCws, cache);
            tech = res.Tech;
            if (tech is null)
                throw new StepExportException(StepExportDiagnostics.NoTechnology(path,
                    res.Diagnostics.Count > 0 ? " (" + string.Join(" ", res.Diagnostics).TrimEnd('.') + ")" : ""));
        }

        var solids = Em3dLayoutSolids.From(new EmLayoutSource(path, view, tech, view.DbuPerMicron), tech, null,
                                           new Em3dLayoutSolidsOptions(null, EmSetup.DefaultOperatingTempC, Instance: true));
        if (solids.Refusal is { } refusal) throw new StepExportException(StepExportDiagnostics.LayoutRefused(refusal));
        if (solids.Solids.Count + solids.Sheets.Count == 0) throw new StepExportException(StepExportDiagnostics.NothingToExport(0));

        var notes = new List<string>(solids.Notes);
        Em3dAirBox? box = null;
        if (options.IncludeAirBox)
        {
            var b = Extent(solids.Solids, solids.Sheets);
            box = Em3dGenerator.PaddedAirBox(new EmSetup { Solver3D = Em3dSolver.Palace }, (b.X0, b.Y0, b.X1, b.Y1, b.Z0, b.Z1), 1e9,
                                             floorZ: null, waves: [], [], out string? why);
            if (box is null) notes.Add("The air box was asked for, but none could be sized" + (why is { } w ? $": {w}" : ".") + " It is not in the file.");
        }
        return Compose(path, isLayout: true, options, Units(view.DisplayUnit), solids.Solids, solids.Sheets, _ => "", tech, box, [], 0, notes,
                       System.IO.Path.GetFileNameWithoutExtension(path));
    }

    private static (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Extent(IEnumerable<Em3dSolid> solids, IEnumerable<Em3dSheet> sheets)
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var b in solids.Select(s => Em3dProblem.Bounds(s.Primitive)).Concat(sheets.Select(s => s.WorldBounds())))
        {
            x0 = Math.Min(x0, b.Item1); y0 = Math.Min(y0, b.Item2); z0 = Math.Min(z0, b.Item3);
            x1 = Math.Max(x1, b.Item4); y1 = Math.Max(y1, b.Item5); z1 = Math.Max(z1, b.Item6);
        }
        return (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>The request: the parts (world, µm), each one's cuts, the assemblies (root first) and the header.</summary>
    private static StepExportPlan Compose(string path, bool isLayout, StepExportOptions o, string units, IReadOnlyList<Em3dSolid> solids,
                                          IReadOnlyList<Em3dSheet> sheets, Func<string, string> groupOf, Technology? tech, Em3dAirBox? box,
                                          IReadOnlyList<C3dInstanceFrame> instances, int excluded, List<string> notes, string rootName)
    {
        var precedence = Em3dPrecedence.Of(solids, sheets);
        var items = new List<Item>();
        foreach (var s in solids)
            items.Add(new Item(s.Name, s.Material, s.Role, s.Primitive, null, groupOf(s.Name), precedence.Of(s), Em3dProblem.Bounds(s.Primitive)));
        foreach (var sh in sheets)
        {
            var b = sh.WorldBounds();
            var n = Normal(sh);
            double t = o.ThickenSheets ? sh.ThicknessM : 0;
            items.Add(new Item(sh.Name, sh.Material, Em3dRole.Conductor, null, sh, groupOf(sh.Name), precedence.Of(sh),
                (Math.Min(b.X0, b.X0 + n.X * t), Math.Min(b.Y0, b.Y0 + n.Y * t), Math.Min(b.Z0, b.Z0 + n.Z * t),
                 Math.Max(b.X1, b.X1 + n.X * t), Math.Max(b.Y1, b.Y1 + n.Y * t), Math.Max(b.Z1, b.Z1 + n.Z * t))));
        }
        if (o.ThickenSheets && sheets.Count(s => s.ThicknessM <= 0) is int flat and > 0)
            notes.Add($"{flat} sheet{(flat == 1 ? " states" : "s state")} no thickness, so {(flat == 1 ? "it is" : "they are")} written as " +
                      $"{(flat == 1 ? "a face" : "faces")} all the same.");
        if (o.ThickenSheets && sheets.Any(s => s.ThicknessM > 0))
            notes.Add("Each thickened sheet grows from its plane along the plane's normal (up, for a layout's sheets) by the thickness it states.");

        // ── the tree: flat, or one sub-assembly per placed element, shared where the contents are the same ──
        var frames = instances.ToDictionary(i => i.Path, StringComparer.Ordinal);
        var groups = items.Select((it, i) => (it, i)).GroupBy(x => frames.ContainsKey(x.it.Group) ? x.it.Group : "", StringComparer.Ordinal)
                          .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList(), StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var inst in instances) (children.TryGetValue(Parent(inst.Path), out var l) ? l : children[Parent(inst.Path)] = []).Add(inst.Path);

        var assemblies = new List<JsonObject>();
        var sentGroups = new List<string> { "" };
        var parts = new List<int>();                                  // item index per part, in request order
        var partOfItem = new Dictionary<int, int>();
        var prototypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);

        string Signature(string p)
        {
            if (signatures.TryGetValue(p, out var hit)) return hit;
            var f = frames[p];
            var inv = f.World.Inverse();
            var sb = new StringBuilder().Append(f.Layout ? "L|" : "D|").Append(f.DocumentPath).Append('\n');
            foreach (int i in groups.GetValueOrDefault(p) ?? [])
            {
                var it = items[i];
                sb.Append(Relative(it.Name, p)).Append('|').Append(it.Material).Append('|').Append(it.Role).Append('|')
                  .Append(LocalCanon(it, inv)).Append('\n');
            }
            foreach (string c in children.GetValueOrDefault(p) ?? [])
                sb.Append('<').Append(Signature(c)).Append('@').Append(Canon(frames[c].World.Then(inv))).Append(">\n");
            return signatures[p] = sb.ToString();
        }

        int Part(int item)
        {
            if (partOfItem.TryGetValue(item, out int k)) return k;
            parts.Add(item);
            return partOfItem[item] = parts.Count - 1;
        }

        JsonObject Component(string key, int index, C3dTransform? location, string? name)
        {
            var c = new JsonObject { [key] = index };
            if (location is { } l) c["location"] = Matrix(l);
            if (name is not null) c["name"] = name;
            return c;
        }

        int Assembly(string p)
        {
            string sig = Signature(p);
            if (prototypes.TryGetValue(sig, out int a)) return a;
            var node = new JsonObject { ["name"] = frames[p].CellName };
            assemblies.Add(node);
            int index = assemblies.Count - 1;
            prototypes[sig] = index;
            sentGroups.Add(p);
            var comps = new JsonArray();
            foreach (int i in groups.GetValueOrDefault(p) ?? []) comps.Add(Component("part", Part(i), null, null));
            foreach (string c in children.GetValueOrDefault(p) ?? [])
                comps.Add(Component("assembly", Assembly(c), frames[c].World.Then(frames[p].World.Inverse()), Relative(c, p)));
            node["components"] = comps;
            return index;
        }

        var root = new JsonObject { ["name"] = rootName };
        assemblies.Add(root);
        var rootComps = new JsonArray();
        var top = o.Assembly ? groups.GetValueOrDefault("") ?? [] : [.. Enumerable.Range(0, items.Count)];
        foreach (int i in top) rootComps.Add(Component("part", Part(i), null, null));
        int? airPart = null;
        if (box is not null)
        {
            items.Add(new Item("airbox", "", Em3dRole.Air, new Em3dBox(box.Min, box.Max), null, "", int.MaxValue,
                               (box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z)));
            airPart = Part(items.Count - 1);
            rootComps.Add(Component("part", airPart.Value, null, null));
        }
        if (o.Assembly)
            foreach (string c in children.GetValueOrDefault("") ?? [])
                rootComps.Add(Component("assembly", Assembly(c), frames[c].World, c));
        root["components"] = rootComps;

        // ── the parts: geometry, colour, cuts, and the frame of the sub-assembly that holds each ──
        var blobs = new List<GeometryKernelBlob>();
        var blobNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var partNodes = new JsonArray();
        var names = new List<string>();
        foreach (int item in parts)
        {
            var it = items[item];
            bool isAir = airPart is { } ap && parts[ap] == item;
            string group = isAir ? "" : it.Group;
            string name = o.Assembly && !isAir ? Relative(it.Name, frames.ContainsKey(group) ? group : "") : it.Name;
            names.Add(name);
            var node = new JsonObject { ["name"] = name, ["geometry"] = Geometry(it, o.ThickenSheets, blobs, blobNames) };
            node["colour"] = isAir ? null : Colour(tech, it);
            if (!o.AsDrawn && !it.IsFace(o.ThickenSheets))
            {
                // Every solid part of the same group (all of them, flattened) that outranks this one and overlaps it.
                var cut = new JsonArray();
                for (int j = 0; j < items.Count; j++)
                {
                    // Only what is written: in an assembly, a sub-assembly shared by several elements is written once.
                    if (j == item || !partOfItem.TryGetValue(j, out int pj)) continue;
                    var other = items[j];
                    if (other.IsFace(o.ThickenSheets) || (airPart is { } a2 && parts[a2] == j)) continue;
                    if (o.Assembly && (isAir ? !InTop(j) : other.Group != it.Group)) continue;
                    bool higher = isAir || other.Priority > it.Priority || (other.Priority == it.Priority && j > item);
                    if (higher && Overlap(it.Bounds, other.Bounds)) cut.Add(pj);
                }
                if (cut.Count > 0) node["cut"] = cut;
            }
            if (o.Assembly && !isAir && frames.TryGetValue(group, out var frame)) node["local"] = Matrix(frame.World.Inverse());
            partNodes.Add(node);
        }

        bool InTop(int j) => !o.Assembly || !frames.ContainsKey(items[j].Group);

        var request = new JsonObject
        {
            ["units"] = units,
            ["schema"] = o.Schema == StepExportSchema.Ap242 ? "ap242" : "ap214",
            ["header"] = new JsonObject
            {
                ["name"] = "",
                ["description"] = Description(o),
                ["system"] = OriginatingSystem,
            },
            ["parts"] = partNodes,
            ["assemblies"] = new JsonArray([.. assemblies]),
        };
        if (o.Assembly && instances.Count > 0)
            notes.Add("As assembly: precedence is applied within each sub-assembly and not across them, so where two placed cells " +
                      "overlap, both are written whole.");

        int sheetParts = parts.Count(i => items[i].Sheet is not null);
        int faces = parts.Count(i => items[i].IsFace(o.ThickenSheets));
        return new StepExportPlan
        {
            SourcePath = path, IsLayout = isLayout, Options = o, Units = units, PartNames = names,
            Solids = parts.Count - faces, Faces = faces, Sheets = sheetParts, SubAssemblies = assemblies.Count - 1, Excluded = excluded,
            Notes = notes, Request = request, Blobs = blobs,
        };
    }

    /// <summary>R-em3d69-1b — the header's description says which model was written.</summary>
    public static string Description(StepExportOptions o)
        => "circuitRF elaborated model; " + (o.Assembly ? "as assembly" : "flattened") + "; " +
           (o.AsDrawn ? "as drawn: solids overlap where the document draws them"
                      : "precedence applied: the solids are disjoint (metal over dielectric, then construction order)") +
           (o.ThickenSheets ? "; sheets thickened" : "; sheets as faces") + (o.IncludeAirBox ? "; air box included" : "");

    private static string Parent(string path) => path.LastIndexOf('/') is int k and >= 0 ? path[..k] : "";

    private static string Relative(string name, string group)
        => group.Length > 0 && name.StartsWith(group + "/", StringComparison.Ordinal) ? name[(group.Length + 1)..] : name;

    private static bool Overlap((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a,
                                (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
        => a.X0 < b.X1 && b.X0 < a.X1 && a.Y0 < b.Y1 && b.Y0 < a.Y1 && a.Z0 < b.Z1 && b.Z0 < a.Z1;

    private static Point3 Normal(Em3dSheet sh)
    {
        if (sh.Frame is not { } f) return new Point3(0, 0, 1);
        var n = f.Normal;
        double l = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        return l > 0 ? new Point3(n.X / l, n.Y / l, n.Z / l) : new Point3(0, 0, 1);
    }

    /// <summary>R-em3d69-2b — the material's colour (<c>#rrggbb</c>), a dielectric at the 3D view's alpha; none without one.</summary>
    private static JsonArray? Colour(Technology? tech, Item it)
    {
        if (tech?.FindMaterial(it.Material)?.Color is not { Length: 7 } hex || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out uint v))
            return null;
        double alpha = it.Role == Em3dRole.Dielectric ? DielectricAlpha / 255.0 : 1;
        return new JsonArray(((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0, alpha);
    }

    // ── geometry, µm ────────────────────────────────────────────────────────────────────────────

    private const double Um = 1e6;

    private static JsonArray P(Point3 p) => new(p.X * Um, p.Y * Um, p.Z * Um);

    private static JsonArray Ring(IEnumerable<Point3> ring)
    {
        var a = new JsonArray();
        Point3? prev = null;
        var list = ring.ToList();
        foreach (var q in list)
        {
            if (prev is { } pr && pr == q) continue;          // a repeated point is an edge of no length
            a.Add(P(q));
            prev = q;
        }
        if (list.Count > 1 && a.Count > 1 && list[0] == prev) a.RemoveAt(a.Count - 1);
        return a;
    }

    private static JsonObject Geometry(Item it, bool thicken, List<GeometryKernelBlob> blobs, Dictionary<string, string> blobNames)
    {
        if (it.Sheet is { } sh)
        {
            var outline = Ring(sh.Outline.Select(sh.World));
            var holes = new JsonArray([.. sh.Holes.Select(h => (JsonNode)Ring(h.Select(sh.World)))]);
            if (!(thicken && sh.ThicknessM > 0)) return new JsonObject { ["kind"] = "face", ["outline"] = outline, ["holes"] = holes };
            var n = Normal(sh);
            double t = sh.ThicknessM;
            return new JsonObject
            {
                ["kind"] = "prism", ["outline"] = outline, ["holes"] = holes, ["extrude"] = new JsonArray(n.X * t * Um, n.Y * t * Um, n.Z * t * Um),
            };
        }
        switch (it.Primitive)
        {
            case Em3dBox b:
                return new JsonObject { ["kind"] = "box", ["min"] = P(b.Min), ["max"] = P(b.Max) };
            case Em3dExtrudedPolygon e:
                return new JsonObject
                {
                    ["kind"] = "prism",
                    ["outline"] = Ring(e.Outline.Select(q => new Point3(q.X, q.Y, e.ZBottom))),
                    ["holes"] = new JsonArray([.. e.Holes.Select(h => (JsonNode)Ring(h.Select(q => new Point3(q.X, q.Y, e.ZBottom))))]),
                    ["extrude"] = new JsonArray(0.0, 0.0, (e.ZTop - e.ZBottom) * Um),
                };
            case Em3dCylinder c:
                return new JsonObject { ["kind"] = "cylinder", ["from"] = P(c.AxisStart), ["to"] = P(c.AxisEnd), ["radius"] = c.Radius * Um };
            case Em3dSphere s:
                return new JsonObject { ["kind"] = "sphere", ["centre"] = P(s.Center), ["radius"] = s.Radius * Um };
            case Em3dTruncatedSphere t:
                return new JsonObject
                {
                    ["kind"] = "sphere", ["centre"] = P(t.Center), ["radius"] = t.Radius * Um, ["zmin"] = t.ZMin * Um, ["zmax"] = t.ZMax * Um,
                };
            case Em3dPolyhedron ph:
            {
                var loops = new JsonArray();
                foreach (var f in ph.Faces)
                    loops.Add(new JsonObject
                    {
                        ["outer"] = new JsonArray([.. f.Outer.Select(i => (JsonNode)i)]),
                        ["holes"] = new JsonArray([.. f.Holes.Select(h => (JsonNode)new JsonArray([.. h.Select(i => (JsonNode)i)]))]),
                    });
                return new JsonObject { ["kind"] = "polyhedron", ["vertices"] = new JsonArray([.. ph.Vertices.Select(v => (JsonNode)P(v))]), ["loops"] = loops };
            }
            case Em3dSweep w:
                return new JsonObject
                {
                    ["kind"] = "loft", ["smooth"] = w.Section == Em3dSection.Circle,
                    ["rings"] = new JsonArray([.. w.Rings.Select(r => (JsonNode)new JsonArray([.. r.Select(q => (JsonNode)P(q))]))]),
                };
            case Em3dShapeSolid k:
                if (!blobNames.TryGetValue(k.BrepHash, out string? blob))
                {
                    blob = "brep" + blobNames.Count;
                    blobNames[k.BrepHash] = blob;
                    blobs.Add(GeometryKernelBlob.OfBytes(blob, k.Brep.ToArray()));
                }
                return new JsonObject { ["kind"] = "brep", ["blob"] = blob };
            default:
                throw new InvalidOperationException($"'{it.Name}' is a {it.Primitive?.GetType().Name}, which STEP export cannot write.");
        }
    }

    private static JsonArray Matrix(C3dTransform t)
        => new(t.M00, t.M01, t.M02, t.Tx * Um, t.M10, t.M11, t.M12, t.Ty * Um, t.M20, t.M21, t.M22, t.Tz * Um);

    // ── what "the same contents" means (R-em3d69-2c) ────────────────────────────────────────────

    private static string R(double v) => Math.Round(v * 1e10).ToString("R", CultureInfo.InvariantCulture);

    private static string Canon(C3dTransform t)
        => string.Join(",", ((double[])[t.M00, t.M01, t.M02, t.M10, t.M11, t.M12, t.M20, t.M21, t.M22]).Select(v => Math.Round(v * 1e12).ToString(CultureInfo.InvariantCulture)))
           + ";" + R(t.Tx) + "," + R(t.Ty) + "," + R(t.Tz);

    private static string Pt(C3dTransform t, Point3 p)
    {
        var (x, y, z) = (t.M00 * p.X + t.M01 * p.Y + t.M02 * p.Z + t.Tx, t.M10 * p.X + t.M11 * p.Y + t.M12 * p.Z + t.Ty,
                         t.M20 * p.X + t.M21 * p.Y + t.M22 * p.Z + t.Tz);
        return R(x) + " " + R(y) + " " + R(z);
    }

    /// <summary>A box's corners carried into the frame, as their bound — the same local box however the element is turned.</summary>
    private static string BoxCanon(C3dTransform t, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
    {
        double[] lo = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] hi = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        foreach (double x in (double[])[b.X0, b.X1])
            foreach (double y in (double[])[b.Y0, b.Y1])
                foreach (double z in (double[])[b.Z0, b.Z1])
                {
                    double[] q = [t.M00 * x + t.M01 * y + t.M02 * z + t.Tx, t.M10 * x + t.M11 * y + t.M12 * z + t.Ty, t.M20 * x + t.M21 * y + t.M22 * z + t.Tz];
                    for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], q[k]); hi[k] = Math.Max(hi[k], q[k]); }
                }
        return string.Join(" ", lo.Concat(hi).Select(R));
    }

    private static string LocalCanon(Item it, C3dTransform inv)
    {
        if (it.Sheet is { } sh)
            return "sheet " + sh.ThicknessM.ToString("R", CultureInfo.InvariantCulture) + ":" + string.Join(";", sh.Outline.Select(q => Pt(inv, sh.World(q))))
                   + "/" + string.Join("/", sh.Holes.Select(h => string.Join(";", h.Select(q => Pt(inv, sh.World(q))))));
        return it.Primitive switch
        {
            Em3dBox b => "box " + BoxCanon(inv, (b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z)),
            Em3dExtrudedPolygon e => "prism " + string.Join(";", e.Outline.Select(q => Pt(inv, new Point3(q.X, q.Y, e.ZBottom)) + "|" + Pt(inv, new Point3(q.X, q.Y, e.ZTop))))
                                     + "/" + string.Join("/", e.Holes.Select(h => string.Join(";", h.Select(q => Pt(inv, new Point3(q.X, q.Y, e.ZBottom)))))),
            Em3dCylinder c => "cyl " + Pt(inv, c.AxisStart) + ";" + Pt(inv, c.AxisEnd) + ";" + R(c.Radius),
            Em3dSphere s => "sph " + Pt(inv, s.Center) + ";" + R(s.Radius),
            Em3dTruncatedSphere t => "tsph " + Pt(inv, t.Center) + ";" + R(t.Radius) + ";" + Pt(inv, t.Center with { Z = t.ZMin }) + ";" + Pt(inv, t.Center with { Z = t.ZMax }),
            Em3dPolyhedron ph => "poly " + string.Join(";", ph.Vertices.Select(v => Pt(inv, v))) + "|" +
                                 string.Join(";", ph.Faces.Select(f => string.Join(",", f.Outer) + "/" + string.Join("/", f.Holes.Select(h => string.Join(",", h))))),
            Em3dSweep w => "sweep " + w.Section + ";" + string.Join("|", w.Rings.Select(r => string.Join(";", r.Select(q => Pt(inv, q))))),
            Em3dShapeSolid k => "shape " + k.Display.Vertices.Count + ";" +
                                string.Join("|", k.Faces.Select(f => f.Name + " " + f.Kind + " " + BoxCanon(inv, f.Box))),
            _ => "?",
        };
    }
}
