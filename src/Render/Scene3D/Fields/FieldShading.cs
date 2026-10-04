// brief-em3d-109 R-em3d109-2b — the normals a Lit field is shaded with: a stream parallel to the field buffer's vertices (three floats a
// vertex), made by brief 104's ShadingNormals — the only place a shading normal is made — on the field's OWN triangles.
//
// A field buffer is an unindexed triangle list (FieldSurface: three vertices a triangle), so each plot's range is first WELDED by exact
// position (a mesh node shared by several boundary faces lands on bit-identical floats; a slice point cut from one tetrahedron edge
// usually does too), then handed to ShadingNormals as one indexed mesh: smooth across a curved surface, sharp across a crease of more
// than its 30°, exactly as the scene's own shade stream is. Ranges are never welded to each other (two plots are two surfaces).
//
//   * A clip-plane slice is planar, so every triangle's normal is the plane's and so is the result (gate: within float rounding).
//   * A Surfaces plot is its own triangles' rule, as the brief says.
//   * A Faces plot too. The brief asked for the SHADE STREAM's normal there, but a painted face's triangles are the solver mesh's
//     (FieldFacePainter cuts them out of a region's boundary), not the scene's, so no shade-stream vertex corresponds to one; the same
//     function on the same face's mesh triangles gives the same smooth normal without a lookup.
//
// The sign is not meaningful (a surface's winding is the mesh's): fs_field_lit turns every normal toward the viewer.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Fields;

public static class FieldShading
{
    /// <summary>The floats a vertex's normal takes in the stream (x, y, z).</summary>
    public const int Floats = 3;
    public const int Stride = Floats * 4;

    /// <summary>How many field buffers have had normals made in this process — what proves the default view and Exact make none.</summary>
    public static long Built => Interlocked.Read(ref _built);
    private static long _built;

    /// <summary>
    /// The normal of every vertex of <paramref name="vertices"/>, range by range (<paramref name="layers"/>; a vertex in no range gets +z),
    /// as <see cref="Floats"/> floats each.
    /// </summary>
    public static float[] Normals(FieldVertex[] vertices, IReadOnlyList<FieldLayerRange> layers)
    {
        Interlocked.Increment(ref _built);
        var normals = new float[vertices.Length * Floats];
        for (int v = 0; v < vertices.Length; v++) normals[Floats * v + 2] = 1;
        foreach (var r in layers)
        {
            if (r.First < 0 || r.Count < 3 || r.First + r.Count > vertices.Length) continue;
            int tris = r.Count / 3;
            var weld = new Dictionary<(float, float, float), uint>();
            var positions = new List<Vector3>();
            var indices = new uint[tris * 3];
            for (int k = 0; k < tris * 3; k++)
            {
                ref var fv = ref vertices[r.First + k];
                var key = (fv.X, fv.Y, fv.Z);
                if (!weld.TryGetValue(key, out uint at))
                {
                    at = (uint)positions.Count;
                    weld[key] = at;
                    positions.Add(new Vector3(fv.X, fv.Y, fv.Z));
                }
                indices[k] = at;
            }
            var shaded = ShadingNormals.Compute(positions, indices);
            for (int k = 0; k < tris * 3; k++)
            {
                var n = shaded.Normals[shaded.Indices[k]];
                int o = Floats * (r.First + k);
                normals[o] = n.X; normals[o + 1] = n.Y; normals[o + 2] = n.Z;
            }
        }
        return normals;
    }
}
