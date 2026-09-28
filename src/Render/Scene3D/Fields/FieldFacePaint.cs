// brief-em3d-82 — an EM field on ONE picked face or sheet: right-click a face ▸ Plot Field. The fourth place a field is
// drawn, beside the clip plane, the selected region and the conductors (brief 29), and the same path temperature's picked
// faces take (brief 75) wherever the mesh allows it:
//
//   * a dielectric, air or body face, volume quantity — the face's own region's boundary (RegionBoundary), cut down to the
//     face (FieldFaces.OnFace): the region's side, exactly as Plot Temperature paints a face;
//   * a CONDUCTOR face. A Palace conductor is a VOID (GmshGeoWriter recipe step 1): it has no region of its own. A boundary
//     quantity (J_s, Q_s) is the boundary step filtered to the conductor's attribute; a volume quantity (E, B) is the
//     neighbouring regions' value at the face — the boundary of the UNION of every region, cut down to the face. The union,
//     not each region's own boundary: a coplanar interface beside the conductor (the substrate's top where the trace is not)
//     is inside the union and never reaches OnFace's tolerance. There is one side only, so no side is asked;
//   * a SHEET: a volume quantity from one side (FieldSurfaces.OnSheet — the side is the user's choice, because E's normal
//     component jumps across it); a boundary quantity (J_s on a PEC sheet) is single-valued and has no side;
//   * openEMS: the dump carries no regions at all, so every face is SAMPLED (FieldFaces.Sampled), off the face on the side
//     shown — outward from a conductor, INTO a dielectric, air or body (the region's own side, as Palace's picture is), the
//     chosen side of a sheet.
//
// A quantity that cannot be shown on a face is REFUSED with a sentence, never swapped for another one (owner, Q2: keep the
// quantity the user chose).

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>What a picked face belongs to, as the scene knows it.</summary>
public enum FieldFaceRole
{
    /// <summary>A dielectric, the air, or a body: a region of the mesh.</summary>
    Solid,
    /// <summary>A conductor, a via or a wire: a void in Palace's mesh.</summary>
    Conductor,
    /// <summary>A zero-thickness sheet, imprinted inside a volume.</summary>
    Sheet,
}

/// <summary>
/// A face asked for: its object's name and role, its triangles and outward (a sheet's own) normal in scene-local metres, the
/// tolerance a mesh triangle is matched to it with, and — for a sheet — the side shown (+1 along the normal, −1 against it).
/// </summary>
public sealed record FieldFaceTarget(string Object, string Label, FieldFaceRole Role, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> Triangles,
                                     Vector3? Normal, double Tol, int Side = 0);

/// <summary>
/// A painted face: its triangles and values; the unit direction it is drawn nudged along (in front of its own face); and the
/// direction its values were read off the face in (zero when they are the face's own), which the cursor readout repeats.
/// </summary>
public sealed record FieldFacePaint(FieldSurface Surface, Vector3 Toward, Vector3 ReadToward, bool OnBoundary, bool Sampled);

/// <summary>Paints picked faces with one quantity of one solution. One per geometry build: region boundaries it cuts are kept
/// for the next face of the same region.</summary>
public sealed class FieldFacePainter
{
    private readonly FieldQuantity _q;
    private readonly FieldStep? _volume, _boundary;
    private readonly IReadOnlyList<FieldGroup> _groups;
    private readonly (double X, double Y, double Z) _origin;
    private readonly FieldSampler? _sampler;
    private readonly CancellationToken _ct;
    private readonly Dictionary<string, FieldSurface> _regions = new(StringComparer.Ordinal);

    /// <param name="sampler">openEMS: the volume's sampler — faces are sampled, since the dump carries no regions. Null for a
    /// Palace run.</param>
    public FieldFacePainter(FieldQuantity q, FieldStep? volume, FieldStep? boundary, IReadOnlyList<FieldGroup> groups,
                            (double X, double Y, double Z) origin, FieldSampler? sampler = null, CancellationToken ct = default)
    {
        _q = q; _volume = volume; _boundary = boundary; _groups = groups; _origin = origin; _sampler = sampler; _ct = ct;
    }

