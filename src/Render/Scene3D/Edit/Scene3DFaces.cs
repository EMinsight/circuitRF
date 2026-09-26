// brief-em3d-43 R-em3d43-2c / -3b / -6b — one face's geometry, read off the scene's own triangles: its
// vertices, its area and its normal, and the vertex nearest the cursor on screen. The cost is ONE
// object's triangles (its batch), never the scene's — which is what keeps Vertex mode's hover cheap.
//
// Everything is in scene-local metres, which is world metres less the scene's origin: a length and an
// area are the same in both, and a point is converted with Scene3DModel.ToWorld.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Edit;

public static class Scene3DFaces
{
    /// <summary>The index range of object <paramref name="id"/>'s triangles, or an empty range.</summary>
    public static (int First, int Count) TrianglesOf(Scene3DModel scene, uint id)
    {
        var (first, count, _) = TrianglesAt(scene, id);
        return (first, count);
    }

    /// <summary>brief-em3d-48 — the index range and the offset its vertices are drawn at (an array element's).</summary>
    public static (int First, int Count, Vector3 Offset) TrianglesAt(Scene3DModel scene, uint id)
    {
        var o = scene.Object(id);
        if (o is { Element: >= 0 } && o.Element < scene.Elements.Length)
        {
            var el = scene.Elements[o.Element];
            for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++)
                if (scene.Batches[k].ObjectId == id) return (scene.Batches[k].FirstIndex, scene.Batches[k].IndexCount, scene.Batches[k].Offset);
            return (0, 0, default);
        }
        foreach (var b in scene.Batches)
            if (b.ObjectId == id) return (b.FirstIndex, b.IndexCount, b.Offset);
        return (0, 0, default);
    }

    /// <summary>The distinct corners of face <paramref name="face"/> of object <paramref name="id"/>.</summary>
    public static List<Vector3> Vertices(Scene3DModel scene, uint id, int face)
    {
        var (first, count, offset) = TrianglesAt(scene, id);
        var seen = new HashSet<Vector3>();
        var list = new List<Vector3>();
        for (int i = first; i < first + count; i++)
        {
            var v = scene.Vertices[scene.Indices[i]];
            if ((int)v.Face != face && !(face < 0)) continue;
            var p = new Vector3(v.X, v.Y, v.Z) + offset;
            if (seen.Add(p)) list.Add(p);
        }
        return list;
    }

    /// <summary>Face <paramref name="face"/>'s area (m²) and its outward unit normal: the sum of its triangles'
    /// areas, and their area-weighted normal. A curved face (a cylinder's side) has no single normal: its
    /// weighted normals cancel, and the length says so — null then.</summary>
    public static (double Area, Vector3? Normal) AreaAndNormal(Scene3DModel scene, uint id, int face)
    {
        var (first, count) = TrianglesOf(scene, id);
        double area = 0;
        var sum = Vector3.Zero;
        for (int i = first; i + 2 < first + count; i += 3)
        {
            var a = scene.Vertices[scene.Indices[i]];
            if ((int)a.Face != face) continue;
            var b = scene.Vertices[scene.Indices[i + 1]];
            var c = scene.Vertices[scene.Indices[i + 2]];
            var n = Vector3.Cross(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z), new Vector3(c.X - a.X, c.Y - a.Y, c.Z - a.Z));
            area += 0.5 * n.Length();
            sum += n;
        }
        double len = sum.Length();
        return (area, area > 0 && len > 1e-6 * 2 * area ? Vector3.Normalize(sum) : null);
    }

    /// <summary>
    /// R-em3d43-3b — Vertex mode picks THROUGH the face: of face <paramref name="face"/>'s corners, the one
    /// nearest the cursor on screen, when it is within <paramref name="radiusPx"/>; otherwise none.
    /// brief-em3d-47 R-em3d47-5 — on a cylinder the candidates are its two cap centres, whatever the face: its
    /// tessellation's vertices are not design.
    /// </summary>
    public static Vector3? NearestVertexOnScreen(Scene3DModel scene, uint id, int face, in Camera3D camera,
                                                 float px, float py, float width, float height, float radiusPx)
    {
        Vector3? best = null;
        float bestD2 = radiusPx * radiusPx;
        IEnumerable<Vector3> candidates = scene.Object(id)?.CapCentres is { } caps
            ? caps.Select(c => scene.ToLocal(c.X, c.Y, c.Z))
            : Vertices(scene, id, face);
        foreach (var p in candidates)
        {
            var (x, y, visible) = camera.Project(p, width, height);
            if (!visible) continue;
            float d2 = (x - px) * (x - px) + (y - py) * (y - py);
            if (d2 <= bestD2) { bestD2 = d2; best = p; }
        }
        return best;
    }
}
