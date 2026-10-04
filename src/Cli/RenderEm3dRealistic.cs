using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Theming;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// brief-em3d-110 — <c>circuitrf render pkg.c3d -o shot.png --look realistic</c>: the 3D view's realistic picture, with no window and no
/// GPU. What the GUI's Export Picture makes in the realistic view, this makes in a batch.
///
/// <para><b>This file draws nothing</b>, on <see cref="Render"/>'s terms (cli.md §13.1). The scene is <see cref="Scene3DBuilder"/>'s,
/// the frame's plan <see cref="Scene3DFramePlan"/>'s (the object the GPU backends read), every pixel <see cref="RealisticPicture"/>'s —
/// the CPU mirror of the shader, shading with Pbr.cs — and the legend, caption and indicator <see cref="FieldPicture"/>'s, as Export
/// Picture paints them. What is here is the argument rule, the Look's overrides, the camera, the refusals and the report.</para>
///
/// <para><b>The format is the contract</b> (R-em3d110-1b): the picture is drawn with the <c>.c3d</c>'s own Look, and
/// <c>--look-set Key=value</c> overrides one key of it on this run's copy, read by the file's reader and held to the file's validation —
/// so a new Look key needs no new flag, and a value the file could not hold is refused in the words <c>check</c> uses.</para>
/// </summary>
internal static class RenderEm3dRealistic
{
    /// <summary>The Look a run draws with — the file's, each <c>--look-set</c> applied — and the document it belongs to.</summary>
    internal sealed record LookRead(C3dLook Look, int DbuPerMicron, string DocumentPath, IReadOnlyList<string> Overrides);

    /// <summary>A field drawn on the picture: the 3D view's field layer for it, and what the report and the picture say of it.</summary>
    internal sealed record FieldPart(Scene3DFieldGeometry Geometry, IReadOnlyCollection<string> Covered, FieldQuantity Quantity, FieldColorScale Scale,
                                     double PhaseRad, IReadOnlyList<string> Legend, string? Caption, RenderFieldJson Report, string Line);

    // ── refusals ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>R-em3d110-1f — what a realistic picture cannot be, refused before anything is read: not a 3D view (a <c>.cem</c> has no
    /// appearances), not vector (it is pixels), and none of the plain picture's section and outline options.</summary>
    public static int? Refusal(string path, RenderEm3d.Request req)
    {
        var kind = DocumentKinds.Classify(Path.GetFullPath(path));
        if (kind != DocumentKind.ThreeD) return JsonRun.Fail(CliDiagnostics.RenderLookNotA3dView(path, DocumentKinds.Name(kind)));
        if (req.Format != "png") return JsonRun.Fail(CliDiagnostics.RenderLookNotPng(req.Format.ToUpperInvariant()));
        const string Whole = "a realistic picture is a view of the whole model, taken from --iso, --view-dir or the Look's Camera.";
        const string Chrome = "the realistic view hides the CAD chrome, and its picture is the scene as it looks.";
        string? option = req.Sections.Count > 0 ? "--section" : req.Tight ? "--tight" : req.Labels ? "--labels" : req.Axes ? "--axes"
                       : req.ScaleBar ? "--scale-bar" : null;
        if (option is not null) return JsonRun.Fail(CliDiagnostics.RenderLookOptionNotApplicable(option, option is "--section" or "--tight" ? Whole : Chrome));
        return null;
    }

