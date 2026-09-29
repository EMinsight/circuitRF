using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D.Fields;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf render cavity.c3d -o cut.png --field Field1</c> and <c>--list-fields</c> — a 3D view's field plot
/// (brief-em3d-83) drawn with no window (brief-em3d-84).
///
/// <para><b>This file draws nothing and does no field arithmetic</b>, on <see cref="Render"/>'s terms (cli.md §13.1). The plot
/// is resolved by <see cref="FieldPlotResolver"/> — the 3D view's and the 3D editor's own resolution, moved below the firewall
/// for this — the cut and its range are <see cref="FieldSection"/>'s, the colours and the thinning
/// <see cref="Em3dSectionField"/>'s, and every pixel <see cref="Em3dSectionRenderer"/>'s. What is here is the argument rule,
/// the refusals and the report.</para>
///
/// <para><b>A plot is drawn from ITS run or not at all.</b> A plot whose saved solution, setup or quantity is missing is a
/// refusal carrying R-em3d83-5's sentence verbatim: never the nearest frequency, never an outline passed off as the field.
/// A stale run still draws, as in the 3D view, and says so.</para>
/// </summary>
internal static class RenderEm3dField
{
    /// <summary>The environment variable a test lowers the vector thinning limit with (gate 10); unset, the measured
    /// <see cref="Em3dSectionField.VectorTriangleLimit"/>.</summary>
    internal const string ThinLimitVariable = "CRF_FIELD_THIN_LIMIT";

    // ── --list-fields ────────────────────────────────────────────────────────

