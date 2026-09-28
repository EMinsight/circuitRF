// ================================================================
//  GridAndWireframeTests.cs — 3D editor bugs round 1: the drawing grid is an infinite plane, and an object with
//  no material is drawn as a wireframe that is still picked, selected and edited. Structure and counters headless;
//  the pixels on the real Metal backend (macOS only). CRF_VIEWER3D_PNG=<dir> writes what Metal drew.
// ================================================================

using System.Numerics;
using Avalonia.Input;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class GridAndWireframeTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3dr1-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── the grid ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shader's ray for each clip position lands on the plane exactly where the view-projection puts that point back
    /// (so the grid is drawn where the plane is, in every projection and either y convention), and in perspective the
    /// plane is found BEYOND the scene's far plane — which the old quad, drawn through the scene-bracketing projection
    /// and faded out around the focus, could never reach.
    /// </summary>
    [Theory]
    [InlineData(Projection3D.Perspective, false)]
    [InlineData(Projection3D.Perspective, true)]
    [InlineData(Projection3D.Orthographic, false)]
    public void Grid_EveryPixelsRayMeetsThePlaneWhereTheCameraDrawsIt_AndThePlaneRunsPastTheScene(Projection3D projection, bool flipY)
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 50, 50, 20)]));
        var v = vm.Viewer;
        v.View.Camera = Camera3D.Fit(v.Scene.ContentMin, v.Scene.ContentMax, W / H, projection);
        v.View.Camera.Yaw = -0.7f; v.View.Camera.Pitch = 0.45f;
        var plan = new Scene3DFramePlan();
        plan.Plan(v.Scene, v.View, (int)W, (int)H, flipY, false, v.MeshOverlay, v.SectionOverlay, v.GridOverlay);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Grid && d.Count == 6);

        var g = plan.Uniforms.AsSpan(Scene3DFramePlan.GridAt, PlaneGrid.Floats).ToArray();
        var m = plan.Uniforms.AsSpan(0, 16).ToArray();
        var n = new Vector3(g[8], g[9], g[10]);
        int hits = 0, beyondFar = 0;
        for (float y = -0.95f; y <= 0.951f; y += 0.1f)
            for (float x = -0.95f; x <= 0.951f; x += 0.1f)
            {
                if (PlaneGrid.RayHit(g, x, y) is not { } p) continue;
                hits++;
                Assert.Equal(g[11], Vector3.Dot(p, n), 1e-6f * MathF.Max(1, p.Length()));
                // Column-major, clip = M · p.
                float cx = m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12];
                float cy = m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13];
                float cz = m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14];
                float cw = m[3] * p.X + m[7] * p.Y + m[11] * p.Z + m[15];
                Assert.Equal(x, cx / cw, 2e-3f);
                Assert.Equal(y, cy / cw, 2e-3f);
                if (cz / cw > 1) beyondFar++;
            }
        Assert.True(hits > 200, $"only {hits} of 400 clip positions reach the plane");
        if (projection == Projection3D.Perspective) Assert.True(beyondFar > 20, $"only {beyondFar} hits lie beyond the far plane");
    }

    /// <summary>
    /// A light/dark switch with a .c3d open recolours its drawing grid AND its background together. The grid's flag was
    /// read once, at open; the background was re-read on the control's own variant event, which runs before App assigns
    /// <c>ThemeService.CurrentVariant</c> — so it took the OLD variant, and the two ended up in opposite themes. The scene
    /// is rebuilt too: its palette is per variant.
    /// </summary>
    [Fact]
    public void Grid_ThemeVariantSwitch_RecoloursGridAndBackgroundTogether_AndRebuildsTheScene()
    {
        var entry = CircuitRF.Render.ThemeService.CurrentVariant;
        try
        {
            CircuitRF.Render.ThemeService.CurrentVariant = CircuitRF.Render.ColorVariant.Light;
            var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 50, 50, 20)]));
            var v = vm.Viewer;
            Assert.False(v.View.DrawingGrid!.Dark);
            Assert.True(v.View.Background.R > 0.5f, "a light theme opened on a dark background");
            long before = v.Source.Requested;

            CircuitRF.Render.ThemeService.CurrentVariant = CircuitRF.Render.ColorVariant.Dark;

            Assert.True(v.View.DrawingGrid!.Dark);
            Assert.True(v.View.Background.R < 0.5f, "the background stayed light");
            Assert.True(SpinWait.SpinUntil(() => v.Source.Requested > before, TimeSpan.FromSeconds(10)), "the scene was not rebuilt");
        }
        finally { CircuitRF.Render.ThemeService.CurrentVariant = entry; }
    }

    /// <summary>On Metal, the far part of a perspective view — the top rows, all of them beyond the scene — shows grid
    /// lines, and a small scene still has its grid across the whole bottom of the view.</summary>
    [Fact]
    public void Grid_OnMetal_LinesReachTheFarEdgeOfTheView()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var entry = CircuitRF.Render.ThemeService.CurrentVariant;
        CircuitRF.Render.ThemeService.CurrentVariant = CircuitRF.Render.ColorVariant.Dark;   // background and grid both follow it
        byte[] px; (float R, float G, float B) clear;
        try
        {
            var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 50, 50, 20)]));
            var scene = vm.Viewer.Scene;
            var view = vm.Viewer.View;
            view.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / H);
            view.Camera.Yaw = -0.7f; view.Camera.Pitch = 0.45f;
            (px, clear) = RenderMetal(vm, view, "grid-perspective");
        }
        finally { CircuitRF.Render.ThemeService.CurrentVariant = entry; }
        int w = (int)W, h = (int)H;
        int Band(int y0, int y1)
        {
            int k = 0;
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = 4 * (y * w + x);
                    if (Math.Abs(px[i] - clear.R * 255) > 6 || Math.Abs(px[i + 1] - clear.G * 255) > 6 || Math.Abs(px[i + 2] - clear.B * 255) > 6) k++;
                }
            return k;
        }
        int top = Band(0, h / 10), bottomLeft = Band(h * 9 / 10, h);
        Assert.True(top > w * h / 10 / 50, $"the top tenth of the view has {top} grid pixels");
        Assert.True(bottomLeft > w * h / 10 / 50, $"the bottom tenth of the view has {bottomLeft} grid pixels");
    }

    // ── the wireframe ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A box, a cylinder and a sheet with no material — and a box whose material the technology does not define — are
    /// all in the scene as wireframes: triangles at alpha 0 (for the pick) and their feature edges as an always-drawn
    /// line batch; never in a solver's lists. No material is a warning the solver ignores (round 2); a material the
    /// technology lacks is still a refusal.
    /// </summary>
    [Fact]
    public void Wireframe_AnObjectWithNoMaterial_IsInTheSceneAsEdges_AndNeverInASolversLists()
    {
        var vm = Open(Write(
        [
            Box("solid", "Gold", 0, 0, 0, 50, 50, 20),
            Box("ghost", null, 100, 0, 0, 50, 50, 20),
            Box("typo", "Gld", 200, 0, 0, 50, 50, 20),
            new C3dCylinder { Name = "post", Base = new C3dPoint3(300 * Um, 25 * Um, 0), Length = 30 * Um, Radius = 10 * Um },
            new C3dSheet { Name = "leaf", Plane = C3dPlane.XY, Offset = 0, Rect = new C3dRect { Min = new C3dPoint2(0, 100 * Um), Size = new C3dPoint2(40 * Um, 20 * Um) } },
        ]));
        var scene = vm.Viewer.Scene;
        var e = vm.Elaboration!;
        Assert.Equal(["solid"], e.Solids.Select(s => s.Name));
        Assert.Empty(e.Sheets);
        Assert.Equal(["ghost", "typo", "post"], e.UnassignedSolids.Select(s => s.Name));
        Assert.Equal(["leaf"], e.UnassignedSheets.Select(s => s.Name));
        Assert.Contains(C3dElaborator.NoMaterialWarning("ghost"), e.Warnings);
        Assert.DoesNotContain(e.Refusals, r => r.Contains("'ghost'"));
        Assert.Contains(e.Refusals, r => r.Contains("'typo' is made of 'Gld'"));

        var solid = scene.Objects.Single(o => o.Name == "solid");
        Assert.False(solid.Wireframe);
        Assert.DoesNotContain(scene.LineBatches, b => b.ObjectId == solid.Id);
        foreach (var (name, edges) in new[] { ("ghost", 12), ("typo", 12), ("post", 0), ("leaf", 4) })
        {
            var o = scene.Objects.Single(x => x.Name == name);
            Assert.True(o.Wireframe, name);
            Assert.True(o.Translucent && (o.Rgba >> 24) == 0, name);
            Assert.True(o.Pickable && o.Selectable && vm.Viewer.View.IsVisible(o.Id), name);
            Assert.Contains(scene.Batches, b => b.ObjectId == o.Id && b.Translucent);
            var lines = scene.LineBatches.Single(b => b.ObjectId == o.Id);
            if (name != "post") Assert.Equal(2 * edges, lines.VertexCount);
            else Assert.True(lines.VertexCount > 2 * 4 + 2 * 2 * 8, $"the cylinder draws {lines.VertexCount / 2} lines");   // four generators and two rims
        }
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, vm.Viewer.View, (int)W, (int)H, false, false, vm.Viewer.MeshOverlay, vm.Viewer.SectionOverlay, vm.Viewer.GridOverlay);
        uint ghostId = scene.Objects.Single(o => o.Name == "ghost").Id;
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Lines && d.Buffer == Scene3DBuffer.SceneLines);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Translucent
            && scene.Batches.Any(b => b.ObjectId == ghostId && b.FirstIndex == d.First));
    }

    /// <summary>The wireframe is hovered, clicked, face-picked and moved like any object.</summary>
    [Fact]
    public void Wireframe_IsSelectedByAClick_InObjectAndFaceMode_AndHasBoundsForAMove()
    {
        var vm = Open(Write([Box("ghost", null, 0, 0, 0, 100, 100, 20)]));
        var v = vm.Viewer;
        v.View.Camera = Camera3D.Fit(v.Scene.ContentMin, v.Scene.ContentMax, W / H, Projection3D.Orthographic);
        v.View.Camera.SetStandardView(StandardView3D.Top);
        v.View.Camera.Target = (v.Scene.ContentMin + v.Scene.ContentMax) * 0.5f;

        ClickCentre(v);
        Assert.Equal("ghost", v.Scene.Object(v.Selection.Single().Object)!.Name);
        v.SelectMode = Scene3DSelectMode.Face;
        ClickCentre(v);
        Assert.Equal("Face zmax · Box \"ghost\"", v.Name(v.Selection.Single()));

        var b = vm.BoundsDbu([new C3dTarget(false, 0)]);
        Assert.NotNull(b);
        Assert.Equal((0, 0, 0, 100 * Um, 100 * Um, 20 * Um), (b!.Value.X0, b.Value.Y0, b.Value.Z0, b.Value.X1, b.Value.Y1, b.Value.Z1));
    }

    /// <summary>On Metal: the wireframe's edges are drawn and its faces are not filled, and the GPU's ID pass finds it
    /// under a pixel inside a face — the pick pass draws its (transparent) triangles.</summary>
    [Fact]
    public void Wireframe_OnMetal_DrawsEdgesNotFaces_AndTheIdPassFindsIt()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var vm = Open(Write([Box("ghost", null, 0, 0, 0, 100, 100, 40)]));
        vm.ShowDrawingGrid = false;
        var scene = vm.Viewer.Scene;
        var view = vm.Viewer.View;
        view.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / H);
        view.Camera.Yaw = -0.7f; view.Camera.Pitch = 0.5f;
        view.CursorX = W / 2; view.CursorY = H / 2;
        uint ghost = scene.Objects.Single(o => o.Name == "ghost").Id;
        Assert.Equal(ghost, Scene3DPicking.IdAtPixel(scene, view.Camera, W / 2, H / 2, W, H, view.Visible));
        var (px, clear) = RenderMetal(vm, view, "wireframe", out var metal);
        using (metal)
        {
            Assert.True(metal.PickedSomething);
            Assert.Equal(ghost, metal.PickedId);
        }
        int drawn = 0, area = 0;
        for (int i = 0; i < px.Length; i += 4)
            if (Math.Abs(px[i] - clear.R * 255) > 6 || Math.Abs(px[i + 1] - clear.G * 255) > 6 || Math.Abs(px[i + 2] - clear.B * 255) > 6) drawn++;
        for (int y = 0; y < (int)H; y++)
            for (int x = 0; x < (int)W; x++)
                if (Scene3DPicking.IdAtPixel(scene, view.Camera, x, y, W, H, view.Visible) == ghost) area++;
        Assert.True(drawn > 200, $"only {drawn} pixels of edge were drawn");
        Assert.True(drawn < area / 4, $"{drawn} pixels drawn over a {area}-pixel silhouette: the faces were filled");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private (byte[] Pixels, (float R, float G, float B) Clear) RenderMetal(C3dEditorViewModel vm, Viewer3DViewState view, string name)
    {
        var r = RenderMetal(vm, view, name, out var metal);
        metal.Dispose();
        return r;
    }

    private (byte[] Pixels, (float R, float G, float B) Clear) RenderMetal(C3dEditorViewModel vm, Viewer3DViewState view, string name,
                                                                           out CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend metal)
    {
        var scene = vm.Viewer.Scene;
        var m = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        metal = m;
        m.CreateOffscreenImages((int)W, (int)H, 1);
        var session = new Viewer3DSession(() => m);
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        for (ulong f = 1; f <= 3; f++)
        {
            plan.Plan(scene, view, (int)W, (int)H, m.FlipY, pick: view.CursorX >= 0, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(0, plan, f, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        }
        var px = m.ReadImage(0);
        if (Environment.GetEnvironmentVariable("CRF_VIEWER3D_PNG") is { Length: > 0 } dir)
        {
            using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo((int)W, (int)H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            using var f = File.Create(Path.Combine(dir, name + ".png"));
            bmp.Encode(f, SkiaSharp.SKEncodedImageFormat.Png, 100);
        }
        return (px, plan.Clear);
    }

    private C3dEditorViewModel Open(string c3d)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)), "the scene never settled");
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private static C3dBox Box(string name, string? material, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private string Write(List<C3dObject> objects)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument { Objects = objects });
        return path;
    }

    private static void ClickCentre(Viewer3DViewModel v)
    {
        v.Hover(W / 2, H / 2);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, W / 2, H / 2, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
        v.Click(shift: false);
        Assert.Single(v.Selection);
    }
}
