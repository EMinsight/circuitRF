// ================================================================
//  FaceImageTests.cs — brief-em3d-101 Phase B: an image mapped onto a named flat face of an object. The file's spelling, the
//  face frame (never mirrored seen from outside), the clip to the face, transparency independent of the object's, removal
//  through one function, replace, refusals, visibility through SetRowsVisible, what is carried for free, and the CLI.
//  Pixels on the real Metal backend (macOS only; CRF_VIEWER3D_PNG=<dir> writes what Metal drew).
// ================================================================

using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class FaceImageTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-fimg101-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── gate 1: the file ─────────────────────────────────────────────────────────────────────

    /// <summary>Two face images on one object (one sized, rotated and offset, one fitted), one carried by an operation, and one on a
    /// face that no longer exists read and write back byte for byte — the stale one kept, never dropped.</summary>
    [Fact]
    public void Gate1_RoundTrip_ByteIdentical_StaleFaceKept()
    {
        var (_, dir) = Workspace();
        var box = Box("lid", "Gold", 0, 0, 0, 100, 80, 20);
        box.FaceImages =
        [
            new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = "../../ref/die.png" }, Transparency = 30, RotationDeg = 90,
                               Width = 50 * Um, Height = 40 * Um, Offset = new C3dPoint2(5 * Um, -3 * Um) },
            new C3dFaceImage { Face = "xmin", Image = new C3dImage { Path = "../../ref/side.png" } },
            new C3dFaceImage { Face = "gone", Image = new C3dImage { Path = "../../ref/old.png" }, Hidden = true },
        ];
        var op = new C3dBoolean
        {
            Name = "cut", Op = C3dBooleanOp.Subtract, Blank = Box("", "Gold", 200, 0, 0, 50, 50, 20), Tools = [Box("t", "Gold", 210, 10, 10, 10, 10, 20)],
            FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = "../../ref/die.png" } }],
        };
        string path = Write(dir, [box, op]);
        string text = File.ReadAllText(path);
        Assert.Contains("\"FaceImages\": [", text);
        Assert.Contains("\"RotationDeg\": 90", text);
        Assert.Contains("\"Face\": \"gone\"", text);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.LoadFromFile(path)));
        // drawing only: what a run compares does not see them
        Assert.DoesNotContain("FaceImages", C3dPersistence.SerializeForRun(C3dPersistence.LoadFromFile(path)));
    }

    // ── gate 2: the frame ────────────────────────────────────────────────────────────────────

    /// <summary>On each of a box's six faces and on a tilted polyhedron face, the frame reads unmirrored from outside (right × up is
    /// the outward normal), up is +z projected into the face (+y on a ±z face), and a fitted image is centred on the face.</summary>
    [Fact]
    public void Gate2_Frame_UnmirroredFromOutside_UpByTheRule()
    {
        var box = new Em3dSolid("b", "Gold", Em3dRole.Conductor, new Em3dBox(new Point3(0, 0, 0), new Point3(2, 1, 3)), 0);
        var mesh = Em3dTessellation.Of(box);
        var names = Em3dTessellation.BoxFaces;
        foreach (string face in names)
        {
            int fi = Array.IndexOf([.. names], face);
            var tris = mesh.Triangles.Where(t => t.Face == fi).Select(t => (mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C])).ToList();
            var f = C3dImages.FaceFrame(tris, out _)!.Value;
            var outward = face switch
            {
                "xmin" => new Point3(-1, 0, 0), "xmax" => new Point3(1, 0, 0), "ymin" => new Point3(0, -1, 0),
                "ymax" => new Point3(0, 1, 0), "zmin" => new Point3(0, 0, -1), _ => new Point3(0, 0, 1),
            };
            Assert.Equal(1, Dot(f.Normal, outward), 6);
            Assert.Equal(1, Dot(Cross(f.Right, f.Up), f.Normal), 6);              // right × up = outward: not mirrored from outside
            var up = face is "zmin" or "zmax" ? new Point3(0, 1, 0) : new Point3(0, 0, 1);
            Assert.Equal(1, Dot(f.Up, up), 6);
            var placed = C3dImages.OnFace(tris, new C3dFaceImage { Face = face }, "x.png", (64, 48), 1e-9, out _)!;
            var centre = new Point3((placed.Right.X + placed.Up.X) / 2, (placed.Right.Y + placed.Up.Y) / 2, (placed.Right.Z + placed.Up.Z) / 2);
            Assert.Equal(0, Dist(centre, f.Centre), 9);
        }
        // a tilted face: a wedge's slope
        var wedge = new Em3dPolyhedron(
            [new(0, 0, 0), new(2, 0, 0), new(2, 1, 0), new(0, 1, 0), new(0, 0, 1), new(0, 1, 1)],
            [new([0, 3, 2, 1], [], "bottom"), new([0, 1, 4], [], "front"), new([3, 5, 2], [], "back"), new([0, 4, 5, 3], [], "left"),
             new([1, 2, 5, 4], [], "slope")]);
        var wm = Em3dTessellation.Of(new Em3dSolid("w", "Gold", Em3dRole.Conductor, wedge, 0));
        int si = 4;
        var st = wm.Triangles.Where(t => t.Face == si).Select(t => (wm.Vertices[t.A], wm.Vertices[t.B], wm.Vertices[t.C])).ToList();
        var sf = C3dImages.FaceFrame(st, out _)!.Value;
        Assert.True(sf.Normal.X > 0 && sf.Normal.Z > 0, "the slope faces up and out along +x");
        Assert.Equal(1, Dot(Cross(sf.Right, sf.Up), sf.Normal), 6);
        Assert.True(sf.Up.Z > 0, "up climbs the slope");
        // curved: a cylinder's side has no frame
        var cyl = Em3dTessellation.Of(new Em3dSolid("c", "Gold", Em3dRole.Conductor, new Em3dCylinder(new Point3(0, 0, 0), new Point3(0, 0, 1), 1), 0));
        var side = cyl.Triangles.Where(t => t.Face == 2).Select(t => (cyl.Vertices[t.A], cyl.Vertices[t.B], cyl.Vertices[t.C])).ToList();
        Assert.Null(C3dImages.FaceFrame(side, out string? why));
        Assert.Equal("curved", why);
    }

    /// <summary>On Metal, a four-quadrant image on every face of a box, seen from outside that face, reads each quadrant's colour
    /// where the frame rule puts it — unmirrored and upright.</summary>
    [Fact]
    public void Gate2_Frame_OnMetal_EveryFaceReadsItsQuadrants()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        var box = Box("b", "Gold", 0, 0, 0, 100, 80, 60);
        string c3d = Path.Combine(dir, "cell.c3d");
        box.FaceImages = [.. C3dBox.FaceNameList.Select(f => new C3dFaceImage { Face = f, Image = new C3dImage { Path = C3dImages.Store(c3d, png) } })];
        var vm = Open(Write(dir, [box]));
        vm.ShowDrawingGrid = false;
        var scene = vm.Viewer.Scene;
        Assert.Equal(6, scene.PlacedFaceImages.Count);
        var views = new Dictionary<string, StandardView3D>
        {
            ["zmax"] = StandardView3D.Top, ["zmin"] = StandardView3D.Bottom, ["ymin"] = StandardView3D.Front,
            ["ymax"] = StandardView3D.Back, ["xmin"] = StandardView3D.Left, ["xmax"] = StandardView3D.Right,
        };
        foreach (var placed in scene.PlacedFaceImages)
        {
            string face = placed.Use.Record.Face;
            var view = vm.Viewer.View;
            view.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / H, Projection3D.Orthographic);
            view.Camera.SetStandardView(views[face]);
            view.Camera.Target = (scene.ContentMin + scene.ContentMax) * 0.5f;
            var px = RenderMetal(vm, view, "face-" + face);
            // the picture's quadrant centres, where the frame rule puts them: red top-left, green top-right, blue bottom-left, yellow
            var p = placed.Placed;
            Vector3 At(double fr, double fu)
            {
                double x = p.Origin.X + fr * (p.Right.X - p.Origin.X) + fu * (p.Up.X - p.Origin.X);
                double y = p.Origin.Y + fr * (p.Right.Y - p.Origin.Y) + fu * (p.Up.Y - p.Origin.Y);
                double z = p.Origin.Z + fr * (p.Right.Z - p.Origin.Z) + fu * (p.Up.Z - p.Origin.Z);
                return scene.ToLocal(x, y, z);
            }
            (int R, int G, int B) Read(Vector3 w)
            {
                var (x, y, visible) = view.Camera.Project(w, W, H);
                Assert.True(visible, face);
                int i = ((int)y * (int)W + (int)x) * 4;
                return (px[i], px[i + 1], px[i + 2]);
            }
            var tl = Read(At(0.25, 0.75)); var tr = Read(At(0.75, 0.75)); var bl = Read(At(0.25, 0.25)); var br = Read(At(0.75, 0.25));
            Assert.True(tl.R > 200 && tl.G < 80 && tl.B < 80, $"{face}: top-left {tl}");
            Assert.True(tr.G > 200 && tr.R < 80, $"{face}: top-right {tr}");
            Assert.True(bl.B > 200 && bl.R < 80, $"{face}: bottom-left {bl}");
            Assert.True(br.R > 200 && br.G > 200 && br.B < 80, $"{face}: bottom-right {br}");
            // and seen from outside the picture is not mirrored: right then up turns counter-clockwise on the screen
            var (ox, oy, _) = view.Camera.Project(At(0, 0), W, H);
            var (rx, ry, _) = view.Camera.Project(At(1, 0), W, H);
            var (ux, uy, _) = view.Camera.Project(At(0, 1), W, H);
            Assert.True((rx - ox) * -(uy - oy) - -(ry - oy) * (ux - ox) > 0, $"{face}: mirrored on screen");
        }
    }

    // ── gate 3: the clip ─────────────────────────────────────────────────────────────────────

    /// <summary>An image twice its face's size is drawn only on the face; a half-size one leaves the face's own colour round it.</summary>
    [Fact]
    public void Gate3_Clip_OnlyWhereTheFaceIs()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string c3d = Path.Combine(dir, "cell.c3d");
        var box = Box("b", "Gold", 0, 0, 0, 100, 80, 20);
        box.FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) }, Width = 200 * Um, Height = 160 * Um }];
        var vm = Open(Write(dir, [box]));
        vm.ShowDrawingGrid = false;
        var scene = vm.Viewer.Scene;
        var view = vm.Viewer.View;
        view.Camera = Camera3D.Fit(scene.ContentMin - new Vector3(100e-6f), scene.ContentMax + new Vector3(100e-6f), W / H, Projection3D.Orthographic);
        view.Camera.SetStandardView(StandardView3D.Top);
        view.Camera.Target = scene.ToLocal(50e-6, 40e-6, 20e-6);
        var px = RenderMetal(vm, view, "clip-twice");
        (int R, int G, int B) Read(double x, double y)
        {
            var (sx, sy, _) = view.Camera.Project(scene.ToLocal(x * 1e-6, y * 1e-6, 20e-6), W, H);
            int i = ((int)sy * (int)W + (int)sx) * 4;
            return (px[i], px[i + 1], px[i + 2]);
        }
        var bg = view.Background;
        var outside = Read(-30, 40);                         // inside the 200 × 160 picture, outside the face
        Assert.True(Math.Abs(outside.R - bg.R * 255) < 8 && Math.Abs(outside.G - bg.G * 255) < 8, $"drawn off the face: {outside}");
        var inside = Read(10, 70);                           // a quadrant of the picture, on the face
        Assert.True(inside.R > 200 || inside.G > 200 || inside.B > 200, $"nothing drawn on the face: {inside}");

        vm.SetFaceImage(0, "zmax", "half", fi => { fi.Width = 50 * Um; fi.Height = 40 * Um; });
        Settle(vm);
        px = RenderMetal(vm, view, "clip-half");
        var rim = Read(5, 5);                                // on the face, outside the half-size picture: the face's gold
        var gold = vm.Viewer.Scene.Objects.Single(o => o.Name == "b").Rgba;
        Assert.True(Math.Abs(rim.R - (int)(gold & 0xFF)) < 60 && rim.B < 150, $"the face's colour was not left round it: {rim}");
    }

    // ── gate 4: transparency is the image's own ──────────────────────────────────────────────

    /// <summary>An object at 80 % with its face image at 0 % draws the image opaque — and an opaque object with its image at 80 %
    /// draws the image see-through.</summary>
    [Fact]
    public void Gate4_Transparency_IndependentOfTheObject()
    {
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string c3d = Path.Combine(dir, "cell.c3d");
        var box = Box("b", "Gold", 0, 0, 0, 100, 80, 20);
        box.Transparency = 80;
        box.FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } }];
        var vm = Open(Write(dir, [box]));
        var scene = vm.Viewer.Scene;
        var image = scene.ImageBatches.Single();
        Assert.False(image.Translucent, "a lid at 80 % made its marking translucent");
        Assert.Equal(255u, scene.ImageVertices[image.FirstVertex].Rgba >> 24);
        Assert.True(scene.Objects.Single(o => o.Name == "b").Translucent);

        vm.SetTransparency([0], [], null, "Opaque");
        vm.SetFaceImage(0, "zmax", "see-through", fi => fi.Transparency = 80);
        Settle(vm);
        scene = vm.Viewer.Scene;
        image = scene.ImageBatches.Single();
        Assert.True(image.Translucent);
        Assert.Equal(51u, scene.ImageVertices[image.FirstVertex].Rgba >> 24);
        Assert.False(scene.Objects.Single(o => o.Name == "b").Translucent);
        // and in the frame plan the image ties FaceImage: over its face
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, vm.Viewer.View, (int)W, (int)H, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.ImageTranslucent && d.Tie == Scene3DDepthTie.FaceImage);
    }

    // ── gates 5 and 6: removal, all three ways, through one function; replace ────────────────

    /// <summary>The face menu's Remove Image, the Inspector's button and the record's Delete each call RemoveFaceImages (a
    /// comment-stripped scan) and are one undo entry that undo restores; deleting the object takes its images and undo brings them
    /// back; Map Image… on a face that has one replaces it as one entry.</summary>
    [Fact]
    public void Gate5And6_Removal_ThreeWays_OneFunction_AndReplace()
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitrf.slnx"))) repo = Path.GetDirectoryName(repo)!;
        string Code(string rel) => Regex.Replace(File.ReadAllText(Path.Combine(repo, rel)), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        string face = Code("src/Ui/ThreeD/C3dEditorViewModel.FaceImages.cs");
        string inspector = Code("src/Ui/ThreeD/C3dPropertiesViewModel.FaceImages.cs");
        string deletes = Code("src/Ui/ThreeD/C3dEditorViewModel.cs");
        Assert.Contains("\"Remove Image\", () => RemoveFaceImages(", face);                    // the face's menu, the row's
        Assert.Contains("editor.RemoveFaceImages(", inspector);                                  // the Inspector's button
        Assert.Contains("SelectedFaceImages() is { Count: > 0 } faceImages) { RemoveFaceImages(faceImages)", deletes);   // Delete
        // nothing else writes a face image away
        foreach (string f in Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories))
            if (!f.EndsWith("C3dEditorViewModel.FaceImages.cs") && !f.EndsWith("C3dHierarchy.cs"))
                Assert.DoesNotContain("FaceImages?.RemoveAll", Code(Path.GetRelativePath(repo, f)));

        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string png2 = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q2.png"));
        var vm = Open(Write(dir, [Box("b", "Gold", 0, 0, 0, 100, 80, 20)]));
        vm.MapFaceImage(0, "zmax", png);
        string mapped = C3dPersistence.SerializeObject(vm.Document.Objects[0]);
        int undo = vm.UndoEntries;
        vm.MapFaceImage(0, "zmax", png2);                                  // replace
        Assert.Equal(undo + 1, vm.UndoEntries);
        Assert.Single(vm.Document.Objects[0].FaceImages!);
        Assert.EndsWith("q2.png", vm.Document.Objects[0].FaceImages![0].Image.Path);
        vm.UndoRedo.Undo();
        Assert.Equal(mapped, C3dPersistence.SerializeObject(vm.Document.Objects[0]));
        Settle(vm);

        // the face's menu
        uint id = vm.Viewer.Scene.Objects.Single(o => o.Name == "b").Id;
        int zmax = Array.IndexOf([.. vm.Viewer.Scene.Object(id)!.FaceNames], "zmax");
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(id, zmax)]);
        var remove = vm.Viewer.ContextMenuItems().Single(i => i.Header == "Remove Image");
        undo = vm.UndoEntries;
        remove.Run!();
        Assert.Equal(undo + 1, vm.UndoEntries);
        Assert.Null(vm.Document.Objects[0].FaceImages);
        vm.UndoRedo.Undo();
        Assert.Equal(mapped, C3dPersistence.SerializeObject(vm.Document.Objects[0]));
        Settle(vm);

        // the record, clicked in the view: Delete
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        uint rec = vm.Viewer.Scene.Objects.Single(o => o.Name == C3dEditorViewModel.FaceImageSceneName("b", "zmax")).Id;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(rec)]);
        Assert.Equal(("b/zmax"), vm.Properties.Heading.Replace("Image on ", ""));
        undo = vm.UndoEntries;
        Assert.True(vm.DeleteSelection());
        Assert.Equal(undo + 1, vm.UndoEntries);
        Assert.Null(vm.Document.Objects[0].FaceImages);
        vm.UndoRedo.Undo();
        Settle(vm);

        // deleting the object takes its images with it; undo brings them back
        vm.DeleteObjects([0]);
        Assert.Empty(vm.Document.Objects);
        vm.UndoRedo.Undo();
        Assert.Equal(mapped, C3dPersistence.SerializeObject(vm.Document.Objects[0]));
    }

    // ── gate 7: refusals ─────────────────────────────────────────────────────────────────────

    /// <summary>A cylinder's side and an instance's face refuse Map Image with the reason.</summary>
    [Fact]
    public void Gate7_Refusals_CurvedFaceAndInstanceFace()
    {
        var (ws, dir) = Workspace();
        string dieDir = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(ws, "Die");
        CircuitRF.Design.Cells.CellCreate.WriteThreeDView(dieDir, "Die", new C3dDocument { Objects = [Box("chip", "Gold", 0, 0, 0, 10, 10, 10)] });
        var cyl = new C3dCylinder { Name = "c", Material = "Gold", Base = new C3dPoint3(200 * Um, 0, 0), Length = 20 * Um, Radius = 10 * Um };
        string top = Write(dir, [cyl]);
        var doc = C3dPersistence.LoadFromFile(top);
        doc.Instances.Add(new C3dInstance { Name = "U1", CellRef = "../../Die" });
        C3dPersistence.SaveToFile(top, doc);
        var vm = Open(top);
        var c = vm.Viewer.Scene.Objects.Single(o => o.Name == "c");
        Assert.Equal(C3dImages.CurvedFace("side"), vm.MapImageRefusal(c, Array.IndexOf([.. c.FaceNames], "side")));
        Assert.Null(vm.MapImageRefusal(c, Array.IndexOf([.. c.FaceNames], "top")));
        var part = vm.Viewer.Scene.Objects.Single(o => o.Name == "U1/chip");
        Assert.Equal(C3dEditorViewModel.InstanceFaceImage, vm.MapImageRefusal(part, 0));
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(c.Id, Array.IndexOf([.. c.FaceNames], "side"))]);
        var item = vm.Viewer.ContextMenuItems().Single(i => i.Header == "Map Image…");
        Assert.False(item.Enabled);
        Assert.Equal(C3dImages.CurvedFace("side"), item.Tip);
    }

    // ── gate 8: visibility ───────────────────────────────────────────────────────────────────

    /// <summary>The row's tick, H and Hide all each hide a face image through SetRowsVisible: saved, one undo entry each.</summary>
    [Fact]
    public void Gate8_Visibility_TickHAndHideAll_Saved_OneEntry()
    {
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string c3d = Path.Combine(dir, "cell.c3d");
        var box = Box("b", "Gold", 0, 0, 0, 100, 80, 20);
        box.FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } }];
        var vm = Open(Write(dir, [box]));
        C3dTreeItem Row() => vm.Tree.SelectMany(g => g.Items).SelectMany(i => i.Children.Prepend(i)).Single(r => r.Kind == C3dEditorViewModel.FaceImageKind);
        Assert.Equal("Image on zmax", Row().Label);
        Assert.Equal("q.png", Row().Detail);

        int undo = vm.UndoEntries;
        Row().IsVisible = false;                                       // the tick
        Assert.Equal(undo + 1, vm.UndoEntries);
        Assert.True(vm.Document.Objects[0].FaceImages![0].Hidden);
        Settle(vm);
        Assert.Empty(vm.Viewer.Scene.ImageBatches);
        // hidden, it is in no scene: Delete with its ROW selected still removes it, as one entry (R-em3d101-9b)
        vm.SelectedTreeItem = Row();
        undo = vm.UndoEntries;
        Assert.True(vm.DeleteSelection());
        Assert.Equal(undo + 1, vm.UndoEntries);
        Assert.Null(vm.Document.Objects[0].FaceImages);
        vm.UndoRedo.Undo();
        Settle(vm);
        Row().IsVisible = true;
        Settle(vm);

        uint rec = vm.Viewer.Scene.Objects.Single(o => o.Name == C3dEditorViewModel.FaceImageSceneName("b", "zmax")).Id;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(rec)]);
        undo = vm.UndoEntries;
        Assert.True(vm.HideOrShowSelection());                         // H
        Assert.Equal(undo + 1, vm.UndoEntries);
        Assert.True(vm.Document.Objects[0].FaceImages![0].Hidden);
        vm.UndoRedo.Undo();
        Settle(vm);

        undo = vm.UndoEntries;
        vm.HideAllTreeObjectsCommand.Execute(null);                    // Hide all
        Assert.Equal(undo + 1, vm.UndoEntries);
        Assert.True(vm.Document.Objects[0].FaceImages![0].Hidden);
        Assert.True(vm.Document.Objects[0].Hidden);
    }

    /// <summary>Hidden ticked in the Inspector keeps the image selected — its row and its section stay up, so it can be shown again
    /// there — and the size boxes emptied go back to the default, fitted to the face.</summary>
    [Fact]
    public void Inspector_HiddenKeepsTheSection_EmptySizesFit()
    {
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string c3d = Path.Combine(dir, "cell.c3d");
        var box = Box("b", "Gold", 0, 0, 0, 100, 80, 20);
        box.FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) }, Width = 40 * Um, Height = 30 * Um }];
        var vm = Open(Write(dir, [box]));
        uint rec = vm.Viewer.Scene.Objects.Single(o => o.Name == C3dEditorViewModel.FaceImageSceneName("b", "zmax")).Id;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(rec)]);
        Assert.True(vm.Properties.HasFaceImage);

        vm.Properties.FaceImageHidden = true;
        Settle(vm);
        Assert.True(vm.Document.Objects[0].FaceImages![0].Hidden);
        // the row selected is the tree's row as it is NOW: the adopted scene rebuilds the record rows, and a selected row that is
        // no longer in the tree is one the tree view drops (an empty selection, the Inspector gone)
        C3dTreeItem Row() => vm.Tree.SelectMany(g => g.Items).SelectMany(i => i.Children.Prepend(i)).Single(r => r.Kind == C3dEditorViewModel.FaceImageKind);
        Assert.Same(Row(), vm.SelectedTreeItem);
        Assert.Equal([Row()], vm.SelectedTreeItems);
        Assert.True(vm.Properties.HasFaceImage);
        vm.Properties.FaceImageHidden = false;
        Settle(vm);
        Assert.False(vm.Document.Objects[0].FaceImages![0].Hidden);

        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.Viewer.Scene.Objects.Single(o => o.Name == C3dEditorViewModel.FaceImageSceneName("b", "zmax")).Id)]);
        int undo = vm.UndoEntries;
        vm.Properties.FaceImageWidth = "";
        vm.Properties.CommitFaceImageSize(width: true);              // one emptied: that size follows the other
        Assert.Equal((null, 30 * Um), (vm.Document.Objects[0].FaceImages![0].Width, vm.Document.Objects[0].FaceImages![0].Height));
        vm.Properties.FaceImageHeight = "";
        vm.Properties.CommitFaceImageSize(width: false);             // both emptied: the default, fitted
        Assert.True(vm.Document.Objects[0].FaceImages![0].Fitted);
        Assert.Equal(undo + 2, vm.UndoEntries);
        Assert.Null(vm.Properties.FaceImageError);
    }

    // ── gate 9: carried for free ─────────────────────────────────────────────────────────────

    /// <summary>Rename, duplicate, array and copy/paste into another folder keep an object's face images, their paths resolving.</summary>
    [Fact]
    public void Gate9_CarriedForFree_RenameDuplicateArrayCopyPaste()
    {
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        var vm = Open(Write(dir, [Box("b", "Gold", 0, 0, 0, 100, 80, 20)]));
        vm.MapFaceImage(0, "zmax", png);
        Assert.Null(vm.Rename(0, "lid"));
        Assert.Equal("zmax", vm.Document.Objects[0].FaceImages!.Single().Face);
        Assert.True(vm.InsertCopies([new C3dTarget(false, 0)], [C3dTransform.Translation(new C3dPoint3(200 * Um, 0, 0)),
                                                                 C3dTransform.Translation(new C3dPoint3(400 * Um, 0, 0))], "Array"));
        Assert.Equal(3, vm.Document.Objects.Count(o => o.FaceImages is [{ Face: "zmax" }]));
        Settle(vm);
        Assert.Equal(3, vm.Viewer.Scene.PlacedFaceImages.Count);

        var payload = C3dFragment.Build(vm.Document, null, new C3dCopySelection { Objects = [0] }, new C3dCopyContext(vm.FilePath, ws, C3dCell.None, null));
        string target = Path.Combine(ws, "elsewhere", "deep", "3d", "t.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var into = new C3dDocument();
        var ctx = new C3dPasteContext(target, ws, C3dCell.None, null);
        C3dFragment.Apply(into, null, ctx, payload, C3dFragment.Plan(into, null, ctx, payload).Defaults());
        var pasted = into.Objects.Single().FaceImages!.Single();
        Assert.Equal(Path.GetFullPath(png), C3dImages.Resolve(target, pasted.Image.Path));
    }

    // ── gate 10: the CLI ─────────────────────────────────────────────────────────────────────

    /// <summary><c>check</c> warns on a face the object no longer has and on a missing file, exiting 0; <c>render --iso</c> as a
    /// PROCESS writes the in-process renderer's SVG byte for byte with face images in it.</summary>
    [Fact]
    public void Gate10_Cli_CheckWarns_RenderIsoMatches()
    {
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string c3d = Path.Combine(dir, "cell.c3d");
        var box = Box("b", "Gold", 0, 0, 0, 100, 80, 20);
        box.FaceImages =
        [
            new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } },
            new C3dFaceImage { Face = "nowhere", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } },
            new C3dFaceImage { Face = "xmax", Image = new C3dImage { Path = "../../ref/lost.png" } },
            // turned away from the iso's viewer (+x +y +z): seen only through the box, so not drawn
            new C3dFaceImage { Face = "zmin", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } },
            new C3dFaceImage { Face = "ymin", Image = new C3dImage { Path = C3dImages.Store(c3d, png) }, Width = 0 },
        ];
        string path = Write(dir, [box]);
        var (code, so, se) = Cli("check", path);
        Assert.True(code == 0, so + se);
        Assert.Contains("no longer has", so + se);
        Assert.Contains("lost.png", so + se);
        Assert.Contains("not positive", so + se);

        string svg = Path.Combine(_root, "iso.svg");
        var (exit, stdout, stderr) = Cli("render", path, "-o", svg, "--iso", "--size", "400x300");
        Assert.True(exit == 0, stderr + stdout);
        string fromCli = File.ReadAllText(svg);
        Assert.Contains("<image", fromCli);
        var e = new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(path), path, Path.Combine(ws, ".cws"));
        var problem = C3dProblemAssembly.ViewProblem(e.Solids, e.Sheets, e.Materials, [], C3dProblemAssembly.ExtentBox(e.Extent()!.Value));
        problem = CircuitRF.Render.Em3dSceneImages.WithImageSheets(problem, e);
        var theme = CircuitRF.Render.ThemeResolver.Resolve(CircuitRF.Render.ThemeResolver.DefaultThemeName, ws);
        var style = new CircuitRF.Render.Em3dRenderStyle(CircuitRF.Render.Em3dSectionRenderer.ObjectColours(problem, e.Origins, e.Technology, theme, CircuitRF.Render.ColorVariant.Light),
                                                         theme, CircuitRF.Render.ColorVariant.Light, CircuitRF.Render.DocumentExtents.DefaultMargin, Transparent: false)
        { ObjectTransparency = Scene3DTransparency.MapOf(e.Provenance) };
        var scene = CircuitRF.Render.Em3dSectionScene.Build(problem, CircuitRF.Render.Em3dView.Iso);
        scene = scene with
        {
            Images = [.. CircuitRF.Render.Em3dSceneImages.Of(problem, e.Images, CircuitRF.Render.Em3dSectionScene.Project),
                      .. CircuitRF.Render.Em3dSceneImages.OfFaces(Scene3DFaceImages.Of(problem, e), CircuitRF.Render.Em3dSectionScene.Project,
                                                                 mirrored: CircuitRF.Render.Em3dSectionScene.ProjectIsMirrored)],
        };
        Assert.Equal(2, scene.Images.Count);                 // zmax, and xmax's placeholder; "nowhere" is not drawn
        string inProcess = System.Text.Encoding.UTF8.GetString(CircuitRF.Cli.VectorPage.Svg(400, 300, c => CircuitRF.Render.Em3dSectionRenderer.Draw(c, 400, 300, scene, style)));
        Assert.Equal(ReferenceImageTests.StripSkiaIds(inProcess), ReferenceImageTests.StripSkiaIds(fromCli));
    }

    /// <summary>A face image moves with its object while the object is dragged (it is a record of its own in the scene, so the
    /// drag's moving set did not take it and it stood where the object was until the drop). Another object's does not.</summary>
    [Fact]
    public void Drag_TheFaceImageFollowsItsObject()
    {
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string c3d = Path.Combine(dir, "cell.c3d");
        var moved = Box("b", "Gold", 0, 0, 0, 100, 80, 20);
        var still = Box("c", "Gold", 300, 0, 0, 100, 80, 20);
        moved.FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } }];
        still.FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } }];
        var vm = Open(Write(dir, [moved, still]));
        vm.Viewer.FitCommand.Execute(null);
        vm.Viewer.View.Selected = vm.SceneObject("b")!.Id;
        vm.Viewer.Hover(W / 2, H / 2);
        Assert.True(vm.GizmoDrag(GizmoHandle.AxisX));
        vm.Viewer.Hover(W / 2 + 60, H / 2);
        vm.Viewer.OnPicked(0, 0, Vector3.Zero, false);
        var preview = Assert.IsType<Scene3DPreview>(vm.Viewer.View.Preview);
        Assert.True(preview.IsMoving(vm.SceneObject(C3dEditorViewModel.FaceImageSceneName("b", "zmax"))!.Id));
        Assert.False(preview.IsMoving(vm.SceneObject(C3dEditorViewModel.FaceImageSceneName("c", "zmax"))!.Id));
        vm.GizmoCancel();
    }

    /// <summary>A hole drilled through a face carves its image in a picture made outside the view (`render --iso`) as the view
    /// carves it: the image is clipped around the hole, not drawn across it.</summary>
    [Fact]
    public void Bore_ThePictureClipsAFaceImageAroundTheHole_AsTheViewDoes()
    {
        var (ws, dir) = Workspace();
        string png = ReferenceImageTests.Quadrants(Path.Combine(ws, "ref", "q.png"));
        string c3d = Path.Combine(dir, "cell.c3d");
        var slab = Box("sub", "Gold", 0, 0, 0, 100, 100, 20);
        slab.Role = Em3dRole.Dielectric;
        slab.FaceImages = [new C3dFaceImage { Face = "zmax", Image = new C3dImage { Path = C3dImages.Store(c3d, png) } }];
        var hole = new C3dCylinder { Name = "hole", Material = "Gold", Role = Em3dRole.Air, Base = new C3dPoint3(50 * Um, 50 * Um, 0), Length = 20 * Um, Radius = 10 * Um };
        string path = Write(dir, [slab, hole]);
        var e = new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(path), path, Path.Combine(ws, ".cws"));
        var problem = C3dProblemAssembly.ViewProblem(e.Solids, e.Sheets, e.Materials, [], C3dProblemAssembly.ExtentBox(e.Extent()!.Value));
        var placed = Assert.Single(Scene3DFaceImages.Of(problem, e));
        bool Covers(double x, double y) => placed.Triangles.Any(t => Inside(t, x, y));
        Assert.False(Covers(50e-6, 50e-6), "drawn across the hole");
        Assert.True(Covers(10e-6, 10e-6));

        var vm = Open(path);                                              // and the view clips it the same way
        Assert.False(Assert.Single(vm.Viewer.Scene.PlacedFaceImages).Triangles.Any(t => Inside(t, 50e-6, 50e-6)));
    }

    private static bool Inside((Point3 A, Point3 B, Point3 C) t, double x, double y)
    {
        static double Side(Point3 p, Point3 q, double x, double y) => (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
        double d1 = Side(t.A, t.B, x, y), d2 = Side(t.B, t.C, x, y), d3 = Side(t.C, t.A, x, y);
        return (d1 >= 0 && d2 >= 0 && d3 >= 0) || (d1 <= 0 && d2 <= 0 && d3 <= 0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Dist(Point3 a, Point3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static C3dBox Box(string name, string? material, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private (string Ws, string Dir) Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        return (ws, dir);
    }

    private static string Write(string dir, List<C3dObject> objects)
    {
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument { Objects = objects });
        return path;
    }

    private C3dEditorViewModel Open(string c3d)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)), "the scene never settled");

    private static (int ExitCode, string StdOut, string StdErr) Cli(params string[] args)
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitrf.slnx"))) repo = Path.GetDirectoryName(repo)!;
        return CircuitRF.Ui.Tests.Em3d.CliProcess.Run(repo, [], args);
    }

    private static byte[] RenderMetal(C3dEditorViewModel vm, Viewer3DViewState view, string name)
    {
        var scene = vm.Viewer.Scene;
        using var m = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        m.CreateOffscreenImages((int)W, (int)H, 1);
        var session = new Viewer3DSession(() => m);
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        for (ulong f = 1; f <= 2; f++)
        {
            plan.Plan(scene, view, (int)W, (int)H, m.FlipY, pick: false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(0, plan, f, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        }
        var px = m.ReadImage(0);
        if (Environment.GetEnvironmentVariable("CRF_VIEWER3D_PNG") is { Length: > 0 } dir)
        {
            using var bmp = new SKBitmap(new SKImageInfo((int)W, (int)H, SKColorType.Rgba8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            using var f = File.Create(Path.Combine(dir, name + ".png"));
            bmp.Encode(f, SKEncodedImageFormat.Png, 100);
        }
        return px;
    }
}
