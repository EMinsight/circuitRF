// brief-em3d-74 R-em3d74-4a — the Gmsh writer's THERMAL MODE: not a second writer. The same solids through the same
// EmitPrimitive, the same precedence cut (metal over dielectric, then construction order), the same one BooleanFragments,
// the same entities file and the same entity check — what differs is what is kept:
//
//   * no air box and no background: a thermal run meshes solids only (overview §1b), and every solid — conductor or
//     dielectric — is a VOLUME with its material (in EM a conductor is a void);
//   * a heat-source sheet is an embedded surface with its own physical group (brief 72 Q2: the fragment keeps it, the volume
//     is not split); a volumetric source is its solid's own group, so nothing is written for it here;
//   * each face a boundary or a probe names is a group — its pieces' boxes, intersected with the solid's own boundary so a
//     coplanar neighbour's face is never taken; a boundary's face keeps only its single-sided (exterior) surfaces, and
//     `*exposed*` is every single-sided surface no boundary face claimed;
//   * spot and line probes are not geometry — they are evaluated on the mesh;
//   * sizing has no wavelength: the largest element is a fraction of the problem's largest side, each solid has at least
//     MinThroughThickness elements through its thinnest dimension, a heat source's surroundings are refined to its smaller
//     side / SizeFromSources, growth follows Grading (the same Distance/Threshold fields), and mesh regions add Box fields.
//
// Every single-sided surface no group names is INSULATED by definition in a thermal run, so the entity table's
// unclassified count is written as 0: the check that guards Palace against a silent magnetic wall has nothing to guard here.

