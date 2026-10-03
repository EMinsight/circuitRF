// brief-em3d-101 Phase B (R-em3d101-9) — images mapped onto faces in the 3D editor: mapping one, editing it, and removing it.
//
// THE RECORD LIVES ON ITS OBJECT (C3dObject.FaceImages), so every edit here is one replacement of that object: one undo entry,
// written through ChangeFaceImages. Rename, delete, duplicate, array, group, copy/paste and undo carry a face image because they
// carry the object.
//
// ONE FUNCTION REMOVES ONE (R-em3d101-9b): RemoveFaceImages. The face's menu, the Inspector's Remove Image button, the record's own
// menu (clicked in the view) and its tree row's menu and Delete all call it; removing the object removes its face images with it.
//
// IN THE VIEW A FACE IMAGE IS A RECORD, drawn over its face as a face boundary's tint is (Scene3DBuilder, C3dImages.FacePrefix): a
// click on it selects it and its row, its menu is the record's, and B steps to the face beneath.

using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The kind of a face image's row, beneath its object in the tree.</summary>
    public const string FaceImageKind = "Image";

    /// <summary>The refusal on an instance's face (R-em3d101-9a).</summary>
    public const string InstanceFaceImage = "Open the cell to map an image onto its faces.";

    /// <summary>The face image on face <paramref name="face"/> of the object at <paramref name="index"/> (the first, D4), or null.</summary>
    public C3dFaceImage? FaceImageAt(int index, string face)
        => index >= 0 && index < Document.Objects.Count ? Document.Objects[index].FaceImages?.FirstOrDefault(f => f.Face == face) : null;

    /// <summary>The scene's name for the face image on <paramref name="face"/> of <paramref name="objectName"/>.</summary>
    public static string FaceImageSceneName(string objectName, string face) => C3dImages.FacePrefix + objectName + "/" + face;

    /// <summary>(object index, face) of a face image's scene object, or null when <paramref name="o"/> is not one of this document's.</summary>
    private (int Index, string Face)? FaceImageOf(Scene3DObject o)
    {
        if (!o.Tint || !o.Name.StartsWith(C3dImages.FacePrefix, StringComparison.Ordinal)) return null;
        string spelled = o.Name[C3dImages.FacePrefix.Length..];
        int slash = spelled.LastIndexOf('/');
        if (slash <= 0) return null;
        int index = Document.Objects.FindIndex(d => d.Name == spelled[..slash]);
        return index >= 0 ? (index, spelled[(slash + 1)..]) : null;
    }

    /// <summary>The one face image the Inspector shows: the view's selection when it is exactly one, or null.</summary>
    public (int Index, string Face)? SelectedFaceImageForInspector() => SelectedFaceImages() is [var one] ? one : null;

    /// <summary>The face images the view has selected (clicked in the view, or their rows), each once; empty when the selection
    /// is not all face images.</summary>
    private List<(int Index, string Face)> SelectedFaceImages()
    {
        var list = new List<(int, string)>();
        foreach (var o in Viewer.SelectedObjects())
        {
            if (FaceImageOf(o) is not { } fi) return [];
            if (!list.Contains(fi)) list.Add(fi);
        }
        return list;
    }

    // ── the writers ────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE writer of face images: each object named in <paramref name="change"/>'s keys replaced, once, as ONE undo
    /// entry. False when nothing changed.</summary>
    private bool ChangeFaceImages(string description, IEnumerable<int> objects, Action<C3dObject> change)
    {
        var slots = new List<C3dEditSlot>();
        foreach (int i in objects.Where(i => i >= 0 && i < Document.Objects.Count).Distinct().Order())
        {
            string before = C3dPersistence.SerializeObject(Document.Objects[i]);
            var copy = C3dPersistence.DeserializeObject(before);
            change(copy);
            if (copy.FaceImages is { Count: 0 }) copy.FaceImages = null;
            string after = C3dPersistence.SerializeObject(copy);
            if (after != before) slots.Add(new C3dEditSlot(false, i, before, after));
        }
        return slots.Count > 0 && Push(new C3dEdit(description, slots, ApplySlots));
    }

    /// <summary>Why an image cannot be mapped onto face <paramref name="face"/> of scene object <paramref name="o"/>, or null: an
    /// instance's face (its cell's own .c3d holds it), a curved face, or a face of nothing this document owns.</summary>
    public string? MapImageRefusal(Scene3DObject o, int face)
    {
        if (InstanceOf(o) is not null) return InstanceFaceImage;
        if (o.Tint || o.Kind is Scene3DKind.Port or Scene3DKind.Boundary) return "An image is mapped onto a face of an object.";
        if (Document.Objects.FindIndex(d => d.Name == o.Name) < 0) return "An image is mapped onto a face of this document's own objects.";
        var (_, normal) = Scene3DFaces.AreaAndNormal(Viewer.Scene, o.Id, face);
        return normal is null ? C3dImages.CurvedFace(o.FaceName(face)) : null;
    }

    /// <summary>
    /// Map Image… (R-em3d101-9a): the file at <paramref name="absolute"/> FITTED onto face <paramref name="face"/> of the object at
    /// <paramref name="index"/> — replacing the image already there (D4: one per face), as one undo entry with the old one in it.
    /// </summary>
    public void MapFaceImage(int index, string face, string absolute)
    {
        if (index < 0 || index >= Document.Objects.Count || !C3dImages.IsImageFile(absolute)) return;
        string full = Path.GetFullPath(absolute);
        Scene3DTextures.Refresh(full);
        var name = Document.Objects[index].Name;
        bool replace = FaceImageAt(index, face) is not null;
        ChangeFaceImages($"{(replace ? "Replace" : "Map")} image on {name}/{face}", [index], o =>
        {
            var list = o.FaceImages ??= [];
            list.RemoveAll(f => f.Face == face);
            list.Add(new C3dFaceImage { Face = face, Image = new C3dImage { Path = C3dImages.Store(FilePath, full) } });
        });
        StatusMessage = $"{(replace ? "Replaced" : "Mapped")} '{Path.GetFileName(full)}' onto {name}/{face}.";
    }

    /// <summary>
    /// THE one remover (R-em3d101-9b): the face images on <paramref name="which"/> (object index, face) taken off their objects, as
    /// ONE undo entry for the gesture. The face menu, the Inspector's button, the record's menu and its row's Delete all call it.
    /// </summary>
    public void RemoveFaceImages(IReadOnlyList<(int Index, string Face)> which)
    {
        if (which.Count == 0) return;
        var byObject = which.GroupBy(w => w.Index).ToDictionary(g => g.Key, g => g.Select(w => w.Face).ToHashSet(StringComparer.Ordinal));
        if (Viewer.SelectedObjects().Any(o => FaceImageOf(o) is not null)) Viewer.SetSelection([]);
        string what = which.Count == 1 && which[0].Index < Document.Objects.Count ? $"the image on {Document.Objects[which[0].Index].Name}/{which[0].Face}"
                                                                                 : $"{which.Count} images";
        ChangeFaceImages($"Remove {what}", byObject.Keys, o => o.FaceImages?.RemoveAll(f => byObject[Document.Objects.FindIndex(d => d.Name == o.Name)].Contains(f.Face)));
        StatusMessage = $"Removed {what}.";
    }

    /// <summary>One face image's edit (transparency, rotation, size, offset, Fit to Face, file): one undo entry.</summary>
    public void SetFaceImage(int index, string face, string description, Action<C3dFaceImage> change)
    {
        if (FaceImageAt(index, face) is null) return;
        ChangeFaceImages(description, [index], o => { if (o.FaceImages?.FirstOrDefault(f => f.Face == face) is { } fi) change(fi); });
    }

    /// <summary>R-em3d101-9c — face images' Hidden, saved, one undo entry: what their rows' ticks, H and Hide all write
    /// (SetRowsVisible calls it inside its own entry).</summary>
    public void SetFaceImagesHidden(IReadOnlyList<(int Index, string Face)> which, bool hidden)
    {
        var byObject = which.GroupBy(w => w.Index).ToDictionary(g => g.Key, g => g.Select(w => w.Face).ToHashSet(StringComparer.Ordinal));
        ChangeFaceImages(hidden ? "Hide image" : "Show image", byObject.Keys, o =>
        {
            int i = Document.Objects.FindIndex(d => d.Name == o.Name);
            foreach (var fi in o.FaceImages ?? []) if (byObject[i].Contains(fi.Face)) fi.Hidden = hidden;
        });
    }

    // ── the menus ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Face mode, one face selected: Map Image… (or Replace Image… and Remove Image when it has one), disabled with the
    /// reason on an instance's face and a curved one.</summary>
    private IEnumerable<Viewer3DMenuItem> FaceImageMenuItems()
    {
        if (Viewer.SelectMode != Scene3DSelectMode.Face || Viewer.Selection is not [{ Face: >= 0 } item]) yield break;
        if (Viewer.Scene.Object(item.Object) is not { } o || o.Tint || o.Kind is Scene3DKind.Port || BoxFaceOf(o) is not null) yield break;
        string face = o.FaceName(item.Face);
        if (MapImageRefusal(o, item.Face) is { } why) { yield return new Viewer3DMenuItem("Map Image…", Enabled: false, Tip: why); yield break; }
        int index = Document.Objects.FindIndex(d => d.Name == o.Name);
        bool has = FaceImageAt(index, face) is not null;
        yield return new Viewer3DMenuItem(has ? "Replace Image…" : "Map Image…", () => _ = PickOntoFace(index, face),
            Tip: has ? "Another image on this face, in place of the one there (one undo entry)." : "An image fitted onto this face: a die photo, a marking, a fixture.");
        if (has) yield return new Viewer3DMenuItem("Remove Image", () => RemoveFaceImages([(index, face)]));
    }

    private async Task PickOntoFace(int index, string face)
    {
        if (PickImageFile is not { } pick || await pick("Map Image") is not { } file) return;
        MapFaceImage(index, face, file);
    }

    /// <summary>R-em3d101-9e — the menu of a face image clicked in the view: the record's, never the face's.</summary>
    private List<Viewer3DMenuItem> FaceImageRecordMenuItems()
    {
        var images = SelectedFaceImages();
        if (images.Count == 0) return [];
        var items = new List<Viewer3DMenuItem>();
        if (images.Count == 1)
        {
            var (index, face) = images[0];
            items.Add(new Viewer3DMenuItem("Select Face", () => Report(SelectBoundaryFace(Document.Objects[index].Name + "/" + face)),
                Tip: "The face this image lies on, in Face mode (B from the image reaches it too)."));
            items.Add(new Viewer3DMenuItem("Replace Image…", () => _ = PickOntoFace(index, face)));
        }
        items.Add(new Viewer3DMenuItem(images.Count == 1 ? "Remove Image" : "Remove Images", () => RemoveFaceImages(images)));
        items.Add(new Viewer3DMenuItem("Hide", () => SetFaceImagesHidden(images, true)));
        items.Add(Viewer3DMenuItem.Separator);
        items.Add(new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false)));
        return items;
    }

    /// <summary>A face image row's menu (in the tree): the record's own items, through the same functions.</summary>
    private IEnumerable<Viewer3DMenuItem> FaceImageRowItems(C3dTreeItem row)
    {
        if (row.FaceImageHost is not (>= 0 and var index) || row.FaceImageFace is not { } face) yield break;
        yield return new Viewer3DMenuItem("Select Face", () => Report(SelectBoundaryFace(Document.Objects[index].Name + "/" + face)));
        yield return new Viewer3DMenuItem("Replace Image…", () => _ = PickOntoFace(index, face));
        yield return new Viewer3DMenuItem("Remove Image", () => RemoveFaceImages([(index, face)]));
        yield return Viewer3DMenuItem.Separator;
        yield return new Viewer3DMenuItem(row.IsVisible ? "Hide" : "Show", () => SetRowsVisible([row], !row.IsVisible, $"{(row.IsVisible ? "Hide" : "Show")} {row.Label}"));
        yield return new Viewer3DMenuItem("Properties", () => ShowProperties(rename: false));
    }

    /// <summary>D6 — Shift held on a drop over a flat face of this document's objects: the image mapped onto that face (true);
    /// null when there is no such face under the drop, so the drop places a sheet as a plain drop does.</summary>
    private bool? MapDroppedImageOntoFace(string path, float x, float y)
    {
        var (w, h) = Viewer.ViewSize;
        var (id, face) = Scene3DPicking.PairAtPixel(Viewer.Scene, Viewer.View.Camera, x, y, w, h, Viewer.View.Visible);
        if (Viewer.Scene.Object(id) is not { } o || face == Scene3DVertex.NoFace || MapImageRefusal(o, (int)face) is not null) return null;
        MapFaceImage(Document.Objects.FindIndex(d => d.Name == o.Name), o.FaceName((int)face), path);
        return true;
    }

    /// <summary>R-em3d101-8g — why a face image is not drawn (its face is gone, or is curved), or null.</summary>
    public string? FaceImageProblem(string objectName, C3dFaceImage fi)
    {
        if (Viewer.Scene.FaceImageProblems.TryGetValue(objectName + "/" + fi.Face, out var why)) return why;
        if (Elaboration is { } e && e.FaceImages.FirstOrDefault(u => u.Object == objectName && u.Record.Face == fi.Face) is { } use && C3dImages.Unresolved(e, use))
            return $"'{objectName}' no longer has the face '{fi.Face}'";
        return C3dImages.Problem(C3dImages.Resolve(FilePath, fi.Image.Path));
    }
}
