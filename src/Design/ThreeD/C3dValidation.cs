// brief-em3d-41 R-em3d41-2f — what is wrong with a 3D view that reads.
//
// ALL of it, not the first (R-em3d3-1c's rule): a list that stopped at the first defect would make a
// user fix a document one refusal at a time. One finding per defect — a duplicated name is one
// finding however many objects share it, an open polyhedron is one finding naming every bad edge —
// so "five defects report five" is a statement about the document and not about the validator's loop.
//
// This is the ONE validator: `circuitrf check` reports exactly these, and the editor (brief 43) shows
// exactly these on open. A rule living only in `check` would pass a document headlessly that the
// application then refuses (cli.md's `check` rule).

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD;

public static class C3dValidation
{
    /// <summary>The name no object may have: the air box's faces are <c>airbox/xmin</c> ….</summary>
    public const string ReservedName = "airbox";

    /// <summary>How far a vertex may sit off its face's plane, in DBU.</summary>
    public const double PlanarityToleranceDbu = 1.0;

    /// <summary>
    /// Every problem <paramref name="doc"/> has.
    /// </summary>
    /// <param name="isKnownMaterial">
    /// Whether the document's technology defines a material name, or null when no technology resolved —
    /// in which case materials are not checked here at all, and the caller says why (a warning per
    /// object for a missing technology would bury the one finding that matters).
    /// </param>
    public static IReadOnlyList<Diagnostic> Validate(C3dDocument doc, Func<string, bool>? isKnownMaterial = null)
    {
        var found = new List<Diagnostic>();

        Names(doc, found);

        foreach (var o in doc.Objects)
        {
            if (o is not C3dPolyline)
            {
                // brief-em3d-50: a wire's omitted material is wBond's default metal, which the technology must still define.
                string? material = o is C3dWire w ? C3dWires.MaterialOf(w) : o.Material;
                if (string.IsNullOrWhiteSpace(material)) found.Add(C3dDiagnostics.NoMaterial(o.Name));
                else if (isKnownMaterial is not null && !isKnownMaterial(material))
                    found.Add(C3dDiagnostics.UnknownMaterial(o.Name, material));
            }

            switch (o)
            {
                case C3dBox b:        Box(b, found);        break;
                case C3dPrism p:      Prism(p, found);      break;
                case C3dCylinder c:   Cylinder(c, found);   break;
                case C3dSheet s:      Sheet(s, found);      break;
                case C3dPolyline l:   Polyline(l, found);   break;
                case C3dPolyhedron h: Polyhedron(h, found); break;
                case C3dWire w:       Wire(w, found);       break;
            }

            Unread(o.Unread, $"'{o.Name}'", found);
        }

        foreach (var i in doc.Instances)
        {
            if (string.IsNullOrWhiteSpace(i.CellRef)) found.Add(C3dDiagnostics.InstanceNoCell(i.Name));
            if (i.Array is { } a && (a.Counts.Count != 3 || a.Counts.Any(n => n < 1)))
                found.Add(C3dDiagnostics.ArrayCounts(i.Name));
            Unread(i.Unread, $"The instance '{i.Name}'", found);
        }

        Unread(doc.Unread, "The document", found);
        return found;
    }

