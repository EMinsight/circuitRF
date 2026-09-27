// ================================================================
//  Snap3DGateTests.cs — the gate for brief-em3d-44: 3D snapping, on the CPU path (R-em3d44-2e), counters and
//  view-model state only — no pixel is looked at (overview §1n). Gate 8 compares the Metal patch with the CPU
//  one where Metal exists; gate 9 is Firewall.Tests.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class Snap3DGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;                    // a micrometre in metres
    private const long Um = 1000;                       // DBU per µm at the default 1000 DBU/µm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-snap44-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public Snap3DGateTests() => Snap3DPreference.TestOverrideActive = true;   // never the developer's preferences

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. near, not all ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_AHoverExaminesTheFeaturesOfTheFacesInThePatch_TheSameAt100And10000Boxes()
    {
        var counts = new List<(Snap3DCounters Snap, long Triangles, Snap3DResult Result)>();
        foreach (int n in new[] { 10, 100 })
        {
            var scene = Grid(n);
            var corner = new Point3(100 * UmM, 100 * UmM, 5 * UmM);          // box (5, 5)'s top corner
            var (patch, query, r) = Hover(scene, TopAt(scene, corner, 0.1f, dx: -3, dy: 3));
            counts.Add((query.Counters, patch.TrianglesRasterized, r));
            Assert.Equal(Snap3DKind.Vertex, r.Kind);
            AssertAt(corner, r.World);
        }
        Assert.Equal(counts[0].Snap.FeaturesExamined, counts[1].Snap.FeaturesExamined);
        Assert.Equal(counts[0].Snap.FacesInPatch, counts[1].Snap.FacesInPatch);
        Assert.Equal(counts[0].Triangles, counts[1].Triangles);
        // A box face is 4 corners, 4 edges (each a midpoint and a nearest point) and a centre: 13.
        Assert.InRange(counts[1].Snap.FeaturesExamined, 1, counts[1].Snap.FacesInPatch * 13);
    }

    // ── 2. instances are free ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_A30By30ArrayShares_OneTable_AndAHoverTransformsOnlyTheElementsInThePatch()
    {
        string ws = Workspace();
        // The child: a 24-sided prism, 50 µm across the corners — 48 corners, 72 edges and 26 faces.
        var outline = Enumerable.Range(0, 24).Select(k => new C3dPoint2(
            (long)Math.Round(50 * Um * Math.Cos(2 * Math.PI * k / 24)), (long)Math.Round(50 * Um * Math.Sin(2 * Math.PI * k / 24)))).ToList();
        WriteC3d(ws, "die", new C3dDocument { Objects = [new C3dPrism { Name = "pad", Material = "Gold", Outline = outline, Height = 10 * Um }] });
        var vm = Open(WriteC3d(ws, "pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U", CellRef = "../../die", Array = new C3dArray { Counts = [30, 30, 1], Pitch = new C3dPoint3(200 * Um, 200 * Um, 0) } }],
        }), ws);
        var v = vm.Viewer;
        var scene = v.Scene;
        // 900 elements, and (3D editor round 3) the air box a design with no setup draws: six faces and its edges.
        Assert.Equal(900, scene.Objects.Count(o => o.Kind != CircuitRF.Render.Scene3D.Scene3DKind.Boundary));
        Assert.Equal(7, scene.Objects.Count(o => o.Kind == CircuitRF.Render.Scene3D.Scene3DKind.Boundary));
        Assert.Equal(1, scene.FeatureTableCount);                           // the child is tabled once
        Assert.True(scene.Features[0].Table!.FeatureCount >= 200, $"{scene.Features[0].Table!.FeatureCount} features");
        Assert.All(scene.Features.Where(f => f.Table is not null), f => Assert.True(f.Shared));   // the air box's parts have none

        // Element [12,17]'s first corner, on its top.
        var corner = new Point3((12 * 200 + outline[0].U / (double)Um) * UmM, (17 * 200 + outline[0].V / (double)Um) * UmM, 10 * UmM);
        v.View.Camera = TopAt(scene, corner, 0.25f, dx: 2, dy: 2);
        HoverVm(v, W / 2, H / 2);
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);
        Assert.Equal("U[12,17,0]/pad", scene.Object(v.Snap.Object)!.Name);
        Assert.InRange(v.SnapQuery.Counters.ElementsTransformed, 1, 2);
        // Snapping into an instance is exact: its placement is a translation, at the parent's scale.
        var p = vm.ToDocumentPoint(v.Snap);
        Assert.True(p.Exact);
        Assert.Equal(new C3dPoint3(12 * 200 * Um + outline[0].U, 17 * 200 * Um + outline[0].V, 10 * Um), p.Dbu);
    }

    // ── 3. visible only; X-ray ──────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AVertexBehindAFace_IsNoCandidate_AndWithXRayItIs()
    {
        var scene = Build(
            new Em3dSolid("lid", "Gold", Em3dRole.Conductor, BoxUm(0, 0, 20, 100, 100, 30), 1),
            new Em3dSolid("block", "Alumina", Em3dRole.Dielectric, BoxUm(40, 40, 0, 60, 60, 10), 2));
        uint block = scene.Objects.Single(o => o.Name == "block").Id;
        Assert.True(scene.Object(block)!.Translucent);
        var corner = new Point3(40 * UmM, 40 * UmM, 10 * UmM);
        var cam = TopAt(scene, corner, 0.5f, dx: 2, dy: -2);

        var (_, _, hidden) = Hover(scene, cam);
        Assert.NotEqual(block, hidden.Object);                               // the lid hides it: not a candidate

        var clip = new ClipPlane3D { Enabled = true, Axis = ClipAxis3D.Z, Offset = 1 };   // keeps everything
        var (_, q, xray) = Hover(scene, cam, clip: clip);
        Assert.Equal(Snap3DKind.Vertex, xray.Kind);
        Assert.Equal(block, xray.Object);
        AssertAt(corner, xray.World);
        Assert.Equal(1, q.Counters.XRayObjects);
    }

    // ── 4. priority ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AVertexBeatsANearerMidpoint()
    {
        // The box's front edge is 12 px long on screen: the cursor 4 px from its corner is 2 px from its midpoint.
        var scene = Build(new Em3dSolid("b", "Gold", Em3dRole.Conductor, BoxUm(0, 0, 0, 100, 100, 10), 1));
        var corner = new Point3(0, 0, 10 * UmM);
        var (_, _, r) = Hover(scene, TopAt(scene, corner, 100f / 12, dx: -4, dy: 0.5f));
        Assert.Equal(Snap3DKind.Vertex, r.Kind);
        AssertAt(corner, r.World);
        Assert.InRange(r.Distance, 3.5f, 4.5f);

        var (_, _, mid) = Hover(scene, TopAt(scene, corner, 100f / 12, dx: -4, dy: 0.5f), Snap3DKinds.All & ~Snap3DKinds.Vertex);
        Assert.Equal(Snap3DKind.Midpoint, mid.Kind);                         // and without vertices, the midpoint
        Assert.True(mid.Distance < r.Distance);
    }

    // ── 5. self-exclusion ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_DuringAFaceDrag_ThatFacesCorners_AtTheOldAndNewPositions_NeverSnap()
    {
        string ws = Workspace();
        var vm = Open(WriteC3d(ws, "cell", new C3dDocument { Objects = [Box("b", 0, 0, 0, 100, 100, 100)] }), ws);
        var v = vm.Viewer;
        Iso(v);
        var top = new Point3(0, 0, 100 * UmM);

        HoverAt(v, top);
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);                        // the control: it snaps
        AssertAt(top, v.Snap.World);

        var g = vm.BeginGesture("Move face zmax", [0], excludeObjects: false);
        var b = vm.SceneObject("b")!;
        g.ExcludeFace("b", b.FaceNames.ToList().IndexOf("zmax"));
        HoverAt(v, top);                                                     // the old position
        Assert.NotEqual(Snap3DKind.Vertex, v.Snap.Kind);

        g.Update(o => o.Placement.Origin = new C3dPoint3(30 * Um, 0, 0));
        Settle(vm);
        var moved = new Point3(30 * UmM, 0, 100 * UmM);
        HoverAt(v, moved);                                                   // the new position
        Assert.NotEqual(Snap3DKind.Vertex, v.Snap.Kind);
        HoverAt(v, new Point3(30 * UmM, 0, 0));                              // a corner the drag does not move still snaps
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);

        g.Commit();
        Assert.Null(v.SnapExclusion);
        HoverAt(v, moved);
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);
    }

    // ── 6. exactness ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_AnUnrotatedCornerIsAnExactDbuPoint_ARotatedOneIsFlagged()
    {
        string ws = Workspace();
        var rotated = Box("r", 300, 0, 0, 100, 100, 10);
        rotated.Placement.Rotate.Add(new C3dRotation { Axis = C3dAxis.Z, Deg = 30 });
        var vm = Open(WriteC3d(ws, "cell", new C3dDocument { Objects = [Box("b", 0, 0, 0, 100, 100, 10), rotated] }), ws);
        var v = vm.Viewer;
        Iso(v);

        HoverAt(v, NearestCorner(v, "b"));
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);
        var exact = vm.ToDocumentPoint(v.Snap);
        Assert.True(exact.Exact);
        Assert.Contains(exact.Dbu.X, new long[] { 0, 100 * Um });
        Assert.Contains(exact.Dbu.Y, new long[] { 0, 100 * Um });
        Assert.Contains(exact.Dbu.Z, new long[] { 0, 10 * Um });
        Assert.DoesNotContain("≈", v.SnapText);
        Assert.StartsWith("Vertex · (", v.SnapText);

        HoverAt(v, NearestCorner(v, "r"));
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);
        Assert.Equal("r", v.Scene.Object(v.Snap.Object)!.Name);
        Assert.False(vm.ToDocumentPoint(v.Snap).Exact);
        Assert.Contains("≈", v.SnapText);
    }

    // ── 7. zero allocations per hover ────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_AThousandHovers_AllocateNothing_OnTheCpuPath()
    {
        var scene = Grid(10);
        var cam = TopAt(scene, new Point3(100 * UmM, 100 * UmM, 5 * UmM), 0.1f, dx: -3, dy: 3);
        var patch = new Scene3DIdPatch();
        var query = new SnapQuery3D();
        var grid = new Snap3DGrid(C3dPlane.XY, 0, 1 * Um, 1000);
        var settings = new Snap3DSettings(Snap3DKinds.All, 8, Grid: grid);
        var visible = new bool[scene.Objects.Length];
        Array.Fill(visible, true);
        int size = Scene3DIdPatch.SizeFor(8);
        int snaps = 0;
        void Run(int i)
        {
            float x = W / 2 + i % 7 - 3, y = H / 2 + i % 5 - 2;
            patch.Render(scene, cam, x, y, W, H, size, visible);
            if (query.Query(scene, patch, settings, null, visible, default).IsSnap) snaps++;
        }
        for (int i = 0; i < 50; i++) Run(i);                                  // steady state
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Run(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1050, snaps);
    }

    // ── 8. the GPU's patch is the CPU's ──────────────────────────────────────────────────────

    /// <summary>macOS only: the Metal ID pass narrowed to a 17 × 17 patch reads back the (object, face) the CPU
    /// patch computes — texel for texel, save where the two sides of an edge differ by a rasterisation rule.</summary>
    [Fact]
    public void Gate8_TheMetalPatch_AgreesWithTheCpuPatch()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scene = Build(
            new Em3dSolid("a", "Gold", Em3dRole.Conductor, BoxUm(0, 0, 0, 100, 100, 20), 1),
            new Em3dSolid("b", "Gold", Em3dRole.Conductor, BoxUm(60, 60, 20, 90, 90, 40), 2),
            new Em3dSolid("sub", "Alumina", Em3dRole.Dielectric, BoxUm(-50, -50, -30, 150, 150, 0), 3));
        const int w = 320, h = 200, size = 17;
        var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        metal.CreateOffscreenImages(w, h, 1);
        using var session = new Viewer3DSession(() => metal);
        session.EnsureBackend();
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h) };
        view.Camera.Yaw = -0.7f; view.Camera.Pitch = 0.5f;
        view.Adopt(scene, null);
        // The corner of box b nearest the camera: three faces of b and one of a meet around it.
        var (cx, cy, _) = view.Camera.Project(scene.ToLocal(90 * UmM, 60 * UmM, 20 * UmM), w, h);
        view.CursorX = MathF.Floor(cx) + 0.5f; view.CursorY = MathF.Floor(cy) + 0.5f;

        var plan = new Scene3DFramePlan { PickSize = size };
        for (ulong f = 1; f <= 4; f++)
        {
            plan.Plan(scene, view, w, h, metal.FlipY, pick: true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(0, plan, f, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        }
        var gpu = metal.PickPatch;
        Assert.NotNull(gpu);
        Assert.Equal(size, gpu!.Size);
        var cpu = new Scene3DIdPatch();
        cpu.Render(scene, view.Camera, view.CursorX, view.CursorY, w, h, size, view.Visible);

        int same = 0, boundary = 0;
        var pairs = new HashSet<(uint, uint)>();
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                int k = j * size + i;
                if (gpu.Ids[k] == cpu.Ids[k] && gpu.Faces[k] == cpu.Faces[k]) { same++; if (cpu.Ids[k] != 0) pairs.Add((cpu.Ids[k], cpu.Faces[k])); continue; }
                bool neighbour = false;
                for (int dj = -1; dj <= 1 && !neighbour; dj++)
                    for (int di = -1; di <= 1 && !neighbour; di++)
                    {
                        int n = cpu.IndexOf(cpu.X0 + i + di, cpu.Y0 + j + dj);
                        neighbour = n >= 0 && cpu.Ids[n] == gpu.Ids[k] && cpu.Faces[n] == gpu.Faces[k];
                    }
                Assert.True(neighbour, $"texel ({i}, {j}): GPU ({gpu.Ids[k]}, {gpu.Faces[k]}), CPU ({cpu.Ids[k]}, {cpu.Faces[k]})");
                boundary++;
            }
        Assert.True(same >= size * size * 0.9, $"{same} of {size * size} texels agreed exactly ({boundary} on a boundary)");
        Assert.True(pairs.Count >= 3, $"only {pairs.Count} distinct faces in the patch");
        Assert.Equal(metal.PickedId, gpu.Ids[(size / 2) * size + size / 2]);   // the centre is the old 1 × 1 answer
    }

    // ── fixtures: scenes built directly ──────────────────────────────────────────────────────

    /// <summary>The same point to far below a DBU: 100 × 1e-6 and the elaboration's exact 1e-4 differ in the last bit.</summary>
    private static void AssertAt(Point3 want, Point3 got)
        => Assert.True(Math.Abs(want.X - got.X) < 1e-15 && Math.Abs(want.Y - got.Y) < 1e-15 && Math.Abs(want.Z - got.Z) < 1e-15,
                       $"expected {want}, got {got}");

    private static Em3dBox BoxUm(double x0, double y0, double z0, double x1, double y1, double z1)
        => new(new Point3(x0 * UmM, y0 * UmM, z0 * UmM), new Point3(x1 * UmM, y1 * UmM, z1 * UmM));

    private static Scene3DModel Build(params Em3dSolid[] solids)
    {
        double x0 = double.MaxValue, y0 = x0, z0 = x0, x1 = double.MinValue, y1 = x1, z1 = x1;
        foreach (var s in solids)
        {
            var b = Em3dProblem.Bounds(s.Primitive);
            x0 = Math.Min(x0, b.X0); y0 = Math.Min(y0, b.Y0); z0 = Math.Min(z0, b.Z0);
            x1 = Math.Max(x1, b.X1); y1 = Math.Max(y1, b.Y1); z1 = Math.Max(z1, b.Z1);
        }
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(solids,
            [], [new Em3dMaterial("Gold", 1, null, 0, 1, 4.1e7), new Em3dMaterial("Alumina", 9.8, null, 1e-4, 1, 0)], [],
            new Em3dAirBox(new Point3(x0, y0, z0), new Point3(x1, y1, z1), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        return Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false));
    }

    /// <summary>n × n boxes, 10 × 10 × 5 µm at a 20 µm pitch.</summary>
    private static Scene3DModel Grid(int n)
    {
        var solids = new List<Em3dSolid>(n * n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                solids.Add(new Em3dSolid($"b{i}_{j}", "Gold", Em3dRole.Conductor, BoxUm(i * 20, j * 20, 0, i * 20 + 10, j * 20 + 10, 5), solids.Count + 1));
        return Build([.. solids]);
    }

    /// <summary>
    /// Straight down, orthographic, at <paramref name="umPerPixel"/> µm a pixel, with <paramref name="p"/>
    /// (dx, dy) pixels from the view's centre — where every hover here puts the cursor.
    /// </summary>
    private static Camera3D TopAt(Scene3DModel scene, Point3 p, float umPerPixel, float dx, float dy)
    {
        var c = new Camera3D { FovY = Camera3D.DefaultFovY, Projection = Projection3D.Orthographic };
        c.SetStandardView(StandardView3D.Top);
        c.SceneCentre = (scene.BoundsMin + scene.BoundsMax) * 0.5f;
        c.SceneRadius = (scene.BoundsMax - scene.BoundsMin).Length() * 0.5f;
        float m = umPerPixel * (float)UmM;
        c.Distance = m * H / (2 * MathF.Tan(c.FovY * 0.5f));
        // Screen x is world +x and screen y is world −y: the point is (dx, dy) px from the cursor at the centre.
        c.Target = scene.ToLocal(p.X - dx * m, p.Y + dy * m, p.Z);
        return c;
    }

    private static (Scene3DIdPatch Patch, SnapQuery3D Query, Snap3DResult Result) Hover(Scene3DModel scene, Camera3D cam,
        Snap3DKinds kinds = Snap3DKinds.All & ~Snap3DKinds.Grid, ClipPlane3D clip = default)
    {
        var patch = new Scene3DIdPatch();
        patch.Render(scene, cam, W / 2, H / 2, W, H, Scene3DIdPatch.SizeFor(8), [], clip);
        var query = new SnapQuery3D();
        var r = query.Query(scene, patch, new Snap3DSettings(kinds, 8), null, [], clip);
        return (patch, query, r);
    }

    // ── fixtures: the editor ─────────────────────────────────────────────────────────────────

    private C3dEditorViewModel Open(string c3d, string ws)
    {
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        Assert.True(vm.Viewer.SnapEnabled);
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    private static void Iso(Viewer3DViewModel v)
    {
        v.View.Camera = Camera3D.Fit(v.Scene.ContentMin, v.Scene.ContentMax, W / H);
        v.View.Camera.Yaw = -0.9f; v.View.Camera.Pitch = 0.5f;
    }

    /// <summary>A hover at (<paramref name="x"/>, <paramref name="y"/>) and the ID pass's answer for it — the fake
    /// backend reads one texel, so the snap renders its patch on the CPU.</summary>
    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    /// <summary>A hover 1.5 px from where world point <paramref name="p"/> lands.</summary>
    private static void HoverAt(Viewer3DViewModel v, Point3 p)
    {
        var (x, y, front) = v.View.Camera.Project(v.Scene.ToLocal(p.X, p.Y, p.Z), W, H);
        Assert.True(front);
        HoverVm(v, x + 1.5f, y + 0.5f);
    }

    /// <summary>The corner of object <paramref name="name"/> nearest the camera — certainly visible.</summary>
    private static Point3 NearestCorner(Viewer3DViewModel v, string name)
    {
        var o = v.Scene.Objects.Single(x => x.Name == name);
        var f = v.Scene.FeaturesOf(o.Id);
        return Enumerable.Range(0, f.Table!.Vertices.Length).Select(f.Vertex)
                         .MinBy(p => v.View.Camera.ViewDepth(v.Scene.ToLocal(p.X, p.Y, p.Z)));
    }

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Gold", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }
}
