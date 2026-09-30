using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Theming;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using RfCore.Export;
using SkiaSharp;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf render amp.cem -o top.svg --section z=35um</c> — a 3D EM setup as a section or an
/// isometric outline, with no solver installed and no window (brief-em3d-5 R-em3d5-2).
///
/// <para><b>This file draws nothing</b>, on <see cref="Render"/>'s terms: the cut is
/// <c>Em3dSectionScene</c>'s and every pixel is <c>Em3dSectionRenderer</c>'s, both in
/// <c>CircuitRF.Render</c>, written onto the same SVG/PDF/PNG surfaces every other picture goes
/// through (<see cref="VectorPage"/>). What is here is the argument rule, the refusals and the
/// report.</para>
///
/// <para><b>It starts no process</b> (R-em3d5-3d): the problem is generated in this process from the
/// documents, and nothing is meshed.</para>
/// </summary>
internal static class RenderEm3d
{
    /// <summary>What <see cref="Render"/> hands over: the options a 3D picture reads, the first one
    /// typed that it does not, and the verb's own theme resolution and encoder, so neither is
    /// repeated here.</summary>
    internal sealed record Request(
        string Output, string Format, IReadOnlyList<string> Sections, bool Iso, string? Inapplicable,
        int Width, int Height, double Scale, double Margin, bool Transparent, ColorVariant Variant,
        Func<string, (ColorTheme Theme, string Name, string From, int? Refusal)> themeOf,
        Func<int, int, Action<SKCanvas>, byte[]> emit)
    {
        // brief-em3d-84 — a .c3d's field plot.
        public string? Field { get; init; }
        public bool ListFields { get; init; }
        public double? Phase { get; init; }
        public bool NoLegend { get; init; }
        public bool NoThin { get; init; }
        /// <summary>brief-em3d-88 Q2 — each solid labelled with its material.</summary>
        public bool Labels { get; init; }
        /// <summary>brief-em3d-88 Q2 — the page is the section alone, its height following the section's aspect.</summary>
        public bool Tight { get; init; }
        /// <summary>brief-em3d-88 — the 3D view's axis indicator, bottom left of the frame.</summary>
        public bool Axes { get; init; }
        /// <summary>brief-em3d-88 — the 3D view's scale bar, bottom right of the frame (a section only).</summary>
        public bool ScaleBar { get; init; }
        /// <summary>brief-em3d-89 — a Surfaces or Faces plot's direction: a standard view's name, or x,y,z toward the viewer.</summary>
        public string? ViewDir { get; init; }
        /// <summary>brief-em3d-89 — the region a Surfaces plot of a volume quantity is drawn on (the 3D view's selection).</summary>
        public string? Region { get; init; }
        /// <summary>brief-em3d-89 — draw the modelled half only, not its reflections across the symmetry planes.</summary>
        public bool NoMirror { get; init; }
        /// <summary>Whether -o was typed (--list-fields takes none).</summary>
        public bool OutputStated { get; init; } = true;
        /// <summary>brief-em3d-92 D5 — <c>name=percent</c> overrides of a .c3d's transparency, applied to the copy this run reads.</summary>
        public IReadOnlyList<string> Transparency { get; init; } = [];
    }

