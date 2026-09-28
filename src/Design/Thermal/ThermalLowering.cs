// brief-em3d-74 R-em3d74-4 — a .c3d's thermal setup as a Gmsh thermal script, and the table that says what every tag of
// the mesh it makes is (R-em3d74-4c): which volume group is which solid and material, which surface group a heat source,
// which a named face for a boundary or a probe, which the exposed faces. The solver's input is built from these tags
// alone, so nothing downstream consults the document's geometry again.
//
// WHAT IS LEFT OUT, AND SAID: air (never meshed, overview §1b); a sheet object (a surface has no volume to conduct
// through); a bond wire (a 1D element from brief 77 — until then the problem is meshed without it, and a note says so).

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Thermal;

/// <summary>A volume group: the solid, its material (as elaborated), and its tag.</summary>
public sealed record ThermalRegionTag(string Solid, string Material, int Tag);

/// <summary>What a face group is for: a boundary's exterior surfaces, or a probe's whole face.</summary>
public sealed record ThermalFaceTag(string Face, bool ExteriorOnly, int Tag);

/// <summary>R-em3d74-4c — the lowering and its tag table.</summary>
public sealed record ThermalLowering(
    GmshThermalInput                      Input,
    GmshLowering                          Gmsh,
    IReadOnlyList<ThermalRegionTag>       Regions,
    IReadOnlyDictionary<string, int>      SheetSourceTags,
    IReadOnlyDictionary<string, int>      SolidSourceRegions,
    IReadOnlyList<ThermalFaceTag>         Faces,
    int?                                  ExposedTag,
    IReadOnlyList<string>                 Notes)
{
    /// <summary>The tag of face <paramref name="face"/> as a boundary (exterior only) or a probe reads it.</summary>
    public int? FaceTag(string face, bool exteriorOnly)
        => Faces.FirstOrDefault(f => f.Face == face && f.ExteriorOnly == exteriorOnly)?.Tag;

    /// <summary>The region index of solid <paramref name="solid"/>, or −1.</summary>
    public int RegionOf(string solid)
    {
        for (int i = 0; i < Regions.Count; i++) if (Regions[i].Solid == solid) return i;
        return -1;
    }
}

public static class ThermalLowerings
{
    /// <summary>brief-em3d-74's defaults for the setup's Mesh section (brief 73 left them to this brief).</summary>
    public const int DefaultOrder = 2;
    public const double DefaultSizeFromSources = 4;
    public const int DefaultMinThroughThickness = 2;
    /// <summary>brief 72 Q7: a Threshold steeper than ~0.3 of growth per unit distance left slivers; 1.3 is that slope.</summary>
    public const double DefaultGrading = 1.3;
    /// <summary>The largest element, as a fraction of the problem's largest side.</summary>
    public const double MaxFraction = 0.1;

    /// <summary>The sizing a setup's Mesh section asks for, with this brief's defaults filling what it omits.</summary>
    public static GmshThermalSizing Sizing(CemThermalMesh? m, double scale = 1)
        => new(MaxFraction, m?.SizeFromSources ?? DefaultSizeFromSources, m?.MinThroughThickness ?? DefaultMinThroughThickness,
               m?.Grading ?? DefaultGrading, m?.Order ?? DefaultOrder, scale);

    /// <summary>
    /// brief-em3d-75 — the pieces of the face spelled <paramref name="spelled"/> (<c>object/face</c>) on its solid in
    /// <paramref name="solids"/> (index <paramref name="solid"/>), world metres — where a thermal boundary or a face probe
    /// lies. The ONE placement both the mesher's face groups and the editor's tint use, so what is tinted is what is meshed.
    /// Null with the reason.
    /// </summary>
    public static List<Em3dFacePolygon>? FacePieces(C3dDocument doc, C3dElaboration e, IReadOnlyList<Em3dSolid> solids, string spelled,
                                                    out int solid, out string? refusal)
    {
        solid = -1;
        refusal = null;
        int slash = spelled.LastIndexOf('/');
        if (slash <= 0 || slash == spelled.Length - 1) { refusal = $"'{spelled}' is not a face: a face is spelled object/face."; return null; }
        var (target, targetFace) = C3dKernelUse.Resolve(doc, spelled[..slash], spelled[(slash + 1)..]);
        int si = -1;
        for (int i = 0; i < solids.Count; i++) if (solids[i].Name == target) { si = i; break; }
        if (si < 0 || !e.Provenance.TryGetValue(target, out var prov))
        {
            refusal = $"The face '{spelled}' is on '{target}', which is not a meshed solid of this thermal run.";
            return null;
        }
        var primNames = Em3dFaceGeometry.FaceNames(solids[si].Primitive);
        var mine = Enumerable.Range(0, Math.Min(prov.FaceNames.Count, primNames.Count))
                             .Where(i => C3dKernelUse.Covers(targetFace, prov.FaceNames[i])).Select(i => primNames[i]).Distinct().ToList();
        if (mine.Count == 0) { refusal = $"'{target}' has no face '{targetFace}'."; return null; }
        var pieces = new List<Em3dFacePolygon>();
        foreach (string f in mine)
        {
            if (Em3dFaceGeometry.Pieces(solids[si].Primitive, f, out string? why) is not { } p)
            {
                refusal = $"The face '{spelled}' cannot be placed in the mesh: {why}.";
                return null;
            }
            pieces.AddRange(p);
        }
        solid = si;
        return pieces;
    }

