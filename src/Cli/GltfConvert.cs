using System.Globalization;
using CircuitRF.Render;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Export;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Cli;

/// <summary>
/// brief-em3d-111 R-em3d111-3c — <c>circuitrf convert pkg.c3d -o pkg.glb</c>: the model with its appearances, and optionally a field
/// plot's colours, as binary glTF. What File ▸ Export ▸ glTF… writes, written in a batch.
///
/// <para><b>This file decides nothing about the file.</b> What is in it is <see cref="GltfExport"/>'s — the function the dialog calls;
/// the scene is <see cref="Scene3DBuilder"/>'s, built from the problem the 3D editor draws (the elaboration's solids and sheets, so a
/// <c>Model: false</c> part is written as it is drawn); the field is resolved by <c>render --field</c>'s own resolution
/// (<see cref="RenderEm3d.Request.FieldSink"/>). What is here is the view state headlessly — the document's, with no hidden-by-session
/// state — the camera rule (the Look's Camera when the document saves one, else none), and the report.</para>
/// </summary>
internal static class GltfConvert
{
    /// <summary>The aspect a Look's Camera frames at: <c>render</c>'s default picture's.</summary>
    internal const float CameraAspect = (float)Render.DefaultWidth / Render.DefaultHeight;

    public static int Export(string input, string output, bool assembly, string? fieldName, string? region)
    {
        Console.Error.WriteLine("[circuitRF] 3d -> glTF");
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(input); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ConvertFailed(ex.Message)); }
        var e = new C3dElaborator().Elaborate(doc, input, DocumentKinds.AncestorCws(input));
        if (!e.Ok) return JsonRun.Fail(CliDiagnostics.ConvertGltfDoesNotElaborate(e.Refusals[0], e.Refusals.Count));
        RunHost.Cancellation.ThrowIfCancellationRequested();

        var (theme, _, _) = Render.DefaultTheme(input);
        var scene = Scene(e, theme, ColorVariant.Light);
        var visible = GltfExport.DocumentVisibility(scene, e, doc);

        GltfField? field = null;
        if (fieldName is not null)
        {
            RenderEm3dRealistic.FieldPart? part = null;
            var req = new RenderEm3d.Request(output, "png", [], false, null, Render.DefaultWidth, Render.DefaultHeight, 1, 0, false,
                ColorVariant.Light, themeOf: p => { var (t, n, f) = Render.DefaultTheme(p); return (t, n, f, null); }, emit: (_, _, _) => [])
            {
                Field = fieldName, Region = region, FieldSink = p => { part = p; return 0; },
            };
            int resolved = RenderEm3dField.Draw(input, req);
            if (resolved != 0 || part is null) return resolved != 0 ? resolved : 1;
            field = new GltfField(fieldName, part.Geometry.Vertices, part.Quantity, part.Scale, part.PhaseRad, part.Covered);
            Console.Error.WriteLine("[circuitRF] " + part.Line);
        }
        else if (region is not null) return JsonRun.Fail(CliDiagnostics.ConvertGltfFlagsWithoutGltf());

        // R-em3d111-1f — the CLI writes the Look's Camera when the document saves one, and no camera otherwise
        GltfCamera? camera = null;
        if (doc.Look?.Camera is { } saved && saved.Faults().Count == 0)
        {
            var c = new Camera3D { FovY = Camera3D.DefaultFovY };
            c.FitBounds(scene.ContentMin, scene.ContentMax, CameraAspect);
            c.SetPictureCamera(saved, doc.DbuPerMicron, scene.ToLocal);
            camera = new GltfCamera(c, CameraAspect);
        }

        RunHost.Control?.BeginStage("write");
        var result = GltfExport.Build(new GltfExportSource(scene, visible, e, doc, Path.GetFileNameWithoutExtension(input)),
                                      new GltfExportOptions { Assembly = assembly, Field = field, Camera = camera }, RunHost.Cancellation);
        if (result.Objects == 0 && field is null) return JsonRun.Fail(CliDiagnostics.ConvertGltfNothing(input));
        string written;
        try { written = GltfExport.Write(result, output); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return JsonRun.Fail(CliDiagnostics.ConvertFailed(ex.Message)); }

        foreach (string note in result.Notes)
        {
            Console.Error.WriteLine($"note: {note}");
            JsonRun.Note(CliDiagnostics.ConvertNote(note));
        }
        Console.Error.WriteLine($"[circuitRF] {result.Summary}" + (camera is not null ? " The Look's Camera is the file's camera." : ""));
        Console.Error.WriteLine($"[circuitRF] {result.Bytes.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes");
        Console.WriteLine(written);
        JsonRun.AddOutput("glb", written);
        return 0;
    }

    /// <summary>
    /// The 3D editor's scene of <paramref name="e"/> as a freshly opened document draws it: the problem it draws (C3dProblemAssembly.ViewProblem
    /// of the elaboration's solids and sheets — what the editor's Assemble builds, the air box at the display extent), its origin, its face
    /// names, its transparency and its appearances.
    /// </summary>
    internal static Scene3DModel Scene(C3dElaboration e, ColorTheme theme, ColorVariant variant)
    {
        var extent = e.DisplayExtent() ?? FieldPlotResolver.EmptyExtent;
        var problem = C3dProblemAssembly.ViewProblem(e.Solids, e.Sheets, e.Materials, [], C3dProblemAssembly.ExtentBox(extent));
        return Scene3DBuilder.Build(problem, 0, e.Origins, e.Technology, theme, variant, null,
            new Scene3DBuildOptions(FaceNames: name => e.Provenance.TryGetValue(name, out var p) ? p.FaceNames : null,
                                    DrawAirBox: false, Origin: FieldPlotResolver.SceneOrigin(extent), HideOutermostDielectric: false,
                                    Transparency: Scene3DTransparency.Of(e.Provenance),
                                    Appearance: CircuitRF.Design.ThreeD.Appearance.AppearanceOverride.Of(e.Provenance)));
    }
}
