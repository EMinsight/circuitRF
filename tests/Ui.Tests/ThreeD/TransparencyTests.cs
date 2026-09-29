// ================================================================
//  TransparencyTests.cs — brief-em3d-92: an object's transparency, saved only when set, written to a group's every member
//  as one undo entry, packed into the scene's alpha (a conductor's too, an instance's multiplied on), painted by the vector
//  drawing and the CLI's section, and capped by ONE constant (C3dTransparency.Max) at every entry point. Pixels were not
//  seen: these read the model, the scene's bytes and the files written.
// ================================================================

using System.Numerics;
using System.Text;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class TransparencyTests : IDisposable
{
    private const long Um = 1000;
    private const double Mm = 1e-3;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-transp-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public TransparencyTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. the document ─────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 1: the key round-trips on an object, a group's members and an instance, and is written only when set —
    /// so a document stating none spells exactly what it did; and a run does not see it (a display edit is never stale).</summary>
    [Fact]
    public void TheKey_RoundTrips_IsWrittenOnlyWhenSet_AndIsNotPartOfTheRunsModel()
    {
        var doc = Doc();
        string plain = C3dPersistence.Serialize(doc);
        Assert.DoesNotContain("Transparency", plain);
        string forRun = C3dPersistence.SerializeForRun(doc);

        doc.Objects[0].Transparency = 60;
        doc.Objects[1].Transparency = 20;
        doc.Objects[2].Transparency = 70;
        doc.Instances[0].Transparency = 50;
        string text = C3dPersistence.Serialize(doc);
        Assert.Contains("\"Transparency\": 60", text);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
        Assert.Equal(forRun, C3dPersistence.SerializeForRun(doc));
        Assert.Equal(60, doc.Objects[0].Transparency);                       // restored after the run's spelling
    }

    // ── 3. the scene ────────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 3: a stated 60 % is 255 × 0.4 = 102 in the vertex alpha and translucent — a conductor included, which is
    /// otherwise opaque; a conductor stating none stays opaque.</summary>
    [Fact]
    public void TheScene_PacksTheAlpha_AConductorIncluded()
    {
        var problem = Problem(Solid("lid", 0, 0, 0, 2, 2, 1), Solid("pin", 3, 0, 0, 4, 1, 1));
        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false,
            Transparency: n => n == "lid" ? new Scene3DTransparency(60) : null));
        var lid = scene.Objects.Single(o => o.Name == "lid");
        var pin = scene.Objects.Single(o => o.Name == "pin");
        Assert.Equal(102u, lid.Rgba >> 24);
        Assert.True(lid.Translucent);
        Assert.All(scene.Vertices.Skip(lid.FirstVertex).Take(lid.VertexCount), v => Assert.Equal(102u, v.Rgba >> 24));
        Assert.Equal(255u, pin.Rgba >> 24);
        Assert.False(pin.Translucent);
    }

    // ── 2. groups, the Inspector, instances, the cap in the editor ──────────────────────────────

    /// <summary>Gate 2: a group whose members say 20 and 70 reads "mixed"; 40 is written to both — the nested group's member
    /// too — as ONE undo entry, and undo restores 20 and 70. A slider drag previews without writing and is one entry on
    /// release. An instance at 50 % of a part at 50 % draws it at 25 % opacity. The cap holds in the editor and the box.</summary>
    [Fact]
    public void AGroup_ReadsMixed_ItsRowWritesEveryMember_OneUndoEntry_AndAnInstanceMultiplies()
    {
        var doc = Doc();
        doc.Objects[1].Transparency = 20;                                   // b, in G
        doc.Objects[2].Transparency = 70;                                   // c, in G/inner
        doc.Instances[0].Transparency = 50;                                 // U1, whose chip states 50
        var vm = Open(doc);

        Assert.Equal(64u, vm.SceneObject("U1/chip")!.Rgba >> 24);          // 255 × 0.5 × 0.5 = 63.75

        vm.Viewer.OnPicked(vm.SceneObject("b")!.Id, 0, Vector3.Zero, true);
        vm.Viewer.Click(false);                                             // a click on a member selects the whole group
        var p = vm.Properties;
        Assert.True(p.IsGroup && p.HasTransparency);
        Assert.Equal(C3dPropertiesViewModel.TransparencyMixed, p.TransparencyPlaceholder);

        int entries = vm.UndoEntries;
        p.TransparencyText = "40";
        p.CommitTransparencyText();
        Assert.Equal(new int?[] { 40, 40 }, [vm.Document.Objects[1].Transparency, vm.Document.Objects[2].Transparency]);
        Assert.Equal(entries + 1, vm.UndoEntries);
        vm.UndoRedo.Undo();
        Assert.Equal(new int?[] { 20, 70 }, [vm.Document.Objects[1].Transparency, vm.Document.Objects[2].Transparency]);
        Settle(vm);

        // A drag: each position previewed, nothing written, then one entry on release.
        vm.Viewer.OnPicked(vm.SceneObject("b")!.Id, 0, Vector3.Zero, true);
        vm.Viewer.Click(false);
        int previews = vm.TransparencyPreviews;
        entries = vm.UndoEntries;
        vm.Properties.TransparencySlider = 10;
        Settle(vm);                                                          // the preview's scene is adopted mid-drag: it reloads the panel
        Assert.Equal(10, vm.Properties.TransparencySlider);
        vm.Properties.TransparencySlider = 30;
        Assert.Equal(previews + 2, vm.TransparencyPreviews);
        Assert.Equal(20, vm.Document.Objects[1].Transparency);
        vm.Properties.CommitTransparencySlider();
        Assert.Equal(new int?[] { 30, 30 }, [vm.Document.Objects[1].Transparency, vm.Document.Objects[2].Transparency]);
        Assert.Equal(entries + 1, vm.UndoEntries);

        // The cap, through the editor's edit and through the Inspector's box.
        int a = vm.Document.Objects.FindIndex(o => o.Name == "a");
        Assert.Null(vm.SetTransparency([a], [], C3dTransparency.Max, "max"));
        Assert.Equal(C3dTransparency.Max, vm.Document.Objects[a].Transparency);
        Assert.NotNull(vm.SetTransparency([a], [], C3dTransparency.Max + 1, "over"));
        Assert.Equal(C3dTransparency.Max, vm.Document.Objects[a].Transparency);
        Settle(vm);
        vm.Properties.TransparencyText = (C3dTransparency.Max + 1).ToString();
        vm.Properties.CommitTransparencyText();
        Assert.Contains($"0 to {C3dTransparency.Max} %", vm.Properties.Error);
    }

    // ── 4. the drawing, and the cap in check and the CLI ────────────────────────────────────────

    /// <summary>Gate 4: a see-through lid hides nothing by default — the edges of what is behind it are drawn solid — and hides
    /// them as today with TransparentObjectsOcclude; a section fills it at fill-opacity 0.4 and stays vector. The cap is the
    /// one constant in check's validator and in the CLI's parser.</summary>
    [Fact]
    public void TheDrawing_SeesThroughALid_UnlessAskedToOcclude_AndFillsItAtItsAlpha_AndTheCapHoldsInCheckAndTheCli()
    {
        // Front looks along +y from −y: the lid (y −2…−1) stands in front of the block (y 0…1) and covers it.
        var problem = Problem(Solid("lid", 0, -2, 0, 4, -1, 4), Solid("block", 1, 0, 1, 2, 1, 2));
        var see = new Dictionary<string, Scene3DTransparency> { ["lid"] = new(60) };
        var front = Em3dProjection.Standard(Em3dStandardView.Front);
        // The block alone hides only its own back edges; seen through the lid it is drawn exactly so — its front edges solid.
        var alone = Em3dSectionScene.Outline(Problem(problem.Solids[1]), front, new Em3dOutlineOptions { Hidden = Em3dHiddenEdges.Dashed }, out _)
                                    .Lines.Select(l => (l.A, l.B, l.Hidden)).ToList();
        Assert.Contains(alone, l => !l.Hidden);
        var through = Em3dSectionScene.Outline(problem, front, Em3dDrawingExport.OutlineOptions(Em3dHiddenEdges.Dashed, see, false), out _);
        Assert.Equal(alone, through.Lines.Where(l => l.Object == "block").Select(l => (l.A, l.B, l.Hidden)));
        var occluded = Em3dSectionScene.Outline(problem, front, Em3dDrawingExport.OutlineOptions(Em3dHiddenEdges.Dashed, see, true), out _);
        Assert.All(occluded.Lines.Where(l => l.Object == "block"), l => Assert.True(l.Hidden));

        var request = new Em3dDrawingRequest
        {
            Format = Em3dDrawingFormat.Svg, Views = [], Sections = [new Em3dView(Em3dViewKind.SectionY, -1.5 * Mm)],
            ObjectTransparency = see, DocumentName = "lid.c3d",
        };
        string svg = Encoding.UTF8.GetString(Em3dDrawingExport.Sheet(problem, new Dictionary<string, SKColor> { ["lid"] = new(200, 120, 40) },
                                                                       ColorTheme.BuiltIn, request, out _, out _));
        // Skia spells the float it holds (0.40000001), so the value is read, not the text.
        var opacity = System.Text.RegularExpressions.Regex.Match(svg, "fill=\"#C87828\" fill-opacity=\"([0-9.]+)\"");
        Assert.True(opacity.Success, "the lid's fill carries no fill-opacity");
        Assert.Equal(0.4, double.Parse(opacity.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        byte[] pdf = Em3dDrawingExport.Sheet(problem, new Dictionary<string, SKColor> { ["lid"] = new(200, 120, 40) }, ColorTheme.BuiltIn,
                                             request with { Format = Em3dDrawingFormat.Pdf }, out _, out _);
        Assert.DoesNotContain("/Subtype /Image", Encoding.Latin1.GetString(pdf));   // the transparency group did not rasterise the page

        var doc = Doc();
        doc.Objects[0].Transparency = C3dTransparency.Max;
        Assert.DoesNotContain(C3dValidation.Validate(doc), d => d.Id == "c3d.transparency.range");
        doc.Objects[0].Transparency = C3dTransparency.Max + 1;
        Assert.Single(C3dValidation.Validate(doc), d => d.Id == "c3d.transparency.range");

        JsonRun.Reset();
        Assert.Null(RenderTransparency.Parse([$"a={C3dTransparency.Max}"], out _));
        Assert.Equal(1, RenderTransparency.Parse([$"a={C3dTransparency.Max + 1}"], out _));
    }

    // ── 5. the CLI as a process ─────────────────────────────────────────────────────────────────

    /// <summary>Gate 5: <c>render --iso</c> as a PROCESS writes the bytes the in-process renderer writes for the same document;
    /// <c>--transparency a=80</c> changes a section's picture and leaves the file on disk as it was. (An isometric outline
    /// fills nothing, so it is the section that shows the override.)</summary>
    [Fact]
    public void TheVerb_AsAProcess_MatchesTheRenderer_AndTheOverrideChangesThePictureNotTheFile()
    {
        var doc = Doc();
        doc.Objects[0].Transparency = 60;                                  // a, a conductor: its section fill is at 0.4
        string path = Write(doc, out string ws);
        byte[] before = File.ReadAllBytes(path);

        string iso = Path.Combine(_root, "iso.png");
        var (exit, stdout, stderr) = Cli("render", path, "-o", iso, "--iso", "--size", "400x300");
        Assert.True(exit == 0, stderr + stdout);
        var e = new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(path), path, Path.Combine(ws, ".cws"));
        var problem = C3dProblemAssembly.ViewProblem(e.Solids, e.Sheets, e.Materials, [], C3dProblemAssembly.ExtentBox(e.Extent()!.Value));
        var theme = ThemeResolver.Resolve(ThemeResolver.DefaultThemeName, ws);
        var style = new Em3dRenderStyle(Em3dSectionRenderer.ObjectColours(problem, e.Origins, e.Technology, theme, ColorVariant.Light),
                                        theme, ColorVariant.Light, DocumentExtents.DefaultMargin, Transparent: false)
        {
            ObjectTransparency = Scene3DTransparency.MapOf(e.Provenance),
        };
        var scene = Em3dSectionScene.Build(problem, Em3dView.Iso);
        Assert.True(File.ReadAllBytes(iso).AsSpan().SequenceEqual(VectorPage.Png(400, 300, c => Em3dSectionRenderer.Draw(c, 400, 300, scene, style))),
                    "the process's picture differs from the in-process render");

        string plain = Path.Combine(_root, "plain.png"), over = Path.Combine(_root, "over.png");
        Assert.Equal(0, Cli("render", path, "-o", plain, "--section", "z=50um", "--size", "400x300").ExitCode);
        Assert.Equal(0, Cli("render", path, "-o", over, "--section", "z=50um", "--size", "400x300", "--transparency", "a=80").ExitCode);
        Assert.False(File.ReadAllBytes(plain).AsSpan().SequenceEqual(File.ReadAllBytes(over)));
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "the override wrote the document");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, long x, string? group = null, string material = "Copper")
        => new() { Name = name, Material = material, Group = group, Min = new C3dPoint3(x * 200 * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 100 * Um) };

    /// <summary>a alone; b in G; c in G/inner (a nested group); U1 placing the die, whose chip states 50 %.</summary>
    private static C3dDocument Doc() => new()
    {
        Objects = [Box("a", 0), Box("b", 1, "G"), Box("c", 2, "G/inner")],
        Instances = [new C3dInstance { Name = "U1", CellRef = "../../die", Placement = new C3dPlacement { Origin = new C3dPoint3(0, 500 * Um, 0) } }],
    };

    private static Em3dSolid Solid(string name, double x0, double y0, double z0, double x1, double y1, double z1)
        => new(name, "Copper", Em3dRole.Conductor, new Em3dBox(new Point3(x0 * Mm, y0 * Mm, z0 * Mm), new Point3(x1 * Mm, y1 * Mm, z1 * Mm)), 0);

    private static Em3dProblem Problem(params Em3dSolid[] solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        return new Em3dProblem(solids, [], [], [],
            new Em3dAirBox(new Point3(-50 * Mm, -50 * Mm, -50 * Mm), new Point3(50 * Mm, 50 * Mm, 50 * Mm), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
    }

    /// <summary>A workspace with a technology, the die cell and <paramref name="doc"/> as Cell/3d/Cell.c3d.</summary>
    private string Write(C3dDocument doc, out string ws)
    {
        ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string die = Path.Combine(ws, "die", "3d");
        Directory.CreateDirectory(die);
        var chip = Box("chip", 0);
        chip.Transparency = 50;
        C3dPersistence.SaveToFile(Path.Combine(die, "die.c3d"), new C3dDocument { Objects = [chip] });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string path = Write(doc, out string ws);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");

    private static (int ExitCode, string StdOut, string StdErr) Cli(params string[] args)
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitrf.slnx"))) repo = Path.GetDirectoryName(repo)!;
        return CircuitRF.Ui.Tests.Em3d.CliProcess.Run(repo, [], args);
    }
}