    public static int Draw(string path, Request req)
    {
        if (req.Inapplicable is { } option) return JsonRun.Fail(CliDiagnostics.RenderEm3dNotApplicable(option));

        // brief-em3d-84 — a field plot is a .c3d's record: a .cem has none, and every field option says which it is.
        string? fieldOption = req.Field is not null ? "--field" : req.ListFields ? "--list-fields" : req.Phase is not null ? "--phase"
                            : req.NoLegend ? "--no-legend" : req.NoThin ? "--no-thin" : req.Labels ? "--labels" : req.Tight ? "--tight"
                            : req.ViewDir is not null ? "--view-dir" : req.Region is not null ? "--region" : req.NoMirror ? "--no-mirror" : null;
        if (fieldOption is not null && DocumentKinds.Classify(Path.GetFullPath(path)) != DocumentKind.ThreeD)
            return JsonRun.Fail(CliDiagnostics.RenderFieldOnCem(fieldOption, path));
        // brief-em3d-92 D5 — a .c3d's objects only, and every entry parsed and range-checked before the file is read.
        if (req.Transparency.Count > 0 && DocumentKinds.Classify(Path.GetFullPath(path)) != DocumentKind.ThreeD)
            return JsonRun.Fail(CliDiagnostics.RenderTransparencyNotA3dView(path, DocumentKinds.Name(DocumentKinds.Classify(Path.GetFullPath(path)))));
        if (RenderTransparency.Parse(req.Transparency, out _) is { } badTransparency) return badTransparency;
        if (req.ListFields) return RenderEm3dField.List(path, req);
        if (req.Field is not null) return RenderEm3dField.Draw(path, req);
        if (fieldOption is not null) return JsonRun.Fail(CliDiagnostics.RenderFieldOptionNeedsField(fieldOption));

        // R-em3d5-2b: exactly one view, refused together rather than ordered — and decided before the
        // file is read, so two incompatible questions get the same answer whether or not it parses.
        var asked = req.Sections.Select(s => $"--section {s}").ToList();
        if (req.Iso) asked.Add("--iso");
        if (asked.Count > 1) return JsonRun.Fail(CliDiagnostics.RenderEm3dMultipleViews(string.Join(" and ", asked)));

        Em3dView? view = null;
        if (req.Sections.Count == 1)
        {
            var (parsed, refusal) = ParseSection(req.Sections[0]);
            if (refusal is { } r) return r;
            view = parsed;
        }
        else if (req.Iso) view = Em3dView.Iso;
        if (req.ScaleBar && req.Iso) return JsonRun.Fail(CliDiagnostics.RenderEm3dScaleBarIso());

        Em3dSetupSource loaded;
        var overrides = new RenderTransparency(path, req.Transparency);
        try { loaded = Em3dSetupSource.Load(path, overrides.Apply); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(path, ex.Message)); }
        if (overrides.Refusal is { } unknown) return unknown;

        // R-em3d5-2a: a planar setup's picture is its layout, so the refusal names it.
        if (!loaded.Setup.Is3D)
            return JsonRun.Fail(CliDiagnostics.RenderEm3dPlanar(
                path, loaded.Resolution.LayoutPath ?? loaded.Setup.LayoutRef));
        if (view is null) return JsonRun.Fail(CliDiagnostics.RenderEm3dViewRequired(path));
        if (loaded.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.RenderEm3dUnbuildable(path, why));

