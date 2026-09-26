// ================================================================
//  OperationsGateTests.cs — the gate for brief-em3d-46: object operations in the 3D editor, headless.
//  Counters and document bytes only — no pixel is looked at (overview §1n); the GPU is a recording fake.
//  Gate 5 (the canonical form) and gate 8's hit test are below the firewall and need no editor.
// ================================================================

using System.Numerics;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class OperationsGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;                       // DBU per µm at the default 1000 DBU/µm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-ops46-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public OperationsGateTests() => Snap3DPreference.TestOverrideActive = true;   // never the developer's preferences

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. a drag is a preview: 500 moves elaborate, tessellate and upload nothing; the commit elaborates the moved ──

    [Fact]
    public void Gate1_FiveHundredMovesArePreviewOnly_AndOneCommitElaboratesTheMovedObject()
    {
        var fake = new PatchRecordingBackend();
        var vm = Open(Pads(), fake);
        var v = vm.Viewer;
        v.Session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        Frame(v, plan);
        Select(vm, "pad");

        // The key with the cursor over the selection: the snapped corner under it is the base, at once (R-em3d46-2a).
        HoverAt(v, NearestTopCorner(v, "pad"));
        Assert.True(vm.DrawKey(Key.G, KeyModifiers.None));
        var move = Assert.IsType<MoveTool>(vm.Tool);
        Assert.NotNull(move.Base);

        var pad = vm.SceneObject("pad")!;
        int padDraws = v.Scene.Batches.Count(b => b.ObjectId == pad.Id) + v.Scene.LineBatches.Count(b => b.ObjectId == pad.Id)
                     + v.Scene.EdgeBatches.Count(b => b.ObjectId == pad.Id);
        long elaborated = vm.ObjectsElaborated, tessellated = vm.TessellationMisses, builds = v.Source.Builds;
        long uploaded = fake.Counters.UploadBytesTotal;
        long previews = vm.PreviewUpdates;
        for (int i = 0; i < 500; i++)
        {
            HoverVm(v, 30 + i * 13 % 340, 40 + i * 7 % 220);
            plan.Plan(v.Scene, v.View, (int)W, (int)H, false, pick: true, v.MeshOverlay, v.SectionOverlay, v.GridOverlay);
            v.Session.Frame(i % 3, plan, (ulong)i + 2, v.Scene, v.MeshOverlay, v.SectionOverlay, v.GridOverlay, false);
            Assert.InRange(plan.TransformBytes, 1, Scene3DFramePlan.TransformBytesPerDraw * padDraws);
            Assert.DoesNotContain(plan.PickDraws.Take(plan.PickDrawCount), d => v.Scene.Batches.Any(b => b.ObjectId == pad.Id && b.FirstIndex == d.First));
        }
        Assert.Equal(elaborated, vm.ObjectsElaborated);
        Assert.Equal(tessellated, vm.TessellationMisses);
        Assert.Equal(builds, v.Source.Builds);
        Assert.Equal(uploaded, fake.Counters.UploadBytesTotal);
        Assert.True(vm.PreviewUpdates - previews >= 400, "the preview did not follow the cursor");
        Assert.False(vm.IsDirty);

        // The commit: the document changes once, the moved object re-elaborates, one entry.
        int entries = vm.UndoEntries;
        ClickAt(vm, 20, 60, 0);
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(1, vm.ObjectsElaborated - elaborated);
        Assert.NotEqual(default, vm.Document.Objects.Single(o => o.Name == "pad").Placement.Origin);
        Assert.Null(v.View.Preview);
    }

    // ── 2. an instance moved, rotated or arrayed is not re-elaborated ────────────────────────

    [Fact]
    public void Gate2_MovingRotatingAndArrayingAnInstance_NeverReElaboratesItsChild()
    {
        string ws = Workspace();
        WriteC3d(ws, "die", new C3dDocument { Objects = [Box("chip", 0, 0, 0, 20, 20, 5)] });
        string parent = WriteC3d(ws, "pkg", new C3dDocument
        {
            SnapDbu = Um,
            Objects = [Box("base", -50, -50, -10, 150, 150, 10)],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../die" }],
        });
        var vm = Open(parent);
        long children = vm.ChildrenElaborated;
        Assert.True(children > 0);

        SelectInstance(vm, "U1");
        vm.StartMove(C3dMoveLock.AxisX);
        ClickAt(vm, 0, 0, 0);                                             // the base
        vm.OpenField(null);
        vm.FieldText = "30";
        vm.FieldEnter();
        Settle(vm);
        Assert.Equal(new C3dPoint3(30 * Um, 0, 0), vm.Document.Instances[0].Placement.Origin);

        SelectInstance(vm, "U1");
        vm.RotateQuick(C3dAxis.Z, 90);
        Settle(vm);
        SelectInstance(vm, "U1");
        vm.OpenArray();
        vm.ArrayCountX = "3";
        vm.AcceptArray();
        Settle(vm);
        Assert.Equal(3, vm.Document.Instances.Count);
        Assert.Equal(["U1", "U2", "U3"], vm.Document.Instances.Select(i => i.Name));
        Assert.Equal(children, vm.ChildrenElaborated);
    }

    // ── 3. exact moves; four quarter turns are the identity, byte for byte ───────────────────

    [Fact]
    public void Gate3_CornerToCornerIsTheirIntegerDifference_AndFourQuarterTurnsRestoreTheFile()
    {
        var vm = Open(Pads());
        var v = vm.Viewer;
        byte[] original = File.ReadAllBytes(vm.FilePath);

        Select(vm, "pad");
        vm.StartMove();
        var from = NearestTopCorner(v, "pad");
        var to = NearestTopCorner(v, "other");
        ClickAt(vm, from.X / UmM, from.Y / UmM, from.Z / UmM);
        ClickAt(vm, to.X / UmM, to.Y / UmM, to.Z / UmM);
        Settle(vm);
        long D(double m) => (long)Math.Round(m / 1e-9);
        Assert.Equal(new C3dPoint3(D(to.X) - D(from.X), D(to.Y) - D(from.Y), D(to.Z) - D(from.Z)),
                     vm.Document.Objects.Single(o => o.Name == "pad").Placement.Origin);
        vm.UndoRedo.Undo();
        Settle(vm);

        foreach (var axis in new[] { C3dAxis.X, C3dAxis.Y, C3dAxis.Z })
            for (int k = 0; k < 4; k++)
            {
                Select(vm, "pad");
                vm.RotateQuick(axis, 90);
                Settle(vm);
                Assert.True(vm.Document.Objects.Single(o => o.Name == "pad").Placement.Rotate.Count <= 3);
            }
        Assert.Null(vm.Save());
        Assert.Equal(original, File.ReadAllBytes(vm.FilePath));

        // A typed angle that is not a quarter turn is stored as it was asked for.
        Select(vm, "pad");
        vm.StartRotate();
        vm.OpenField(null);
        vm.FieldText = "30";
        vm.FieldEnter();
        Settle(vm);
        var r = Assert.Single(vm.Document.Objects.Single(o => o.Name == "pad").Placement.Rotate);
        Assert.Equal((C3dAxis.Z, 30.0), (r.Axis, r.Deg));
    }

    // ── 4. a mirror keeps a polyhedron outside-out, and twice is the file again ──────────────

    [Fact]
    public void Gate4_MirroringAPolyhedron_KeepsItsSignedVolume_AndMirroringTwiceRestoresTheFile()
    {
        var tet = new C3dPolyhedron
        {
            Name = "tet", Material = "Gold",
            Vertices = [new(0, 0, 0), new(10 * Um, 0, 0), new(0, 10 * Um, 0), new(0, 0, 10 * Um)],
            Faces =
            [
                new C3dFace { Name = "bottom", Outer = [0, 2, 1] }, new C3dFace { Name = "front", Outer = [0, 1, 3] },
                new C3dFace { Name = "left", Outer = [0, 3, 2] }, new C3dFace { Name = "slope", Outer = [1, 2, 3] },
            ],
        };
        var vm = Open(Write([tet, Box("far", 200, 0, 0, 10, 10, 10)]));
        byte[] original = File.ReadAllBytes(vm.FilePath);
        double v0 = Volume(vm);
        Assert.True(v0 > 0);
        foreach (var plane in new[] { C3dPlane.XY, C3dPlane.YZ, C3dPlane.XZ })
        {
            Select(vm, "tet");
            vm.MirrorAcross(plane);
            Settle(vm);
            Assert.True(vm.Document.Objects[0].Placement.MirrorX);
            double v1 = Volume(vm);
            Assert.True(v1 > 0, $"mirrored across {plane}, the tetrahedron is inside out");
            Assert.True(Math.Abs(v1 - v0) <= 1e-12 * v0, $"{v1} vs {v0}");
            Select(vm, "tet");
            vm.MirrorAcross(plane);
            Settle(vm);
            Assert.Null(vm.Save());
            Assert.Equal(original, File.ReadAllBytes(vm.FilePath));
        }

        static double Volume(C3dEditorViewModel vm)
            => Assert.IsType<Em3dPolyhedron>(vm.Elaboration!.Solids.Single(s => s.Name == "tet").Primitive).SignedVolume();
    }

    // ── 5. the canonical form ───────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_RandomQuarterTurnsAndMirrors_CanonicaliseToThreeEntriesAndMirrorX_WithTheProductsMatrix()
    {
        var rng = new Random(46);
        for (int trial = 0; trial < 20; trial++)
        {
            var p = new C3dPlacement();
            var expected = C3dTransform.Identity;
            for (int step = 0; step < 8; step++)
            {
                var pivot = new C3dPoint3(rng.Next(-5, 6) * Um, rng.Next(-5, 6) * Um, rng.Next(-5, 6) * Um);
                var op = rng.Next(4) == 0
                    ? C3dOperations.Mirror((C3dPlane)rng.Next(3), pivot)
                    : C3dOperations.Rotation((C3dAxis)rng.Next(3), (rng.Next(3) + 1) * 90, pivot);
                p = p.Then(op, out bool exact);
                expected = expected.Then(op);
                Assert.True(exact);
            }
            Assert.InRange(p.Rotate.Count, 0, 3);
            Assert.All(p.Rotate, r => Assert.Contains(r.Deg, new[] { 90.0, 180.0, -90.0 }));
            Assert.Equal(p.Rotate.Select(r => r.Axis).OrderByDescending(a => a), p.Rotate.Select(r => r.Axis));   // Z, Y, X
            var got = p.ToTransform();
            Assert.Equal(expected.IntegerMatrix(), got.IntegerMatrix());
            Assert.Equal((expected.Tx, expected.Ty, expected.Tz), (got.Tx, got.Ty, got.Tz));
        }
        // A rotation that is not a quarter turn: Z-Y-X Euler angles, the same matrix.
        var free = new C3dPlacement().Then(C3dOperations.Rotation(C3dAxis.X, 30, default).Then(C3dOperations.Rotation(C3dAxis.Z, 20, default)), out _);
        var m = free.ToTransform();
        var want = C3dTransform.Rotation(C3dAxis.X, 30).Then(C3dTransform.Rotation(C3dAxis.Z, 20));
        Assert.True(Math.Abs(m.M01 - want.M01) + Math.Abs(m.M12 - want.M12) + Math.Abs(m.M20 - want.M20) < 1e-9);
    }

    // ── 6. Duplicate then Esc leaves the document as it was ──────────────────────────────────

    [Fact]
    public void Gate6_DuplicateThenEsc_LeavesTheDocumentByteIdentical()
    {
        var vm = Open(Pads());
        string before = C3dPersistence.Serialize(vm.Document);
        Select(vm, "pad");
        Assert.True(vm.DrawKey(Key.D, KeyModifiers.Control));
        Assert.True(Assert.IsType<MoveTool>(vm.Tool).KeepsOriginal);
        HoverVm(vm.Viewer, 60, 60);
        Assert.NotNull(vm.Viewer.View.Preview);
        Assert.True(vm.DrawKey(Key.Escape, KeyModifiers.None));
        Assert.Null(vm.Tool);
        Assert.Null(vm.Viewer.View.Preview);
        Assert.Equal(before, C3dPersistence.Serialize(vm.Document));
        Assert.False(vm.IsDirty);
        Assert.Equal(0, vm.UndoEntries);
    }

    // ── 7. Order moves Em3dSolid.Order as the list moved ────────────────────────────────────

    [Fact]
    public void Gate7_OrderMovesTheElaboratedOrderExactlyAsTheList()
    {
        var vm = Open(Write([Box("a", 0, 0, 0, 10, 10, 10), Box("b", 5, 0, 0, 10, 10, 10), Box("c", 10, 0, 0, 10, 10, 10)]));
        string[] Solids() => [.. vm.Elaboration!.Solids.OrderBy(s => s.Order).Select(s => s.Name)];
        Select(vm, "a");
        vm.Order(C3dEditorViewModel.OrderMove.ToFront);
        Settle(vm);
        Assert.Equal(["b", "c", "a"], vm.Document.Objects.Select(o => o.Name));
        Assert.Equal(["b", "c", "a"], Solids());
        Select(vm, "c");
        vm.Order(C3dEditorViewModel.OrderMove.Backward);
        Settle(vm);
        Assert.Equal(["c", "b", "a"], vm.Document.Objects.Select(o => o.Name));
        Assert.Equal(["c", "b", "a"], Solids());
    }

    // ── 8. the gizmo's hit test, headless — and a drag on its X arrow moves along X only ────────

    [Fact]
    public void Gate8_GizmoHitTestPicksTheRightHandle_AndDraggingAnArrowIsAConstrainedMove()
    {
        var cam = new Camera3D { FovY = Camera3D.DefaultFovY, Distance = 1f, Yaw = -0.8f, Pitch = 0.5f, Target = Vector3.Zero, SceneRadius = 0.5f };
        var g = GizmoGeometry.Layout(cam, Vector3.Zero, W, H)!;
        for (int k = 0; k < 3; k++)
        {
            Assert.True(g.AxisUsable[k]);
            foreach (float along in new[] { 20f, 40f, GizmoGeometry.ArmPixels })
            {
                var p = g.Centre + g.Directions[k] * along;
                if (Enumerable.Range(0, 3).Any(s => g.SquareUsable[s] && Near(g.Squares[s], p))) continue;
                Assert.Equal(GizmoHandle.AxisX + k, GizmoGeometry.Hit(g, p.X, p.Y));
            }
            var c = (g.Squares[k][0] + g.Squares[k][2]) / 2;
            Assert.Equal(GizmoHandle.PlaneX + k, GizmoGeometry.Hit(g, c.X, c.Y));
        }
        Assert.Equal(GizmoHandle.None, GizmoGeometry.Hit(g, g.Centre.X + 150, g.Centre.Y + 150));

        // Through the pane: hover the X arrow, press, drag, release — Y and Z are untouched.
        var vm = Open(Pads());
        var v = vm.Viewer;
        Select(vm, "pad");
        var lay = v.GizmoNow()!;
        var tip = lay.Centre + lay.Directions[0] * 45;
        HoverVm(v, tip.X, tip.Y);
        Assert.Equal(GizmoHandle.AxisX, v.GizmoHover);
        Assert.True(v.PressGizmo());
        var far = lay.Centre + lay.Directions[0] * 110 + new Vector2(0, 25);
        HoverVm(v, far.X, far.Y);
        v.ReleaseGizmo();
        Settle(vm);
        var o = vm.Document.Objects.Single(x => x.Name == "pad").Placement.Origin;
        Assert.True(o.X != 0 && o.Y == 0 && o.Z == 0, $"moved to {o}");

        static bool Near(Vector2[] q, Vector2 p) => q.Min(c => Vector2.Distance(c, p)) < 12;
    }

    // ── 9. Measure ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_MeasureReadsTwoCornersExactly_CopiesLosslesslyInEveryUnit_AndWritesNothing()
    {
        var vm = Open(Pads());
        var v = vm.Viewer;
        string before = C3dPersistence.Serialize(vm.Document);
        var a = NearestTopCorner(v, "pad");
        var b = NearestTopCorner(v, "other");
        long D(double m) => (long)Math.Round(m / 1e-9);
        long[] pa = [D(a.X), D(a.Y), D(a.Z)], pb = [D(b.X), D(b.Y), D(b.Z)];

        Assert.True(v.HandleKey(Key.M, KeyModifiers.None, gestureInProgress: false));
        ClickAt(vm, a.X / UmM, a.Y / UmM, a.Z / UmM);
        ClickAt(vm, b.X / UmM, b.Y / UmM, b.Z / UmM);
        Assert.True(v.MeasureP2Fixed);
        Assert.Empty(v.Selection);                                        // measuring selects nothing
        Assert.Equal(before, C3dPersistence.Serialize(vm.Document));
        Assert.False(vm.IsDirty);
        Assert.Equal(0, vm.UndoEntries);

        foreach (var unit in LayoutEditorUnits)
        {
            vm.DisplayUnit = unit;
            var r = v.MeasureReadout!;
            long Parse(Viewer3DMeasureValue val)
            {
                Assert.True(LayoutUnits.TryParse(val.Copy, LayoutUnit.Nm, vm.Document.DbuPerMicron, out long dbu), val.Copy);
                return dbu;
            }
            var rows = r.Rows;
            Assert.Equal(["P1", "P2", "Δ"], rows.Select(x => x.Heading));
            Assert.Equal(pa, new[] { Parse(rows[0].X), Parse(rows[0].Y), Parse(rows[0].Z) });
            Assert.Equal(pb, new[] { Parse(rows[1].X), Parse(rows[1].Y), Parse(rows[1].Z) });
            Assert.Equal(new[] { pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2] }, new[] { Parse(rows[2].X), Parse(rows[2].Y), Parse(rows[2].Z) });
            double dist = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (double)(pb[k] - pa[k]) * (pb[k] - pa[k])));
            Assert.Equal((long)Math.Round(dist), Parse(r.Distance!));
            Assert.All(rows, x => Assert.StartsWith(x.X.Number, x.X.Text));   // what is shown is what is copied
            var lines = r.CopyAll().TrimEnd('\n').Split('\n');
            Assert.Equal(5, lines.Length);
            Assert.Equal("\tx\ty\tz\tunit", lines[0]);
            Assert.All(lines.Skip(1), l => Assert.Equal(5, l.Split('\t').Length));
        }
        var now = C3dPersistence.Deserialize(C3dPersistence.Serialize(vm.Document));
        now.DisplayUnit = C3dPersistence.Deserialize(before).DisplayUnit;
        Assert.Equal(before, C3dPersistence.Serialize(now));                // the unit moved nothing
    }

    private static readonly LayoutUnit[] LayoutEditorUnits = [LayoutUnit.Nm, LayoutUnit.Um, LayoutUnit.Mm, LayoutUnit.Mil, LayoutUnit.Inch];

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A 40 × 40 × 20 µm pad at x = 100 µm and a 35 × 35 × 15 µm one at x = 0, y = 45 µm — sizes that are
    /// not a whole number of mils, which is what a fixed precision gets wrong.</summary>
    private string Pads() => Write([Box("pad", 100, 0, 0, 40, 40, 20), Box("other", 0, 45, 0, 35, 35, 15)]);

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Gold", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private string Write(List<C3dObject> objects) => WriteC3d(Workspace(), "cell", new C3dDocument { SnapDbu = Um, Objects = objects });

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

    private C3dEditorViewModel Open(string c3d, PatchRecordingBackend? backend = null)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = backend ?? new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        Assert.True(vm.Viewer.SnapEnabled);
        // An iso look at the content, about 0.7 µm a pixel.
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(150 * UmM, 90 * UmM, 60 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    private static void Frame(Viewer3DViewModel v, Scene3DFramePlan plan)
    {
        plan.Plan(v.Scene, v.View, (int)W, (int)H, false, false, v.MeshOverlay, v.SectionOverlay, v.GridOverlay);
        v.Session.Frame(0, plan, 1, v.Scene, v.MeshOverlay, v.SectionOverlay, v.GridOverlay, false);
    }

    private static void Select(C3dEditorViewModel vm, string name)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject(name)!.Id)]);
    }

    private static void SelectInstance(C3dEditorViewModel vm, string instance)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection(vm.Viewer.Scene.Objects.Where(o => vm.InstanceOf(o) == instance).Select(o => Scene3DItem.OfObject(o.Id)));
        Assert.NotEmpty(vm.Viewer.Selection);
    }

    /// <summary>A hover at (<paramref name="x"/>, <paramref name="y"/>) and the ID pass's answer for it (the CPU patch).</summary>
    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    /// <summary>A hover 1.5 px from where world point <paramref name="p"/> lands — near enough for a vertex snap.</summary>
    private static void HoverAt(Viewer3DViewModel v, Point3 p)
    {
        var (x, y, front) = v.View.Camera.Project(v.Scene.ToLocal(p.X, p.Y, p.Z), W, H);
        Assert.True(front);
        HoverVm(v, x + 1.5f, y + 0.5f);
    }

    /// <summary>A click exactly where world point (µm) lands.</summary>
    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        HoverVm(v, sx, sy);
        v.Click(false);
    }

    /// <summary>The top corner of <paramref name="name"/> nearest the camera — certainly visible.</summary>
    private static Point3 NearestTopCorner(Viewer3DViewModel v, string name)
    {
        var o = v.Scene.Objects.Single(x => x.Name == name);
        var f = v.Scene.FeaturesOf(o.Id);
        var corners = Enumerable.Range(0, f.Table!.Vertices.Length).Select(f.Vertex).ToList();
        double top = corners.Max(p => p.Z);
        return corners.Where(p => Math.Abs(p.Z - top) < 1e-12).MinBy(p => v.View.Camera.ViewDepth(v.Scene.ToLocal(p.X, p.Y, p.Z)));
    }
}
