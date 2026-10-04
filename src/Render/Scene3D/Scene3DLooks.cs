// brief-em3d-108 R-em3d108-1c / R-em3d108-2c — an appearance edit is a UNIFORM edit (overview §3b). A scene keeps, for every object
// that owns geometry, the question the builder asked the resolver (Scene3DModel.AppearanceRequests), so a look can be worked out
// again with NO builder: no elaboration, no tessellation, no normals, no feature tables. Restyle re-asks each question (through the
// caller's map — a dialog's unsaved material, a slider's override), interns the answers into a table by the builder's own rule
// (Intern, which the builder calls too), and returns a COPY of the scene that differs in three things only: the table, the slots on
// the objects whose row moved, and those objects' shade vertices. The session then uploads the table (its rows changed) and patches
// the shade stream for exactly those objects (Scene3DPatch compares object by object) — geometry bytes, 0.

using CircuitRF.Design.ThreeD.Appearance;

namespace CircuitRF.Render.Scene3D;

/// <summary>One object's look as the builder worked it out: the question it asked, the answer, and its role's default (what it
/// takes when the table is full).</summary>
public readonly record struct Scene3DLook(AppearanceRequest Request, AppearanceValues Own, AppearanceValues Default)
{
    /// <summary>The look <paramref name="request"/> resolves to, now.</summary>
    public static Scene3DLook Of(AppearanceRequest request)
        => new(request, AppearanceResolver.Resolve(request).Values,
               AppearanceResolver.RoleDefaultOf(request.Role, request.MaterialRole, request.Palette));
}

public static class Scene3DLooks
{
    private static long _restyles;

    /// <summary>How many scenes have been restyled (a look worked out again without the builder) in this process.</summary>
    public static long Restyles => Interlocked.Read(ref _restyles);

    /// <summary>
    /// brief-em3d-105 R-em3d105-4 — the appearance table and each look's row in it. Equal appearances share a row. Past
    /// <see cref="Scene3DBuilder.AppearanceSlots"/> distinct ones, the role defaults go in first and every object's own after them
    /// while there is room; a look whose own did not fit takes its role's default's row, and is counted. A null look has no row (−1).
    /// </summary>
    public static (AppearanceValues[] Table, int[] Slots, int Fallbacks) Intern(IReadOnlyList<Scene3DLook?> looks)
    {
        var table = new List<AppearanceValues>();
        var row = new Dictionary<AppearanceValues, int>();
        bool Add(AppearanceValues v)
        {
            if (row.ContainsKey(v)) return true;
            if (table.Count >= Scene3DBuilder.AppearanceSlots) return false;
            row[v] = table.Count;
            table.Add(v);
            return true;
        }
        var owned = looks.Where(l => l is not null).Select(l => l!.Value).ToList();
        if (owned.Select(l => l.Own).Distinct().Count() > Scene3DBuilder.AppearanceSlots)
            foreach (var l in owned) Add(l.Default);
        var slots = new int[looks.Count];
        int fallbacks = 0;
        for (int k = 0; k < looks.Count; k++)
        {
            if (looks[k] is not { } l) { slots[k] = -1; continue; }
            if (Add(l.Own)) slots[k] = row[l.Own];
            else { fallbacks++; slots[k] = row.TryGetValue(l.Default, out int d) ? d : 0; }
        }
        return ([.. table], slots, fallbacks);
    }

    /// <summary>
    /// <paramref name="scene"/> with every object's look asked again through <paramref name="map"/> (given the object's name and the
    /// builder's question, the question to ask now; the identity reproduces the scene's own table exactly). The geometry, the colours,
    /// the batches and the generation are the scene's own; only the table, the slots that moved and their objects' shade vertices
    /// differ. A scene with no requests (an empty one, or a viewer's that predates them) is returned as it is.
    /// </summary>
    public static Scene3DModel Restyle(Scene3DModel scene, Func<string, AppearanceRequest, AppearanceRequest> map)
    {
        var requests = scene.AppearanceRequests;
        int owned = scene.OwnedObjects;
        if (requests.Length != owned || owned == 0) return scene;
        Interlocked.Increment(ref _restyles);
        var looks = new Scene3DLook?[owned];
        var asked = new AppearanceRequest?[owned];
        for (int k = 0; k < owned; k++)
            if (requests[k] is { } r)
            {
                var now = map(scene.Objects[k].Name, r);
                asked[k] = now;
                looks[k] = Scene3DLook.Of(now);
            }
        var (table, slots, fallbacks) = Intern(looks);

        var objects = (Scene3DObject[])scene.Objects.Clone();
        Scene3DShadeVertex[]? shade = null;
        for (int k = 0; k < owned; k++)
        {
            var o = objects[k];
            if (slots[k] == o.AppearanceSlot) continue;
            objects[k] = o = o.Copy();
            o.AppearanceSlot = slots[k];
            if (scene.ShadeVertices.Length != scene.Vertices.Length || slots[k] < 0) continue;
            shade ??= (Scene3DShadeVertex[])scene.ShadeVertices.Clone();
            for (int v = o.FirstVertex; v < o.FirstVertex + o.VertexCount; v++)
                shade[v].Slot = (uint)slots[k] | (shade[v].Slot & ~Scene3DShadeVertex.SlotMask);
        }
        // An element draws its prototype's shade vertices, so it takes its prototype's row.
        for (int k = owned; k < objects.Length; k++)
            if (scene.Object(objects[k].Prototype) is { } proto && objects[(int)proto.Id - 1].AppearanceSlot is var slot
                && slot != objects[k].AppearanceSlot)
            {
                objects[k] = objects[k].Copy();
                objects[k].AppearanceSlot = slot;
            }
        return scene.WithLooks(objects, shade ?? scene.ShadeVertices, table, fallbacks, asked);
    }
}
