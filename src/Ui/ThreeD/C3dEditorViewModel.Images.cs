// brief-em3d-101 R-em3d101-2 / -3 — reference images in the 3D editor: placing one, and what an image sheet offers.
//
// ONE PLACEMENT FUNCTION (R-em3d101-2). The toolbar's Insert Image…, 3D ▸ Draw ▸ Image… and a file dropped on the view all call
// PlaceImageSheet, and it is the only place an image sheet is built: a second caller with its own sizing would be a copy that
// drifts. It places on the CURRENT drawing plane, centred where a ray through the view (its centre, or the drop point) meets the
// plane, at a long edge of about a quarter of the view's width AT THAT DEPTH, the image's pixel aspect kept — R-bmp-4's rule,
// carried over from the layout. Several files make one sheet each, side by side along the plane's right axis, as one undo entry.
//
// AN IMAGE SHEET IS A SHEET (R-em3d101-1). Everything a sheet does it does — Model, a material, a port from its row, move,
// rotate, group, copy, array, hide. What is added here is only what the image brings: Locked, Drawing Plane from Image, Replace
// Image…, Resolve Path…, Refresh Image, Remove Image, and the corner resize that keeps the picture's aspect.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The toolbar button's and the menu item's tooltip.</summary>
    public const string InsertImageTip = "Insert Image…: a reference image as a sheet on the drawing plane, not modelled";

    /// <summary>A placed image's long edge, as a fraction of the view's visible width at the placement point (R-bmp-4).</summary>
    public const double ImageViewFraction = 0.25;

    /// <summary>The smallest pane side, DIPs, a placement sizes against; below it the pane is taken as not laid out.</summary>
    public const float MinPlacementPixels = 16;

    /// <summary>The status line's sentence for a gesture a Locked image sheet refuses.</summary>
    public static string LockedRefusal(string name) => $"'{name}' is a locked image: unlock it (its menu, or the Inspector) to move it.";

    /// <summary>Messages for the Messages panel — (text, is a warning). The shell posts them; a test reads them.</summary>
    public Action<string, bool>? PostMessage { get; set; }

    /// <summary>Image sheets placed, by any of the three gestures.</summary>
    public int ImagePlacements { get; private set; }

    /// <summary>Why Insert Image… cannot run in this editor now, or null.</summary>
    public string? InsertImageRefusal()
        => IsViewOnly ? "A setup's 3D view shows its problem; open the 3D view itself to place an image."
         : _entered is not null ? "An image is placed in the document, not inside a boolean: leave the boolean first." : null;

    /// <summary>Insert Image… (the button and the menu): <paramref name="paths"/> on the drawing plane, centred where the view's
    /// centre ray meets it.</summary>
    public void InsertImages(IReadOnlyList<string> paths) => PlaceImageSheet(paths, null);

    /// <summary>A drop of image files at (<paramref name="px"/>, <paramref name="py"/>), device pixels of the view.</summary>
    public void DropImages(IReadOnlyList<string> paths, float px, float py) => PlaceImageSheet(paths, (px, py));

    /// <summary>brief-em3d-101 R-em3d101-2c — a drag of files over the view: accepted when every one is an image this build reads.</summary>
    public bool FileDragOver(IReadOnlyList<string> paths)
    {
        bool ok = paths.Count > 0 && paths.All(C3dImages.IsImageFile) && InsertImageRefusal() is null;
        if (_fileDragStatus is null || StatusMessage != _fileDragStatus) _statusBeforeFileDrag = StatusMessage;
        StatusMessage = ok ? (paths.Count == 1 ? $"Drop to place '{Path.GetFileName(paths[0])}' on {PlaneText}." : $"Drop to place {paths.Count} images on {PlaneText}.")
                      : InsertImageRefusal() ?? "Only an image file (" + string.Join(", ", C3dImages.Extensions) + ") is placed by a drop.";
        _fileDragStatus = StatusMessage;
        return ok;
    }

    /// <summary>The status line a file drag over the view wrote, and what it said before: a drag that leaves without dropping
    /// puts that back (<see cref="EndFileDrag"/>), so "Drop to place…" does not stay up once nothing is being dragged.</summary>
    private string? _fileDragStatus, _statusBeforeFileDrag;

    /// <summary>A file drag ended. Left without a drop: the drag's own sentence goes, if nothing has said anything since.</summary>
    private void EndFileDrag(bool dropped)
    {
        if (!dropped && _fileDragStatus is not null && StatusMessage == _fileDragStatus) StatusMessage = _statusBeforeFileDrag ?? "";
        _fileDragStatus = _statusBeforeFileDrag = null;
    }

    /// <summary>brief-em3d-101 — files dropped on the view: image sheets, one undo entry for all of them. Anything else does
    /// nothing. With <paramref name="shift"/> over a flat face (D6), Phase B maps the image onto that face instead.</summary>
    public bool FileDrop(IReadOnlyList<string> paths, float x, float y, bool shift)
    {
        bool ok = FileDragOver(paths);
        EndFileDrag(dropped: true);
        if (!ok) return false;
        if (shift && MapDroppedImageOntoFace(paths[0], x, y) is { } mapped) return mapped;
        DropImages(paths, x, y);
        return true;
    }

    /// <summary>
    /// THE one place an image sheet is built (gate 2): <paramref name="paths"/> (absolute; anything that is not an image file is
    /// skipped) as sheets on the drawing plane, centred where the ray through <paramref name="pixel"/> — the view's centre when
    /// null — meets the plane (the view's target projected onto it when the plane is edge-on), one undo entry, the new sheets
    /// selected. Returns the names placed.
    /// </summary>
    public IReadOnlyList<string> PlaceImageSheet(IReadOnlyList<string> paths, (float X, float Y)? pixel)
    {
        if (InsertImageRefusal() is { } refused) { StatusMessage = refused; return []; }
        var files = paths.Where(C3dImages.IsImageFile).Select(Path.GetFullPath).ToList();
        if (files.Count == 0) { StatusMessage = "Nothing to place: an image is a " + string.Join(", ", C3dImages.Extensions) + " file."; return []; }

        // ── where: the plane point under the ray, and the view's width there ─────────────────────
        var (vw, vh) = Viewer.ViewSize;
        // A pane not laid out yet, or squeezed to a sliver, has no width to take a quarter of (its aspect collapses the visible
        // width to nothing and the sheet came out at the snap minimum): it is sized as the default view would size it, and
        // centred on the view's target, since no ray through it means anything.
        bool usable = vw >= MinPlacementPixels && vh >= MinPlacementPixels;
        if (!usable) (vw, vh) = Viewer3DViewModel.DefaultViewSize;
        float px = pixel?.X ?? vw / 2, py = pixel?.Y ?? vh / 2;
        int dbu = Document.DbuPerMicron;
        double per = C3dLowering.Metres(1, dbu);
        var cam = Viewer.View.Camera;
        var (origin, dir) = Viewer.RayAt(px, py);
        Point3 at;
        if (usable && _plane.Hit(origin, dir, dbu) is { } hit) at = hit;
        else
        {
            var (tx, ty, tz) = Viewer.Scene.ToWorld(cam.Target);
            var t = new Point3(tx, ty, tz);
            double w = C3dLowering.Metres(_plane.OffsetDbu, dbu);
            at = _plane.Normal switch { C3dAxis.X => t with { X = w }, C3dAxis.Y => t with { Y = w }, _ => t with { Z = w } };
        }
        var local = Viewer.Scene.ToLocal(at.X, at.Y, at.Z);
        double tan = Math.Tan((cam.FovY > 0 && cam.FovY < MathF.PI ? cam.FovY : Camera3D.DefaultFovY) * 0.5);
        double depth = cam.Projection == Projection3D.Orthographic ? cam.Distance
                     : Math.Max(1e-12, System.Numerics.Vector3.Dot(local - cam.Eye, cam.Forward));
        double visibleWidth = 2 * depth * tan * Math.Max(1e-6, vw / Math.Max(1f, vh));
        long longEdge = TidyLength(ImageViewFraction * visibleWidth / per);

        // ── the sheets: each its pixel aspect, side by side along the plane's u ──────────────────
        var sizes = new List<(string File, long U, long V)>();
        foreach (string f in files)
        {
            if (C3dImages.PixelSize(f) is { Width: > 0, Height: > 0 } px2)
                sizes.Add(px2.Width >= px2.Height
                    ? (f, longEdge, Math.Max(1, (long)Math.Round((double)longEdge * px2.Height / px2.Width)))
                    : (f, Math.Max(1, (long)Math.Round((double)longEdge * px2.Width / px2.Height)), longEdge));
            else
            {
                PostMessage?.Invoke($"Could not read the image dimensions of '{Path.GetFileName(f)}': placed as a 4:3 box. Resolve Path… " +
                                    "or Replace Image… once the file reads.", true);
                sizes.Add((f, longEdge, Math.Max(1, (long)Math.Round(longEdge * 3.0 / 4.0))));
            }
        }
        long gap = files.Count > 1 ? Math.Max(1, longEdge / 10) : 0;
        long row = sizes.Sum(s => s.U) + gap * (sizes.Count - 1);
        var c = new C3dPoint3((long)Math.Round(at.X / per), (long)Math.Round(at.Y / per), (long)Math.Round(at.Z / per));
        var cuv = _plane.ToUv(c);
        long u = cuv.U - row / 2;
        var used = new HashSet<string>(Document.Objects.Select(o => o.Name).Concat(Document.Instances.Select(i => i.Name)).Concat(NestedNames())
                                               .Concat(ThermalPlaceNames()), StringComparer.Ordinal);
        var slots = new List<C3dEditSlot>();
        var names = new List<string>();
        int next = Document.Objects.Count;
        foreach (var (file, su, sv) in sizes)
        {
            // image1, image2, … — the smallest free number (NextName's rule), each added to used so a batch never collides.
            int n = 1;
            while (used.Contains(C3dImages.NameStem + n)) n++;
            string name = C3dImages.NameStem + n;
            used.Add(name);
            var sheet = new C3dSheet
            {
                Name = name, Plane = _plane.Plane, Offset = _plane.OffsetDbu, Model = false,
                Rect = new C3dRect { Min = new C3dPoint2(Snap(u), Snap(cuv.V - sv / 2)), Size = new C3dPoint2(su, sv) },
                Image = new C3dImage { Path = C3dImages.Store(FilePath, file) },
            };
            u += su + gap;
            names.Add(name);
            slots.Add(new C3dEditSlot(false, next++, null, C3dPersistence.SerializeObject(sheet)));
        }
        if (!Push(new C3dEdit(names.Count == 1 ? $"Insert image {names[0]}" : $"Insert {names.Count} images", slots, ApplySlots))) return [];
        ImagePlacements += names.Count;
        _selectAfterAdopt = names;
        StatusMessage = names.Count == 1
            ? $"Placed \"{names[0]}\" ({Path.GetFileName(sizes[0].File)}) on {PlaneText}, not modelled."
            : $"Placed {names.Count} images on {PlaneText}, not modelled.";
        ShowProperties(false);
        return names;

        long Snap(long v) => Document.SnapDbu > 0 ? (long)Math.Round((double)v / Document.SnapDbu, MidpointRounding.AwayFromZero) * Document.SnapDbu : v;
    }

    /// <summary>A length in DBU rounded to a tidy number in the document's display unit — 1, 2 or 5 times a power of ten, the
    /// nearest — and then to the snap step: what a placed image's long edge is.</summary>
    private long TidyLength(double dbu)
    {
        double unitDbu = (double)LayoutUnits.ToDbu(1m, Document.DisplayUnit, Document.DbuPerMicron);
        double x = Math.Max(1e-12, dbu / Math.Max(1e-12, unitDbu));
        double p = Math.Pow(10, Math.Floor(Math.Log10(x)));
        double best = new[] { 1, 2, 5, 10 }.Select(k => k * p).MinBy(v => Math.Abs(Math.Log(v / x)));
        long d = Math.Max(1, (long)Math.Round(best * unitDbu));
        if (Document.SnapDbu > 0) d = Math.Max(Document.SnapDbu, (long)Math.Round((double)d / Document.SnapDbu) * Document.SnapDbu);
        return d;
    }

    // ── an image sheet, found ─────────────────────────────────────────────────────────────────

    /// <summary>The document index of the one selected object when it is an image sheet (Object mode), else −1.</summary>
    private int SelectedImageSheet()
        => Targets() is [{ Instance: false } t] && t.Index < Document.Objects.Count && Document.Objects[t.Index] is C3dSheet { Image: not null } ? t.Index : -1;

    /// <summary>The image sheet at <paramref name="index"/>, or null.</summary>
    public C3dSheet? ImageSheetAt(int index)
        => index >= 0 && index < Document.Objects.Count && Document.Objects[index] is C3dSheet { Image: not null } s ? s : null;

    /// <summary>The absolute path an image sheet's file resolves to, against this document (R-em3d101-1b).</summary>
    public string? ImagePathOf(C3dSheet s) => C3dImages.Resolve(FilePath, s.Image?.Path);

    /// <summary>Why the image of <paramref name="s"/> cannot be drawn, or null: what its tree row's warning and Resolve Path… say.</summary>
    public string? ImageProblemOf(C3dSheet s) => s.Image is null ? null : C3dImages.Problem(ImagePathOf(s));

    /// <summary>R-em3d101-1g — the refusal for a gesture that would move <paramref name="targets"/> when one is a locked image
    /// sheet, or null.</summary>
    private string? LockedRefusalOf(IEnumerable<C3dTarget> targets)
        => targets.FirstOrDefault(t => !t.Instance && t.Index < Document.Objects.Count && Document.Objects[t.Index] is C3dSheet { Locked: true })
           is { Instance: false } locked && Document.Objects[locked.Index] is C3dSheet { Locked: true } s ? LockedRefusal(s.Name) : null;

    /// <summary>The selection's targets when none is a locked image sheet; otherwise the status line says why, and false.</summary>
    private bool HaveMovableTargets(out IReadOnlyList<C3dTarget> targets)
    {
        if (!HaveTargets(out targets)) return false;
        if (LockedRefusalOf(targets) is { } why) { StatusMessage = why; return false; }
        return true;
    }

    // ── what an image sheet offers ────────────────────────────────────────────────────────────

    /// <summary>One replacement of object <paramref name="index"/>, written by <paramref name="change"/> on a copy: one undo entry.
    /// False when it changed nothing.</summary>
    private bool ChangeObject(int index, string description, Action<C3dObject> change)
    {
        if (index < 0 || index >= Document.Objects.Count) return false;
        string before = C3dPersistence.SerializeObject(Document.Objects[index]);
        var copy = C3dPersistence.DeserializeObject(before);
        change(copy);
        string after = C3dPersistence.SerializeObject(copy);
        return after != before && Push(new C3dEdit(description, [new C3dEditSlot(false, index, before, after)], ApplySlots));
    }

    /// <summary>Lock or unlock an image sheet (R-em3d101-1g): one undo entry.</summary>
    public void SetImageLocked(int index, bool locked)
    {
        if (ImageSheetAt(index) is not { } s) return;
        if (!ChangeObject(index, $"{(locked ? "Lock" : "Unlock")} {s.Name}", o => ((C3dSheet)o).Locked = locked)) return;
        StatusMessage = locked ? $"'{s.Name}' is locked: selected and edited, never moved." : $"'{s.Name}' is unlocked.";
    }

    /// <summary>Points an image sheet at <paramref name="absolute"/> (Replace Image…, Resolve Path…, the Inspector's Browse…): one
    /// undo entry, and the new file is read afresh.</summary>
    public void SetImagePath(int index, string absolute, string verb = "Replace image of")
    {
        if (ImageSheetAt(index) is not { } s) return;
        string full = Path.GetFullPath(absolute);
        Scene3DTextures.Refresh(full);
        ChangeObject(index, $"{verb} {s.Name}", o => ((C3dSheet)o).Image!.Path = C3dImages.Store(FilePath, full));
        Viewer.Regenerate();
    }

    /// <summary>Refresh Image: the file read again — for one edited outside circuitRF. Nothing in the document changes.</summary>
    public void RefreshImage(int index)
    {
        if (ImageSheetAt(index) is not { } s) return;
        Scene3DTextures.Refresh(ImagePathOf(s));
        Viewer.Regenerate();
        StatusMessage = $"Read '{C3dImages.FileName(s.Image!)}' again.";
    }

    /// <summary>Remove Image (R-em3d101-3b): the sheet stays, an ordinary sheet — what turns a traced reference into a real one.</summary>
    public void RemoveSheetImage(int index)
    {
        if (ImageSheetAt(index) is not { } s) return;
        ChangeObject(index, $"Remove image of {s.Name}", o => { ((C3dSheet)o).Image = null; ((C3dSheet)o).Locked = false; });
        StatusMessage = $"'{s.Name}' is an ordinary sheet now.";
    }

    /// <summary>Drawing Plane from Image (R-em3d101-2g): the drawing plane set to the image sheet's plane and offset — tracing then
    /// takes one click to set up. A sheet its placement turns off the three planes is refused, as a tilted face is.</summary>
    public string? PlaneFromImage(int index)
    {
        if (ImageSheetAt(index) is not { } s) return "Select an image sheet.";
        var t = s.Placement.ToTransform();
        var normal = DrawingPlane.NormalOf(s.Plane);
        var n = normal switch { C3dAxis.X => (1.0, 0.0, 0.0), C3dAxis.Y => (0.0, 1.0, 0.0), _ => (0.0, 0.0, 1.0) };
        var (nx, ny, nz) = (t.M00 * n.Item1 + t.M01 * n.Item2 + t.M02 * n.Item3, t.M10 * n.Item1 + t.M11 * n.Item2 + t.M12 * n.Item3,
                            t.M20 * n.Item1 + t.M21 * n.Item2 + t.M22 * n.Item3);
        C3dAxis? axis = Math.Abs(Math.Abs(nx) - 1) < 1e-9 ? C3dAxis.X : Math.Abs(Math.Abs(ny) - 1) < 1e-9 ? C3dAxis.Y
                      : Math.Abs(Math.Abs(nz) - 1) < 1e-9 ? C3dAxis.Z : null;
        if (axis is not { } a) return $"'{s.Name}' is turned off the XY, YZ and XZ planes, and a drawing plane is one of the three.";
        var p = t.Apply(DrawingPlane.With(default, normal, s.Offset));
        long offset = (long)Math.Round(a switch { C3dAxis.X => p.X, C3dAxis.Y => p.Y, _ => p.Z }, MidpointRounding.AwayFromZero);
        SetPlane(new DrawingPlane(DrawingPlane.PlaneNormalTo(a), offset));
        StatusMessage = $"Drawing plane: {PlaneText}, from the image \"{s.Name}\". Trace it with the Polygon tool.";
        return null;
    }

    /// <summary>
    /// Width and height of an image sheet's rectangle, DBU (the Inspector's rows): with <paramref name="keepAspect"/>, the one
    /// that did NOT change follows the other at the IMAGE's pixel aspect (R-em3d101-3b) — the rectangle's present aspect only
    /// when the file cannot be read. One undo entry; the rectangle stays where its minimum corner is.
    /// </summary>
    public void SetImageSize(int index, long? width, long? height, bool keepAspect)
    {
        if (ImageSheetAt(index) is not { Rect: { } r } s || width is null && height is null) return;
        double aspect = ImageAspectOf(s) ?? (r.Size.V != 0 ? Math.Abs((double)r.Size.U / r.Size.V) : 1);
        long w = width ?? r.Size.U, h = height ?? r.Size.V;
        if (keepAspect && width is not null && height is null) h = Math.Max(1, (long)Math.Round(w / aspect));
        else if (keepAspect && height is not null && width is null) w = Math.Max(1, (long)Math.Round(h * aspect));
        if (w <= 0 || h <= 0) { StatusMessage = "A sheet's width and height are above zero."; return; }
        ChangeObject(index, $"Resize {s.Name}", o => WriteImageRect(o, new C3dRect { Min = r.Min, Size = new C3dPoint2(w, h) }));
    }

    /// <summary>An image sheet's rectangle written as numbers: an expression either size was bound to is unbound with it, as the
    /// sheet's own Size rows do for a number (SetFieldText) — else the next resolve would put the bound value back.</summary>
    private static void WriteImageRect(C3dObject o, C3dRect rect)
    {
        foreach (string path in (string[])["Rect.Size[0]", "Rect.Size[1]"])
            if (C3dBindings.Find(o, path) is { } f) C3dBindings.SetExpr(f.Owner, f.Spec, f.Component, null);
        ((C3dSheet)o).Rect = rect;
    }

    /// <summary>The image's own pixel aspect (width ÷ height), or null when its file cannot be read.</summary>
    public double? ImageAspectOf(C3dSheet s)
        => ImagePathOf(s) is { } file && C3dImages.PixelSize(file) is { Width: > 0, Height: > 0 } px ? (double)px.Width / px.Height : null;

    /// <summary>Reset to Image Aspect: the height follows the width at the image's own pixel aspect.</summary>
    public void ResetImageAspect(int index)
    {
        if (ImageSheetAt(index) is not { Rect: { } r } s) return;
        if (ImagePathOf(s) is not { } file || C3dImages.PixelSize(file) is not { Width: > 0, Height: > 0 } px)
        {
            StatusMessage = $"'{s.Name}''s image cannot be read, so it has no aspect to go back to.";
            return;
        }
        long h = Math.Max(1, (long)Math.Round((double)r.Size.U * px.Height / px.Width));
        ChangeObject(index, $"Reset {s.Name} to its image's aspect", o => WriteImageRect(o, new C3dRect { Min = r.Min, Size = new C3dPoint2(r.Size.U, h) }));
    }

    // ── menus ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A file picker the shell supplies (code-behind: the UI firewall keeps StorageProvider out of the view model):
    /// a title, and the absolute path chosen or null.</summary>
    public Func<string, Task<string?>>? PickImageFile { get; set; }

    /// <summary>Opens the picker and points the sheet at the file chosen.</summary>
    private async Task PickInto(int index, string title, string verb)
    {
        if (PickImageFile is not { } pick || await pick(title) is not { } file) return;
        SetImagePath(index, file, verb);
    }

    /// <summary>R-em3d101-3c — what an image sheet's context menu adds (the view's and its tree row's): everything a sheet offers
    /// stays where it is.</summary>
    private IEnumerable<Viewer3DMenuItem> ImageItems(int index)
    {
        if (ImageSheetAt(index) is not { } s) yield break;
        yield return new Viewer3DMenuItem("Drawing Plane from Image", () => { if (PlaneFromImage(index) is { } why) StatusMessage = why; });
        yield return new Viewer3DMenuItem(s.Locked ? "Unlock" : "Lock", () => SetImageLocked(index, !s.Locked),
            Tip: "A locked image stays selectable and editable in the Inspector, but Move, Rotate, the gizmo and vertex moves are refused.");
        yield return new Viewer3DMenuItem("Replace Image…", () => _ = PickInto(index, "Replace Image", "Replace image of"));
        if (ImageProblemOf(s) is { } problem)
            yield return new Viewer3DMenuItem("Resolve Path…", () => _ = PickInto(index, "Resolve Image Path", "Resolve image path of"),
                                              Tip: $"Its image cannot be drawn: {problem}.");
        yield return new Viewer3DMenuItem("Refresh Image", () => RefreshImage(index), Tip: "Read the file again — after editing it outside circuitRF.");
        yield return new Viewer3DMenuItem("Remove Image", () => RemoveSheetImage(index), Tip: "The sheet stays, an ordinary sheet: a traced reference becomes a real one.");
    }

    /// <summary>The view's context menu items for a selected image sheet (Object mode), with a separator after them.</summary>
    private IEnumerable<Viewer3DMenuItem> ImageMenuItems()
    {
        int index = SelectedImageSheet();
        if (index < 0) yield break;
        foreach (var item in ImageItems(index)) yield return item;
        yield return Viewer3DMenuItem.Separator;
    }
}