    /// <summary>The face painted, or null with the reason it cannot be.</summary>
    public FieldFacePaint? Paint(FieldFaceTarget t, out string? why)
    {
        why = null;
        string name = FieldNames.Friendly(_q.Array.Name);
        var n = t.Normal ?? Vector3.Zero;
        int side = t.Side is 1 or -1 ? t.Side : 1;
        FieldFacePaint? Done(FieldSurface s, Vector3 toward, Vector3 read, bool onBoundary, bool sampled, out string? w)
        {
            w = s.TriangleCount == 0 ? $"{t.Label}: no part of the solved mesh lies on this face." : null;
            return w is null ? new FieldFacePaint(s, toward, read, onBoundary, sampled) : null;
        }

        if (_sampler is not null)
        {
            // openEMS — no regions, no boundary step: every face is sampled off the face, along its normal.
            if (t.Normal is null) { why = $"{t.Label} is curved: openEMS's field is read off a face along its normal, and this face has none."; return null; }
            if (_volume?.Load(_q.Array.Name) is not { } va) { why = $"{t.Label}: the run holds no {name}."; return null; }
            var read = t.Role switch { FieldFaceRole.Conductor => n, FieldFaceRole.Sheet => side * n, _ => -n };
            var surface = FieldFaces.Sampled(_sampler, va, t.Triangles, Vector3D.From(read), _origin, _ct);
            return Done(surface, t.Role == FieldFaceRole.Sheet ? side * n : n, read, false, true, out why);
        }

        var region = Attributes(t.Object, 3, null);
        if (region.Count > 0)
        {
            // A region of the mesh (a dielectric, the air, a body — or a conductor meshed as a volume): its own side.
            if (_q.OnBoundary) { why = $"{t.Label}: {name} is drawn on conductors and sheets; choose a volume quantity to show it on this face."; return null; }
            if (_volume?.Load(_q.Array.Name) is not { } a) { why = $"{t.Label}: the solution holds no {name} in its volume."; return null; }
            var whole = Region(t.Object, region, a);
            return Done(FieldFaces.OnFace(whole, t.Triangles, t.Tol), n, -n, false, false, out why);
        }

        if (t.Role == FieldFaceRole.Sheet)
        {
            if (_q.OnBoundary)
                return Boundary(t, "Sheet", n, out why);
            if (t.Normal is null) { why = $"{t.Label} has no single normal, so it has no top or bottom side."; return null; }
            if (_volume?.Load(_q.Array.Name) is not { } a) { why = $"{t.Label}: the solution holds no {name} in its volume."; return null; }
            var s = FieldSurfaces.OnSheet(_volume.Mesh, a, t.Triangles, side, t.Tol, _origin, _ct);
            return Done(s, side * n, side * n, false, false, out why);
        }

        if (Attributes(t.Object, 2, "Conductor").Count == 0)
        {
            why = $"{t.Label}: '{t.Object}' is not in the solved mesh (it has no group in the run's groups.json).";
            return null;
        }
        if (_q.OnBoundary) return Boundary(t, "Conductor", n, out why);
        // A void: the value is its neighbours', read from the boundary of every region together.
        if (_volume?.Load(_q.Array.Name) is not { } v) { why = $"{t.Label}: the solution holds no {name} in its volume."; return null; }
        var all = _groups.Where(g => g.Dimension == 3).Select(g => g.Attribute).ToHashSet();
        if (all.Count == 0) { why = $"{t.Label}: the run's groups.json names no region to read the field beside the conductor from."; return null; }
        var union = Region("\0every region", all, v);
        return Done(FieldFaces.OnFace(union, t.Triangles, t.Tol), n, n, false, false, out why);
    }

    /// <summary>A boundary quantity on a conductor's or a sheet's face: the boundary step's triangles of its attribute.</summary>
    private FieldFacePaint? Boundary(FieldFaceTarget t, string kind, Vector3 n, out string? why)
    {
        why = null;
        var attrs = Attributes(t.Object, 2, kind);
        if (_boundary?.Load(_q.Array.Name) is not { } b || attrs.Count == 0)
        {
            why = $"{t.Label}: the solution holds no {FieldNames.Friendly(_q.Array.Name)} on this {kind.ToLowerInvariant()}.";
            return null;
        }
        var s = FieldFaces.OnFace(FieldSurfaces.Boundary(_boundary.Mesh, b, attrs, _origin), t.Triangles, t.Tol);
        if (s.TriangleCount == 0) { why = $"{t.Label}: no part of the solved mesh lies on this face."; return null; }
        return new FieldFacePaint(s, t.Side is 1 or -1 ? t.Side * n : n, Vector3.Zero, true, false);
    }

    private HashSet<int> Attributes(string name, int dimension, string? kind)
        => _groups.Where(g => g.Name == name && g.Dimension == dimension && (kind is null || g.Kind == kind)).Select(g => g.Attribute).ToHashSet();

    private FieldSurface Region(string key, IReadOnlySet<int> attributes, FieldArray a)
    {
        if (!_regions.TryGetValue(key, out var s))
            _regions[key] = s = FieldSurfaces.RegionBoundary(_volume!.Mesh, a, attributes, _origin, _ct);
        return s;
    }
}