    /// <summary>
    /// The lowering of <paramref name="doc"/> (resolved in place by its elaboration <paramref name="e"/>) for thermal setup
    /// <paramref name="t"/>, or null with the reason. <paramref name="scale"/> multiplies every element size.
    /// </summary>
    public static ThermalLowering? Build(C3dDocument doc, C3dElaboration e, CemThermal t, double scale, out string? refusal)
    {
        refusal = null;
        var notes = new List<string>();
        double m = 1e-6 / doc.DbuPerMicron;

        // ── the solids: every one with a material, except air and (until brief 77) the bond wires ──
        var wireNames = new HashSet<string>(e.Wires.Select(w => w.Name).Concat(e.DrawnWires.Keys), StringComparer.Ordinal);
        var solids = new List<Em3dSolid>();
        int wires = 0, air = 0;
        foreach (var s in e.Solids)
        {
            if (ThermalMaterials.NotMeshed(s.Role, s.Material)) { air++; continue; }
            if (wireNames.Contains(s.Name)) { wires++; continue; }
            solids.Add(s);
        }
        if (wires > 0)
            notes.Add($"{wires} bond wire(s) are left out of this thermal run: a wire is a 1D conduction element, which a later " +
                      "version adds. The rest of the problem is meshed and solved without them.");
        if (air > 0) notes.Add($"{air} air solid(s) are not meshed: a thermal run conducts through solids only.");
        if (e.Sheets.Count > 0)
            notes.Add($"{e.Sheets.Count} sheet object(s) have no volume, so they carry no heat in a thermal run.");
        if (solids.Count == 0) { refusal = "This 3D view holds no solid a thermal run meshes (air and bond wires are not meshed)."; return null; }
        int interfaces = doc.ContactResistances.Count + (e.Technology?.ResolvedThermalInterfaces.Count ?? 0);
        if (interfaces > 0)
            notes.Add("Interface resistances (the technology's material pairs and the document's contact overrides) are not applied " +
                      "in this version: every contact between solids conducts perfectly. A later version joins the two sides " +
                      "through the stated resistance.");

        // ── heat sources: sheets embedded, solids by their region ──
        var sheets = new List<GmshThermalSheet>();
        var solidSources = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in doc.HeatSources)
        {
            if (h.Solid is { } sname)
            {
                int ri = solids.FindIndex(s => s.Name == sname);
                if (ri < 0) { refusal = $"Heat source '{h.Name}' is spread through '{sname}', which is not a meshed solid of this thermal run."; return null; }
                solidSources[h.Name] = ri;
                continue;
            }
            if (h.Sheet is not { } hs) continue;
            var frame = hs.Plane switch
            {
                C3dPlane.YZ => new Em3dPlaneFrame(new Point3(hs.Offset * m, 0, 0), new Point3(0, 1, 0), new Point3(0, 0, 1)),
                C3dPlane.XZ => new Em3dPlaneFrame(new Point3(0, hs.Offset * m, 0), new Point3(1, 0, 0), new Point3(0, 0, 1)),
                _           => new Em3dPlaneFrame(new Point3(0, 0, hs.Offset * m), new Point3(1, 0, 0), new Point3(0, 1, 0)),
            };
            List<Point2> outline = hs.Rect is { } r
                ? [new(r.Min.U * m, r.Min.V * m), new((r.Min.U + r.Size.U) * m, r.Min.V * m),
                   new((r.Min.U + r.Size.U) * m, (r.Min.V + r.Size.V) * m), new(r.Min.U * m, (r.Min.V + r.Size.V) * m)]
                : [.. hs.Outline.Select(p => new Point2(p.U * m, p.V * m))];
            var holes = hs.Holes.Select(hole => (IReadOnlyList<Point2>)[.. hole.Select(p => new Point2(p.U * m, p.V * m))]).ToList();
            sheets.Add(new GmshThermalSheet(h.Name, new Em3dSheet(h.Name, "", outline, holes, 0, 0, 0) { Frame = frame }));
        }

        // ── named faces: each boundary's (exterior only) and each probe's (whole) ──
        var faces = new List<GmshThermalFace>();
        bool exposed = false;
        string? Face(string spelled, bool exteriorOnly)
        {
            if (faces.Any(f => f.Name == spelled && f.ExteriorOnly == exteriorOnly)) return null;
            if (FacePieces(doc, e, solids, spelled, out int si, out string? why) is not { } pieces) return why;
            faces.Add(new GmshThermalFace(spelled, si, pieces, exteriorOnly));
            return null;
        }
        foreach (var b in t.Boundaries ?? [])
        {
            if (b.Face == C3dThermal.ExposedFaces) { exposed = true; continue; }
            if (Face(b.Face, true) is { } why) { refusal = why; return null; }
        }
        foreach (var p in doc.Probes)
        {
            string? spelled = p.Face ?? p.Spot?.Face;
            if (spelled is not null && Face(spelled, false) is { } why) { refusal = why; return null; }
        }

        var input = new GmshThermalInput(solids, sheets, faces, exposed, C3dProblemAssembly.MeshRegions(doc), Sizing(t.Mesh, scale));
        var gmsh = GmshGeoWriter.WriteThermal(input);
        if (!gmsh.Ok) { refusal = gmsh.Refusal; return null; }

        // ── the tag table, from the groups the writer made, in the order it made them ──
        int k = 0;
        var regions = solids.Select(s => new ThermalRegionTag(s.Name, s.Material, gmsh.Groups[k++].Attribute)).ToList();
        var sheetTags = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in sheets) sheetTags[s.Name] = gmsh.Groups[k++].Attribute;
        var faceTags = faces.Select(f => new ThermalFaceTag(f.Name, f.ExteriorOnly, gmsh.Groups[k++].Attribute)).ToList();
        int? exposedTag = exposed ? gmsh.Groups[k].Attribute : null;
        return new ThermalLowering(input, gmsh, regions, sheetTags, solidSources, faceTags, exposedTag, notes);
    }
}