    // ── names ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Objects and instances share one namespace: both are addressed by name, and a port on
    /// <c>U1/…</c> must not be able to mean two things. Compared case-insensitively, for the same
    /// reason cell names are.</summary>
    private static void Names(C3dDocument doc, List<Diagnostic> found)
    {
        var named = doc.Objects.Select(o => (Kind: "object", o.Name))
                   .Concat(doc.Instances.Select(i => (Kind: "instance", i.Name)))
                   .ToList();

        foreach (var (kind, name) in named)
        {
            if (NameValidator.Validate(name) is { } why) found.Add(C3dDiagnostics.InvalidName(kind, name, why));
            else if (string.Equals(name, ReservedName, StringComparison.OrdinalIgnoreCase))
                found.Add(C3dDiagnostics.ReservedName(name));
        }

        foreach (var g in named.Where(n => n.Name.Length > 0)
                               .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1))
            found.Add(C3dDiagnostics.DuplicateName(g.Key, g.Count()));
    }

    private static void Unread(Dictionary<string, System.Text.Json.JsonElement>? keys, string owner, List<Diagnostic> found)
    {
        if (keys is null) return;
        foreach (var k in keys.Keys) found.Add(C3dDiagnostics.UnreadKey(owner, k));
    }

    // ── primitives ───────────────────────────────────────────────────────────────────────────

    private static void Box(C3dBox b, List<Diagnostic> found)
    {
        if (b.Size.X == 0 || b.Size.Y == 0 || b.Size.Z == 0)
            found.Add(C3dDiagnostics.ZeroVolume(b.Name, "box", "a size component is zero"));
    }

    private static void Prism(C3dPrism p, List<Diagnostic> found)
    {
        bool outlineOk = Loop(p.Name, "outline", p.Outline, found);
        for (int h = 0; h < p.Holes.Count; h++) Loop(p.Name, $"hole {h}", p.Holes[h], found);

        if (p.Height == 0)
            found.Add(C3dDiagnostics.ZeroVolume(p.Name, "prism", "its height is zero"));
        else if (outlineOk && TwiceArea(p.Outline) == 0)
            found.Add(C3dDiagnostics.ZeroVolume(p.Name, "prism", "its outline encloses no area"));
    }

    private static void Cylinder(C3dCylinder c, List<Diagnostic> found)
    {
        if (c.Radius <= 0)
            found.Add(C3dDiagnostics.ZeroVolume(c.Name, "cylinder", "its radius is not positive"));
        else if (c.Length == 0)
            found.Add(C3dDiagnostics.ZeroVolume(c.Name, "cylinder", "its length is zero"));
    }

    private static void Sheet(C3dSheet s, List<Diagnostic> found)
    {
        bool hasOutline = s.Outline.Count > 0;
        if (s.Rect is null && !hasOutline) { found.Add(C3dDiagnostics.SheetShape(s.Name, "has neither a Rect nor an Outline")); return; }
        if (s.Rect is not null && hasOutline) { found.Add(C3dDiagnostics.SheetShape(s.Name, "has both a Rect and an Outline; it must have one")); return; }

        if (s.Rect is { } r)
        {
            if (r.Size.U == 0 || r.Size.V == 0) found.Add(C3dDiagnostics.SheetShape(s.Name, "is a rectangle with no area"));
            return;
        }

        Loop(s.Name, "outline", s.Outline, found);
        for (int h = 0; h < s.Holes.Count; h++) Loop(s.Name, $"hole {h}", s.Holes[h], found);
    }

    private static void Polyline(C3dPolyline l, List<Diagnostic> found)
    {
        if (l.VertexCount < 2) found.Add(C3dDiagnostics.PolylineTooShort(l.Name, l.VertexCount));
    }

    private static void Wire(C3dWire w, List<Diagnostic> found)
    {
        if (!w.Placement.IsDefault) found.Add(C3dDiagnostics.WirePlacement(w.Name));
        if (w.Points.Distinct().Count() < 2)
            found.Add(C3dDiagnostics.WireShape(w.Name, $"has {w.Points.Distinct().Count()} distinct point(s); it needs at least two"));
        if (w.DiameterUm is { } d && !(d > 0))
            found.Add(C3dDiagnostics.WireShape(w.Name, "has a diameter that is not positive"));
        foreach (var (end, which) in new[] { (w.Start, "start"), (w.End, "end") })
            if (end.FootLengthUm is { } f && !(f > 0))
                found.Add(C3dDiagnostics.WireShape(w.Name, $"has a foot length at its {which} that is not positive"));
        if (w.Role is { } role && role != CircuitRF.Engine.Em3d.Em3dRole.Conductor)
            found.Add(C3dDiagnostics.WireShape(w.Name, $"has the role {role}; a wire is a conductor"));
    }

    /// <summary>A loop needs three DISTINCT points; returns whether it has them.</summary>
    private static bool Loop(string name, string which, List<C3dPoint2> points, List<Diagnostic> found)
    {
        int distinct = points.Distinct().Count();
        if (distinct >= 3) return true;
        found.Add(C3dDiagnostics.TooFewPoints(name, which, distinct));
        return false;
    }

    /// <summary>Twice the signed area, exactly — Int128, because two DBU coordinates of a large board
    /// multiplied overflow a long and a double would make "exactly zero" a matter of rounding.</summary>
    private static Int128 TwiceArea(List<C3dPoint2> ring)
    {
        Int128 sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += (Int128)a.U * b.V - (Int128)b.U * a.V;
        }
        return sum;
    }

    // ── polyhedron ───────────────────────────────────────────────────────────────────────────

    private static void Polyhedron(C3dPolyhedron h, List<Diagnostic> found)
    {
        int n = h.Vertices.Count;
        bool indicesOk = true;

        var faceNames = new HashSet<string>(StringComparer.Ordinal);
        var reported  = new HashSet<string>(StringComparer.Ordinal);
        for (int f = 0; f < h.Faces.Count; f++)
        {
            var face = h.Faces[f];
            if (string.IsNullOrWhiteSpace(face.Name)) found.Add(C3dDiagnostics.FaceUnnamed(h.Name, f));
            else if (!faceNames.Add(face.Name) && reported.Add(face.Name))
                found.Add(C3dDiagnostics.FaceNameDuplicate(h.Name, face.Name));

            foreach (var loop in Loops(face))
            {
                if (loop.Count < 3) { found.Add(C3dDiagnostics.FaceTooSmall(h.Name, FaceLabel(face, f))); indicesOk = false; }
                foreach (int i in loop)
                    if (i < 0 || i >= n)
                    {
                        found.Add(C3dDiagnostics.FaceIndex(h.Name, FaceLabel(face, f), i, n));
                        indicesOk = false;
                    }
            }
        }

        // Closure and planarity read vertices through the indices; with a bad index they would only
        // report the same defect a second time, in a worse sentence.
        if (!indicesOk) return;

        Closure(h, found);
        for (int f = 0; f < h.Faces.Count; f++) Planarity(h, h.Faces[f], f, found);
    }

    private static IEnumerable<List<int>> Loops(C3dFace face)
    {
        yield return face.Outer;
        foreach (var hole in face.Holes) yield return hole;
    }

    private static string FaceLabel(C3dFace face, int index)
        => string.IsNullOrWhiteSpace(face.Name) ? index.ToString(CultureInfo.InvariantCulture) : face.Name;

    /// <summary>
    /// Every edge used by exactly two faces, in opposite directions: each directed edge a→b appears
    /// exactly once, and so does b→a. That is a closed, consistently oriented surface — the
    /// orientation half matters because a face wound the wrong way points its normal INTO the solid.
    /// </summary>
    private static void Closure(C3dPolyhedron h, List<Diagnostic> found)
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var face in h.Faces)
            foreach (var loop in Loops(face))
                for (int k = 0; k < loop.Count; k++)
                {
                    var e = (loop[k], loop[(k + 1) % loop.Count]);
                    directed[e] = directed.GetValueOrDefault(e) + 1;
                }

        var bad = new SortedSet<(int, int)>();
        foreach (var ((a, b), count) in directed)
        {
            if (count != 1 || directed.GetValueOrDefault((b, a)) != 1)
                bad.Add(a < b ? (a, b) : (b, a));
        }
        if (bad.Count == 0) return;

        const int Named = 8;
        var list = bad.Take(Named).Select(e => $"{e.Item1}–{e.Item2} ({Point(h.Vertices[e.Item1])} to {Point(h.Vertices[e.Item2])})");
        string edges = string.Join("; ", list) + (bad.Count > Named ? $"; and {bad.Count - Named} more" : "");
        found.Add(C3dDiagnostics.NotClosed(h.Name, edges));

        static string Point(C3dPoint3 p) => FormattableString.Invariant($"[{p.X}, {p.Y}, {p.Z}]");
    }

    /// <summary>
    /// Newell's normal, then the largest distance of any vertex (holes included) from the plane through
    /// the loop's centroid. Newell's rather than a cross product of two edges because it is the
    /// least-squares plane for a non-planar loop, so the deviation it reports is the honest one rather
    /// than one that depends on which vertices happened to come first.
    /// </summary>
    private static void Planarity(C3dPolyhedron h, C3dFace face, int index, List<Diagnostic> found)
    {
        var v = h.Vertices;
        double nx = 0, ny = 0, nz = 0, cx = 0, cy = 0, cz = 0;
        var outer = face.Outer;
        for (int k = 0; k < outer.Count; k++)
        {
            var a = v[outer[k]];
            var b = v[outer[(k + 1) % outer.Count]];
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
            cx += a.X; cy += a.Y; cz += a.Z;
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len == 0) { found.Add(C3dDiagnostics.FaceNoArea(h.Name, FaceLabel(face, index))); return; }

        nx /= len; ny /= len; nz /= len;
        cx /= outer.Count; cy /= outer.Count; cz /= outer.Count;

        double worst = 0;
        foreach (var loop in Loops(face))
            foreach (int i in loop)
                worst = Math.Max(worst, Math.Abs(nx * (v[i].X - cx) + ny * (v[i].Y - cy) + nz * (v[i].Z - cz)));

        if (worst > PlanarityToleranceDbu)
            found.Add(C3dDiagnostics.NotPlanar(h.Name, FaceLabel(face, index), Math.Round(worst, 1)));
    }
}
