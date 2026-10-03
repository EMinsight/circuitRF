// ================================================================
//  ReferenceImageTests.cs — brief-em3d-101 Phase A: a reference image placed in the 3D view as a sheet. The document's
//  spelling, the one placement path, the defaults, the underlay tie, the texture upload counter, the picture on Metal
//  (macOS only; CRF_VIEWER3D_PNG=<dir> writes what Metal drew), hierarchy, archive, copy/paste, staleness, Locked and the CLI.
// ================================================================

using System.Numerics;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class ReferenceImageTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-img101-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── gate 7: the picture ──────────────────────────────────────────────────────────────────

    /// <summary>A four-quadrant PNG on an XY sheet, seen from the top on Metal, reads its own colour in each quadrant: red top-left,
    /// green top-right, blue bottom-left, yellow bottom-right — so +x is the picture's right and +y its up, and nothing is
    /// mirrored (R-em3d101-1c). The texture is RGBA8 UNORM, so a texel arrives at its own value.</summary>
    [Fact]
    public void Gate7_FourQuadrants_OnMetal_ReadUprightAndUnmirrored()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        var vm = Open(Write(dir, [Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(Path.Combine(dir, "cell.c3d"), png))]));
        vm.ShowDrawingGrid = false;
        var scene = vm.Viewer.Scene;
        Assert.Single(scene.ImageBatches);
        var view = vm.Viewer.View;
        view.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / H, Projection3D.Orthographic);
        view.Camera.SetStandardView(StandardView3D.Top);
        view.Camera.Target = (scene.ContentMin + scene.ContentMax) * 0.5f;
        var px = RenderMetal(vm, view, "quadrants");
        int Pixel(Vector3 world, int channel)
        {
            var (x, y, visible) = view.Camera.Project(world, W, H);
            Assert.True(visible, "off screen");
            return px[((int)y * (int)W + (int)x) * 4 + channel];
        }
        Vector3 At(double fx, double fy) => scene.ToLocal(fx * 100e-6, fy * 80e-6, 0);
        // top-left red, top-right green, bottom-left blue, bottom-right yellow
        Assert.True(Pixel(At(0.25, 0.75), 0) > 200 && Pixel(At(0.25, 0.75), 1) < 60 && Pixel(At(0.25, 0.75), 2) < 60, "top-left is not red");
        Assert.True(Pixel(At(0.75, 0.75), 1) > 200 && Pixel(At(0.75, 0.75), 0) < 60, "top-right is not green");
        Assert.True(Pixel(At(0.25, 0.25), 2) > 200 && Pixel(At(0.25, 0.25), 0) < 60, "bottom-left is not blue");
        Assert.True(Pixel(At(0.75, 0.25), 0) > 200 && Pixel(At(0.75, 0.25), 1) > 200 && Pixel(At(0.75, 0.25), 2) < 60, "bottom-right is not yellow");
    }

    // ── gate 1: the file ─────────────────────────────────────────────────────────────────────

    /// <summary>An image sheet — relative path, Locked, Transparency 40, Model off — reads and writes back byte for byte, its keys
    /// spelled as the schema says, and the format version is unchanged.</summary>
    [Fact]
    public void Gate1_RoundTrip_ByteIdentical_NoFormatVersionChange()
    {
        var (ws, dir) = Workspace();
        Quadrants(Path.Combine(ws, "ref", "quad.png"));
        var sheet = Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, "../../ref/quad.png");
        sheet.Locked = true;
        sheet.Transparency = 40;
        string path = Write(dir, [sheet]);
        string text = File.ReadAllText(path);
        Assert.Contains("\"Image\": {", text);
        Assert.Contains("\"Path\": \"../../ref/quad.png\"", text);
        Assert.Contains("\"Locked\": true", text);
        Assert.Contains("\"Model\": false", text);
        var doc = C3dPersistence.LoadFromFile(path);
        Assert.Equal(C3dPersistence.CurrentFormatVersion, doc.FormatVersion);
        Assert.Equal(text, C3dPersistence.Serialize(doc));
        // a sheet with none writes neither key
        var plain = new C3dDocument { Objects = [new C3dSheet { Name = "s", Rect = new C3dRect { Size = new C3dPoint2(Um, Um) } }] };
        string p = C3dPersistence.Serialize(plain);
        Assert.DoesNotContain("Image", p);
        Assert.DoesNotContain("Locked", p);
    }

    // ── gate 2: one placement path ───────────────────────────────────────────────────────────

    /// <summary>The button and a drop at the view's centre make the same document, each one undo entry; three files dropped are
    /// one entry; and a comment-stripped scan finds an image sheet built in exactly one function, PlaceImageSheet.</summary>
    [Fact]
    public void Gate2_ButtonAndDrop_OnePath_OneUndoEntryEach()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        var a = Open(Write(dir, [Box("b", "Gold", 0, 0, -10, 100, 100, 10)]));
        int undo0 = a.UndoEntries;
        a.InsertImages([png]);
        Assert.Equal(undo0 + 1, a.UndoEntries);
        Settle(a);
        string viaButton = C3dPersistence.SerializeObject(a.Document.Objects[^1]);

        var (ws2, dir2) = Workspace();
        File.Copy(png, Path.Combine(Directory.CreateDirectory(Path.Combine(ws2, "ref")).FullName, "quad.png"));
        var b = Open(Write(dir2, [Box("b", "Gold", 0, 0, -10, 100, 100, 10)]));
        int undo1 = b.UndoEntries;
        Assert.True(b.FileDrop([Path.Combine(ws2, "ref", "quad.png")], W / 2, H / 2, shift: false));
        Assert.Equal(undo1 + 1, b.UndoEntries);
        Settle(b);
        Assert.Equal(viaButton, C3dPersistence.SerializeObject(b.Document.Objects[^1]));

        // three files: one entry, three sheets side by side
        int undo2 = b.UndoEntries;
        string p2 = Quadrants(Path.Combine(ws2, "ref", "q2.png")), p3 = Quadrants(Path.Combine(ws2, "ref", "q3.png"));
        Assert.True(b.FileDrop([Path.Combine(ws2, "ref", "quad.png"), p2, p3], W / 2, H / 2, shift: false));
        Assert.Equal(undo2 + 1, b.UndoEntries);
        Assert.Equal(3, b.Document.Objects.Count(o => o is C3dSheet { Image: not null }) - 1);
        // a file that is not an image is refused, not half-imported
        Assert.False(b.FileDragOver([Path.Combine(ws2, "tech.ctech")]));

        // the one function
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitrf.slnx"))) repo = Path.GetDirectoryName(repo)!;
        var hits = new List<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            string code = StripComments(File.ReadAllText(f));
            for (int at = code.IndexOf("new C3dSheet", StringComparison.Ordinal); at >= 0; at = code.IndexOf("new C3dSheet", at + 1, StringComparison.Ordinal))
            {
                int end = code.IndexOf("};", at, StringComparison.Ordinal);
                if (end > at && code[at..end].Contains("Image =", StringComparison.Ordinal)) hits.Add(Path.GetFileName(f) + ":" + Enclosing(code, at));
            }
        }
        Assert.Equal(["C3dEditorViewModel.Images.cs:PlaceImageSheet"], hits);
    }

    // ── gate 3: the defaults ─────────────────────────────────────────────────────────────────

    /// <summary>A placed image sheet is not modelled, has no material, raises no no-material warning, draws its picture and is
    /// listed under Not Modeled by type; turning Model on puts it in the solve and raises the warning.</summary>
    [Fact]
    public void Gate3_Defaults_NotModelled_NoWarning_ModelOnWarns()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        var vm = Open(Write(dir, [Box("b", "Gold", 0, 0, -10, 100, 100, 10)]));
        var names = vm.PlaceImageSheet([png], null);
        Settle(vm);
        string name = Assert.Single(names);
        Assert.Equal("image1", name);                        // R-em3d101-2e: image1, image2, …
        var sheet = Assert.IsType<C3dSheet>(vm.Document.Objects.Single(o => o.Name == name));
        Assert.False(sheet.Model);
        Assert.Null(sheet.Material);
        Assert.Null(sheet.ThicknessUm);
        Assert.Null(sheet.Transparency);
        var e = vm.Elaboration!;
        Assert.DoesNotContain(C3dElaborator.NoMaterialWarning(name), e.Warnings);
        Assert.Contains(vm.Viewer.Scene.ImageBatches, b => vm.Viewer.Scene.Object(b.ObjectId)!.Name == name);
        Assert.Empty(C3dValidation.Validate(vm.Document, _ => true, null, vm.FilePath).Where(d => d.Id == "c3d.material.missing"));
        vm.TreeGrouping = C3dTreeGrouping.Primitive;
        Assert.Contains(vm.Tree, g => g.Header == C3dEditorViewModel.NotModeledHeader && g.Items.Any(i => i.Name == name && i.Detail!.Contains("quad.png")));

        vm.SetModel([vm.Document.Objects.IndexOf(sheet)], [], true, "Model");
        Settle(vm);
        Assert.False(C3dModelled.IsOff(vm.Elaboration!, name));
        Assert.Contains(C3dElaborator.NoMaterialWarning(name), vm.Elaboration!.Warnings);
    }

    // ── gate 4: sizing ───────────────────────────────────────────────────────────────────────

    /// <summary>The long edge is about a quarter of the view's visible width at the placement depth, the pixel aspect kept to a
    /// DBU; a file that will not decode is a 4:3 box and a Messages warning, never an exception.</summary>
    [Fact]
    public void Gate4_Sizing_QuarterOfTheView_AspectKept_UndecodableIsFourByThree()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));            // 64 x 48
        var vm = Open(Write(dir, [Box("b", "Gold", 0, 0, -10, 100, 100, 10)]));
        var cam = vm.Viewer.View.Camera;
        cam.Projection = Projection3D.Orthographic;
        vm.Viewer.View.Camera = cam;
        vm.InsertImages([png]);
        var r = ((C3dSheet)vm.Document.Objects[^1]).Rect!;
        double visible = 2 * cam.Distance * Math.Tan(cam.FovY / 2) * (W / H) / 1e-9;     // DBU (1 nm)
        double ratio = r.Size.U / visible;
        Assert.InRange(ratio, 0.15, 0.40);
        Assert.InRange(r.Size.V - r.Size.U * 48.0 / 64.0, -1, 1);

        var said = new List<(string, bool)>();
        vm.PostMessage = (t, w) => said.Add((t, w));
        string bad = Path.Combine(ws, "ref", "bad.png");
        File.WriteAllText(bad, "not an image");
        vm.InsertImages([bad]);
        var b = ((C3dSheet)vm.Document.Objects[^1]).Rect!;
        Assert.InRange(b.Size.V - b.Size.U * 3.0 / 4.0, -1, 1);
        Assert.Contains(said, m => m.Item2 && m.Item1.Contains("4:3"));
    }

    // ── gate 5: the underlay ─────────────────────────────────────────────────────────────────

    /// <summary>A rectangle traced on the image's own plane is drawn over it — the image's draw ties Underlay, the rectangle's
    /// None — and a click there picks the rectangle, on the CPU and (macOS) in Metal's ID pass.</summary>
    [Fact]
    public void Gate5_Underlay_ATracedRectangleWinsTheDepthTieAndThePick()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        var traced = new C3dSheet { Name = "trace", Material = "Gold", Plane = C3dPlane.XY, Rect = new C3dRect { Min = new C3dPoint2(30 * Um, 20 * Um), Size = new C3dPoint2(40 * Um, 40 * Um) } };
        var vm = Open(Write(dir, [Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(Path.Combine(dir, "cell.c3d"), png)), traced]));
        vm.ShowDrawingGrid = false;
        var scene = vm.Viewer.Scene;
        var view = vm.Viewer.View;
        view.Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / H, Projection3D.Orthographic);
        view.Camera.SetStandardView(StandardView3D.Top);
        view.Camera.Target = scene.ToLocal(50e-6, 40e-6, 0);
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, (int)W, (int)H, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        var draws = plan.Draws.Take(plan.DrawCount).ToList();
        Assert.Contains(draws, d => d.Pipeline == Scene3DPipeline.Image && d.Tie == Scene3DDepthTie.Underlay && d.Texture == 0);
        uint traceId = scene.Objects.Single(o => o.Name == "trace").Id;
        Assert.Equal(Scene3DDepthTie.None, Scene3DFramePlan.TieOf(scene, traceId));
        Assert.True(Scene3DFramePlan.DepthBias(Scene3DDepthTie.Underlay).Constant > Scene3DFramePlan.DepthBias(Scene3DDepthTie.Behind).Constant);
        var (id, _) = Scene3DPicking.PairAtPixel(scene, view.Camera, W / 2, H / 2, W, H, view.Visible);
        Assert.Equal(traceId, id);
        if (!OperatingSystem.IsMacOS()) return;
        view.CursorX = W / 2; view.CursorY = H / 2;
        using var m = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        m.CreateOffscreenImages((int)W, (int)H, 1);
        var session = new Viewer3DSession(() => m);
        session.EnsureBackend();
        var p2 = new Scene3DFramePlan();
        for (ulong f = 1; f <= 3; f++)
        {
            p2.Plan(scene, view, (int)W, (int)H, m.FlipY, pick: true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(0, p2, f, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        }
        m.ReadImage(0);
        Assert.Equal(traceId, m.PickedId);
    }

    // ── gate 6: the upload counter ───────────────────────────────────────────────────────────

    /// <summary>A texture is uploaded per FILE: moving, hiding, making transparent and Model-toggling an image sheet uploads none,
    /// a second sheet on the same file uploads none, and Refresh Image uploads one. A counter, not a time.</summary>
    [Fact]
    public void Gate6_UploadCounter_KeyedByFile_NeverByEdit()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad6.png"));
        var vm = Open(Write(dir, [Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(Path.Combine(dir, "cell.c3d"), png))]));
        var residency = new Scene3DTextureResidency<int>();
        int Sync() { long before = residency.Uploads; residency.Sync(vm.Viewer.Scene, _ => 1, _ => { }); return (int)(residency.Uploads - before); }
        Assert.Equal(1, Sync());
        vm.ApplyTransform([new C3dTarget(false, 0)], C3dTransform.Translation(new C3dPoint3(10 * Um, 0, 0)), true, true, "Move");
        Settle(vm);
        Assert.Equal(0, Sync());
        vm.ChangeHidden("Hide", [0], _ => true);
        Settle(vm);
        Assert.Equal(0, Sync());
        vm.ChangeHidden("Show", [0], _ => false);
        vm.SetTransparency([0], [], 50, "Transparency");
        Settle(vm);
        Assert.Equal(0, Sync());
        vm.SetModel([0], [], true, "Model");
        Settle(vm);
        Assert.Equal(0, Sync());
        vm.InsertImages([png]);
        Settle(vm);
        Assert.Equal(2, vm.Viewer.Scene.ImageBatches.Length);
        Assert.Equal(0, Sync());
        vm.RefreshImage(0);
        Settle(vm);
        Assert.Equal(1, Sync());
    }

    // ── gate 9: hierarchy ────────────────────────────────────────────────────────────────────

    /// <summary>An image sheet inside a placed cell whose .c3d is in another folder resolves against the CHILD's document and is
    /// drawn in the parent.</summary>
    [Fact]
    public void Gate9_Hierarchy_ResolvesAgainstTheChild_AndDraws()
    {
        var (ws, _) = Workspace();
        string dieDir = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(ws, "Die");
        string png = Quadrants(Path.Combine(dieDir, "photos", "die.png"));
        string dieC3d = CircuitRF.Design.Cells.CellCreate.WriteThreeDView(dieDir, "Die", new C3dDocument());
        var die = C3dPersistence.LoadFromFile(dieC3d);
        die.Objects.Add(Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(dieC3d, png)));
        C3dPersistence.SaveToFile(dieC3d, die);
        Assert.Equal("../photos/die.png", die.Objects[0] is C3dSheet { Image: { } i } ? i.Path : null);
        string topDir = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(ws, "Top");
        string top = CircuitRF.Design.Cells.CellCreate.WriteThreeDView(topDir, "Top", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die" }],
            Objects = [Box("b", "Gold", 0, 0, -10, 100, 100, 10)],
        });
        var e = new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(top), top, Path.Combine(ws, ".cws"));
        Assert.Equal(Path.GetFullPath(png), e.Images["U1/image1"].Path);
        var vm = Open(top);
        Assert.Contains(vm.Viewer.Scene.ImageBatches, b => vm.Viewer.Scene.Object(b.ObjectId)!.Name == "U1/image1");
    }

    // ── gate 10: the archive ─────────────────────────────────────────────────────────────────

    /// <summary>An image inside the workspace and one outside it are both found by the archive's reference walk and both
    /// repointed — with no code change to the walk.</summary>
    [Fact]
    public void Gate10_Archive_FindsAndRepointsBothImages()
    {
        var (ws, dir) = Workspace();
        string inside = Quadrants(Path.Combine(ws, "ref", "in.png"));
        string outside = Quadrants(Path.Combine(_root, "elsewhere", "out.png"));
        string c3d = Write(dir, [Sheet("image1", C3dPlane.XY, 0, 0, 10, 10, C3dImages.Store(Path.Combine(dir, "cell.c3d"), inside)),
                                 Sheet("image2", C3dPlane.XY, 20, 0, 10, 10, C3dImages.Store(Path.Combine(dir, "cell.c3d"), outside))]);
        Assert.True(Path.IsPathRooted(((C3dSheet)C3dPersistence.LoadFromFile(c3d).Objects[1]).Image!.Path), "outside the workspace is absolute");
        var found = CircuitRF.Ui.Archive.DocumentFileRefs.Find(c3d, ws).Select(Path.GetFullPath).ToList();
        Assert.Contains(Path.GetFullPath(inside), found);
        Assert.Contains(Path.GetFullPath(outside), found);
        string? text = CircuitRF.Ui.Archive.DocumentFileRefs.Rewrite(c3d, ws, (stored, _) => stored.EndsWith("in.png") ? "moved/in.png" : stored.EndsWith("out.png") ? "moved/out.png" : null);
        Assert.NotNull(text);
        File.WriteAllText(c3d, text);
        var back = C3dPersistence.LoadFromFile(c3d);
        Assert.Equal(["moved/in.png", "moved/out.png"], back.Objects.OfType<C3dSheet>().Select(s => s.Image!.Path).ToArray());
    }

    // ── gate 11: copy and paste across folders ───────────────────────────────────────────────

    /// <summary>Copied out of one document and pasted into another in a different folder, an image still resolves to its file.</summary>
    [Fact]
    public void Gate11_CopyPaste_AcrossFolders_KeepsAResolvingPath()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        string source = Write(dir, [Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(Path.Combine(dir, "cell.c3d"), png))]);
        var src = C3dPersistence.LoadFromFile(source);
        var payload = C3dFragment.Build(src, null, new C3dCopySelection { Objects = [0] },
                                        new C3dCopyContext(source, ws, C3dCell.None, null));
        string target = Path.Combine(ws, "other", "deeper", "3d", "t.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var doc = new C3dDocument();
        var plan = C3dFragment.Plan(doc, null, new C3dPasteContext(target, ws, C3dCell.None, null), payload);
        var r = C3dFragment.Apply(doc, null, new C3dPasteContext(target, ws, C3dCell.None, null), payload, plan.Defaults());
        Assert.Null(r.Refusal);
        var pasted = Assert.IsType<C3dSheet>(Assert.Single(doc.Objects));
        Assert.False(Path.IsPathRooted(pasted.Image!.Path), "inside the workspace a pasted image is stored relative");
        Assert.Equal(Path.GetFullPath(png), C3dImages.Resolve(target, pasted.Image.Path));
    }

    /// <summary>Save As into another folder spells the image's path for the new file — and the undo history's copies of it too, so
    /// undoing past the save never puts back a path written for the old folder.</summary>
    [Fact]
    public void Gate11_SaveAs_UndoPastTheSave_KeepsAResolvingPath()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        var vm = Open(Write(dir, [Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(Path.Combine(dir, "cell.c3d"), png))]));
        vm.SetImageLocked(0, true);
        string target = Path.Combine(ws, "other", "deeper", "3d", "t.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Assert.Null(vm.SaveAs(target));
        string Resolved() => C3dImages.Resolve(vm.FilePath, ((C3dSheet)vm.Document.Objects[0]).Image!.Path)!;
        Assert.Equal(Path.GetFullPath(png), Resolved());
        vm.UndoRedo.Undo();                                   // the lock's entry, written before the save
        Assert.False(((C3dSheet)vm.Document.Objects[0]).Locked);
        Assert.Equal(Path.GetFullPath(png), Resolved());
        vm.UndoRedo.Redo();
        Assert.Equal(Path.GetFullPath(png), Resolved());
    }

    /// <summary>A file that was missing when first drawn is drawn once it is there (copied in, restored), with no Refresh Image —
    /// as the Inspector and <c>check</c>, which read the file afresh, already say.</summary>
    [Fact]
    public void BrokenImage_IsReadAgainOnceItsFileAppears()
    {
        string png = Path.Combine(_root, "late", "late.png");
        Assert.Same(Scene3DTextures.Placeholder, Scene3DTextures.Get(png));
        Assert.Same(Scene3DTextures.Placeholder, Scene3DTextures.Get(png));      // still missing: still the checker
        Quadrants(png);
        var t = Scene3DTextures.Get(png);
        Assert.NotSame(Scene3DTextures.Placeholder, t);
        Assert.Same(t, Scene3DTextures.Get(png));                                // and kept, so nothing uploads again
        Scene3DTextures.Refresh(png);
    }

    // ── gate 12: an image edit is never stale ────────────────────────────────────────────────

    /// <summary>Re-pointing an image or changing its transparency leaves what a run compares untouched; toggling Model does not.</summary>
    [Fact]
    public void Gate12_NotStale_ImageEditsLeaveTheRunAsItWas_ModelDoesNot()
    {
        var doc = new C3dDocument { Objects = [Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, "a.png")] };
        string run = C3dPersistence.SerializeForRun(doc);
        ((C3dSheet)doc.Objects[0]).Image!.Path = "b.png";
        doc.Objects[0].Transparency = 60;
        ((C3dSheet)doc.Objects[0]).Locked = true;
        Assert.Equal(run, C3dPersistence.SerializeForRun(doc));
        Assert.Contains("b.png", C3dPersistence.Serialize(doc));
        doc.Objects[0].Model = true;
        Assert.NotEqual(run, C3dPersistence.SerializeForRun(doc));
    }

    // ── gate 13: Locked ──────────────────────────────────────────────────────────────────────

    /// <summary>A locked image sheet refuses Move, Rotate, the quick turn, the gizmo and a vertex move, each with a status
    /// sentence and the document unchanged; it is still selected, and the Inspector's edits still write.</summary>
    [Fact]
    public void Gate13_Locked_RefusesMoves_AllowsSelectionAndInspector()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        var sheet = Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(Path.Combine(dir, "cell.c3d"), png));
        sheet.Locked = true;
        var vm = Open(Write(dir, [sheet]));
        uint id = vm.Viewer.Scene.Objects.Single(o => o.Name == "image1").Id;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(id)]);
        Assert.Single(vm.Targets());
        string before = C3dPersistence.Serialize(vm.Document);
        string refusal = C3dEditorViewModel.LockedRefusal("image1");
        vm.StartMove(); Assert.Equal(refusal, vm.StatusMessage); vm.StatusMessage = "";
        vm.StartRotate(); Assert.Equal(refusal, vm.StatusMessage); vm.StatusMessage = "";
        vm.RotateQuick(C3dAxis.Z, 90); Assert.Equal(refusal, vm.StatusMessage); vm.StatusMessage = "";
        Assert.False(vm.GizmoDrag(CircuitRF.Render.Scene3D.Edit.GizmoHandle.AxisX)); Assert.Equal(refusal, vm.StatusMessage);
        vm.Viewer.SelectMode = Scene3DSelectMode.Vertex;
        vm.Viewer.SetSelection([Scene3DItem.OfVertex(id, vm.Viewer.Scene.ToLocal(0, 0, 0))]);
        vm.StatusMessage = "";
        vm.StartVertexMove(); Assert.Equal(refusal, vm.StatusMessage);
        Assert.Equal(refusal, vm.SetVertexCoordinates(new C3dPoint3(-50 * Um, 0, 0)));
        // a sheet's face is the whole sheet: Face mode's Move Along Normal, Move and Align to Face would move it too
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(id, 0)]);
        foreach (Action start in new Action[] { vm.StartPushPull, vm.StartFaceMove, vm.StartAlignToFace })
        {
            vm.StatusMessage = "";
            start();
            Assert.Null(vm.Tool);
            Assert.Equal(refusal, vm.StatusMessage);
        }
        Assert.Equal(before, C3dPersistence.Serialize(vm.Document));
        // Keep aspect follows the IMAGE's pixels (64 × 48), not the rectangle's present 100 × 80 (R-em3d101-3b)
        vm.SetImageSize(0, 200 * Um, null, keepAspect: true);
        Assert.Equal(new C3dPoint2(200 * Um, 150 * Um), ((C3dSheet)vm.Document.Objects[0]).Rect!.Size);
    }

    // ── gate 14: the CLI ─────────────────────────────────────────────────────────────────────

    /// <summary><c>check</c> warns on an image file that is not there and exits 0; <c>render --iso</c> as a PROCESS writes the
    /// in-process renderer's SVG byte for byte, and the SVG carries the picture as an &lt;image&gt;.</summary>
    [Fact]
    public void Gate14_Cli_CheckWarns_RenderIsoMatchesAndCarriesTheImage()
    {
        var (ws, dir) = Workspace();
        string png = Quadrants(Path.Combine(ws, "ref", "quad.png"));
        string path = Write(dir, [Box("b", "Gold", 0, 0, -10, 100, 100, 10),
                                  Sheet("image1", C3dPlane.XY, 0, 0, 100, 80, C3dImages.Store(Path.Combine(dir, "cell.c3d"), png)),
                                  Sheet("lost", C3dPlane.XY, 200, 0, 10, 10, "../../ref/missing.png")]);
        var (code, so, se) = Cli("check", path);
        Assert.True(code == 0, so + se);
        Assert.Contains("missing.png", so + se);
        Assert.Contains("cannot be drawn", so + se);

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
        {
            ObjectTransparency = Scene3DTransparency.MapOf(e.Provenance),
        };
        var scene = CircuitRF.Render.Em3dSectionScene.Build(problem, CircuitRF.Render.Em3dView.Iso);
        scene = scene with { Images = CircuitRF.Render.Em3dSceneImages.Of(problem, e.Images, CircuitRF.Render.Em3dSectionScene.Project) };
        Assert.Equal(2, scene.Images.Count);                 // the missing one is drawn as the placeholder
        string inProcess = System.Text.Encoding.UTF8.GetString(CircuitRF.Cli.VectorPage.Svg(400, 300, c => CircuitRF.Render.Em3dSectionRenderer.Draw(c, 400, 300, scene, style)));
        // Skia numbers a document's clip paths and images per PROCESS (RenderCliVerbTests' StripSkiaIds): only those may differ
        Assert.Equal(StripSkiaIds(inProcess), StripSkiaIds(fromCli));
    }

    internal static string StripSkiaIds(string svg) => System.Text.RegularExpressions.Regex.Replace(svg, @"\b(cl|img|gr|fp)_[0-9a-z]+\b", "$1_N");

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, string? material, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)), "the scene never settled");

    private static (int ExitCode, string StdOut, string StdErr) Cli(params string[] args)
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitrf.slnx"))) repo = Path.GetDirectoryName(repo)!;
        return CircuitRF.Ui.Tests.Em3d.CliProcess.Run(repo, [], args);
    }

    /// <summary>The source with // and /* */ comments blanked (string literals kept).</summary>
    private static string StripComments(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>The name of the method whose body holds position <paramref name="at"/>: the last "Name(" before an opening brace.</summary>
    private static string Enclosing(string code, int at)
    {
        string[] keywords = ["if", "for", "foreach", "while", "switch", "using", "lock", "catch", "return", "new", "nameof"];
        var m = System.Text.RegularExpressions.Regex.Matches(code[..at], @"\b(\w+)\s*\([^;{}]*\)\s*\{")
                     .Where(x => !keywords.Contains(x.Groups[1].Value)).ToList();
        return m.Count > 0 ? m[^1].Groups[1].Value : "?";
    }

    /// <summary>A PNG of 64 × 48 in four quadrants: red top-left, green top-right, blue bottom-left, yellow bottom-right.</summary>
    internal static string Quadrants(string path, int w = 64, int h = 48)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            using var p = new SKPaint();
            p.Color = new SKColor(255, 0, 0); c.DrawRect(0, 0, w / 2f, h / 2f, p);
            p.Color = new SKColor(0, 255, 0); c.DrawRect(w / 2f, 0, w / 2f, h / 2f, p);
            p.Color = new SKColor(0, 0, 255); c.DrawRect(0, h / 2f, w / 2f, h / 2f, p);
            p.Color = new SKColor(255, 255, 0); c.DrawRect(w / 2f, h / 2f, w / 2f, h / 2f, p);
        }
        using var f = File.Create(path);
        bmp.Encode(f, SKEncodedImageFormat.Png, 100);
        return path;
    }

    internal static C3dSheet Sheet(string name, C3dPlane plane, long u, long v, long su, long sv, string imagePath, long offset = 0)
        => new()
        {
            Name = name, Plane = plane, Offset = offset * Um, Model = false,
            Rect = new C3dRect { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(su * Um, sv * Um) },
            Image = new C3dImage { Path = imagePath },
        };

    private (string Ws, string Dir) Workspace()
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
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)), "the scene never settled");
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
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