    public static int List(string path, RenderEm3d.Request req)
    {
        string? alone = req.OutputStated ? "-o" : req.Field is not null ? "--field" : req.Sections.Count > 0 ? "--section"
                      : req.Iso ? "--iso" : req.Phase is not null ? "--phase" : req.NoLegend ? "--no-legend" : req.NoThin ? "--no-thin" : null;
        if (alone is not null) return JsonRun.Fail(CliDiagnostics.RenderFieldListAlone(alone));

        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(path, ex.Message)); }

        string root = ResultsRoot.For(full, DocumentKinds.AncestorCws(full));
        var sources = new Dictionary<string, Em3dSetupSource>(StringComparer.Ordinal);
        var list = new List<RenderFieldPlotJson>();
        foreach (var plot in doc.FieldPlots)
        {
            RunHost.Cancellation.ThrowIfCancellationRequested();
            var (setupName, run, setupProblem) = Setup(plot, doc, full);
            Em3dProblem? problem = null;
            if (run is not null)
            {
                if (!sources.TryGetValue(setupName, out var src)) sources[setupName] = src = Em3dSetupSource.ForThreeDView(full, setupName);
                problem = src.Generated?.Problem;
            }
            var request = FieldPlotResolver.Request(plot, setupName, run, setupProblem, run);
            string? why = setupProblem;
            if (why is null)
            {
                var found = FieldPlotResolver.Discover(run, root, problem);
                var item = FieldPlotResolver.PickSolution(request.Solution, request.Solver, found.Items, problem);
                why = item is null ? Missing(request, found, problem) : QuantityProblem(item, request);
            }
            list.Add(new RenderFieldPlotJson(
                plot.Name, setupName, request.Solver, SolutionJson(plot.Solution), plot.Solution?.Describe() ?? "the first saved solution",
                plot.Quantity, plot.Mode, plot.On.ToString(), Target(plot, doc), plot.Hidden, why is null, why, NotHeadless(plot)?.Render()));
        }
        JsonRun.FieldPlots = list;

        if (list.Count == 0) Console.WriteLine($"{Path.GetFileName(full)} has no field plots.");
        foreach (var p in list)
        {
            var plot = doc.FieldPlots.First(x => x.Name == p.Name);
            Console.WriteLine($"{p.Name}: {FieldPlotResolver.QuantitySymbol(plot)} · {p.Describe} · {p.Target} · setup '{p.Setup}'" +
                              (p.Hidden ? " · hidden" : ""));
            Console.WriteLine(p.Problem is { } why ? $"  data: missing — {why}" : "  data: ready");
            if (p.Headless is { } h) Console.WriteLine($"  --field: {h}");
        }
        return 0;
    }

    // ── --field ──────────────────────────────────────────────────────────────

    public static int Draw(string path, RenderEm3d.Request req)
    {
        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(path, ex.Message)); }

        // The plot, by exact name — an unknown one is a refusal LISTING them, as --view lists views.
        var plot = doc.FieldPlots.FirstOrDefault(p => p.Name == req.Field);
        if (plot is null)
            return JsonRun.Fail(CliDiagnostics.RenderFieldUnknown(req.Field!, path,
                doc.FieldPlots.Count == 0 ? "(none)" : string.Join(", ", doc.FieldPlots.Select(p => p.Name))));
        if (NotHeadless(plot) is { } notYet) return JsonRun.Fail(notYet);

        // The view follows the plot: a ClipPlane plot IS a section. Another picture is a refusal, never a re-cut.
        var own = OwnView(plot, doc);
        string plane = $"{own.Axis} = {Em3dSectionScene.FormatLength(own.At)}";
        var asked = req.Sections.Select(s => $"--section {s}").ToList();
        if (req.Iso) asked.Add("--iso");
        if (asked.Count > 1) return JsonRun.Fail(CliDiagnostics.RenderEm3dMultipleViews(string.Join(" and ", asked)));
        if (req.Iso) return JsonRun.Fail(CliDiagnostics.RenderFieldViewDisagrees(plot.Name, plane, "--iso"));
        if (req.Sections.Count == 1)
        {
            var (parsed, refusal) = RenderEm3d.ParseSection(req.Sections[0]);
            if (refusal is { } r) return r;
            if (parsed.Kind != own.Kind || Math.Abs(parsed.At - own.At) > 1e-12 + 1e-9 * Math.Abs(own.At))
                return JsonRun.Fail(CliDiagnostics.RenderFieldViewDisagrees(plot.Name, plane, $"--section {req.Sections[0]}"));
        }

        // Its setup, its run, its solution by value, its quantity — or R-em3d83-5's sentence.
        var (setupName, run, setupProblem) = Setup(plot, doc, full);
        if (setupProblem is { } noSetup) return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, noSetup));
        var loaded = Em3dSetupSource.ForThreeDView(full, setupName);
        if (loaded.Refusal is { } unbuildable) return JsonRun.Fail(CliDiagnostics.RenderEm3dUnbuildable(path, unbuildable));
        var problem = loaded.Generated!.Problem!;
        RunHost.Cancellation.ThrowIfCancellationRequested();

        string root = ResultsRoot.For(full, DocumentKinds.AncestorCws(full));
        var request = FieldPlotResolver.Request(plot, setupName, run, null, run);
        var found = FieldPlotResolver.Discover(run, root, problem);
        var item = FieldPlotResolver.PickSolution(request.Solution, request.Solver, found.Items, problem);
        if (item is null) return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, Missing(request, found, problem)));
        string? runDir = found.DirectoryOf(item);
        if (runDir is not null) Console.Error.WriteLine($"[circuitRF] fields: {runDir}");

        FieldStep? volume, boundary;
        try
        {
            volume = item.Solution.VolumePvtu is { } v ? FieldStep.Open(v, item.Run.ToMetres) : null;
            boundary = item.Solution.BoundaryPvtu is { } b ? FieldStep.Open(b, item.Run.ToMetres) : null;
        }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
        { return JsonRun.Fail(CliDiagnostics.RenderFieldUnreadable(plot.Name, e.Message)); }
        var offered = FieldQuantity.Offered(volume?.Arrays ?? [], boundary?.Arrays ?? []);
        if (FieldPlotResolver.PickQuantity(offered, request, out string? gone) is not { } q)
            return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, gone!));
        if (q.OnBoundary || volume is null) return JsonRun.Fail(CliDiagnostics.RenderFieldOnBoundary(plot.Name, q.Label));
        if (req.Phase is not null && !q.Animated) return JsonRun.Fail(CliDiagnostics.RenderFieldPhaseNotAnimated(plot.Name, q.Label));

        // A stale run still draws (as in the 3D view), and says so: the one comparison the editor's banner makes.
        bool stale = false;
        if (runDir is not null && C3dRunDocument.Check(runDir, doc) is { Stale: true } st)
        {
            stale = true;
            var d = CliDiagnostics.RenderFieldStale(plot.Name, st.Written.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }

        // The cut at the 3D view's own origin, on the plane as the view holds it — the same triangles the window draws.
        var origin = FieldPlotResolver.SceneOrigin(loaded.Elaboration?.DisplayExtent());
        var clip = FieldPlotResolver.ScenePlane(plot, doc.DbuPerMicron, origin);
        FieldSectionCut? cut;
        try { cut = FieldSection.Cut(q, volume, origin, clip, request.Db, request.Percentile, RunHost.Cancellation); }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
        { return JsonRun.Fail(CliDiagnostics.RenderFieldUnreadable(plot.Name, e.Message)); }
        if (cut is null) return JsonRun.Fail(CliDiagnostics.RenderFieldMissingData(plot.Name, gone ?? $"The run no longer offers {FieldNames.Friendly(q.Array.Name)}."));

        double phaseDeg = q.Animated ? req.Phase ?? 0 : 0;
        var legend = req.NoLegend ? [] : FieldPlotResolver.LegendLines(plot.Name, q, cut.Scale, item.Label, phaseDeg, loopSeconds: null);
        int? thin = req.NoThin ? null : ThinLimit();

        Em3dFieldLayer Layer() => Em3dSectionField.Build(cut, phaseDeg * Math.PI / 180, raster: req.Format == "png", thin, legend);
        RenderFieldJson Report(Em3dFieldLayer l) => new(
            plot.Name, setupName, request.Solver, SolutionJson(plot.Solution), item.Label, q.Array.Name, q.Mode.ToString(), plot.On.ToString(),
            l.Triangles, l.TrianglesDrawn,
            new RenderFieldRangeJson(cut.Scale.Lo, cut.Scale.Hi, cut.Scale.Unit, cut.Scale.Db, cut.Scale.Percentile),
            stale, runDir, q.Animated ? phaseDeg : null);
        string Line(Em3dFieldLayer l)
        {
            string G(double x) => x.ToString("G4", CultureInfo.InvariantCulture);
            string unit = cut.Scale.Db ? $"dB{(cut.Scale.Unit.Length > 0 ? " re 1 " + cut.Scale.Unit : "")}" : cut.Scale.Unit;
            return $"{plot.Name}: {q.Symbol} at {item.Label}, {l.Triangles:N0} triangles" +
                   (l.TrianglesDrawn != l.Triangles ? $", drawn as {l.TrianglesDrawn:N0}" : "") +
                   $", {G(cut.Scale.Lo)} … {G(cut.Scale.Hi)}{(unit.Length > 0 ? " " + unit : "")}" +
                   (q.Animated ? $", φ = {phaseDeg.ToString("0.##", CultureInfo.InvariantCulture)}°" : "") +
                   (stale ? " (the model has changed since this run)" : "");
        }
        return RenderEm3d.Picture(path, req, loaded, own, (Layer, Report, Line));
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The setup a plot reads: the one it pins, or — a plot pinning none (its Setup left unset) — the document's only setup,
    /// which is what the 3D view has active on opening it. The name, the setup as its run is named, or the sentence saying
    /// there is none.
    /// </summary>
    private static (string Name, EmSetup? Run, string? Problem) Setup(C3dFieldPlot plot, C3dDocument doc, string full)
    {
        string? name = plot.Setup;
        if (name is null)
        {
            var all = C3dSetups.Read(doc);
            if (all.Count != 1) return ("", null, FieldPlotResolver.NoActiveSetup);
            name = all[0].Name;
        }
        var (run, problem) = FieldPlotResolver.ResolveSetup(doc, full, name);
        return (name, run, problem);
    }

    /// <summary>R-em3d83-5 — why a plot's run lacks its solution (or its files could not be read at all).</summary>
    private static string Missing(FieldPlotRequest request, FieldDiscovery found, Em3dProblem? problem)
        => found.Items.Count == 0 && found.Why is { } unreadable
            ? "The fields could not be read: " + unreadable
            : FieldPlotResolver.PlotProblem(request, found.Items, found.Ran, problem) ?? "The run holds no solution this plot can show.";

    /// <summary>A step that no longer offers the plot's quantity (the second half of R-em3d83-5), or null.</summary>
    private static string? QuantityProblem(FieldSolutionItem item, FieldPlotRequest request)
    {
        try
        {
            var vol = item.Solution.VolumePvtu is { } v ? FieldStep.Open(v, item.Run.ToMetres) : null;
            var bnd = item.Solution.BoundaryPvtu is { } b ? FieldStep.Open(b, item.Run.ToMetres) : null;
            FieldPlotResolver.PickQuantity(FieldQuantity.Offered(vol?.Arrays ?? [], bnd?.Arrays ?? []), request, out string? why);
            return why;
        }
        catch (Exception e) when (e is FieldReadException or IOException or UnauthorizedAccessException)
        { return "The fields could not be read: " + e.Message; }
    }

    /// <summary>Why <c>--field</c> will not draw <paramref name="plot"/> yet (owner decision Q1), or null.</summary>
    private static CircuitRF.Diagnostics.Diagnostic? NotHeadless(C3dFieldPlot plot)
    {
        if (plot.On == C3dFieldPlotOn.Surfaces) return CliDiagnostics.RenderFieldNotHeadless(plot.Name, plot.IsTemperature ? "every exposed face" : "surfaces");
        if (plot.On == C3dFieldPlotOn.Faces) return CliDiagnostics.RenderFieldNotHeadless(plot.Name, $"{plot.Faces.Count} face{(plot.Faces.Count == 1 ? "" : "s")}");
        if (plot.IsTemperature) return CliDiagnostics.RenderFieldTemperature(plot.Name);
        return null;
    }

    /// <summary>A ClipPlane plot's own section: its axis at its offset, world metres.</summary>
    private static Em3dView OwnView(C3dFieldPlot plot, C3dDocument doc)
        => new(plot.Axis switch { C3dAxis.X => Em3dViewKind.SectionX, C3dAxis.Y => Em3dViewKind.SectionY, _ => Em3dViewKind.SectionZ },
               FieldPlotResolver.PlaneMetres(plot, doc.DbuPerMicron));

    private static string Target(C3dFieldPlot plot, C3dDocument doc) => plot.On switch
    {
        C3dFieldPlotOn.ClipPlane => $"clip {(plot.Axis ?? C3dAxis.Z).ToString().ToLowerInvariant()} = " +
                                    Em3dSectionScene.FormatLength(FieldPlotResolver.PlaneMetres(plot, doc.DbuPerMicron)),
        C3dFieldPlotOn.Surfaces => plot.IsTemperature ? "all faces" : "surfaces",
        _ => string.Join(", ", plot.Faces.Select(f => f.Face)),
    };

    private static RenderFieldSolutionJson? SolutionJson(C3dFieldSolution? s)
        => s is null ? null : new RenderFieldSolutionJson(s.GHz, s.Port, s.Mode, s.Terminal, s.Point);

    private static int ThinLimit()
        => int.TryParse(Environment.GetEnvironmentVariable(ThinLimitVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0
            ? n : Em3dSectionField.VectorTriangleLimit;
}