    // ── the Look ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d110-1b — the document's Look with each <c>--look-set Key=value</c> applied in order: the key is one of
    /// <see cref="C3dLook.Keys"/> (spelled as the file spells it, case aside); the value is read as the file's JSON reads it (a number, a
    /// boolean, an object such as a Camera, <c>null</c> for the default — anything else is a string) by the file's own reader, then held to
    /// the file's own Look validation (<see cref="C3dValidation.LookFaults"/>). An error that override brought is the refusal.
    /// </summary>
    public static int? ReadLook(string path, RenderEm3d.Request req, out LookRead? read)
    {
        read = null;
        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(path, ex.Message)); }
        var look = doc.Look?.Clone() ?? new C3dLook();
        var overrides = new List<string>();
        foreach (string entry in req.LookSet)
        {
            int eq = entry.IndexOf('=');
            string key = eq > 0 ? entry[..eq].Trim() : "";
            if (key.Length == 0) return JsonRun.Fail(CliDiagnostics.RenderLookSetMalformed(entry));
            string? canonical = C3dLook.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) return JsonRun.Fail(CliDiagnostics.RenderLookSetUnknownKey(key, string.Join(", ", C3dLook.Keys)));
            string text = entry[(eq + 1)..].Trim();
            JsonNode? value;
            try { value = JsonNode.Parse(text); }
            catch (JsonException) { value = JsonValue.Create(text); }       // a bare word (Lit, #ffffff, Studio) is a string
            var before = Errors(look, full);
            var json = JsonNode.Parse(C3dPersistence.SerializeLook(look))!.AsObject();
            json[canonical] = value;
            try { look = C3dPersistence.DeserializeLook(json.ToJsonString()) ?? new C3dLook(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            { return JsonRun.Fail(CliDiagnostics.RenderLookSetUnreadable(entry, e.Message)); }
            if (Errors(look, full).FirstOrDefault(d => !before.Contains(d)) is { } brought)
                return JsonRun.Fail(CliDiagnostics.RenderLookSetInvalid(entry, brought));
            overrides.Add($"{canonical}={text}");
        }
        read = new LookRead(look, doc.DbuPerMicron, full, overrides);
        return null;

        static List<string> Errors(C3dLook look, string full)
            => [.. C3dValidation.LookFaults(look, full).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Render())];
    }

    /// <summary>
    /// R-em3d110-1d — the camera's direction: <c>--iso</c> or <c>--view-dir</c> (brief 89's spellings, orthographic), else null for the
    /// Look's own Camera (overview D17: perspective included), else the refusal naming all three.
    /// </summary>
    public static int? Direction(string path, RenderEm3d.Request req, LookRead read, out Em3dProjection? direction)
    {
        direction = null;
        var asked = new List<string>();
        if (req.Iso) asked.Add("--iso");
        if (req.ViewDir is { } vd) asked.Add($"--view-dir {vd}");
        if (asked.Count > 1) return JsonRun.Fail(CliDiagnostics.RenderEm3dMultipleViews(string.Join(" and ", asked)));
        if (asked.Count == 1)
        {
            var (projection, bad) = RenderEm3dField.Direction(req.ViewDir, req.Iso);
            if (bad is { } b) return b;
            direction = projection;
            return null;
        }
        if (read.Look.Camera is { } camera && camera.Faults().Count == 0) return null;
        return JsonRun.Fail(CliDiagnostics.RenderLookDirectionRequired(path));
    }

    // ── the picture without a field ────────────────────────────────────────────────────────────────────────────────────

    public static int Draw(string path, RenderEm3d.Request req)
    {
        if (ReadLook(path, req, out var read) is { } badLook) return badLook;
        if (Direction(path, req, read!, out var direction) is { } noCamera) return noCamera;
        if (RenderEm3d.Load(path, req, out var loaded) is { } notLoaded) return notLoaded;
        if (loaded.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.RenderEm3dUnbuildable(path, why));
        var (theme, _, _, themeRefusal) = req.themeOf(loaded.Path);
        if (themeRefusal is { } tr) return tr;
        RunHost.Cancellation.ThrowIfCancellationRequested();
        return Picture(path, req, read!, loaded, Scene(loaded, theme, req.Variant, airBox: true), direction, field: null);
    }

    /// <summary>
    /// The 3D view's scene of <paramref name="loaded"/>'s problem, built as `render --field` builds a surface plot's (brief 89): the
    /// document's origin, its face names, its transparency and its appearances — and, for a realistic picture, its air box, which the Look's
    /// ShowAirBox shows again. One builder call for both pictures.
    /// </summary>
    public static Scene3DModel Scene(Em3dSetupSource loaded, ColorTheme theme, ColorVariant variant, bool airBox)
    {
        var e = loaded.Elaboration;
        var origin = FieldPlotResolver.SceneOrigin(e?.DisplayExtent());
        return Scene3DBuilder.Build(loaded.Generated!.Problem!, 0, loaded.Generated.Origins, e?.Technology, theme, variant, null,
            new Scene3DBuildOptions(FaceNames: name => e is not null && e.Provenance.TryGetValue(name, out var p) ? p.FaceNames : null,
                                    DrawAirBox: airBox, Origin: origin, HideOutermostDielectric: false,
                                    Transparency: e is null ? null : Scene3DTransparency.Of(e.Provenance),
                                    Appearance: e is null ? null : CircuitRF.Design.ThreeD.Appearance.AppearanceOverride.Of(e.Provenance)));
    }

    // ── the picture ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="scene"/> drawn realistic from <paramref name="direction"/> (orthographic) or the Look's Camera, with
    /// <paramref name="field"/> on it when given, at <c>--size</c> × <c>--scale</c> device pixels, <c>--supersample</c> times that each way
    /// before it is brought down; the legend, the caption and the Lit/Blended indicator painted on as Export Picture paints them; written
    /// to <c>-o</c> and reported. A cancelled run throws before anything is written.
    /// </summary>
    public static int Picture(string path, RenderEm3d.Request req, LookRead read, Em3dSetupSource loaded, Scene3DModel scene,
                              Em3dProjection? direction, FieldPart? field)
    {
        int structural = loaded.Generated!.Problem!.Validate().Count;
        if (structural > 0)
        {
            var d = CliDiagnostics.RenderEm3dProblems(path, structural);
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }
        int w = Math.Max(1, (int)Math.Round(req.Width * req.Scale)), h = Math.Max(1, (int)Math.Round(req.Height * req.Scale));

        // the camera: the direction asked, fitted to the scene as the 3D view's Fit frames it — or the Look's own
        var camera = new Camera3D { FovY = Camera3D.DefaultFovY };
        if (direction is { } dir)
        {
            camera.Projection = Projection3D.Orthographic;
            var t = dir.Toward;
            camera.Yaw = Math.Abs(t.Z) > 1 - 1e-12 ? -MathF.PI / 2 : (float)Math.Atan2(t.Y, t.X);
            camera.Pitch = (float)Math.Asin(Math.Clamp(t.Z, -1, 1));
        }
        camera.FitBounds(scene.ContentMin, scene.ContentMax, w / (float)h);
        if (direction is null) camera.SetPictureCamera(read.Look.Camera!, read.DbuPerMicron, scene.ToLocal);

        // the environment, as the 3D view makes it (a user's .hdr relative to the .c3d, Studio when it cannot be read — and said)
        RunHost.Control?.BeginStage("environment");
        Console.Error.WriteLine("[circuitRF] environment...");
        var look = RealisticLook.From(read.Look);
        string? hdr = look.HdrPath is { } p ? C3dLook.ResolvePath(p, read.DocumentPath) : null;
        var env = EnvironmentPrefilter.For(look.Studio, hdr);
        if (env.Fallback is { } fellBack)
        {
            var d = CliDiagnostics.RenderLookEnvironmentFallback(fellBack);
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }
        if (look.Shows(Scene3DChrome.Images) && loaded.Elaboration is { } withImages && withImages.Images.Count + withImages.FaceImages.Count > 0)
        {
            var d = CliDiagnostics.RenderLookImagesNotDrawn(withImages.Images.Count + withImages.FaceImages.Count);
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }

        var view = new Viewer3DViewState
        {
            Camera = camera, Realistic = true, Look = look, Environment = env,
            Background = Viewer3DViewState.ThemeBackground(req.Variant == ColorVariant.Dark),
        };
        view.Adopt(scene, null);
        if (field is not null)
        {
            view.ShowField = true;
            view.FieldCovered = [.. scene.Objects.Select(o => field.Covered.Contains(o.Name))];
            FieldUniforms.Write(view.Field.AsSpan(0, FieldUniforms.Floats), field.Quantity, field.Scale, ColorMap3D.For(field.Quantity), field.PhaseRad);
        }
        RunHost.Cancellation.ThrowIfCancellationRequested();

        var shot = RealisticPicture.Take(scene, view, field?.Geometry, w, h, req.Supersample, req.Transparent, (float)req.Scale, RunHost.Control);

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("encode");
        Console.Error.WriteLine("[circuitRF] encode...");
        var legends = field is { Legend.Count: > 0 } f ? [new FieldPictureLegend(f.Legend, ColorMap3D.For(f.Quantity), f.Scale)] : (IReadOnlyList<FieldPictureLegend>)[];
        byte[] bytes = new FieldPictureShot(shot.Rgba, w, h, (float)req.Scale, legends, field?.Caption, req.Variant == ColorVariant.Dark)
        {
            Transparent = shot.Transparent, Supersample = shot.Supersample, Typeface = SkiaFonts.PlexRegular,
            // R-em3d109-4b — the one spelling of the Lit / Blended label, painted whatever the legend options say
            Indicator = field is null ? null : look.FieldIndicator,
        }.Png();
        if (bytes.Length == 0) return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, "the encoder produced no bytes"));
        try
        {
            string? dir2 = Path.GetDirectoryName(Path.GetFullPath(req.Output));
            if (dir2 is { Length: > 0 }) Directory.CreateDirectory(dir2);
            File.WriteAllBytes(req.Output, bytes);
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, ex.Message)); }

        // ── the report ───────────────────────────────────────────────────────────────────────────────────────────────
        string from = direction is not null ? (req.Iso ? "--iso" : "--view-dir") : "Look.Camera";
        string spelled = direction is { } d3 ? d3.Name : "the Look's Camera";
        var objects = scene.Objects.Where(o => view.IsDrawn(o.Id) && (Scene3DFramePlan.ChromeOfObject(scene, o) is not { } row || look.Shows(row)))
                                   .Select(o => o.Name).ToList();
        var back = camera.Back;
        JsonRun.AddOutput("png", req.Output);
        var (_, themeName, themeFrom, _) = req.themeOf(loaded.Path);
        JsonRun.Render = new RenderReportJson(
            path, DocumentKinds.Name(DocumentKind.ThreeD), View: null, "png", Viewport: null, Extents: null,
            new RenderSizeJson(w, h, "device-pixels", req.Scale),
            new RenderThemeJson(themeName, req.Variant == ColorVariant.Dark ? "dark" : "light", themeFrom),
            Layers: null, Detail: null, Counters: null, bytes.Length,
            Em3d: new RenderEm3dJson("realistic", spelled, null, null, "m", 1.0, objects, [], field?.Report)
            {
                Toward = [back.X, back.Y, back.Z],
                Look = new RenderLookJson(env.Label, env.Fallback, look.ExposureEv, look.FieldStyle.ToString(), shot.Supersample, from,
                                          camera.Projection == Projection3D.Orthographic ? "orthographic" : "perspective", read.Overrides,
                                          shot.Counters.SamplesShaded, shot.Counters.ShadowTexelsWritten, shot.Counters.OcclusionPixels),
            });

        Console.WriteLine($"Wrote {req.Output} ({w}x{h} device-pixels, {bytes.Length:N0} bytes)");
        Console.WriteLine($"  Realistic · {env.Label} · {C3dLook.FormatEv(look.ExposureEv)} EV, {spelled}" +
                          $"{(camera.Projection == Projection3D.Orthographic ? ", orthographic" : ", perspective")}, " +
                          $"{shot.Supersample}× supersampled: {objects.Count} object(s)" +
                          (read.Overrides.Count > 0 ? $"; --look-set {string.Join(" ", read.Overrides)}" : ""));
        if (field is not null) Console.WriteLine("  " + field.Line);
        return 0;
    }
}
