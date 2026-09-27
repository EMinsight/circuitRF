// ================================================================
//  EditorRound2ViewportTests.cs — 3D editor bugs round 2, the view itself: a re-based scene keeps the camera on the
//  same world point, a drag's preview is inside the depth range, a camera drag hovers and snaps to nothing, the scale
//  bar is a round number of the display unit, the snap marker takes the snapped object's colour, and a double-click on
//  the axis indicator turns the view. Headless; no pixels.
// ================================================================

using System.Numerics;
using Avalonia;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EditorRound2ViewportTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3dr2-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>An object moved far enough re-bases the scene's origin; the camera stays on the same WORLD point, so
    /// nothing on screen jumps.</summary>
    [Fact]
    public void Rebase_KeepsTheCameraOnTheSameWorldPoint()
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 50, 50, 20)]));
        var v = vm.Viewer;
        var before = v.Scene;
        var world = before.ToWorld(v.View.Camera.Target);

        vm.ChangeObjects("Move b", [0], o => o.Placement.Origin = new C3dPoint3(5000 * Um, 0, 0));
        Settle(vm);

        Assert.NotEqual(before.Origin, v.Scene.Origin);
        var after = v.Scene.ToWorld(v.View.Camera.Target);
        Assert.Equal(world.X, after.X, 1e-9);
        Assert.Equal(world.Y, after.Y, 1e-9);
        Assert.Equal(world.Z, after.Z, 1e-9);
    }

    /// <summary>A preview copy far outside the scene's sphere lies between the frame's near and far planes, and the
    /// view's own camera is left as it was.</summary>
    [Fact]
    public void Preview_FarOutsideTheScene_IsInsideTheFramesDepthRange()
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 50, 50, 20)]));
        var v = vm.Viewer;
        var o = vm.SceneObject("b")!;
        float far = 40 * v.View.Camera.SceneRadius;
        var moving = new bool[v.Scene.Objects.Length];
        moving[o.Id - 1] = true;
        v.View.Preview = new Scene3DPreview(moving, [Matrix4x4.CreateTranslation(far, far, 0)], keepOriginal: false);
        float radius = v.View.Camera.SceneRadius;

        var cam = Scene3DFramePlan.DepthCamera(v.Scene, v.View);
        var (near, farPlane) = cam.DepthRange();
        foreach (var corner in new[] { o.Min, o.Max })
        {
            float depth = cam.ViewDepth(corner + new Vector3(far, far, 0));
            Assert.InRange(depth, near, farPlane);
        }
        Assert.Equal(radius, v.View.Camera.SceneRadius);
        v.View.Preview = null;
    }

    /// <summary>While the camera is dragged nothing is hovered or snapped, and a pick read back from before the drag is
    /// dropped; the release hovers again.</summary>
    [Fact]
    public void CameraGesture_HoversAndSnapsToNothing()
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 50, 50, 20)]));
        var v = vm.Viewer;
        v.Hover(W / 2, H / 2);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, W / 2, H / 2, W, H, v.View.Visible);
        Assert.NotEqual(0u, id);

        v.SetCameraGesture(true);
        v.Hover(W / 2, H / 2);
        v.OnPicked(id, face, Vector3.Zero, true);
        Assert.True(v.View.CursorX < 0);
        Assert.Null(v.HoveredItem);
        Assert.False(v.Snap.IsSnap);

        v.SetCameraGesture(false);
        v.Hover(W / 2, H / 2);
        v.OnPicked(id, face, Vector3.Zero, true);
        Assert.Equal(W / 2, v.View.CursorX);
        Assert.NotNull(v.HoveredItem);
    }

    /// <summary>The scale bar is a 1, 2 or 5 × 10ⁿ of the display unit — 500 mil, not 393.7008 mil.</summary>
    [Theory]
    [InlineData(LayoutUnit.Mil, 0.0100)]      // 393.7 mil
    [InlineData(LayoutUnit.Um, 3.3e-5)]
    [InlineData(LayoutUnit.Mm, 0.0100)]
    public void ScaleBar_IsARoundNumberOfTheDisplayUnit(LayoutUnit unit, double targetMetres)
    {
        int dbu = LayoutUnits.DefaultDbuPerMicron;
        double unitMetres = LayoutUnits.ToDbu(1m, unit, dbu) * 1e-6 / dbu;
        double n = Viewer3DOverlay.ScaleBarLength(targetMetres, unit, dbu) / unitMetres;
        double mantissa = n / Math.Pow(10, Math.Floor(Math.Log10(n) + 1e-9));
        Assert.Contains(Math.Round(mantissa, 9), new[] { 1.0, 2.0, 5.0 });
        if (unit == LayoutUnit.Mil) Assert.Equal(500, n, 1e-6);
    }

    /// <summary>The snap marker is drawn in the snapped object's material colour; the grid and a material-less object keep
    /// the amber.</summary>
    [Fact]
    public void SnapMarker_TakesTheSnappedMaterialsColour()
    {
        var vm = Open(Write([Box("g", "Gold", 0, 0, 0, 50, 50, 20), Box("u", null, 100, 0, 0, 50, 50, 20)]));
        var s = vm.Viewer.Scene;
        var gold = vm.SceneObject("g")!;
        var bare = vm.SceneObject("u")!;
        Snap3DResult At(Snap3DKind kind, uint id) => new(kind, default, id, 0, 0, 0, 0, 0, 0);

        var c = Viewer3DOverlay.SnapColour(s, At(Snap3DKind.Vertex, gold.Id));
        Assert.Equal(((byte)gold.Rgba, (byte)(gold.Rgba >> 8), (byte)(gold.Rgba >> 16), (byte)255), (c.R, c.G, c.B, c.A));
        Assert.NotEqual(Viewer3DOverlay.SnapAmber, c);
        Assert.Equal(Viewer3DOverlay.SnapAmber, Viewer3DOverlay.SnapColour(s, At(Snap3DKind.Vertex, bare.Id)));
        Assert.Equal(Viewer3DOverlay.SnapAmber, Viewer3DOverlay.SnapColour(s, At(Snap3DKind.Grid, gold.Id)));
    }

    /// <summary>A double-click on an axis looks down it (Z Top, Y Front, X Right), again turns to the opposite view, and
    /// off the arms inside the ring is Isometric.</summary>
    [Fact]
    public void AxisIndicator_DoubleClickTurnsTheView()
    {
        var o = Viewer3DOverlay.AxisIndicatorCentre(H);
        var cam = new Camera3D { FovY = Camera3D.DefaultFovY, Distance = 1 };
        cam.SetStandardView(StandardView3D.Iso);
        Point Tip(in Camera3D c, Vector3 axis, double f = 1)
            => new(o.X + Viewer3DOverlay.AxisArm * f * Vector3.Dot(c.Right, axis), o.Y - Viewer3DOverlay.AxisArm * f * Vector3.Dot(c.Up, axis));

        Assert.Equal(StandardView3D.Top, Viewer3DOverlay.AxisIndicatorHit(cam, o, Tip(cam, Vector3.UnitZ)));
        Assert.Equal(StandardView3D.Front, Viewer3DOverlay.AxisIndicatorHit(cam, o, Tip(cam, Vector3.UnitY, 0.6)));
        Assert.Equal(StandardView3D.Right, Viewer3DOverlay.AxisIndicatorHit(cam, o, Tip(cam, Vector3.UnitX)));
        // Between the arms, inside the ring.
        var between = Tip(cam, -Vector3.UnitZ, 0.7);
        Assert.Equal(StandardView3D.Iso, Viewer3DOverlay.AxisIndicatorHit(cam, o, between));
        Assert.Null(Viewer3DOverlay.AxisIndicatorHit(cam, o, new Point(o.X + 200, o.Y)));

        cam.SetStandardView(StandardView3D.Top);
        Assert.Equal(StandardView3D.Bottom, Viewer3DOverlay.AxisIndicatorHit(cam, o, o));
        cam.SetStandardView(StandardView3D.Right);
        Assert.Equal(StandardView3D.Left, Viewer3DOverlay.AxisIndicatorHit(cam, o, o));
    }

    /// <summary>A click anywhere the drawing plane is shown lands on it — including where the plane is nearer the camera
    /// than the near clip plane (the lower part of a zoomed-out orthographic view, which refused as "behind the camera"),
    /// and the point found is the one under the cursor.</summary>
    [Theory]
    [InlineData(Projection3D.Orthographic)]
    [InlineData(Projection3D.Perspective)]
    public void DrawingPlane_UnderEveryPixel_IsFound_EvenNearerThanTheNearPlane(Projection3D projection)
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 20, 20, 20)]));
        var v = vm.Viewer;
        var cam = Camera3D.Fit(v.Scene.ContentMin, v.Scene.ContentMax, W / H, projection);
        cam.Distance *= 20;                                            // zoomed well out: the sphere is small in the view
        v.View.Camera = cam;
        int nearer = 0;
        foreach (float py in new[] { H * 0.5f, H * 0.8f, H * 0.97f })
            foreach (float px in new[] { W * 0.1f, W * 0.5f, W * 0.9f })
            {
                v.Hover(px, py);
                var p = vm.PlanePoint(vm.CursorInput(), out var why);
                Assert.True(p is not null, why);
                double per = CircuitRF.Design.ThreeD.C3dLowering.Metres(1, vm.Document.DbuPerMicron);
                var local = v.Scene.ToLocal(p!.Value.X * per, p.Value.Y * per, p.Value.Z * per);
                var (sx, sy, _) = cam.Project(local, W, H);
                Assert.InRange(sx, px - 1.5f, px + 1.5f);
                Assert.InRange(sy, py - 1.5f, py + 1.5f);
                if (cam.ViewDepth(local) < cam.DepthRange().Near) nearer++;
            }
        if (projection == Projection3D.Orthographic) Assert.True(nearer > 0, "no pixel's plane point lay nearer than the near plane");
    }

    /// <summary>The first box drawn into an EMPTY design does not move the view: the camera the user drew with is the one
    /// kept (it used to be fitted to the new box the moment it appeared).</summary>
    [Fact]
    public void FirstObjectInAnEmptyDesign_DoesNotMoveTheView()
    {
        var vm = Open(Write([]), allowEmpty: true);
        var v = vm.Viewer;
        Assert.Empty(v.Scene.Objects);
        var before = v.View.Camera;
        var world = v.Scene.ToWorld(before.Target);

        vm.Arm(C3dToolKind.Box);
        foreach (var (px, py) in new[] { (W * 0.45f, H * 0.55f), (W * 0.6f, H * 0.65f), (W * 0.6f, H * 0.5f) })
        {
            v.Hover(px, py);
            v.OnPicked(0, CircuitRF.Render.Scene3D.Scene3DVertex.NoFace, Vector3.Zero, false);
            v.Click(false);
        }
        Settle(vm);
        Assert.Single(vm.Document.Objects);
        Assert.NotEmpty(v.Scene.Objects);

        var after = v.View.Camera;
        var w = v.Scene.ToWorld(after.Target);
        Assert.Equal(world.X, w.X, 1e-9);
        Assert.Equal(world.Y, w.Y, 1e-9);
        Assert.Equal(world.Z, w.Z, 1e-9);
        Assert.Equal((before.Distance, before.Yaw, before.Pitch, before.Projection), (after.Distance, after.Yaw, after.Pitch, after.Projection));
    }

    /// <summary>A material-less box whose vertex is moved becomes a polyhedron and is still drawn as its wireframe: its
    /// edges are found with the corners welded by position, since a polyhedron's tessellation repeats each corner per
    /// face (by index it had no edges at all, and vanished).</summary>
    [Fact]
    public void MaterialLessBox_MadeAPolyhedronByAVertexMove_KeepsItsWireframe()
    {
        var vm = Open(Write([Box("lid", null, 0, 0, 0, 60, 40, 20)]));
        var v = vm.Viewer;
        int Lines() => v.Scene.LineBatches.Where(b => b.ObjectId == vm.SceneObject("lid")!.Id).Sum(b => b.VertexCount);
        Assert.Equal(24, Lines());                                     // a box's 12 edges

        var corner = new C3dPoint3(60 * Um, 40 * Um, 20 * Um);
        v.SelectMode = Scene3DSelectMode.Vertex;
        v.SetSelection([Scene3DItem.OfVertex(vm.SceneObject("lid")!.Id, v.Scene.ToLocal(corner.X * 1e-9, corner.Y * 1e-9, corner.Z * 1e-9))]);
        vm.Properties.Reload();
        vm.Properties.VertexZ = "32";
        vm.Properties.CommitVertex();
        Settle(vm);

        Assert.IsType<C3dPolyhedron>(vm.Document.Objects[0]);
        Assert.True(vm.SceneObject("lid")!.Wireframe);
        Assert.InRange(Lines(), 24, 48);                               // its edges, the fold's included
    }

    /// <summary>A design in a workspace with NO technology has no materials, so New Material… had nothing to open. It is
    /// given one: a built-in technology copied into the workspace's tech/ (never over a file already there), referenced by
    /// the design itself as one undo entry — and its materials then resolve.</summary>
    [Fact]
    public void ADesignWithNoTechnology_IsGivenOne_AndItsMaterialsResolve()
    {
        string c3d = Write([Box("b", null, 0, 0, 0, 50, 50, 20)]);
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        File.Delete(Path.Combine(ws, "tech.ctech"));
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile());
        var vm = Open(c3d);
        Assert.Empty(vm.Materials);

        string id = CircuitRF.Design.Workspace.WorkspaceCreate.DefaultTechnologyId;
        string tech = CircuitRF.Design.Workspace.WorkspaceCreate.InstallTechnology(ws, id);
        Assert.Equal(Path.Combine(ws, "tech", id + ".ctech"), tech);
        File.AppendAllText(tech, " ");                                    // the workspace's own copy from here on …
        Assert.Equal(tech, CircuitRF.Design.Workspace.WorkspaceCreate.InstallTechnology(ws, id));
        Assert.EndsWith(" ", File.ReadAllText(tech));                     // … never overwritten

        vm.UseTechnology(tech);
        Settle(vm);
        Assert.Equal($"../../tech/{id}.ctech", vm.Document.TechRef);
        Assert.NotEmpty(vm.Materials);
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Null(vm.Document.TechRef);
    }

    /// <summary>The clip plane cuts the model, not the drawing grid: the grid's fragment shader never asks it.</summary>
    [Fact]
    public void Grid_IsNotCutByTheClipPlane()
    {
        string wgsl = File.ReadAllText(Path.Combine(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(), "src", "Ui", "Viewer3D", "Shaders", "scene.wgsl"));
        int start = wgsl.IndexOf("fn fs_grid(", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = wgsl.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.DoesNotContain("clipped(", wgsl[start..end]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private C3dEditorViewModel Open(string c3d, bool allowEmpty = false)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        if (!allowEmpty) Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)),
                       "the scene never settled");

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
}
