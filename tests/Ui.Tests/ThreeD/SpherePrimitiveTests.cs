// ================================================================
//  SpherePrimitiveTests.cs — the gate for brief-em3d-102: a sphere primitive in the 3D editor. Its file form, the draw
//  tool, validation, lowering and the scene, the solver writers (output only, no solve), the refusals a curved face
//  gives, the centre as a fixed point, thermal's inside test, and the old-worker sentence. The real worker's gate is in
//  GeometryKernelWorkerTests; the toolbar order in EditorToolbarKeysTests.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Input;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class SpherePrimitiveTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sphere102-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public SpherePrimitiveTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. round trip ────────────────────────────────────────────────────────────────────────

    /// <summary>A literal sphere, one whose Radius is a VAR, a placed cell holding one, and a box minus a sphere: each file
    /// loads and saves byte for byte.</summary>
    [Fact]
    public void Gate1_LiteralBoundPlacedAndBoolean_SaveByteIdentical()
    {
        var bound = Sphere("ball", 0, 0, 0, 1);
        C3dBindings.SetExpr(bound, C3dBindings.SpecOf(typeof(C3dSphere), nameof(C3dSphere.Radius))!, 0, new C3dExpr("r_ball", "Um"));
        var top = new C3dDocument
        {
            Variables = [new C3dVariable { Name = "r_ball", Expression = "40", Unit = "Um" }],
            Objects =
            [
                Sphere("lit", 10, 20, 30, 50), bound,
                new C3dBoolean { Name = "dimple", Op = C3dBooleanOp.Subtract, Blank = Box(null, 0, 0, 0, 200, 200, 100), Tools = [Sphere("cut", 100, 100, 100, 40)] },
            ],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Child" }],
        };
        var child = new C3dDocument { Objects = [Sphere("bump", 0, 0, 0, 25)] };
        foreach (var doc in new[] { top, child })
        {
            string text = C3dPersistence.Serialize(doc);
            Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
        }
        string json = C3dPersistence.Serialize(top);
        Assert.Contains("\"$type\": \"Sphere\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Radius\": { \"Expr\": \"r_ball\", \"Unit\": \"Um\" }", json, StringComparison.Ordinal);
    }

    // ── 2. draw ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A click for the centre and a typed 250um commit one sphere CENTRED on the plane (D2: the click is the centre),
    /// in one undo entry; undo removes it and redo puts it back.</summary>
    [Fact]
    public void Gate2_AClickThenTypedRadius_CommitsOneSphereCentredOnThePlane_InOneUndoEntry()
    {
        var vm = Open();
        vm.SetPlane(new DrawingPlane(C3dPlane.XZ, 10 * Um));       // off the origin, so "centred on the plane" is visible
        vm.Arm(C3dToolKind.Sphere);
        Assert.IsType<SphereTool>(vm.Tool);
        ClickAt(vm, 20, 10, 30);
        Assert.Equal(1, vm.Tool!.Step);
        Assert.True(vm.Viewer.HandleKey(Key.D2, KeyModifiers.None, false));
        vm.FieldText = "250um";
        vm.FieldEnter();

        var s = Assert.IsType<C3dSphere>(vm.Document.Objects[^1]);
        Assert.Equal(("sphere1", "Gold"), (s.Name, s.Material));
        Assert.Equal(new C3dPoint3(20 * Um, 10 * Um, 30 * Um), s.Centre);
        Assert.Equal(250 * Um, s.Radius);
        Assert.Equal(1, vm.UndoEntries);
        Assert.StartsWith("Drew sphere \"sphere1\"", vm.StatusMessage, StringComparison.Ordinal);

        vm.UndoRedo.Undo();
        Assert.DoesNotContain(vm.Document.Objects, o => o is C3dSphere);
        vm.UndoRedo.Redo();
        Assert.Equal(250 * Um, Assert.IsType<C3dSphere>(vm.Document.Objects[^1]).Radius);
    }

    // ── 3. validation ────────────────────────────────────────────────────────────────────────

    /// <summary>A zero radius and a negative typed one each refuse with the sentence, and <c>check</c> reports the first.</summary>
    [Fact]
    public void Gate3_ZeroAndNegativeTypedRadius_Refuse_AndCheckReportsIt()
    {
        var zero = new C3dDocument { Objects = [Sphere("ball", 0, 0, 0, 0)] };
        var found = C3dValidation.Validate(zero);
        Assert.Contains(found, d => d.Render().Contains("'ball'", StringComparison.Ordinal) && d.Render().Contains("its radius is not positive", StringComparison.Ordinal));

        var neg = Sphere("ball", 0, 0, 0, 10);
        C3dBindings.SetExpr(neg, C3dBindings.SpecOf(typeof(C3dSphere), nameof(C3dSphere.Radius))!, 0, new C3dExpr("-5", "Um"));
        var res = C3dResolver.Resolve(new C3dDocument { Objects = [neg] }, C3dCell.None);
        Assert.Contains(res.FieldErrors, e => e.Item == "ball" && e.Message.Contains("a size is positive", StringComparison.Ordinal));

        string path = WriteC3d(Workspace(), "Ball", zero);
        var run = RunCli("check", path);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("its radius is not positive", run.StdOut + run.StdErr, StringComparison.Ordinal);
    }

    // ── 4. lowering and the scene ────────────────────────────────────────────────────────────

    /// <summary>Under a rotated placement the sphere lowers to an Em3dSphere whose centre is moved and whose radius is not.</summary>
    [Fact]
    public void Gate4a_ARotatedPlacement_MovesTheCentre_AndKeepsTheRadius()
    {
        var s = Sphere("ball", 10, 0, 0, 5);
        s.Placement = new C3dPlacement { Origin = new C3dPoint3(0, 0, 7 * Um), Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 90 }] };
        var lowered = C3dLowering.Lower(s, C3dTransform.Identity, 1000)!;
        Assert.Equal(C3dLowering.KindSphere, lowered.Kind);
        Assert.Equal(["surface"], lowered.FaceNames);
        var sp = Assert.IsType<Em3dSphere>(lowered.Solid);
        Assert.Equal(0, sp.Center.X, 12);
        Assert.Equal(10 * UmM, sp.Center.Y, 12);
        Assert.Equal(7 * UmM, sp.Center.Z, 12);
        Assert.Equal(5 * UmM, sp.Radius, 15);
    }

    /// <summary>A drawn sphere is its material's kind and colour (a box of the same material beside it), its triangles carry
    /// face 0, <c>surface</c>, and it draws no feature edge; Face mode selects that face.</summary>
    [Fact]
    public void Gate4b_TheSceneDrawsItAsItsMaterial_WithOneNamedCurvedFace()
    {
        var vm = Open(Sphere("ball", 20, 20, 30, 15));
        var ball = vm.SceneObject("ball")!;
        var pad = vm.SceneObject("pad")!;
        Assert.Equal(Scene3DKind.Conductor, ball.Kind);
        Assert.Equal(pad.Kind, ball.Kind);
        Assert.Equal(pad.Rgba, ball.Rgba);
        Assert.Equal(["surface"], ball.FaceNames);
        Assert.Equal(0, vm.Viewer.Scene.FeaturesOf(ball.Id).Table!.Vertices.Length);   // no corners: no feature edge

        SelectFace(vm, "ball", "surface");
        Assert.Equal("surface", vm.FaceSelection()!.Value.Face);
    }

    /// <summary>A sphere with no named face and no origin — a ball — keeps its unnamed face and draws as a wire.</summary>
    [Fact]
    public void Gate4c_ABall_KeepsTheUnknownFace_AndItsWireKind()
    {
        var problem = new Em3dProblem([new Em3dSolid("ball", "Au", Em3dRole.Conductor, new Em3dSphere(new(0, 0, 0), 1e-5), 1)], [],
                                      [new Em3dMaterial("Au", 1, null, 0, 1, 4.1e7)], [],
                                      new Em3dAirBox(new(-1e-4, -1e-4, -1e-4), new(1e-4, 1e-4, 1e-4), new Em3dFaces(Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec)),
                                      new Em3dFrequency(1e9, 2e9, 2, Em3dSweepKind.Linear), 20);
        var scene = Scene3DBuilder.Build(problem, 1);
        var o = scene.Objects.Single(x => x.Name == "ball");
        Assert.Equal(Scene3DKind.Wire, o.Kind);
        Assert.Null(o.CapCentres);
        Assert.All(Enumerable.Range(0, o.VertexCount), i => Assert.Equal((uint)Scene3DBuilder.FaceUnknown, scene.Vertices[o.FirstVertex + i].Face));
    }

    /// <summary>§2f — a section through the centre takes the tessellated default and is one closed outline at the radius,
    /// though the equator's vertices lie exactly on the plane.</summary>
    [Fact]
    public void Gate4d_ASectionThroughTheCentre_IsOneClosedOutline()
    {
        var mesh = Em3dTessellation.Of(new Em3dSolid("ball", "Au", Em3dRole.Conductor, new Em3dSphere(new(1e-4, 2e-4, 3e-4), 5e-5), 1));
        var loop = Assert.Single(Em3dSectionScene.MeshCut(mesh, 2, 3e-4));
        Assert.True(loop.Count >= Em3dTessellation.SphereSegments);
        Assert.All(loop, q => Assert.InRange(Math.Sqrt((q.X - 1e-4) * (q.X - 1e-4) + (q.Y - 2e-4) * (q.Y - 2e-4)), 4.9e-5, 5.0001e-5));
    }

    // ── 5. the solver writers ────────────────────────────────────────────────────────────────

    /// <summary>A one-sphere problem reaches Gmsh's script as <c>Sphere(</c> and CSXCAD as <c>&lt;Sphere</c>. Writer output only.</summary>
    [Fact]
    public void Gate5_TheWriters_SpellTheSphere()
    {
        var p = OneSphereProblem();
        var geo = GmshGeoWriter.Write(p, PalaceSettings.Resolve(new CemPalace()));
        Assert.True(geo.Ok, geo.Refusal);
        Assert.Contains("Sphere(", geo.Geo, StringComparison.Ordinal);

        var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
        var csx = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.True(csx.Ok, csx.Refusal);
        Assert.Contains("<Sphere", csx.Model, StringComparison.Ordinal);
    }

    // ── 7. what the curved surface refuses ───────────────────────────────────────────────────

    /// <summary>A boundary on <c>surface</c> is refused as curved, by name.</summary>
    [Fact]
    public void Gate7a_ABoundaryOnTheSurface_IsRefusedAsCurved()
    {
        Assert.Equal(["surface"], Em3dFaceGeometry.FaceNames(new Em3dSphere(new(0, 0, 0), 1)));
        Assert.Null(Em3dFaceGeometry.Pieces(new Em3dSphere(new(0, 0, 0), 1), "surface", out string? why));
        Assert.Equal("it is a sphere's curved surface, and a boundary is placed on a flat face", why);
    }

    /// <summary>Every face edit, Map Image, Drawing Plane from Face and Convert to Polyhedron: each refused or disabled, saying
    /// why. One editor, one row per surface.</summary>
    [Fact]
    public void Gate7b_EveryFaceCommandOnTheSurface_IsRefusedOrDisabledWithAReason()
    {
        var vm = Open(Sphere("ball", 20, 20, 30, 15));
        var ball = vm.SceneObject("ball")!;
        SelectFace(vm, "ball", "surface");
        var rows = new (string What, Func<string?> Why)[]
        {
            ("Move Along Normal", () => { vm.StartPushPull(); return vm.StatusMessage; }),
            ("Move", () => { vm.StartFaceMove(); return vm.StatusMessage; }),
            ("Extrude to New Solid", () => { vm.StartExtrudeFace(); return vm.StatusMessage; }),
            ("Align to Face", () => { vm.StartAlignToFace(); return vm.StatusMessage; }),
        };
        foreach (var (what, why) in rows)
        {
            vm.StatusMessage = "";
            Assert.True(why() == C3dFaceEditor.SphereFaceEdit, what);
            Assert.Null(vm.Tool);
        }
        foreach (string which in new[] { "PushPull", "FaceMove", "ExtrudeFace", "AlignFace", "CopySheet" })
            Assert.False(vm.CanRunModify(which), which);
        var menu = vm.DrawMenuItems().Where(i => i.Header is "Move Along Normal" or "Move" or "Extrude to New Solid" or "Align to Face…").ToList();
        Assert.Equal(4, menu.Count);
        Assert.All(menu, i => Assert.True(!i.Enabled && i.Tip == C3dFaceEditor.SphereFaceEdit, i.Header));

        Assert.Equal(C3dImages.CurvedFace("surface"), vm.MapImageRefusal(ball, 0));
        Assert.Equal("Face surface is curved: a drawing plane lies on a flat face.", vm.PlaneFromFace(ball.Id, 0));

        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(ball.Id)]);
        Assert.DoesNotContain(vm.DrawMenuItems(), i => i.Header == "Convert to Polyhedron");
        Assert.False(vm.CanRunModify("ConvertPoly"));
        vm.ConvertToPolyhedron();
        Assert.Equal(C3dFaceEditor.SphereConvert, vm.StatusMessage);
        Assert.IsType<C3dSphere>(vm.Document.Objects.Single(o => o.Name == "ball"));
        Assert.Equal(C3dFillets.SphereRefused(C3dEdgeOp.Fillet), vm.EdgeOpRefusal(C3dEdgeOp.Fillet));
    }

    // ── 8. the centre snaps and measures, and does not move ──────────────────────────────────

    [Fact]
    public void Gate8_TheCentre_IsTheVertexModeCandidate_AndAVertexMoveIsRefused()
    {
        var vm = Open(Sphere("ball", 20, 20, 30, 15));
        var v = vm.Viewer;
        var ball = vm.SceneObject("ball")!;
        double M(long dbu) => C3dLowering.Metres(dbu, 1000);
        var centre = new Point3(M(20 * Um), M(20 * Um), M(30 * Um));
        Assert.Equal([centre], ball.CapCentres!);
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(centre.X, centre.Y, centre.Z), W, H);
        Assert.True(front);
        var near = Scene3DFaces.NearestVertexOnScreen(v.Scene, ball.Id, 0, v.View.Camera, sx + 2, sy, W, H, Scene3DSnap.RadiusPixels);
        Assert.Equal(v.Scene.ToLocal(centre.X, centre.Y, centre.Z), near);

        v.SelectMode = Scene3DSelectMode.Vertex;
        v.SetSelection([Scene3DItem.OfVertex(ball.Id, near!.Value)]);
        var sel = vm.VertexSelection()!.Value;
        Assert.Equal((-1, new C3dPoint3(20 * Um, 20 * Um, 30 * Um)), (sel.Vertex, sel.World));
        vm.StartVertexMove();
        Assert.Equal("A sphere's centre snaps and measures but does not move.", vm.StatusMessage);
        Assert.Null(vm.Tool);
    }

    // ── 9. thermal's inside test ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_InsideIsTheBall_NotItsBoundingBox()
    {
        var s = new Em3dSphere(new(1, 2, 3), 1);
        Assert.True(C3dThermal.Inside(s, new Point3(1, 2, 3), 1e-9));
        Assert.False(C3dThermal.Inside(s, new Point3(1.9, 2.9, 3.9), 1e-9));   // in the box's corner, outside the sphere
    }

    // ── §3c: a worker older than spheres ─────────────────────────────────────────────────────

    [Fact]
    public void AnOldWorkersUnknownKind_IsReportedAsAWorkerToRebuild()
    {
        var reply = new GeometryKernelMessage(new JsonObject
        {
            ["ok"] = false, ["code"] = "tree.invalid", ["object"] = "dimple", ["detail"] = "this worker cannot build a \"sphere\"",
        });
        Assert.Equal("This geometry worker predates spheres; rebuild it (tools/geometry-worker/build.sh).",
                     GeometryKernel.Refused(reply, "dimple").Message);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static C3dSphere Sphere(string name, long x, long y, long z, long r)
        => new() { Name = name, Material = "Gold", Centre = new C3dPoint3(x * Um, y * Um, z * Um), Radius = r * Um };

    private static C3dBox Box(string? name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name ?? "", Material = name is null ? null : "Gold", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private static Em3dProblem OneSphereProblem()
    {
        const double mm = 1e-3;
        var materials = new[] { new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7) };
        var solids = new List<Em3dSolid> { new("ball", "Cu", Em3dRole.Conductor, new Em3dSphere(new(0, 0, 0.3 * mm), 0.2 * mm), 1) };
        var box = new Em3dAirBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 1 * mm), new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing));
        var port = new Em3dPort(1, "port/1", "ball", "airbox/zmin", new(0, -0.05 * mm, 0), new(0, 0.05 * mm, 0.1 * mm),
                                new(0, 0, 1), 50, new Em3dReferencePlane(new(0, 0, 0), new(0, 0, 1), 0));
        return new Em3dProblem(solids, [], materials, [port], box, new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    private static void SelectFace(C3dEditorViewModel vm, string obj, string face)
    {
        var o = vm.SceneObject(obj)!;
        int f = Enumerable.Range(0, o.FaceNames.Count).First(i => o.FaceNames[i] == face);
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(o.Id, f)]);
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

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private C3dEditorViewModel Open(params C3dObject[] more)
    {
        string ws = Workspace();
        string path = WriteC3d(ws, "cell", new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "pad", Material = "Gold", Min = new C3dPoint3(100 * Um, 0, 0), Size = new C3dPoint3(40 * Um, 40 * Um, 20 * Um) }, .. more],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(150 * UmM, 80 * UmM, 80 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        v.Hover(sx, sy);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, sx, sy, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
        v.Click(false);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(SpherePrimitiveTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
