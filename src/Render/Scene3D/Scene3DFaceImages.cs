// brief-em3d-101 R-em3d101-10 — where an image mapped onto a face is: the face's own triangles, found by NAME in its object's
// tessellation, and the picture placed in the face's frame (C3dImages.OnFace). One function, called by the 3D view's scene builder
// and by every vector picture (Copy as Vector, Export Drawing…, `render --iso`), so the picture on the screen and on the page agree.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>A face image placed: its object, the face spelled <c>object/face</c>, the picture's corners, and the face's triangles
/// (world metres) it is drawn on and clipped to.</summary>
public sealed record Scene3DPlacedFaceImage(string Object, string Face, C3dFaceImageUse Use, C3dPlacedImage Placed,
                                            IReadOnlyList<(Point3 A, Point3 B, Point3 C)> Triangles);

public static class Scene3DFaceImages
{
    /// <summary>
    /// <paramref name="use"/> placed on its object's face, from the object's <paramref name="mesh"/> (world metres) whose triangle k
    /// lies on face <c>mesh.Triangles[k].Face</c>, named by <paramref name="faceNames"/> (a sheet's whole mesh is its one face,
    /// <paramref name="sheet"/>). Null, with <paramref name="why"/>, when the object has no such face or the face is curved.
    /// </summary>
    public static Scene3DPlacedFaceImage? Place(C3dFaceImageUse use, Em3dTriangleMesh mesh, IReadOnlyList<string> faceNames, bool sheet,
                                                out string? why)
    {
        string face = use.Record.Face;
        var on = new HashSet<int>();
        for (int k = 0; k < faceNames.Count; k++) if (C3dImages.IsFace(faceNames[k], face)) on.Add(k);
        if (on.Count == 0) { why = $"'{use.Object}' has no face '{face}'"; return null; }
        var tris = new List<(Point3, Point3, Point3)>();
        foreach (var t in mesh.Triangles)
            if (on.Contains(sheet ? 0 : t.Face)) tris.Add((mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C]));
        if (tris.Count == 0) { why = $"'{use.Object}' draws nothing on its face '{face}'"; return null; }
        var placed = C3dImages.OnFace(tris, use.Record, use.Path ?? "", C3dImages.PixelSize(use.Path), use.MetresPerDbu, out string? frame);
        if (placed is null) { why = frame == "curved" ? C3dImages.CurvedFace(face) : $"Face {face} {frame}"; return null; }
        why = null;
        return new Scene3DPlacedFaceImage(use.Object, use.FaceSpelled, use, placed, tris);
    }

    /// <summary>Whether <paramref name="use"/> is drawn: not hidden, and the first image on its face (owner decision D4).</summary>
    public static bool Drawn(IReadOnlyList<C3dFaceImageUse> all, C3dFaceImageUse use)
        => !use.Record.Hidden && all.First(u => u.Object == use.Object && u.Record.Face == use.Record.Face).Index == use.Index;

    /// <summary>
    /// The face images of <paramref name="e"/> on the objects <paramref name="problem"/> holds, placed — what a picture made outside
    /// the 3D view (`render --iso`) draws. Each object is tessellated here as the view tessellates it; a face that cannot carry its
    /// image is left out (the view's tree and `check` say why).
    /// </summary>
    public static IReadOnlyList<Scene3DPlacedFaceImage> Of(Em3dProblem problem, C3dElaboration e)
    {
        var list = new List<Scene3DPlacedFaceImage>();
        foreach (var use in e.FaceImages)
        {
            if (!Drawn(e.FaceImages, use) || !e.Provenance.TryGetValue(use.Object, out var p)) continue;
            Em3dTriangleMesh? mesh = null;
            bool sheet = false;
            if (problem.Solids.FirstOrDefault(s => s.Name == use.Object) is { } solid) mesh = Em3dTessellation.Of(solid);
            else if (problem.Sheets.FirstOrDefault(s => s.Name == use.Object) is { } sh) { mesh = Em3dTessellation.OfSheet(sh); sheet = true; }
            if (mesh is null) continue;
            var names = p.FaceNames.Count > 0 ? p.FaceNames : sheet ? Scene3DBuilder.SheetFaceNames : [];
            if (Place(use, mesh, names, sheet, out _) is { } placed) list.Add(placed);
        }
        return list;
    }
}
