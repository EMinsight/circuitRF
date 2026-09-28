// ================================================================
//  EdgesFilletsChamfersTests.cs — the gate for brief-em3d-67: Edge mode (names, picking, the tangent chain, snapping to
//  curves, the keys) and Fillet… / Chamfer… (the commit, the refusals, Enabled, edges through edits). Counters and
//  document bytes only; nothing is looked at. What needs OpenCASCADE is a [KernelFact] and skips, naming what to build,
//  where the worker is not built.
// ================================================================

using System.Numerics;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class EdgesFilletsChamfersTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-edge67-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly List<GeometryKernel> _kernels = [];

    public EdgesFilletsChamfersTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        foreach (var k in _kernels) k.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. names (no kernel) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_ABoxHas12NamedEdges_APrismsFollowItsFaces_ACylinderTwoAndNoSeam_ASheetsRimHasOneSide()
    {
        var outline = new List<C3dPoint2> { new(0, 0), new(40 * Um, 0), new(40 * Um, 30 * Um), new(0, 30 * Um) };
        var vm = Open(Write(
        [
            Box("b", "Gold", 0, 0, 0, 40, 30, 10),
            new C3dPrism { Name = "p", Material = "Gold", Outline = outline, Height = 10 * Um, Placement = new C3dPlacement { Origin = new C3dPoint3(100 * Um, 0, 0) } },
            Cyl("c", "Gold", 200, 0, 0, 10, 8),
            new C3dSheet { Name = "s", Material = "Gold", Plane = C3dPlane.XY, Offset = 50 * Um,
                           Rect = new C3dRect { Min = new C3dPoint2(0, 0), Size = new C3dPoint2(20 * Um, 20 * Um) } },
        ]), Kernel(Absent()));

        string[] box = ["xmax|ymax", "xmax|ymin", "xmax|zmax", "xmax|zmin", "xmin|ymax", "xmin|ymin", "xmin|zmax", "xmin|zmin",
                        "ymax|zmax", "ymax|zmin", "ymin|zmax", "ymin|zmin"];
        Assert.Equal(box, Names(vm, "b"));
        Assert.Equal(12, Names(vm, "p").Count);
        Assert.Contains("side0|top", Names(vm, "p"));
        Assert.Contains("bottom|side3", Names(vm, "p"));
        Assert.Contains("side0|side1", Names(vm, "p"));
        Assert.Equal(["bottom|side", "side|top"], Names(vm, "c"));          // two rims, no seam
        var sheet = Assert.Single(Names(vm, "s"));
        Assert.EndsWith("|", sheet);                                        // a rim has one side
        Assert.All(new[] { "b", "p", "c", "s" }, n => Assert.False(Table(vm, n).Named.FromKernel));
    }

    [Fact]
    public void Gate1b_TwoRunsBetweenOnePairOfFaces_AreNumbered_InTheObjectsOwnFrame_WhateverItsPlacement()
    {
        // Faces 0 and 1 meet along two separate runs, x ∈ [0, 1] and x ∈ [2, 3] µm.
        Point3[] own = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(2, 0, 0), new(3, 0, 0), new(3, 1, 0), new(0, 0, 1), new(3, 0, 1)];
        Em3dTriangle[] tris = [new(0, 1, 2, "t", 0), new(3, 4, 5, "t", 0), new(0, 1, 6, "t", 1), new(3, 4, 7, "t", 1)];
        (string, double) FirstRun(Func<Point3, Point3> place, Func<Point3, Point3>? toOwn)
        {
            var mesh = new Em3dTriangleMesh([.. own.Select(p => place(new Point3(p.X * UmM, p.Y * UmM, p.Z * UmM)))], tris);
            var t = Scene3DFeatureTable.Of(mesh, false, new Scene3DEdgeSource(["a", "b"], ToOwn: toOwn));
            var first = t.Named.Edges.Single(e => e.Name == "a|b|1");
            var c = toOwn is null ? first.Points[0] : toOwn(first.Points[0]);
            return (first.Name, Math.Round(Math.Min(c.X, toOwn is null ? first.Points[1].X : toOwn(first.Points[1]).X) / UmM, 6));
        }
        Assert.Equal(2, Scene3DFeatureTable.Of(new Em3dTriangleMesh([.. own], tris), false, new Scene3DEdgeSource(["a", "b"]))
                                            .Named.Edges.Count(e => e.Name.StartsWith("a|b|", StringComparison.Ordinal)));
        var still = FirstRun(p => p, null);
        Assert.Equal(("a|b|1", 0.0), still);                                // the run nearer the origin in x first
        // Moved and turned half round about z: in the world the other run is first in x; in its own frame it is not.
        static Point3 Turned(Point3 p) => new(-p.X + 5e-4, -p.Y + 2e-4, p.Z + 1e-4);
        static Point3 Back(Point3 p) => new(-(p.X - 5e-4), -(p.Y - 2e-4), p.Z - 1e-4);
        Assert.Equal(still, FirstRun(Turned, Back));
    }

    // ── 2. picking (no kernel) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_HoverFindsTheEdgeUnderTheCursor_NotAHiddenOne_BReachesIt_AndTheCountIsThePatchs()
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 40, 30, 10)]), Kernel(Absent()));
        var v = vm.Viewer;
        v.SelectMode = Scene3DSelectMode.Edge;
        Iso(v);
        var fr = v.Scene.FeaturesOf(vm.SceneObject("b")!.Id);
        int front = fr.Table!.Named.IndexOf("xmax|zmax"), back = fr.Table.Named.IndexOf("xmin|zmin");
        HoverAtMid(v, fr.Table.Named.Edges[front]);
        Assert.Equal(Scene3DItem.OfEdge(vm.SceneObject("b")!.Id, front), v.HoveredItem);

        HoverAtMid(v, fr.Table.Named.Edges[back]);                           // behind the box: never offered
        Assert.NotEqual(back, v.HoveredItem?.Edge ?? -1);
        bool reached = false;
        for (int k = 0; k < 12 && !reached; k++)
        {
            Assert.True(v.Cycle(+1));
            reached = v.Selection is [{ Edge: var e }] && e == back;
        }
        Assert.True(reached, "B never reached the hidden edge");

        // The hover's cost is the patch's, not the scene's.
        var counts = new List<int>();
        foreach (int n in new[] { 5, 40 })
        {
            var scene = Grid(n);
            var patch = new Scene3DIdPatch();
            var at = new Point3(50 * UmM, 45 * UmM, 5 * UmM);                  // box (2, 2)'s top edge, y = 45 µm
            patch.Render(scene, TopAt(scene, at, 0.1f), W / 2, H / 2, W, H, Scene3DIdPatch.SizeFor(8), [], default);
            var q = new SnapQuery3D();
            Assert.NotNull(q.NearestEdge(scene, patch, 8, [], default));
            counts.Add(q.Counters.EdgesExamined);
        }
        Assert.Equal(counts[0], counts[1]);
        Assert.InRange(counts[1], 1, 8);
    }

    // ── 3. grouping ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AManagedCylindersRim_IsOneEdge_OfTheTessellationsSegmentCount()
    {
        var vm = Open(Write([Cyl("c", "Gold", 0, 0, 0, 10, 8)]), Kernel(Absent()));
        var t = Table(vm, "c");
        var rim = t.Named.Edges.Single(e => e.Name == "bottom|side");
        int bottom = Array.IndexOf([.. vm.SceneObject("c")!.FaceNames], "bottom"), side = Array.IndexOf([.. vm.SceneObject("c")!.FaceNames], "side");
        int segments = Enumerable.Range(0, t.EdgeA.Length).Count(e => (t.EdgeFace0[e], t.EdgeFace1[e]) == (bottom, side) || (t.EdgeFace0[e], t.EdgeFace1[e]) == (side, bottom));
        Assert.True(segments > 8, $"{segments} segments");
        Assert.Equal(segments, rim.Segments);
        Assert.True(rim.Closed);
        Assert.Equal(Scene3DEdgeKind.Circle, rim.Kind);
        Assert.Equal(8 * UmM, rim.Radius, 9);
    }

    // ── 4. a managed object snaps as it did ─────────────────────────────────────────────────────

    [Fact]
    public void Gate4_ManagedObjectsSnapFromTheirTriangles_AsBefore_ForEveryPrimitive()
    {
        var outline = new List<C3dPoint2> { new(0, 0), new(40 * Um, 0), new(20 * Um, 30 * Um) };
        var vm = Open(Write(
        [
            Box("b", "Gold", 0, 0, 0, 40, 30, 10), Cyl("c", "Gold", 100, 0, 0, 10, 8),
            new C3dPrism { Name = "p", Material = "Gold", Outline = outline, Height = 10 * Um, Placement = new C3dPlacement { Origin = new C3dPoint3(200 * Um, 0, 0) } },
            new C3dSheet { Name = "s", Material = "Gold", Plane = C3dPlane.XY, Offset = 50 * Um,
                           Rect = new C3dRect { Min = new C3dPoint2(0, 0), Size = new C3dPoint2(20 * Um, 20 * Um) } },
            (C3dPolyhedron)C3dFaceEditor.ConvertToPolyhedron(Box("q", "Gold", 300, 0, 0, 20, 20, 20)).Object!,
        ]), Kernel(Absent()));
        foreach (string n in new[] { "b", "c", "p", "s", "q" })
        {
            var t = Table(vm, n);
            Assert.False(t.Named.FromKernel);                               // the snap's kernel path never reads it
            Assert.True(t.FeatureCount > 0);
        }
        // The cylinder's rim keeps every snap point it had: each segment's corners and midpoints are still the table's.
        var cyl = Table(vm, "c");
        Assert.Equal(cyl.EdgeA.Length, cyl.Named.Edges.Sum(e => e.Segments) + 0);
    }

    // ── 5. snapping to curves (kernel) ─────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate5_ABoresCircle_NearestIsWithinTheDeflectionAndApproximate_ItsCentreSnaps_AnArcsMidpointIsTheWorkers()
    {
        var kernel = Kernel(KernelForTests.New());
        var vm = Open(Write([Subtract("lid", Box("", "Gold", 0, 0, 500, 4000, 3000, 250), Cyl("bore", "Copper", 2000, 1500, 400, 500, 30)),
                             Fillet("rim", Box("", "Gold", 6000, 0, 0, 1000, 1000, 200), 100, "xmax|ymax")]), kernel);
        var v = vm.Viewer;
        var t = Table(vm, "lid");
        Assert.True(t.Named.FromKernel);
        var circle = t.Named.Edges.Single(e => e.Name == "bore:side|zmax");
        Assert.Equal(Scene3DEdgeKind.Circle, circle.Kind);
        AssertNear(new Point3(2000 * UmM, 1500 * UmM, 750 * UmM), circle.Centre!.Value, 1e-12);
        Assert.Equal(30 * UmM, circle.Radius, 12);
        Assert.Null(circle.Mid);                                            // a closed edge has no midpoint

        // The nearest point: off the polyline's vertices it is approximate, and within the deflection of the true circle.
        var p = circle.Points[0];
        var q = circle.Points[1];
        var chordMid = new Point3((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2);
        v.View.Camera = TopAt(v.Scene, chordMid, 1f);
        HoverVm(v, W / 2, H / 2);
        Assert.Equal(Snap3DKind.Edge, v.Snap.Kind);
        Assert.True(v.Snap.Approximate);
        double r = Math.Sqrt(Math.Pow(v.Snap.World.X - 2000 * UmM, 2) + Math.Pow(v.Snap.World.Y - 1500 * UmM, 2));
        var solid = (Em3dShapeSolid)vm.Elaboration!.Solids.Single(s => s.Name == "lid").Primitive;
        Assert.InRange(Math.Abs(r - 30 * UmM), 0, solid.DisplayDeflectionM + 1e-12);
        Assert.False(vm.ToDocumentPoint(v.Snap).Exact);

        // Its centre, from inside the bore, at a scale where the rim is within the radius.
        var centre = circle.Centre!.Value;
        v.View.Camera = TopAt(v.Scene, centre, 5f);
        HoverVm(v, W / 2, H / 2);
        Assert.Equal(Snap3DKind.Centre, v.Snap.Kind);
        AssertNear(centre, v.Snap.World, 1e-12);
        Assert.True(vm.ToDocumentPoint(v.Snap).Exact);                     // on an exact DBU point
        Assert.Equal(new C3dPoint3(2000 * Um, 1500 * Um, 750 * Um), vm.ToDocumentPoint(v.Snap).Dbu);

        // An open arc's midpoint is the worker's, halfway round the quarter circle.
        var arc = Table(vm, "rim").Named.Edges.Single(e => e.Name == "fillet(xmax|ymax)|zmax");
        Assert.Equal(Scene3DEdgeKind.Arc, arc.Kind);
        double c45 = 100 * (1 - Math.Sqrt(0.5));
        AssertNear(new Point3((7000 - c45) * UmM, (1000 - c45) * UmM, 200 * UmM), arc.Mid!.Value, 1e-11);
    }

    // ── 6. the tangent chain ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_ManagedCollinearEdgesChain_ARightAngleStopsIt()
    {
        // A box drawn as two halves across y's middle, every face a rectangle: xmax.0|zmax.0 and xmax.1|zmax.1 are collinear,
        // and the edges between the halves leave their shared vertex at right angles.
        var vm = Open(Write([HalvedBox("p", 40, 30, 10)]), Kernel(Absent()));
        vm.Viewer.SelectMode = Scene3DSelectMode.Edge;
        vm.Viewer.SelectTangentChain(vm.Viewer.EdgeItem("p", "xmax.0|zmax.0")!.Value, add: false);
        Assert.Equal(["xmax.0|zmax.0", "xmax.1|zmax.1"], vm.Viewer.SelectedEdgeNames().Select(e => e.Edge).Order(StringComparer.Ordinal));
        Assert.False(vm.Viewer.LastChain!.StoppedAtBranch);
    }

    [KernelFact]
    public void Gate6b_TheTopLoopOfABoxWithItsFourVerticalEdgesFilleted_ChainsToEight()
    {
        var vm = Open(Write([Fillet("lid", Box("", "Gold", 0, 0, 0, 1000, 800, 200), 100, "xmax|ymax", "xmax|ymin", "xmin|ymax", "xmin|ymin")]),
                      Kernel(KernelForTests.New()));
        vm.Viewer.SelectMode = Scene3DSelectMode.Edge;
        vm.Viewer.SelectTangentChain(vm.Viewer.EdgeItem("lid", "xmax|zmax")!.Value, add: false);
        Assert.Equal(8, vm.Viewer.Selection.Count);
        Assert.Contains("fillet(xmax|ymax)|zmax", vm.Viewer.SelectedEdgeNames().Select(e => e.Edge));
    }

    // ── 7. the keys ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_EArmsEdgeMode_InTheEditorAndTheViewer_ShiftEExtrudes_AndTheMenusSayShiftE()
    {
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 40, 30, 10)]), Kernel(Absent()));
        Assert.True(vm.Viewer.HandleKey(Key.E, KeyModifiers.None, false));
        Assert.Equal(Scene3DSelectMode.Edge, vm.Viewer.SelectMode);
        var viewer = new Viewer3DViewModel("x.cem", "x", () => new object(), (_, _, _) => Scene3DModel.Empty(), () => new PatchRecordingBackend(),
                                           () => null, a => a());
        Assert.True(viewer.HandleKey(Key.E, KeyModifiers.None, false));
        Assert.Equal(Scene3DSelectMode.Edge, viewer.SelectMode);
        viewer.Dispose();

        SelectFace(vm, "b", "zmax");
        Assert.True(vm.Viewer.HandleKey(Key.E, KeyModifiers.None, false));   // plain E: Edge mode, nothing extruded
        Assert.Equal(Scene3DSelectMode.Edge, vm.Viewer.SelectMode);
        Assert.Null(vm.ArmedTool);
        SelectFace(vm, "b", "zmax");
        Assert.True(vm.Viewer.HandleKey(Key.E, KeyModifiers.Shift, false));
        Assert.Equal(C3dToolKind.ExtrudeFace, vm.ArmedTool);
        Assert.True(vm.Viewer.HandleKey(Key.Escape, KeyModifiers.None, false));

        SelectFace(vm, "b", "zmax");
        // 3D menu cleanup — the key is the item's gesture (the menu's shortcut column), never text in its header.
        Assert.Contains(vm.DrawMenuItems(), i => i.Header == "Extrude to New Solid" && i.Gesture == new KeyGesture(Key.E, KeyModifiers.Shift));
        string window = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Equal(1, CountOf(window, "CommandParameter=\"ExtrudeFace\" InputGesture=\"Shift+E\""));
        Assert.Equal(1, CountOf(window, "ToolTip=\"Shift+E. A new solid"));
        Assert.Equal(0, CountOf(window, "InputGesture=\"E\" ToolTip.Tip=\"A new solid"));
    }

    // ── 8. the commit (kernel) ──────────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate8_FilletOneEdge_OneUndoEntry_NoWorkerCallAtCommit_ABoundaryOnZmaxStillLands_TheNewFaceIsNamed()
    {
        var kernel = Kernel(KernelForTests.New());
        var vm = Open(Write([Box("lid", "Alumina", 0, 0, 0, 400, 300, 100)],
                            doc => doc.FaceBoundaries = [new C3dFaceBoundary { Object = "lid", Face = "zmax", Kind = Em3dFaceBoundaryKind.Pec }]), kernel);
        SelectEdges(vm, "lid", "xmax|zmax");
        vm.OpenFillet(C3dEdgeOp.Fillet);
        Assert.True(vm.FilletOpen, vm.StatusMessage);
        vm.FilletSizeText = "20";
        WaitForFillet(vm);
        Assert.True(vm.CanAcceptFillet, vm.FilletError);
        Assert.False(vm.SceneObject("lid")!.Selectable);                     // the result, seen through …
        Assert.True(vm.SceneObject("lid" + C3dEditorViewModel.FilletTargetSuffix)!.Selectable);   // … its pickable target

        long sent = kernel.RequestsSent;
        int entries = vm.UndoEntries;
        vm.AcceptFillet();
        Settle(vm);
        Assert.Equal(sent, kernel.RequestsSent);
        Assert.Equal(entries + 1, vm.UndoEntries);
        var f = Assert.IsType<C3dFillet>(Assert.Single(vm.Document.Objects));
        Assert.Equal(("lid", "", 20 * Um), (f.Name, f.Target!.Name, f.Radius));
        Assert.Equal(["xmax|zmax"], f.Edges);
        Assert.Contains("fillet(xmax|zmax)", vm.Elaboration!.Provenance["lid"].FaceNames);
        var e = vm.Elaboration;
        Assert.Null(C3dProblemAssembly.FaceBoundaries(vm.Document, e, [.. e.Materials], EmSetup.DefaultOperatingTempC, out var landed));
        Assert.Contains(landed, b => b.Object == "lid" && b.Face.StartsWith("zmax", StringComparison.Ordinal));
        var row = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "lid").Children.Single(c => c.IsFeature);
        Assert.Equal("", row.FeaturePath);
        Assert.StartsWith("Fillet 20", row.Name);
    }

    // ── 9. refusals (kernel) ────────────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate9_ARadiusWiderThanTheFaceBesideIt_NamesTheEdge_OkIsDisabled_TheDocumentUnchanged()
    {
        var vm = Open(Write([Box("lid", "Gold", 0, 0, 0, 100, 100, 30)]), Kernel(KernelForTests.New()));
        string before = C3dPersistence.Serialize(vm.Document);
        SelectEdges(vm, "lid", "xmax|zmax");
        vm.OpenFillet(C3dEdgeOp.Fillet);
        vm.FilletSizeText = "50";
        WaitForFillet(vm);
        Assert.Equal("A 50 µm radius does not fit edge 'xmax|zmax': the faces beside it are 30 µm wide. Try less than 30 µm.", vm.FilletError);
        Assert.False(vm.CanAcceptFillet);
        vm.AcceptFillet();
        vm.CancelFillet();
        Settle(vm);
        Assert.Equal(before, C3dPersistence.Serialize(vm.Document));
    }

    // ── 10. Enabled off (no kernel) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_EnabledOff_IsTheUnwrappedTargetsProblem_AndABoundaryOnTheFilletFaceSaysSo()
    {
        var absent = Kernel(Absent());
        var fillet = Fillet("lid", Box("", "Alumina", 0, 0, 0, 400, 300, 100), 20, "xmax|zmax");
        fillet.Enabled = false;
        string path = Write([fillet], doc => doc.FaceBoundaries = [new C3dFaceBoundary { Object = "lid", Face = "fillet(xmax|zmax)", Kind = Em3dFaceBoundaryKind.Pec }]);
        var doc = C3dPersistence.LoadFromFile(path);
        var plain = C3dPersistence.LoadFromFile(path);
        plain.Objects = [C3dFillets.Unwrap((C3dOperation)plain.Objects[0])];
        var off = C3dElaborator.ElaborateOnce(doc, path, null, kernel: absent);
        var flat = C3dElaborator.ElaborateOnce(plain, path, null, kernel: absent);
        Assert.Equal(Problem(flat), Problem(off));
        Assert.Equal(0, absent.RequestsSent);
        string? why = C3dProblemAssembly.FaceBoundaries(doc, off, [.. off.Materials], EmSetup.DefaultOperatingTempC, out _);
        Assert.Equal("A face boundary cannot be placed: 'fillet(xmax|zmax)' of 'lid' exists only while its fillet is enabled.", why);
    }

    // ── 11. edges through edits (kernel) ────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate11_AFoldedZmax_RoundsBothNewEdges_AndAnEdgeWhoseFaceIsGone_IsARefusalNamingIt()
    {
        var kernel = Kernel(KernelForTests.New());
        var split = SplitTop((C3dPolyhedron)C3dFaceEditor.ConvertToPolyhedron(Box("", "Gold", 0, 0, 0, 400, 300, 100)).Object!);
        var renamed = (C3dPolyhedron)C3dFaceEditor.ConvertToPolyhedron(Box("", "Gold", 1000, 0, 0, 400, 300, 100)).Object!;
        renamed.Faces.Single(f => f.Name == "zmax").Name = "lidtop";
        var vm = Open(Write([Fillet("lid", split, 20, "xmax|zmax"), Fillet("cap", renamed, 20, "xmax|zmax")]), kernel);
        var e = vm.Elaboration!;
        Assert.False(e.KernelRefusals.ContainsKey("lid"), e.KernelRefusals.GetValueOrDefault("lid"));
        Assert.Equal(2, e.Provenance["lid"].FaceNames.Count(n => n.StartsWith("fillet(xmax|zmax", StringComparison.Ordinal)));
        Assert.Equal("Edge 'xmax|zmax' of 'cap' no longer exists: its face 'zmax' was removed. Edit the fillet's edges.", e.KernelRefusals["cap"]);
        var row = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "cap").Children.Single(c => c.IsFeature);
        Assert.Equal(e.KernelRefusals["cap"], row.Refusal);
    }

    // ── 12. no kernel ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate12_WithoutTheKernel_EdgeModePropertiesAndSnapWork_FilletAndChamferSayTheCapabilitysSentence()
    {
        var absent = Kernel(Absent());
        var vm = Open(Write([Box("b", "Gold", 0, 0, 0, 40, 30, 10)]), absent);
        SelectEdges(vm, "b", "xmax|zmax", "ymax|zmax");
        Assert.Equal("2 edges", vm.Properties.Heading);
        Assert.Contains(vm.Properties.Rows, r => r.Label == "Total length" && r.Value.StartsWith("70", StringComparison.Ordinal));
        SelectEdges(vm, "b", "xmax|zmax");
        Assert.Contains(vm.Properties.Rows, r => r.Label == "Faces" && r.Value == "xmax, zmax");
        Assert.Contains(vm.Properties.Rows, r => r.Label == "Length" && r.Value.StartsWith("30", StringComparison.Ordinal));
        foreach (var item in vm.EdgeMenuItems())
        {
            Assert.False(item.Enabled);
            Assert.Equal(GeometryKernel.NeedsKernel(item.Header.TrimEnd('…'), absent.Known!), item.Tip);
        }
        Assert.Contains(vm.Viewer.ContextMenuItems(), i => i.Header == "Select Tangent Chain" && i.Enabled);
        Iso(vm.Viewer);
        HoverAtMid(vm.Viewer, Table(vm, "b").Named.Edges[Table(vm, "b").Named.IndexOf("xmax|zmax")]);
        Assert.True(vm.Viewer.Snap.IsSnap);
        Assert.Equal(0, absent.RequestsSent);
    }

    // ── 3D editor round 5 (kernel) ──────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate13_Round5_AnInspectorEditOfAFillet_KeepsItsRowSelected_ThroughTheRebuild()
    {
        var vm = Open(Write([Fillet("lid", Box("", "Alumina", 0, 0, 0, 400, 300, 100), 20, "xmax|zmax")]), Kernel(KernelForTests.New()));
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == "lid").Children.Single(c => c.IsFeature);
        Assert.True(vm.Properties.IsFeature);
        foreach (bool enabled in (bool[])[false, true])
        {
            vm.Properties.FeatureEnabled = enabled;
            Settle(vm);
            Assert.Equal(enabled, ((C3dOperation)vm.Document.Objects[0]).Enabled);
            Assert.True(vm.SelectedTreeItem?.IsFeature, vm.SelectedTreeItem?.Name);
            Assert.True(vm.Properties.IsFeature, vm.Properties.Heading);
        }
    }

    [KernelFact]
    public void Gate14_Round5_ASecondFillet_IsTheOneEnabled_AndEnablingTheFirst_SwitchesTheSecondOff()
    {
        var vm = Open(Write([Fillet("lid", Box("", "Alumina", 0, 0, 0, 400, 300, 100), 20, "xmax|zmax")]), Kernel(KernelForTests.New()));
        SelectEdges(vm, "lid", "xmin|zmax");
        vm.OpenFillet(C3dEdgeOp.Fillet);
        Assert.True(vm.FilletOpen, vm.StatusMessage);
        vm.FilletSizeText = "10";
        WaitForFillet(vm);
        Assert.True(vm.CanAcceptFillet, vm.FilletError);
        int entries = vm.UndoEntries;
        vm.AcceptFillet();
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal([("", true), ("Target.", false)], C3dFillets.Chain(vm.Document.Objects[0]).Select(c => (c.Path, c.Feature.Enabled)));
        // The kernel builds the switched-off fillet as what it wraps: only the new one's face exists.
        var faces = vm.Elaboration!.Provenance["lid"].FaceNames;
        Assert.Contains("fillet(xmin|zmax)", faces);
        Assert.DoesNotContain("fillet(xmax|zmax)", faces);

        vm.SetFeatureEnabled(0, "Target.", true);
        Settle(vm);
        Assert.Equal(entries + 2, vm.UndoEntries);
        Assert.Equal([("", false), ("Target.", true)], C3dFillets.Chain(vm.Document.Objects[0]).Select(c => (c.Path, c.Feature.Enabled)));
        faces = vm.Elaboration!.Provenance["lid"].FaceNames;
        Assert.Contains("fillet(xmax|zmax)", faces);
        Assert.DoesNotContain("fillet(xmin|zmax)", faces);
    }

    /// <summary>3D editor round 5 — a feature row spells its size in the display unit, so a change of unit re-spells it.</summary>
    [KernelFact]
    public void Round5_AFeatureRow_FollowsTheDisplayUnit()
    {
        var vm = Open(Write([Fillet("lid", Box("", "Alumina", 0, 0, 0, 400, 300, 100), 20, "xmax|zmax")]), Kernel(KernelForTests.New()));
        string Row() => vm.Tree.SelectMany(g => g.Items).SelectMany(i => i.Children).Single(c => c.FeaturePath is not null).Name;
        vm.DisplayUnit = LayoutUnit.Um;
        Assert.StartsWith("Fillet 20 µm", Row(), StringComparison.Ordinal);
        vm.DisplayUnit = LayoutUnit.Mm;
        Assert.StartsWith("Fillet 0.02 mm", Row(), StringComparison.Ordinal);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private GeometryKernel Kernel(GeometryKernel k)
    {
        _kernels.Add(k);
        if (k.Known is null) k.Probe();
        return k;
    }

    private static GeometryKernel Absent() => new(new GeometryKernelOptions
    {
        Locate = () => new GeometryKernelLocation(null, null, "not found", GeometryKernelAbsence.NotBuilt,
                                                  "The worker has not been built here.", "Build it, then check again."),
        DiskCache = false,
    });

    private static Scene3DFeatureTable Table(C3dEditorViewModel vm, string name) => vm.Viewer.Scene.FeaturesOf(vm.SceneObject(name)!.Id).Table!;

    private static List<string> Names(C3dEditorViewModel vm, string name) => [.. Table(vm, name).Named.Edges.Select(e => e.Name).Order(StringComparer.Ordinal)];

    private static string Problem(C3dElaboration e)
        => string.Join("\n", e.Solids.Select(s => $"{s.Name}|{s.Material}|{s.Role}|{s.Order}|{s.Primitive}"));

    private static void SelectEdges(C3dEditorViewModel vm, string obj, params string[] edges)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Edge;
        vm.Viewer.SetSelection([.. edges.Select(e => vm.Viewer.EdgeItem(obj, e) ?? throw new InvalidOperationException($"no edge {e}"))]);
    }

    private static void SelectFace(C3dEditorViewModel vm, string obj, string face)
    {
        var o = vm.SceneObject(obj)!;
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(o.Id, Enumerable.Range(0, o.FaceNames.Count).First(i => o.FaceNames[i] == face))]);
    }

    private static void WaitForFillet(C3dEditorViewModel vm)
    {
        Assert.True(SpinWait.SpinUntil(() => !vm.FilletBusy && (vm.FilletPreviewsDrawn > 0 || vm.FilletError is not null), TimeSpan.FromSeconds(60)),
                    "the preview never arrived");
        Settle(vm);
    }

    /// <summary>A box polyhedron whose top is two faces, zmax.0 and zmax.1, split along y's middle: both meet xmax.</summary>
    private static C3dPolyhedron SplitTop(C3dPolyhedron p)
    {
        var top = p.Faces.Single(f => f.Name == "zmax");
        long z = p.Vertices[top.Outer[0]].Z;
        long ymid = (p.Vertices.Min(v => v.Y) + p.Vertices.Max(v => v.Y)) / 2;
        long x0 = p.Vertices.Min(v => v.X), x1 = p.Vertices.Max(v => v.X);
        int m1 = p.Vertices.Count; p.Vertices.Add(new C3dPoint3(x1, ymid, z));
        int m2 = p.Vertices.Count; p.Vertices.Add(new C3dPoint3(x0, ymid, z));
        foreach (var f in p.Faces)
            for (int k = 0; k < f.Outer.Count; k++)
            {
                var a = p.Vertices[f.Outer[k]];
                var b = p.Vertices[f.Outer[(k + 1) % f.Outer.Count]];
                foreach (var (m, x) in new[] { (m1, x1), (m2, x0) })
                    if (a.X == x && b.X == x && a.Z == z && b.Z == z && Math.Min(a.Y, b.Y) < ymid && Math.Max(a.Y, b.Y) > ymid)
                    {
                        f.Outer.Insert(k + 1, m);
                        k++;
                    }
            }
        var loop = top.Outer;
        int i = loop.IndexOf(m1), j = loop.IndexOf(m2);
        List<int> Arc(int from, int to) { var r = new List<int>(); for (int k = from; ; k = (k + 1) % loop.Count) { r.Add(loop[k]); if (k == to) return r; } }
        p.Faces.Remove(top);
        p.Faces.Add(new C3dFace { Name = "zmax.0", Outer = Arc(i, j) });
        p.Faces.Add(new C3dFace { Name = "zmax.1", Outer = Arc(j, i) });
        return p;
    }

    /// <summary>A box as a polyhedron cut in two across y's middle: ymin, ymax, and each other face as two rectangles.</summary>
    private static C3dPolyhedron HalvedBox(string name, long sx, long sy, long sz)
    {
        long[] ys = [0, sy * Um / 2, sy * Um];
        var v = new List<C3dPoint3>();
        foreach (long y in ys) foreach (long x in (long[])[0, sx * Um]) foreach (long z in (long[])[0, sz * Um]) v.Add(new C3dPoint3(x, y, z));
        int I(int yi, int xi, int zi) => yi * 4 + xi * 2 + zi;
        var faces = new List<C3dFace>
        {
            new() { Name = "ymin", Outer = [I(0, 0, 0), I(0, 1, 0), I(0, 1, 1), I(0, 0, 1)] },
            new() { Name = "ymax", Outer = [I(2, 0, 0), I(2, 0, 1), I(2, 1, 1), I(2, 1, 0)] },
        };
        for (int h = 0; h < 2; h++)
        {
            faces.Add(new() { Name = $"zmax.{h}", Outer = [I(h, 0, 1), I(h, 1, 1), I(h + 1, 1, 1), I(h + 1, 0, 1)] });
            faces.Add(new() { Name = $"zmin.{h}", Outer = [I(h, 0, 0), I(h + 1, 0, 0), I(h + 1, 1, 0), I(h, 1, 0)] });
            faces.Add(new() { Name = $"xmax.{h}", Outer = [I(h, 1, 0), I(h + 1, 1, 0), I(h + 1, 1, 1), I(h, 1, 1)] });
            faces.Add(new() { Name = $"xmin.{h}", Outer = [I(h, 0, 0), I(h, 0, 1), I(h + 1, 0, 1), I(h + 1, 0, 0)] });
        }
        return new C3dPolyhedron { Name = name, Material = "Gold", Vertices = v, Faces = faces };
    }

    private static void AssertNear(Point3 want, Point3 got, double tol)
        => Assert.True(Math.Abs(want.X - got.X) <= tol && Math.Abs(want.Y - got.Y) <= tol && Math.Abs(want.Z - got.Z) <= tol, $"expected {want}, got {got}");

    private static C3dBox Box(string name, string? material, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private static C3dCylinder Cyl(string name, string? material, long x, long y, long z, long length, long radius)
        => new() { Name = name, Material = material, Base = new C3dPoint3(x * Um, y * Um, z * Um), Length = length * Um, Radius = radius * Um };

    private static C3dBoolean Subtract(string name, C3dObject blank, params C3dObject[] tools)
        => new() { Name = name, Op = C3dBooleanOp.Subtract, Blank = blank, Tools = [.. tools] };

    private static C3dFillet Fillet(string name, C3dObject target, long radiusUm, params string[] edges)
        => new() { Name = name, Radius = radiusUm * Um, Edges = [.. edges], Target = target };

    private string Write(List<C3dObject> objects, Action<C3dDocument>? more = null)
    {
        var doc = new C3dDocument { SnapDbu = Um, Objects = objects };
        more?.Invoke(doc);
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }, new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = "Alumina", Epsr = 9.8 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private C3dEditorViewModel Open(string c3d, GeometryKernel kernel)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a(), kernel: kernel);
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
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

    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    /// <summary>A hover where the middle of an edge's run lands (a table of the document's own: no offset).</summary>
    private static void HoverAtMid(Viewer3DViewModel v, Scene3DEdge e)
    {
        var a = e.Points[0];
        var b = e.Points[^1];
        var (x, y, front) = v.View.Camera.Project(v.Scene.ToLocal((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2), W, H);
        Assert.True(front);
        HoverVm(v, x, y);
    }

    /// <summary>Straight down, orthographic, <paramref name="umPerPixel"/> µm a pixel, centred on <paramref name="p"/>.</summary>
    private static Camera3D TopAt(Scene3DModel scene, Point3 p, float umPerPixel)
    {
        var c = new Camera3D { FovY = Camera3D.DefaultFovY, Projection = Projection3D.Orthographic };
        c.SetStandardView(StandardView3D.Top);
        c.SceneCentre = (scene.BoundsMin + scene.BoundsMax) * 0.5f;
        c.SceneRadius = (scene.BoundsMax - scene.BoundsMin).Length() * 0.5f;
        c.Distance = umPerPixel * (float)UmM * H / (2 * MathF.Tan(c.FovY * 0.5f));
        c.Target = scene.ToLocal(p.X, p.Y, p.Z);
        return c;
    }

    /// <summary>n × n boxes, 10 × 10 × 5 µm at a 20 µm pitch.</summary>
    private static Scene3DModel Grid(int n)
    {
        var solids = new List<Em3dSolid>(n * n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                solids.Add(new Em3dSolid($"b{i}_{j}", "Gold", Em3dRole.Conductor,
                    new Em3dBox(new Point3(i * 20 * UmM, j * 20 * UmM, 0), new Point3((i * 20 + 10) * UmM, (j * 20 + 10) * UmM, 5 * UmM)), solids.Count + 1));
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(solids, [], [new Em3dMaterial("Gold", 1, null, 0, 1, 4.1e7)], [],
            new Em3dAirBox(new Point3(0, 0, 0), new Point3(n * 20 * UmM, n * 20 * UmM, 5 * UmM), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        return Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false));
    }

    private static int CountOf(string text, string what)
    {
        int n = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
