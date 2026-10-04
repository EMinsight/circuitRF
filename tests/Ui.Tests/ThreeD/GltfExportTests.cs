// brief-em3d-111 — glTF export, the GUI path: the 3D editor's own scene, what it shows, and File ▸ Export ▸ glTF…'s dialog.
//
//    1  structure: every file below passes GlbFile.Validate (the header, the chunks, the accessors, the normals, the extensions)
//    2  content: a hidden object, the air box and a port are absent; a group is a parent node; an override is its own material; the
//       glass has transmission and ior; a plain copper box has no extension
//    4  lossless: every written object's material factors equal Scene3DModel.Appearances within 1e-6
//    5  assembly: three instances of one cell write one mesh and three instance nodes; flattened writes three meshes
//    6  the field's rule: vertex colours are the colour map at the vertex value, and no edge spans more than 1/16 of the range unless the
//       four-level cap was reached and counted
//    7  deterministic: the dialog's export twice is byte-identical, and byte-identical to `convert` run as a process (no camera)
//    3a menus: File ▸ Export ▸ glTF… on every surface that lists STEP…
//
// Gates 3, 6's end-to-end and 8 are GltfConvertCliTests'.

using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Export;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class GltfExportTests : IDisposable
{
    private const long Um = 1000;

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-gltf-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public GltfExportTests(ITestOutputHelper output)
    {
        _output = output;
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 2 + 4. content, and the appearances written losslessly ───────────────────────────────────────────────────────

    [Fact]
    public void Gate2And4_WhatIsDrawnIsWritten_AndEveryMaterialIsTheScenesAppearance()
    {
        var vm = Open(Mixed(), out _);
        var scene = vm.Viewer.Scene;
        // the editor draws all three things the file must leave out
        Assert.Contains(scene.Objects, o => o.Kind == Scene3DKind.Port);
        Assert.Contains(scene.Objects, o => Scene3DFramePlan.ChromeOfObject(scene, o) == Scene3DChrome.AirBox);
        Assert.False(vm.Viewer.View.IsVisible(vm.SceneObject("hidden")!.Id));

        var glb = GlbFile.Validate(GltfExport.Build(vm.GltfSource(), new GltfExportOptions()).Bytes);
        var names = glb.Nodes.Select(n => n.GetProperty("name").GetString()).ToList();
        _output.WriteLine(string.Join(", ", names));
        Assert.DoesNotContain("hidden", names);
        Assert.DoesNotContain(names, n => n!.StartsWith("airbox", StringComparison.Ordinal) || n.StartsWith("port", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Cell", "plain", "gilded", "glass", "pa"], names.Where(n => n is not ("m1" or "m2")).ToList());

        int pa = glb.NodeNamed("pa")!.Value;
        Assert.Equal(["m1", "m2"], glb.Children(pa).Select(c => glb.Nodes[c].GetProperty("name").GetString()));

        // R-em3d111-2d — with a field included, what it stands in for is left out, as the view leaves it out
        var scalar = new FieldQuantity(new FieldArrayInfo("V", 1, false, false), false, FieldMode.Value);
        var over = new GltfField("over", [new FieldVertex(), new FieldVertex { X = 1e-4f }, new FieldVertex { Y = 1e-4f }], scalar,
                                 new FieldColorScale(false, 0, 1, 100, false, ""), 0, ["plain"]);
        var withField = GlbFile.Validate(GltfExport.Build(vm.GltfSource(), new GltfExportOptions { Field = over }).Bytes);
        Assert.Null(withField.NodeNamed("plain"));
        Assert.NotNull(withField.NodeNamed("over"));

        string Name(JsonElement m) => m.GetProperty("name").GetString()!;
        var plain = glb.MaterialsOf(glb.NodeNamed("plain")!.Value).Single();
        var gilded = glb.MaterialsOf(glb.NodeNamed("gilded")!.Value).Single();
        var glass = glb.MaterialsOf(glb.NodeNamed("glass")!.Value).Single();
        Assert.Equal("Copper", Name(plain));
        Assert.False(plain.TryGetProperty("extensions", out _));
        Assert.Equal("Copper+override", Name(gilded));
        var ext = glass.GetProperty("extensions");
        Assert.Equal(1, ext.GetProperty(GltfExport.Transmission).GetProperty("transmissionFactor").GetDouble());
        Assert.Equal(1.52, ext.GetProperty(GltfExport.Ior).GetProperty("ior").GetDouble());

        // 4 — each written object's factors are its own slot's, within 1e-6
        foreach (string obj in (string[])["plain", "gilded", "glass", "m1", "m2"])
        {
            var a = scene.Appearances[vm.SceneObject(obj)!.AppearanceSlot];
            var pbr = glb.MaterialsOf(glb.NodeNamed(obj)!.Value).Single().GetProperty("pbrMetallicRoughness");
            var bc = pbr.GetProperty("baseColorFactor");
            Assert.Equal(a.BaseColor.R, bc[0].GetDouble(), 1e-6);
            Assert.Equal(a.BaseColor.G, bc[1].GetDouble(), 1e-6);
            Assert.Equal(a.BaseColor.B, bc[2].GetDouble(), 1e-6);
            Assert.Equal(a.Metallic, pbr.GetProperty("metallicFactor").GetDouble(), 1e-6);
            Assert.Equal(a.Roughness, pbr.GetProperty("roughnessFactor").GetDouble(), 1e-6);
        }
    }

    // ── 5. assembly ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_ThreeInstancesOfOneCell_AreOneMeshAndThreeNodes_AndFlattenedAreThree()
    {
        var doc = new C3dDocument
        {
            Instances = [.. Enumerable.Range(0, 3).Select(k => new C3dInstance
            {
                Name = $"U{k + 1}", CellRef = "../../die",
                Placement = new C3dPlacement { Origin = new C3dPoint3(k * 500 * Um, 0, 0) },
            })],
        };
        var vm = Open(doc, out _);
        var source = vm.GltfSource();

        var asm = GlbFile.Validate(GltfExport.Build(source, new GltfExportOptions { Assembly = true }).Bytes);
        Assert.Single(asm.Meshes);
        var instances = asm.Nodes.Where(n => n.GetProperty("name").GetString() is "U1" or "U2" or "U3").ToList();
        Assert.Equal(3, instances.Count);
        Assert.All(instances, n =>
        {
            var chip = asm.Nodes[n.GetProperty("children")[0].GetInt32()];
            Assert.Equal("chip", chip.GetProperty("name").GetString());
            Assert.Equal(0, chip.GetProperty("mesh").GetInt32());
        });

        var flat = GlbFile.Validate(GltfExport.Build(source, new GltfExportOptions()).Bytes);
        Assert.Equal(3, flat.Meshes.Length);
    }

    // ── 6. the field: per-vertex colours, and the subdivision rule ──────────────────────────────────────────────────────

    /// <summary>One triangle of a linear scalar from 0 to 1 across a 0–1 range (four levels bring every edge to 1/16 exactly), and one in
    /// dB from 1e-6 to 1 over 40 dB (no number of halvings brings its small end within 1/16: the cap is reached and counted). Every vertex's
    /// colour is the colour map at the value there — the channels interpolated as the GPU interpolates them, so a linear scalar's value at
    /// a vertex is its x.</summary>
    [Fact]
    public void Gate6_FieldColoursAreTheMapAtEachVertex_AndNoEdgeSpansMoreThanASixteenth_UnlessCapped()
    {
        var scalar = new FieldQuantity(new FieldArrayInfo("V", 1, false, false), false, FieldMode.Value);
        FieldVertex At(float x, float y, float value) => new() { X = x, Y = y, R0 = value };
        var linear = new GltfField("lin", [At(0, 0, 0), At(1, 0, 1), At(0, 1, 0)], scalar, new FieldColorScale(false, 0, 1, 100, false, ""), 0, []);
        var scene = Scene3DModel.Empty();
        var lin = GltfExport.Build(new GltfExportSource(scene, [], null, null, "f"), new GltfExportOptions { Field = linear });
        var glb = GlbFile.Validate(lin.Bytes);
        Assert.Equal(256, lin.FieldTriangles);
        Assert.Equal(0, lin.FieldCapped);
        var prim = glb.Meshes.Single().GetProperty("primitives")[0];
        Assert.Contains(GltfExport.Unlit, glb.Materials[prim.GetProperty("material").GetInt32()].GetProperty("extensions").EnumerateObject().Select(p => p.Name));
        var pos = glb.Vec3(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
        var col = glb.Vec3(prim.GetProperty("attributes").GetProperty("COLOR_0").GetInt32());
        for (int i = 0; i < pos.Length; i++)
        {
            var want = GltfExport.LinearColour(ColorMap3D.For(scalar), pos[i].X);
            Assert.True(Vector3.Distance(want, col[i]) < 1e-6, $"vertex {i} at x = {pos[i].X}");
        }
        for (int t = 0; t < pos.Length; t += 3)
            for (int k = 0; k < 3; k++) Assert.True(Math.Abs(pos[t + k].X - pos[t + (k + 1) % 3].X) <= GltfExport.FieldSpan + 1e-6);

        var db = new GltfField("db", [At(0, 0, 1e-6f), At(1, 0, 1), At(0, 1, 1e-6f)], scalar with { Mode = FieldMode.Peak },
                               new FieldColorScale(true, -40, 0, 100, false, ""), 0, []);
        var capped = GltfExport.Build(new GltfExportSource(scene, [], null, null, "f"), new GltfExportOptions { Field = db });
        GlbFile.Validate(capped.Bytes);
        Assert.True(capped.FieldCapped > 0);
        Assert.Contains(capped.Notes, n => n.Contains("reached the 4-level cap", StringComparison.Ordinal));
    }

    /// <summary>R-em3d111-1f — a camera node looks down its own −z with +y up: the rotation carries those onto the view's forward and up.</summary>
    [Fact]
    public void TheCameraNodesRotation_LooksAlongTheViewsForward_WithItsUp()
    {
        var cam = new Camera3D { FovY = Camera3D.DefaultFovY, Yaw = 0.7f, Pitch = 0.4f, Distance = 1 };
        var q = GltfExport.CameraRotation(cam);
        Assert.True(Vector3.Distance(Vector3.Transform(-Vector3.UnitZ, q), cam.Forward) < 1e-5);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitY, q), Vector3.Normalize(cam.Up)) < 1e-5);
    }

    // ── 7. deterministic, and the dialog's file is convert's ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate7_TheDialogsExportTwice_AndConvert_AreByteIdentical()
    {
        var vm = Open(Mixed(), out string c3d);
        async Task<byte[]> Dialog(string name)
        {
            string target = Path.Combine(_root, name);
            var dlg = new GltfExportDialogViewModel(target, vm.GltfSource(), vm.GltfCamera(), vm.GltfFields()) { IncludeCamera = false };
            await dlg.PlanAsync();
            Assert.True(dlg.CanExport, dlg.Error ?? "");
            await dlg.ExportCommand.ExecuteAsync(null);
            Assert.Equal(Path.GetFullPath(target), dlg.Written);
            return File.ReadAllBytes(target);
        }
        byte[] a = await Dialog("a.glb"), b = await Dialog("b.glb");
        Assert.Equal(a, b);

        string cli = Path.Combine(_root, "cli.glb");
        var (exit, _, err) = Process("convert", c3d, "-o", cli);
        Assert.True(exit == 0, err);
        GlbFile.Validate(a);
        Assert.Equal(a, File.ReadAllBytes(cli));
    }

    // ── 3a. menus ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExportGltf_IsBesideStepInAllThreeFileMenus_OnOneCommandAndOneTooltip()
    {
        string root = PalaceBackendTests.RepoRoot();
        string window = File.ReadAllText(Path.Combine(root, "src", "Ui", "Views", "WorkspaceWindow.axaml"));
        string torn = File.ReadAllText(Path.Combine(root, "src", "Ui", "Views", "Shared", "TornOffFileMenuView.axaml"));
        Assert.Equal(Regex.Matches(window, "ExportStepCommand").Count, Regex.Matches(window, "ExportGltfCommand").Count);
        Assert.Equal(2, Regex.Matches(window, "ThreeDExportGltfTip").Count);
        Assert.Single(Regex.Matches(torn, "ExportGltfCommand"));
        Assert.Contains("ThreeDExportGltfTip", torn, StringComparison.Ordinal);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, long x, string material = "Copper", string? group = null, long z = 100)
        => new() { Name = name, Material = material, Group = group, Min = new C3dPoint3(x * 200 * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, z * Um) };

    /// <summary>A plain copper box, a hidden one, one restyled to look like Gold, a glass slab, two boxes in group 'pa', a port, and an
    /// EM setup so the editor draws its air box.</summary>
    private static C3dDocument Mixed() => new()
    {
        Objects =
        [
            Box("plain", 0), Hide(Box("hidden", 1)), Restyled(Box("gilded", 2)), Box("glass", 3, "Glass", z: 20),
            Box("m1", 4, group: "pa"), Box("m2", 5, group: "pa"),
        ],
        // a lumped port in the gap between 'plain' and 'hidden', touching both
        Ports = [new C3dPort { Number = 1, Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XZ, Offset = 50 * Um,
                               Rect = new C3dRect { Min = new C3dPoint2(100 * Um, 0), Size = new C3dPoint2(100 * Um, 100 * Um) } }],
        Setups = [EmSetupPersistence.ToEmbedded(new EmSetup { Name = "EM", Solver3D = Em3dSolver.Palace })],
    };

    private static C3dBox Hide(C3dBox b) { b.Hidden = true; return b; }
    private static C3dBox Restyled(C3dBox b) { b.Appearance = new TechAppearance { Like = "Gold" }; return b; }

    private C3dEditorViewModel Open(C3dDocument doc, out string path)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = "Gold", Sigma20 = 4.1e7, Color = "#D4AF37" },
                new TechMaterial { Name = "Glass", Epsr = 4, Appearance = new TechAppearance { Transmission = 1, Ior = 1.52, Roughness = 0.05 } },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string die = Path.Combine(ws, "die", "3d");
        Directory.CreateDirectory(die);
        C3dPersistence.SaveToFile(Path.Combine(die, "die.c3d"), new C3dDocument { Objects = [Box("chip", 0)] });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);

        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
        return vm;
    }

    private static (int Exit, string StdOut, string StdErr) Process(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = typeof(GltfExportTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