        RunHost.Cancellation.ThrowIfCancellationRequested();
        return Picture(path, req, loaded, view.Value, field: null);
    }

    /// <summary>
    /// The picture of <paramref name="loaded"/>'s problem in <paramref name="view"/> — with <paramref name="field"/>'s slice
    /// under it when one is given (brief-em3d-84) — written to <c>-o</c> and reported. The one write-and-report path for a 3D
    /// picture, field or not.
    /// </summary>
    /// <para>brief-em3d-89 — <paramref name="prebuilt"/> is a surface plot's own scene: a projection framed on what it draws,
    /// which <see cref="Em3dSectionScene.Build"/> is never asked for.</para>
    internal static int Picture(string path, Request req, Em3dSetupSource loaded, Em3dView view,
                                (Func<Em3dScene, Em3dFieldLayer> Layer, Func<Em3dFieldLayer, RenderFieldJson> Report, Func<Em3dFieldLayer, string> Line)? field,
                                Em3dScene? prebuilt = null)
    {
        var generated = loaded.Generated!;
        var problem = generated.Problem!;

        int structural = problem.Validate().Count;
        if (structural > 0)
        {
            var d = CliDiagnostics.RenderEm3dProblems(path, structural);
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }

        var (theme, themeName, themeFrom, themeRefusal) = req.themeOf(loaded.Path);
        if (themeRefusal is { } tr) return tr;

        RunHost.Control?.BeginStage("draw");
        Console.Error.WriteLine("[circuitRF] draw...");

        var scene = prebuilt ?? Em3dSectionScene.Build(problem, view);
        var style = new Em3dRenderStyle(
            // A .c3d's objects name its own technology's materials (the elaboration's, as the 3D view colours them).
            Em3dSectionRenderer.ObjectColours(problem, generated.Origins, loaded.Elaboration?.Technology ?? loaded.Resolution.Source?.Technology,
                                              theme, req.Variant),
            theme, req.Variant, req.Margin, req.Transparent)
        {
            Labels = req.Labels, Tight = req.Tight, Axes = req.Axes,
            // brief-em3d-92 — each object's transparency, as the 3D view paints it (a section's fills; an outline fills nothing)
            ObjectTransparency = loaded.Elaboration is { } elaborated ? CircuitRF.Render.Scene3D.Scene3DTransparency.MapOf(elaborated.Provenance) : null,
            // brief-em3d-88 — the bar is rounded and labelled in the document's display unit, as the 3D view's is
            ScaleBar = req.ScaleBar ? DisplayUnit(path) : null,
        };
        var layer = field?.Layer(scene);

        int pxW = (int)Math.Round(req.Width  * req.Scale);
        int pxH = (int)Math.Round(req.Height * req.Scale);
        // brief-em3d-88 Q2 — --tight crops the page to the section: the width stays, the height follows the frame's aspect.
        if (req.Tight)
        {
            double fw = Math.Max(scene.FrameMax.U - scene.FrameMin.U, 1e-30), fh = Math.Max(scene.FrameMax.V - scene.FrameMin.V, 1e-30);
            pxH = (int)Math.Clamp(Math.Round(pxW * fh / fw), 16, 16 * pxW);
        }
        byte[] bytes = req.emit(pxW, pxH, canvas => Em3dSectionRenderer.Draw(canvas, pxW, pxH, scene, style, layer));

        // ── write and report ─────────────────────────────────────────────────
        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("encode");
        Console.Error.WriteLine("[circuitRF] encode...");

        if (bytes.Length == 0)
            return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, "the encoder produced no bytes"));
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(req.Output));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllBytes(req.Output, bytes);
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, ex.Message)); }

        var page = Em3dSectionRenderer.Layout(pxW, pxH, scene, req.Margin, req.Tight);
        var (boxLo, boxHi) = Projected(problem.Boundary, view);
        string unitKind = req.Format == "png" ? "device-pixels" : "points";
        bool isIso = view.Kind == Em3dViewKind.Iso;
        string kind = view.Kind == Em3dViewKind.Projection ? "projection" : isIso ? "iso" : "section";

        JsonRun.AddOutput(req.Format, req.Output);
        JsonRun.Render = new RenderReportJson(
            path, DocumentKinds.Name(DocumentKind.EmSetup), View: null, req.Format,
            new RenderViewportJson(kind,
                                   scene.FrameMin.U, scene.FrameMin.V, scene.FrameMax.U, scene.FrameMax.V,
                                   "m", 1.0, page.Scale, Letterboxed: false),
            new RenderExtentsJson(boxLo.U, boxLo.V, boxHi.U, boxHi.V, "m", 1.0),
            new RenderSizeJson(pxW, pxH, unitKind, req.Scale),
            new RenderThemeJson(themeName, req.Variant == ColorVariant.Dark ? "dark" : "light", themeFrom),
            Layers: null, Detail: null, Counters: null, bytes.Length,
            Em3d: new RenderEm3dJson(kind, view.Projection?.Name ?? view.Plane, view.Axis,
                                     isIso || view.Projection is not null ? null : scene.At, "m", 1.0,
                                     [.. layer?.Surface?.Objects ?? scene.Objects],
                                     layer?.Thermal is not null || layer?.Surface is not null ? [] : [.. scene.Ports.Select(p => p.Number)],
                                     layer is null ? null : field!.Value.Report(layer))
            {
                Toward = view.Projection is { } pr ? [pr.Toward.X, pr.Toward.Y, pr.Toward.Z] : null,
            });

        Console.WriteLine($"Wrote {req.Output} ({pxW}x{pxH} {unitKind}, {bytes.Length:N0} bytes)");
        if (layer?.Surface is { } surface) Console.WriteLine($"  {surface.Title}: {surface.Objects.Count} object(s)");
        else Console.WriteLine($"  {Em3dSectionRenderer.Title(scene)}: {scene.Objects.Count()} object(s)" +
                               (layer?.Thermal is not null ? "" : $", {scene.Ports.Count} port(s)"));
        if (layer is not null) Console.WriteLine("  " + field!.Value.Line(layer));
        return 0;
    }

    /// <summary>A 3D view's display unit and DBU (what its 3D view labels lengths in); a <c>.cem</c>'s picture is in µm.</summary>
    private static (LayoutUnit Unit, int DbuPerMicron) DisplayUnit(string path)
    {
        try
        {
            if (DocumentKinds.Classify(Path.GetFullPath(path)) == DocumentKind.ThreeD &&
                CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(Path.GetFullPath(path)) is { } doc)
                return (doc.DisplayUnit, doc.DbuPerMicron);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return (LayoutUnit.Um, 1000);
    }

    /// <summary>
    /// <c>z=35um</c>, <c>xz@y=1.2mm</c> or <c>yz@x=…</c>. <b>The length carries an SI unit and a bare
    /// number is a refusal</b> (R-em3d5-2b): nanometres, micrometres and millimetres are three
    /// plausible planes on identical text, exactly as on a layout.
    /// </summary>
    internal static (Em3dView View, int? Refusal) ParseSection(string text)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0) return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dSectionMalformed(text)));

        Em3dViewKind? kind = text[..eq].Trim().ToLowerInvariant() switch
        {
            "z"    => Em3dViewKind.SectionZ,
            "xz@y" => Em3dViewKind.SectionY,
            "yz@x" => Em3dViewKind.SectionX,
            _      => null,
        };
        if (kind is null) return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dSectionMalformed(text)));

        string value = text[(eq + 1)..].Trim();
        if (value.Length == 0 || !char.IsLetter(value[^1]))
            return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dUnitRequired(
                text, $"'{text[..eq]}={value}um' or '{text[..eq]}={value}mm'")));

        // Picometre resolution: the parse is exact decimal arithmetic into an integer, and a
        // picometre is far below anything a section can resolve.
        const int PicometresPerMicron = 1_000_000;
        if (!LayoutUnits.TryParse(value, LayoutUnit.Um, PicometresPerMicron, out long pm))
            return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dSectionMalformed(text)));
        return (new Em3dView(kind.Value, pm * 1e-12), null);
    }

    /// <summary>The air box in the picture's own plane.</summary>
    private static (Uv Lo, Uv Hi) Projected(Em3dAirBox box, Em3dView view)
    {
        switch (view.Kind)
        {
            case Em3dViewKind.SectionZ: return (new Uv(box.Min.X, box.Min.Y), new Uv(box.Max.X, box.Max.Y));
            case Em3dViewKind.SectionY: return (new Uv(box.Min.X, box.Min.Z), new Uv(box.Max.X, box.Max.Z));
            case Em3dViewKind.SectionX: return (new Uv(box.Min.Y, box.Min.Z), new Uv(box.Max.Y, box.Max.Z));
        }
        Func<Point3, Uv> project = view.Projection is { } pr ? pr.Project : Em3dSectionScene.Project;
        double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
        for (int k = 0; k < 8; k++)
        {
            var q = project(new Point3((k & 1) == 0 ? box.Min.X : box.Max.X,
                                                        (k & 2) == 0 ? box.Min.Y : box.Max.Y,
                                                        (k & 4) == 0 ? box.Min.Z : box.Max.Z));
            u0 = Math.Min(u0, q.U); v0 = Math.Min(v0, q.V); u1 = Math.Max(u1, q.U); v1 = Math.Max(v1, q.V);
        }
        return (new Uv(u0, v0), new Uv(u1, v1));
    }
}
