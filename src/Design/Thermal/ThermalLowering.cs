// brief-em3d-74 R-em3d74-4 — a .c3d's thermal setup as a Gmsh thermal script, and the table that says what every tag of
// the mesh it makes is (R-em3d74-4c): which volume group is which solid and material, which surface group a heat source,
// which a named face for a boundary or a probe, which the exposed faces. The solver's input is built from these tags
// alone, so nothing downstream consults the document's geometry again.
//
// WHAT IS LEFT OUT, AND SAID: air (never meshed, overview §1b — but brief-em3d-76: an air solid still CUTS what it outranks,
// exactly as in EM, so a plated via's bore leaves its barrel a tube); a sheet object (a surface has no volume to conduct
// through). A bond wire is not meshed either: brief-em3d-77 solves it as a 1D chain (ThermalWireLowering), and what the mesh
// gets from it is a contact patch at each end and a refinement round its centreline.
//
// brief-em3d-76 adds: the resistive contacts (ThermalContacts), each ENABLED effective block in place of what it replaces
// (ThermalEffectiveBlocks), a submodel's clip to its region's box with the planes that cut material, and the symmetry
// planes *exposed* must not take. A mesh region a submodel is cut to does not refine the whole-model run.

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

    /// <summary>brief-em3d-76 R-em3d76-1a — the resistive contacts among the regions (override, else technology pair).</summary>
    public IReadOnlyList<ThermalContact> Contacts { get; init; } = [];

    /// <summary>brief-em3d-76 R-em3d76-2 — each enabled effective block, by its region index.</summary>
    public IReadOnlyDictionary<int, EffectiveBlockLowering> Effective { get; init; } = new Dictionary<int, EffectiveBlockLowering>();

    /// <summary>brief-em3d-76 R-em3d76-3a — a submodel's box (metres) and its cut faces' tag; null for a whole-model run.</summary>
    public (Point3 Min, Point3 Max)? Clip { get; init; }
    public int? CutTag { get; init; }

    /// <summary>brief-em3d-76 — the heat sources a submodel's box leaves out (wholly outside it): their heat reaches the
    /// submodel through its cut faces.</summary>
    public IReadOnlyList<string> SourcesOutside { get; init; } = [];

    /// <summary>brief-em3d-76 R-em3d76-4b — 2ⁿ for n declared symmetry planes.</summary>
    public int SymmetryFactor { get; init; } = 1;

    /// <summary>brief-em3d-76 — for each face tag, the region whose side its triangles take where they lie on a split contact:
    /// the face's own solid.</summary>
    public IReadOnlyDictionary<int, int> TagRegions
    {
        get
        {
            var d = Faces.Where(f => f.Tag > 0).GroupBy(f => f.Tag).ToDictionary(g => g.Key, g => Input.Faces.First(x => x.Name == g.First().Face).Solid);
            // brief-em3d-77 — a wire's contact patch takes its pad's side
            foreach (var w in Wires)
                foreach (var end in new[] { w.Start, w.End })
                    if (PatchTags.TryGetValue(end.Patch, out int tag) && RegionOf(end.Pad) is var r and >= 0) d[tag] = r;
            return d;
        }
    }

    /// <summary>brief-em3d-77 — the bond wires, each as its 1D chain, and each end's contact patch tag by patch name.</summary>
    public IReadOnlyList<ThermalWirePlan> Wires { get; init; } = [];
    public IReadOnlyDictionary<string, int> PatchTags { get; init; } = new Dictionary<string, int>();

    /// <summary>brief-em3d-77 R-em3d77-1 — each current's contact faces (by face-group name, whole faces).</summary>
    public IReadOnlyList<ThermalPortContact> PortContacts { get; init; } = [];

    /// <summary>brief-em3d-78 R-em3d78-2 — the ports with harmonic currents, each with its positive conductor.</summary>
    public IReadOnlyList<ThermalRfPort> RfPorts { get; init; } = [];

    /// <summary>brief-em3d-77 — per region, whether it is a conductor (it carries current where a port's current reaches it).</summary>
    public IReadOnlyList<bool> Conductors { get; init; } = [];

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
        double tol = m;

        // ── the solids: every one with a material, except air and the bond wires (and their balls: brief 77's 1D chains) ──
        var wireNames = new HashSet<string>(e.Wires.SelectMany(ThermalWireLowering.SolidsOf).Concat(e.DrawnWires.Keys), StringComparer.Ordinal);
        var solids = new List<Em3dSolid>();
        var voids = new List<Em3dSolid>();
        int wires = 0, air = 0;
        foreach (var s in e.Solids)
        {
            if (ThermalMaterials.NotMeshed(s.Role, s.Material)) { air++; voids.Add(s); continue; }
            if (wireNames.Contains(s.Name)) { wires++; continue; }
            solids.Add(s);
        }

        // ── brief-em3d-76 R-em3d76-2: each enabled effective block replaces what it stands for ──
        var blocks = new List<EffectiveBlockLowering>();
        var replacedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        int nextOrder = e.Solids.Count == 0 ? 1 : e.Solids.Max(s => s.Order) + 1;
        foreach (var b in doc.EffectiveBlocks.Where(b => b.Enabled))
        {
            if (e.Technology is null) { refusal = $"Effective block '{b.Name}' needs the technology's materials, which did not resolve."; return null; }
            var low = ThermalEffectiveBlocks.Lower(doc, e, b, e.Technology, nextOrder++, out string? whyNot);
            if (low is null) { refusal = whyNot; return null; }
            foreach (string r in low.Removed) replacedBy[r] = b.Name;
            blocks.Add(low);
        }
        if (blocks.Count > 0)
        {
            solids.RemoveAll(s => replacedBy.ContainsKey(s.Name));
            voids.RemoveAll(s => replacedBy.ContainsKey(s.Name));
            solids.AddRange(blocks.Select(bl => bl.Solid));
            notes.AddRange(blocks.Select(bl => bl.Note()));
        }
        int disabled = doc.EffectiveBlocks.Count(b => !b.Enabled);
        if (disabled > 0)
            notes.Add($"{disabled} effective block(s) are disabled: the geometry under them is solved as drawn.");

        // ── brief-em3d-76 R-em3d76-3a: a submodel keeps what its region's box holds ──
        (Point3 Min, Point3 Max)? clip = null;
        if (t.Submodel is { } sm)
        {
            var region = C3dProblemAssembly.MeshRegions(doc).FirstOrDefault(r => r.Name == sm.Region);
            if (region is null) { refusal = $"The submodel's Region '{sm.Region}' is no mesh region of this 3D view with a positive box and size."; return null; }
            clip = (region.Min, region.Max);
            int before = solids.Count;
            solids.RemoveAll(s => !Overlaps(Em3dProblem.Bounds(s.Primitive), region.Min, region.Max, tol));
            if (solids.Count == 0) { refusal = $"The submodel's region '{sm.Region}' holds no meshed solid."; return null; }
            notes.Add($"Submodel: the region '{sm.Region}' holds {solids.Count} of the model's {before} meshed solid(s), cut to its box; its cut " +
                      $"faces are fixed to setup '{sm.From}''s solution.");
        }
        if (air > 0) notes.Add($"{air} air solid(s) are not meshed: a thermal run conducts through solids only.");
        if (e.Sheets.Count > 0)
            notes.Add($"{e.Sheets.Count} sheet object(s) have no volume, so they carry no heat in a thermal run.");
        if (solids.Count == 0) { refusal = "This 3D view holds no solid a thermal run meshes (air and bond wires are not meshed)."; return null; }

        // ── brief-em3d-77 R-em3d77-3: the bond wires as 1D chains, their ends' contact patches ──
        var patches = new List<GmshThermalSheet>();
        List<ThermalWirePlan> plans = [];
        if (wires > 0 && clip is not null)
            notes.Add($"{wires} bond wire solid(s) are left out of this submodel: a submodel solves the region's solids only.");
        else if (wires > 0)
        {
            plans = ThermalWireLowering.Plan(e, solids.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), patches, notes, out string? wireWhy)!;
            if (wireWhy is not null) { refusal = wireWhy; return null; }
        }

        // ── heat sources: sheets embedded, solids by their region ──
        var sheets = new List<GmshThermalSheet>();
        var solidSources = new Dictionary<string, int>(StringComparer.Ordinal);
        var outside = new List<string>();
        foreach (var h in doc.HeatSources)
        {
            if (h.Solid is { } sname)
            {
                int ri = solids.FindIndex(s => s.Name == sname);
                if (ri < 0 && replacedBy.TryGetValue(sname, out string? blk))
                { refusal = $"Heat source '{h.Name}' is spread through '{sname}', which effective block '{blk}' replaces. Disable the block or move the source."; return null; }
                if (ri < 0 && clip is not null && e.Solids.Any(s => s.Name == sname)) { outside.Add(h.Name); continue; }
                if (ri < 0) { refusal = $"Heat source '{h.Name}' is spread through '{sname}', which is not a meshed solid of this thermal run."; return null; }
                if (clip is { } cb && !Within(Em3dProblem.Bounds(solids[ri].Primitive), cb.Min, cb.Max, tol))
                {
                    refusal = $"Heat source '{h.Name}' is spread through '{sname}', which the submodel's box cuts: the submodel would carry only part " +
                              "of its power. Enlarge the region to hold the whole solid.";
                    return null;
                }
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
            var sheet = new Em3dSheet(h.Name, "", outline, holes, 0, 0, 0) { Frame = frame };
            if (clip is { } cs)
            {
                var sb = sheet.WorldBounds();
                if (!Overlaps(sb, cs.Min, cs.Max, -tol)) { outside.Add(h.Name); continue; }
                if (!Within(sb, cs.Min, cs.Max, tol))
                {
                    refusal = $"Heat source '{h.Name}' straddles the submodel's box: the submodel would carry only part of its power, and the " +
                              "sources inside the region must carry what they carry in the whole model. Enlarge the region to hold it.";
                    return null;
                }
            }
            sheets.Add(new GmshThermalSheet(h.Name, sheet));
        }
        if (outside.Count > 0)
            notes.Add($"{outside.Count} heat source(s) lie outside the submodel's box ({string.Join(", ", outside.Select(o => $"'{o}'"))}): their " +
                      "heat reaches it through the cut faces.");

        // ── named faces: each boundary's (exterior only) and each probe's (whole) ──
        var faces = new List<GmshThermalFace>();
        bool exposed = false;
        var dropped = new List<string>();
        string? Face(string spelled, bool exteriorOnly)
        {
            if (faces.Any(f => f.Name == spelled && f.ExteriorOnly == exteriorOnly)) return null;
            int slash = spelled.LastIndexOf('/');
            if (slash > 0 && replacedBy.TryGetValue(C3dKernelUse.Resolve(doc, spelled[..slash], spelled[(slash + 1)..]).Item1, out string? blk))
                return $"The face '{spelled}' is on a solid effective block '{blk}' replaces. Disable the block, or put the condition elsewhere.";
            if (clip is { } cf && slash > 0 && e.Solids.FirstOrDefault(s => s.Name == C3dKernelUse.Resolve(doc, spelled[..slash], spelled[(slash + 1)..]).Item1) is { } owner &&
                !solids.Contains(owner))
            { dropped.Add(spelled); return null; }
            if (FacePieces(doc, e, solids, spelled, out int si, out string? why) is not { } pieces) return why;
            if (clip is { } c)
            {
                pieces = [.. pieces.Where(pc => Overlaps(pc.Bounds(), c.Min, c.Max, -tol))];
                if (pieces.Count == 0) { dropped.Add(spelled); return null; }
            }
            // an effective block that cuts into this solid takes its footprint out of the solid's faces: that footprint is
            // the block's surface now, on the same plane, and still part of the face the condition or probe names
            var also = blocks.Select(bl => solids.IndexOf(bl.Solid))
                             .Where(bi => bi >= 0 && bi != si
                                          && Overlaps(Em3dProblem.Bounds(solids[si].Primitive), BoundsMin(solids[bi]), BoundsMax(solids[bi]), tol)
                                          && pieces.Any(pc => Overlaps(pc.Bounds(), BoundsMin(solids[bi]), BoundsMax(solids[bi]), -tol))).ToList();
            if (also.Count > 0 && faces.All(x => x.Name != spelled))
                notes.Add($"The face '{spelled}' takes in the footprint of effective block(s) {string.Join(", ", also.Select(bi => $"'{solids[bi].Name}'"))} " +
                          "that cut into its solid.");
            faces.Add(new GmshThermalFace(spelled, si, pieces, exteriorOnly) { AlsoSolids = also });
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
        // brief-em3d-77 R-em3d77-1 — each current's contact faces
        var rfPorts = new List<ThermalRfPort>();
        var ports = ThermalCurrents.Contacts(doc, e, t, solids, faces, rfPorts, out string? portWhy);
        if (ports is null) { refusal = portWhy; return null; }
        if (dropped.Count > 0)
            notes.Add($"{dropped.Count} named face(s) lie outside the submodel's box and take no part in it: " +
                      string.Join(", ", dropped.Distinct().Select(d => $"'{d}'")) + ".");

        var symmetry = doc.SymmetryPlanes.Select(p => ((int)p.Axis, p.At * m)).ToList();

        // brief-em3d-76 — a mesh region a submodel is cut to refines THAT submodel, not the whole model it is cut from: the
        // two-step solve exists so the whole model can stay coarse
        var meshRegions = C3dProblemAssembly.MeshRegions(doc).ToList();
        if (t.Submodel is null)
        {
            var cutTo = C3dSetups.Read(doc).Select(x => x.Setup?.Thermal?.Submodel?.Region).OfType<string>().ToHashSet(StringComparer.Ordinal);
            int n0 = meshRegions.Count;
            meshRegions.RemoveAll(r => cutTo.Contains(r.Name));
            if (meshRegions.Count < n0)
                notes.Add($"{n0 - meshRegions.Count} mesh region(s) a submodel is cut to do not refine this whole-model run: the submodel meshes them finely.");
        }

        // brief-em3d-76 — which of the clip box's planes CUT material: one flush with the model's own outer face is not a cut
        var cutPlanes = new List<int>();
        if (clip is { } box)
        {
            double[] lo = [box.Min.X, box.Min.Y, box.Min.Z], hi = [box.Max.X, box.Max.Y, box.Max.Z];
            var all = e.Solids.Where(x => !ThermalMaterials.NotMeshed(x.Role, x.Material)).Select(x => Em3dProblem.Bounds(x.Primitive))
                              .Select(b => (Lo: new[] { b.X0, b.Y0, b.Z0 }, Hi: new[] { b.X1, b.Y1, b.Z1 })).ToList();
            for (int plane = 0; plane < 6; plane++)
            {
                int axis = plane / 2;
                bool low = plane % 2 == 0;
                bool beyond = all.Any(b =>
                    (low ? b.Lo[axis] < lo[axis] - tol : b.Hi[axis] > hi[axis] + tol) &&
                    Enumerable.Range(0, 3).Where(a => a != axis).All(a => b.Lo[a] < hi[a] - tol && b.Hi[a] > lo[a] + tol));
                if (beyond) cutPlanes.Add(plane);
            }
            if (cutPlanes.Count == 0)
            {
                refusal = $"The submodel's region '{t.Submodel!.Region}' holds the whole model: its box cuts nothing, so it is no submodel.";
                return null;
            }
        }
        var input = new GmshThermalInput(solids, [.. sheets, .. patches], faces, exposed, meshRegions, Sizing(t.Mesh, scale))
        {
            Voids = voids, Clip = clip, CutPlanes = cutPlanes, Symmetry = symmetry, WireLines = ThermalWireLowering.SizeLines(plans),
        };
        var gmsh = GmshGeoWriter.WriteThermal(input);
        if (!gmsh.Ok) { refusal = gmsh.Refusal; return null; }

        // ── the tag table, from the groups the writer made, in the order it made them ──
        int k = 0;
        var regions = solids.Select(s => new ThermalRegionTag(s.Name, s.Material, gmsh.Groups[k++].Attribute)).ToList();
        var sheetTags = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in sheets) sheetTags[s.Name] = gmsh.Groups[k++].Attribute;
        var patchTags = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in patches) patchTags[s.Name] = gmsh.Groups[k++].Attribute;
        var faceTags = faces.Select(f => new ThermalFaceTag(f.Name, f.ExteriorOnly, gmsh.Groups[k++].Attribute)).ToList();
        int? exposedTag = exposed ? gmsh.Groups[k++].Attribute : null;
        int? cutTag = clip is not null ? gmsh.Groups[k].Attribute : null;

        // ── brief-em3d-76 R-em3d76-1a: which contacts carry a resistance ──
        var contacts = ThermalContacts.Resolve([.. solids.Select(s => (s.Name, s.Material))], doc.ContactResistances, e.Technology);
        var effective = new Dictionary<int, EffectiveBlockLowering>();
        foreach (var bl in blocks) effective[solids.IndexOf(bl.Solid)] = bl;
        if (doc.SymmetryPlanes.Count > 0)
            notes.Add($"Symmetry: {string.Join(", ", doc.SymmetryPlanes.Select(p => $"{p.Axis} = {(p.At * m * 1e6).ToString("G6", System.Globalization.CultureInfo.InvariantCulture)} µm"))} " +
                      $"— the modelled part is 1/{1 << doc.SymmetryPlanes.Count} of the device; its faces on the plane(s) are insulated, " +
                      "and the sources carry the power of the modelled part. Measures read SymmetryFactor for the whole device.");
        if (plans.Count > 0) notes.Add(ThermalWireLowering.Note(plans));
        notes.AddRange(ports.Select(p => p.Note));
        return new ThermalLowering(input, gmsh, regions, sheetTags, solidSources, faceTags, exposedTag, notes)
        {
            Contacts = contacts, Effective = effective, Clip = clip, CutTag = cutTag, SourcesOutside = outside,
            SymmetryFactor = 1 << doc.SymmetryPlanes.Count, Wires = plans, PatchTags = patchTags, PortContacts = ports, RfPorts = rfPorts,
            Conductors = [.. solids.Select(x => x.Role == Em3dRole.Conductor && !blocks.Any(bl => bl.Solid == x))],
        };
    }

    private static Point3 BoundsMin(Em3dSolid s) { var b = Em3dProblem.Bounds(s.Primitive); return new Point3(b.X0, b.Y0, b.Z0); }
    private static Point3 BoundsMax(Em3dSolid s) { var b = Em3dProblem.Bounds(s.Primitive); return new Point3(b.X1, b.Y1, b.Z1); }

    private static bool Overlaps((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a, Point3 lo, Point3 hi, double tol)
        => a.X0 < hi.X - tol && a.X1 > lo.X + tol && a.Y0 < hi.Y - tol && a.Y1 > lo.Y + tol && a.Z0 < hi.Z - tol && a.Z1 > lo.Z + tol;

    private static bool Within((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a, Point3 lo, Point3 hi, double tol)
        => a.X0 >= lo.X - tol && a.X1 <= hi.X + tol && a.Y0 >= lo.Y - tol && a.Y1 <= hi.Y + tol && a.Z0 >= lo.Z - tol && a.Z1 <= hi.Z + tol;
}