using System.Globalization;
using System.Text;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>A heat-source sheet to embed: its name and its geometry (a sheet's outline in its frame).</summary>
public sealed record GmshThermalSheet(string Name, Em3dSheet Sheet)
{
    /// <summary>brief-em3d-77 — elements across the sheet's smaller side, when not the sizing's SizeFromSources: a wire's
    /// contact patch takes fewer (brief 72 Q8).</summary>
    public double? ElementsAcross { get; init; }
}

/// <summary>A named face of solid <paramref name="Solid"/> (an index into the solids): its planar pieces, and whether only
/// its exterior (single-sided) surfaces count — a boundary condition's face does; a probe's does not.</summary>
public sealed record GmshThermalFace(string Name, int Solid, IReadOnlyList<Em3dFacePolygon> Pieces, bool ExteriorOnly)
{
    /// <summary>brief-em3d-76 — the effective blocks (solid indices) that cut into <see cref="Solid"/> where this face lies: their
    /// surfaces on the face's own plane are the part of the face the block took, and belong to it.</summary>
    public IReadOnlyList<int> AlsoSolids { get; init; } = [];
}

/// <summary>The thermal mesh's sizing (R-em3d74-4a).</summary>
/// <param name="MaxFraction">The largest element as a fraction of the problem's largest side.</param>
/// <param name="SizeFromSources">Elements across a heat source's smaller side.</param>
/// <param name="MinThroughThickness">Elements through each solid's thinnest dimension, at least.</param>
/// <param name="Grading">The ratio by which neighbouring elements may grow.</param>
/// <param name="Order">1 or 2.</param>
/// <param name="Scale">Every size multiplied by this — 0.7 for the mesh-convergence check (R-em3d74-5e).</param>
public sealed record GmshThermalSizing(double MaxFraction, double SizeFromSources, int MinThroughThickness, double Grading,
                                       int Order, double Scale = 1);

/// <summary>Everything a thermal lowering writes: solids (in construction order), source sheets, faces, whether
/// <c>*exposed*</c> is asked for, the mesh regions and the sizing.</summary>
public sealed record GmshThermalInput(IReadOnlyList<Em3dSolid> Solids, IReadOnlyList<GmshThermalSheet> Sheets,
                                      IReadOnlyList<GmshThermalFace> Faces, bool Exposed, IReadOnlyList<Em3dMeshRegion> MeshRegions,
                                      GmshThermalSizing Sizing)
{
    /// <summary>
    /// brief-em3d-76 — the air solids, which are never meshed but still cut what they outrank exactly as they do in an EM
    /// lowering: a plated via's bore (the generator's higher-order air cylinder inside the barrel) leaves the barrel a
    /// TUBE here too. Without them every hollow via conducted as solid copper.
    /// </summary>
    public IReadOnlyList<Em3dSolid> Voids { get; init; } = [];

    /// <summary>brief-em3d-76 R-em3d76-3a — a submodel's box, metres: every solid is intersected with it (Gmsh's own boolean),
    /// and the single-sided surfaces on its six planes that no boundary face claimed become the <c>*cut*</c> group.</summary>
    public (Point3 Min, Point3 Max)? Clip { get; init; }

    /// <summary>Which of the clip box's planes cut material (0 x-min, 1 x-max, 2 y-min, 3 y-max, 4 z-min, 5 z-max): a plane
    /// flush with the model's own outer face is not a cut, and its faces keep their own condition.</summary>
    public IReadOnlyList<int> CutPlanes { get; init; } = [0, 1, 2, 3, 4, 5];

    /// <summary>brief-em3d-76 R-em3d76-4a — mirror planes (axis 0 x, 1 y, 2 z; coordinate in metres): their faces stay
    /// insulated, so <c>*exposed*</c> never takes one.</summary>
    public IReadOnlyList<(int Axis, double AtM)> Symmetry { get; init; } = [];

    /// <summary>brief-em3d-77 R-em3d77-3 — each bond wire's centreline as points, and the element size wanted round it
    /// (metres): a Distance field refines the solid a wire runs through. The wire itself is not geometry here.</summary>
    public IReadOnlyList<(IReadOnlyList<Point3> Points, double SizeM)> WireLines { get; init; } = [];
}

public static partial class GmshGeoWriter
{
    /// <summary>The group name of every single-sided surface no boundary face claimed.</summary>
    public const string ExposedGroup = "*exposed*";

    /// <summary>brief-em3d-76 — the group name of a submodel's cut faces.</summary>
    public const string CutGroup = "*cut*";

    /// <summary>The largest element of a thermal mesh, metres, before <see cref="GmshThermalSizing.Scale"/>.</summary>
    public static double ThermalMaxElementM(GmshThermalInput input)
    {
        var (x0, y0, z0, x1, y1, z1) = Extent(input.Solids);
        return input.Sizing.MaxFraction * Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
    }

    /// <summary>The element size a thermal mesh asks for inside solid <paramref name="s"/>, metres (before the scale): the
    /// largest, or less so that its thinnest dimension takes MinThroughThickness elements.</summary>
    public static double ThermalSolidSizeM(GmshThermalInput input, Em3dSolid s)
    {
        var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(s.Primitive);
        double thin = Math.Min(x1 - x0, Math.Min(y1 - y0, z1 - z0));
        // A solid something cuts a hole in is only as thick as the wall the hole leaves: a plated via's barrel, bored by its
        // air cylinder, is a tube whose box is the via's whole diameter — sized from the box it got about one element through
        // its plating. An outranking AIR solid (a bore — never meshed, so nothing else sizes the wall it leaves) lying strictly
        // inside the solid along an axis, and taking most of it there (not a small hole in a wide board, whose Constant field
        // would refine the whole board), leaves a wall each side. A meshed solid embedded in another is sized by its own box,
        // and the mesh conforms to the face they share; applying this to it would refine a whole passivation round a plate.
        var precedence = Em3dPrecedence.Of([.. input.Solids, .. input.Voids], []);
        int ps = precedence.Of(s);
        foreach (var c in input.Voids)
        {
            int pc = precedence.Of(c);
            if (!(pc > ps || pc == ps && c.Order > s.Order)) continue;
            var cb = Em3dProblem.Bounds(c.Primitive);
            if (!Overlap((x0, y0, z0, x1, y1, z1), cb)) continue;
            double[] lo = [x0, y0, z0], hi = [x1, y1, z1], clo = [cb.X0, cb.Y0, cb.Z0], chi = [cb.X1, cb.Y1, cb.Z1];
            for (int a = 0; a < 3; a++)
            {
                if (!(clo[a] > lo[a] && chi[a] < hi[a]) || chi[a] - clo[a] < 0.5 * (hi[a] - lo[a])) continue;
                thin = Math.Min(thin, Math.Min(clo[a] - lo[a], hi[a] - chi[a]));
            }
        }
        double max = ThermalMaxElementM(input);
        return thin > 0 ? Math.Min(max, thin / Math.Max(1, input.Sizing.MinThroughThickness)) : max;
    }

    /// <summary>The element size at a heat-source sheet, metres (before the scale): its smaller side / SizeFromSources (or
    /// / <paramref name="across"/>, a contact patch's own count).</summary>
    public static double ThermalSourceSizeM(GmshThermalInput input, Em3dSheet sheet, double? across = null)
    {
        var (x0, y0, z0, x1, y1, z1) = sheet.WorldBounds();
        double[] d = [x1 - x0, y1 - y0, z1 - z0];
        Array.Sort(d);
        double smaller = d[1] > 0 ? d[1] : d[2];          // the plane's smaller side: skip the zero thickness
        return Math.Min(ThermalMaxElementM(input), smaller / Math.Max(1e-9, across ?? input.Sizing.SizeFromSources));
    }

    /// <summary>
    /// R-em3d74-4a — the thermal script for <paramref name="input"/>: its groups (volumes with their material, sheets, faces,
    /// exposed), in the order the solver's tags are built from, or the reason it cannot be written.
    /// </summary>
    public static GmshLowering WriteThermal(GmshThermalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Solids.Count == 0) return No("The thermal problem holds no solid with a material, so there is nothing to mesh.");
        if (input.Sizing.Order is not (1 or 2)) return No($"A thermal mesh is order 1 or 2, not {input.Sizing.Order}.");
        if (!(input.Sizing.Grading > 1)) return No("A thermal mesh's Grading must be above 1.");

        var groups = new List<Em3dGroup>();
        int attr = 0;
        // A solid may come out as SEVERAL volumes — a submodel's box across a comb of fingers, a void or a higher-order solid cutting
        // it through — and each is still that solid's material: its group takes them all. What must not happen is a volume
        // LOST in the fragment (the lost count below), and a piece touching nothing that sets its temperature is the solver's
        // own refusal, made per connected body.
        var solidGroups = input.Solids.Select(s => new Em3dGroup(s.Name, ++attr, 3, Em3dGroupKind.Volume, 1, AtLeast: true, s.Material)).ToList();
        groups.AddRange(solidGroups);
        var sheetGroups = input.Sheets.Select(s => new Em3dGroup(s.Name, ++attr, 2, Em3dGroupKind.Sheet, 1, AtLeast: true)).ToList();
        groups.AddRange(sheetGroups);
        // A face both a boundary (its exterior only) and a probe or a current's contact (the whole face) is two groups, and Gmsh
        // refuses a second physical group of one name: the boundary's takes a suffix. Groups are read back by attribute, not name.
        var faceGroups = input.Faces.Select(f => new Em3dGroup(
            f.ExteriorOnly && input.Faces.Any(o => o.Name == f.Name && !o.ExteriorOnly) ? f.Name + " (exterior)" : f.Name,
            ++attr, 2, Em3dGroupKind.FaceBoundary, 1, AtLeast: true)).ToList();
        groups.AddRange(faceGroups);
        Em3dGroup? exposed = input.Exposed ? new Em3dGroup(ExposedGroup, ++attr, 2, Em3dGroupKind.FaceBoundary, 0, AtLeast: true) : null;
        if (exposed is not null) groups.Add(exposed);
        Em3dGroup? cut = input.Clip is not null ? new Em3dGroup(CutGroup, ++attr, 2, Em3dGroupKind.FaceBoundary, 1, AtLeast: true) : null;
        if (cut is not null) groups.Add(cut);

        var g = new StringBuilder();
        void L(string line = "") => g.Append(line).Append('\n');
        L("// Generated by circuitRF for its thermal solver (brief-em3d-74). Do not edit: circuitRF rewrites this file from");
        L("// the setup, and an unchanged file reuses the mesh beside it. Units: micrometres.");
        L("// Run by circuitRF as:  gmsh model.geo -3 -o model.msh    (writes entities.txt beside it)");
        L();
        L("SetFactory(\"OpenCASCADE\");");
        L("Geometry.OCCBooleanPreserveNumbering = 1;");
        L("Geometry.OCCBoundsUseStl = 1;");
        L($"e = {Num(Eps)};");
        L();

        L("// ---- solids, in construction order: conductors and dielectrics alike are meshed volumes ----");
        for (int i = 0; i < input.Solids.Count; i++)
        {
            var s = input.Solids[i];
            L($"// {Comment(s.Name)}: {s.Role}, {Comment(s.Material)}, order {s.Order}");
            EmitPrimitive(g, $"s{i}", s.Primitive);
        }
        L();
        var kernelFiles = input.Solids.Select(x => x.Primitive).OfType<Em3dShapeSolid>()
                               .Select(k => new Em3dKernelFile(KernelFileName(k), k.Brep.ToArray()))
                               .DistinctBy(x => x.FileName).ToList();

        L("// ---- disjoint by construction order: each solid loses what a higher-order solid takes -----");
        var bounds = input.Solids.Select(s => Em3dProblem.Bounds(s.Primitive)).ToList();
        var precedence = Em3dPrecedence.Of([.. input.Solids, .. input.Voids], []);
        if (precedence.Shift > 0) L("// metal takes precedence over dielectric: every metal is ranked above every dielectric (em-3d.md §6.3a)");
        // brief-em3d-76 — an air solid that outranks a meshed one it overlaps cuts it, as in EM, and is then deleted
        var voidCuts = new List<(int Void, List<int> Of)>();
        for (int k = 0; k < input.Voids.Count; k++)
        {
            var v = input.Voids[k];
            int pv = precedence.Of(v);
            var vb = Em3dProblem.Bounds(v.Primitive);
            var of = Enumerable.Range(0, input.Solids.Count)
                               .Where(i => (pv > precedence.Of(input.Solids[i]) || pv == precedence.Of(input.Solids[i]) && v.Order > input.Solids[i].Order)
                                           && Overlap(bounds[i], vb)).ToList();
            if (of.Count == 0) continue;
            voidCuts.Add((k, of));
            L($"// void {Comment(v.Name)}: {Comment(v.Material)}, never meshed; it cuts what it outranks");
            EmitPrimitive(g, $"v{k}", v.Primitive);
        }
        for (int i = 0; i < input.Solids.Count; i++)
        {
            var tools = new List<string>();
            int pi = precedence.Of(input.Solids[i]);
            for (int j = 0; j < input.Solids.Count; j++)
            {
                if (j == i) continue;
                int pj = precedence.Of(input.Solids[j]);
                if ((pj > pi || (pj == pi && j > i)) && Overlap(bounds[i], bounds[j])) tools.Add($"s{j}[]");
            }
            tools.AddRange(voidCuts.Where(c => c.Of.Contains(i)).Select(c => $"v{c.Void}[]"));
            if (tools.Count == 0) continue;
            L($"s{i}[] = BooleanDifference{{ Volume{{s{i}[]}}; Delete; }}{{ Volume{{{string.Join(", ", tools)}}}; }};");
        }
        if (voidCuts.Count > 0) L($"Recursive Delete {{ Volume{{{string.Join(", ", voidCuts.Select(c => $"v{c.Void}[]"))}}}; }}");
        if (input.Clip is { } clip)
        {
            L("// ---- a submodel: every solid cut to the region's box (brief-em3d-76) --------------------------");
            L($"cb = newv; Box(cb) = {{{Um(clip.Min.X)}, {Um(clip.Min.Y)}, {Um(clip.Min.Z)}, {Um(clip.Max.X - clip.Min.X)}, " +
              $"{Um(clip.Max.Y - clip.Min.Y)}, {Um(clip.Max.Z - clip.Min.Z)}}};");
            for (int i = 0; i < input.Solids.Count; i++)
                L($"s{i}[] = BooleanIntersection{{ Volume{{s{i}[]}}; Delete; }}{{ Volume{{cb}}; }};");
            L("Recursive Delete { Volume{cb}; }");
        }
        L();

        L("// ---- heat-source sheets: embedded surfaces --------------------------------------------------");
        for (int k = 0; k < input.Sheets.Count; k++)
        {
            var sh = input.Sheets[k].Sheet;
            L($"// source {Comment(input.Sheets[k].Name)}");
            EmitPlanar(g, $"h{k}", [sh.Outline, .. sh.Holes], sh.World);
        }
        L();

        L("// ---- one fragment: imprints the shared faces and the sheets, splits no volume -------------");
        string volumes = string.Join(", ", Enumerable.Range(0, input.Solids.Count).Select(i => $"s{i}[]"));
        L($"frag[] = BooleanFragments{{ Volume{{{volumes}}}; Delete; }}" +
          (input.Sheets.Count == 0 ? "{ };" : $"{{ Surface{{{string.Join(", ", Enumerable.Range(0, input.Sheets.Count).Select(k => $"h{k}[]"))}}}; Delete; }};"));
        L("allV[] = Volume{:};");
        L("allS[] = Surface{:};");
        L("single[] = CombinedBoundary{ Volume{allV[]}; };");
        L("For k In {0:#single[]-1}");
        L("  single[k] = Abs(single[k]);");
        L("EndFor");
        L("claimed[] = {};");
        L();

        L("// ---- surfaces, recovered by what circuitRF placed ------------------------------------------");
        for (int k = 0; k < input.Sheets.Count; k++)
        {
            L($"// source {Comment(input.Sheets[k].Name)}");
            L($"w{k}[] = {Query(input.Sheets[k].Sheet.WorldBounds(), 0)};");
        }
        for (int k = 0; k < input.Faces.Count; k++)
        {
            var f = input.Faces[k];
            L($"// face {Comment(f.Name)}{(f.ExteriorOnly ? ": its exterior surfaces only (a boundary condition)" : "")}");
            L($"f{k}[] = {Query(f.Pieces[0].Bounds(), 0)};");
            foreach (var pc in f.Pieces.Skip(1)) L($"f{k}[] += {Query(pc.Bounds(), 0)};");
            // only the named solid's own surfaces: a coplanar neighbour inside the same box is never taken
            L($"own[] = Abs(Boundary{{ Volume{{{string.Join(", ", new[] { f.Solid }.Concat(f.AlsoSolids).Select(i => $"s{i}[]"))}}}; }});");
            L($"x[] = f{k}[];");
            L("x[] -= own[];");
            L($"f{k}[] -= x[];");
            if (f.ExteriorOnly)
            {
                L($"x[] = f{k}[];");
                L("x[] -= single[];");
                L($"f{k}[] -= x[];");
                L($"claimed[] += f{k}[];");
            }
        }
        if (input.Clip is { } cc)
        {
            L("// a submodel's cut faces: the single-sided surfaces on the box's planes that cut material, no boundary face claimed");
            L("cut[] = {};");
            var (lo, hi) = cc;
            var planes = new[]
            {
                (lo.X, lo.Y, lo.Z, lo.X, hi.Y, hi.Z), (hi.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z),
                (lo.X, lo.Y, lo.Z, hi.X, lo.Y, hi.Z), (lo.X, hi.Y, lo.Z, hi.X, hi.Y, hi.Z),
                (lo.X, lo.Y, lo.Z, hi.X, hi.Y, lo.Z), (lo.X, lo.Y, hi.Z, hi.X, hi.Y, hi.Z),
            };
            foreach (int k in input.CutPlanes) L($"cut[] += {Query(planes[k], 0)};");
            L("x[] = cut[];");
            L("x[] -= single[];");
            L("cut[] -= x[];");
            L("cut[] -= claimed[];");
            L("claimed[] += cut[];");
        }
        if (exposed is not null)
        {
            L("// every single-sided surface no boundary face claimed");
            L("exposed[] = single[];");
            L("exposed[] -= claimed[];");
            if (input.Symmetry.Count > 0)
            {
                // brief-em3d-76 R-em3d76-4a — a mirror plane's faces are insulated: exposed never takes one
                var (ex0, ey0, ez0, ex1, ey1, ez1) = Extent(input.Solids);
                foreach (var (axis, at) in input.Symmetry)
                {
                    var b = axis switch
                    {
                        0 => (at, ey0, ez0, at, ey1, ez1),
                        1 => (ex0, at, ez0, ex1, at, ez1),
                        _ => (ex0, ey0, at, ex1, ey1, at),
                    };
                    L($"// symmetry plane {"XYZ"[axis]} = {Num(Round(at * 1e6))} um: insulated, never exposed");
                    L($"x[] = {Query(b, 0)};");
                    L("exposed[] -= x[];");
                }
            }
        }
        L();

        L("// ---- physical groups: the solver's tags (groups.json) ---------------------------------------");
        for (int i = 0; i < input.Solids.Count; i++)
            L($"Physical Volume(\"{PhysicalName(solidGroups[i].Name)}\", {solidGroups[i].Attribute}) = {{s{i}[]}};");
        for (int k = 0; k < sheetGroups.Count; k++)
            L($"Physical Surface(\"{PhysicalName(sheetGroups[k].Name)}\", {sheetGroups[k].Attribute}) = {{w{k}[]}};");
        for (int k = 0; k < faceGroups.Count; k++)
            L($"Physical Surface(\"{PhysicalName(faceGroups[k].Name)}\", {faceGroups[k].Attribute}) = {{f{k}[]}};");
        if (exposed is not null)
            L($"Physical Surface(\"{PhysicalName(exposed.Name)}\", {exposed.Attribute}) = {{exposed[]}};");
        if (cut is not null)
            L($"Physical Surface(\"{PhysicalName(cut.Name)}\", {cut.Attribute}) = {{cut[]}};");
        L();

        L("// ---- the entity table circuitRF checks before it believes this mesh ----------------------");
        L($"Printf(\"# circuitRF entity table: group <attribute> <count> <volume tags lost in the fragment>\") > \"{EntitiesFile}\";");
        for (int i = 0; i < input.Solids.Count; i++)
        {
            L($"lost[] = s{i}[];");
            L("lost[] -= allV[];");
            L($"Printf(\"group {solidGroups[i].Attribute} %g %g\", #s{i}[], #lost[]) >> \"{EntitiesFile}\";");
        }
        for (int k = 0; k < sheetGroups.Count; k++)
            L($"Printf(\"group {sheetGroups[k].Attribute} %g 0\", #w{k}[]) >> \"{EntitiesFile}\";");
        for (int k = 0; k < faceGroups.Count; k++)
            L($"Printf(\"group {faceGroups[k].Attribute} %g 0\", #f{k}[]) >> \"{EntitiesFile}\";");
        if (exposed is not null)
            L($"Printf(\"group {exposed.Attribute} %g 0\", #exposed[]) >> \"{EntitiesFile}\";");
        if (cut is not null)
            L($"Printf(\"group {cut.Attribute} %g 0\", #cut[]) >> \"{EntitiesFile}\";");
        for (int i = 0; i < input.Solids.Count; i++)
            if (input.Solids[i].Primitive is Em3dShapeSolid)
                L($"If (ns{i} != 1) Printf(\"kernel_import_count {solidGroups[i].Attribute} %g\", ns{i}) >> \"{EntitiesFile}\"; EndIf");
        L($"Printf(\"all_volumes %g\", #allV[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"classified_volumes %g\", {string.Join(" + ", Enumerable.Range(0, input.Solids.Count).Select(i => $"#s{i}[]"))}) >> \"{EntitiesFile}\";");
        L($"Printf(\"all_surfaces %g\", #allS[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"single_sided %g\", #single[]) >> \"{EntitiesFile}\";");
        L("// an unnamed single-sided surface is insulated in a thermal run, by definition");
        L($"Printf(\"unclassified_single_sided 0\") >> \"{EntitiesFile}\";");
        L();

        // ── sizing ───────────────────────────────────────────────────────────────────────────
        var z = input.Sizing;
        double sizeMax = Round(z.Scale * ThermalMaxElementM(input) * 1e6);
        L("// ---- sizing: no wavelength -------------------------------------------------------------------");
        L($"// Largest element {Num(z.MaxFraction)} of the problem's largest side; at least {z.MinThroughThickness} element(s) " +
          $"through each solid; {Num(z.SizeFromSources)} across each heat source's smaller side; growing by {Num(z.Grading)}" +
          (z.Scale != 1 ? $"; every size x {Num(z.Scale)} (the convergence check)." : "."));
        L($"Mesh.MeshSizeMax = {Num(sizeMax)};");
        L("Mesh.MeshSizeFromPoints = 0;");
        L("Mesh.MeshSizeExtendFromBoundary = 0;");
        L($"Mesh.MeshSizeFromCurvature = {CurvatureElements};");
        int field = 0;
        var fields = new List<int>();
        for (int i = 0; i < input.Solids.Count; i++)
        {
            field++;
            fields.Add(field);
            L($"Field[{field}] = Constant; Field[{field}].VIn = {Num(Round(z.Scale * ThermalSolidSizeM(input, input.Solids[i]) * 1e6))}; " +
              $"Field[{field}].VolumesList = {{s{i}[]}};");
        }
        for (int k = 0; k < input.Sheets.Count; k++)
        {
            double near = Round(z.Scale * ThermalSourceSizeM(input, input.Sheets[k].Sheet, input.Sheets[k].ElementsAcross) * 1e6);
            int d = ++field, t = ++field;
            fields.Add(t);
            L($"Field[{d}] = Distance; Field[{d}].SurfacesList = {{w{k}[]}}; Field[{d}].Sampling = 50;");
            L($"Field[{t}] = Threshold; Field[{t}].InField = {d}; Field[{t}].SizeMin = {Num(near)}; Field[{t}].SizeMax = {Num(sizeMax)}; " +
              $"Field[{t}].DistMin = {Num(near)}; Field[{t}].DistMax = {Num(Round(near + (sizeMax - near) / (z.Grading - 1)))};");
        }
        foreach (var region in input.MeshRegions)
            L(BoxField(++field, region with { SizeM = region.SizeM * z.Scale }, sizeMax, z.Grading, fields));
        // brief-em3d-77 — round each bond wire: points along its centreline, not in the fragment, only a Distance field's
        for (int w = 0; w < input.WireLines.Count; w++)
        {
            var (points, size) = input.WireLines[w];
            double near = Math.Min(sizeMax, Round(z.Scale * size * 1e6));
            L($"// wire {w}: {points.Count} point(s) along its centreline");
            L($"wp{w}[] = {{}};");
            foreach (var q in points) L($"p = newp; Point(p) = {{{Um(q.X)}, {Um(q.Y)}, {Um(q.Z)}}}; wp{w}[] += p;");
            int d = ++field, t = ++field;
            fields.Add(t);
            L($"Field[{d}] = Distance; Field[{d}].PointsList = {{wp{w}[]}};");
            L($"Field[{t}] = Threshold; Field[{t}].InField = {d}; Field[{t}].SizeMin = {Num(near)}; Field[{t}].SizeMax = {Num(sizeMax)}; " +
              $"Field[{t}].DistMin = {Num(near)}; Field[{t}].DistMax = {Num(Round(near + (sizeMax - near) / (z.Grading - 1)))};");
        }
        int min = ++field;
        L($"Field[{min}] = Min; Field[{min}].FieldsList = {{{string.Join(", ", fields)}}};");
        L($"Background Field = {min};");
        L($"Mesh.ElementOrder = {z.Order};");
        if (z.Order == 2) L("Mesh.HighOrderOptimize = 2;");
        L("Mesh.MshFileVersion = 2.2;");
        L("Mesh.Binary = 1;");

        return new GmshLowering(g.ToString(), groups, GroupsJson(groups), null, kernelFiles);
    }

    private static (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Extent(IReadOnlyList<Em3dSolid> solids)
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var s in solids)
        {
            var b = Em3dProblem.Bounds(s.Primitive);
            x0 = Math.Min(x0, b.X0); y0 = Math.Min(y0, b.Y0); z0 = Math.Min(z0, b.Z0);
            x1 = Math.Max(x1, b.X1); y1 = Math.Max(y1, b.Y1); z1 = Math.Max(z1, b.Z1);
        }
        return (x0, y0, z0, x1, y1, z1);
    }
}
