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
public sealed record GmshThermalSheet(string Name, Em3dSheet Sheet);

/// <summary>A named face of solid <paramref name="Solid"/> (an index into the solids): its planar pieces, and whether only
/// its exterior (single-sided) surfaces count — a boundary condition's face does; a probe's does not.</summary>
public sealed record GmshThermalFace(string Name, int Solid, IReadOnlyList<Em3dFacePolygon> Pieces, bool ExteriorOnly);

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
                                      GmshThermalSizing Sizing);

public static partial class GmshGeoWriter
{
    /// <summary>The group name of every single-sided surface no boundary face claimed.</summary>
    public const string ExposedGroup = "*exposed*";

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
        double max = ThermalMaxElementM(input);
        return thin > 0 ? Math.Min(max, thin / Math.Max(1, input.Sizing.MinThroughThickness)) : max;
    }

    /// <summary>The element size at a heat-source sheet, metres (before the scale): its smaller side / SizeFromSources.</summary>
    public static double ThermalSourceSizeM(GmshThermalInput input, Em3dSheet sheet)
    {
        var (x0, y0, z0, x1, y1, z1) = sheet.WorldBounds();
        double[] d = [x1 - x0, y1 - y0, z1 - z0];
        Array.Sort(d);
        double smaller = d[1] > 0 ? d[1] : d[2];          // the plane's smaller side: skip the zero thickness
        return Math.Min(ThermalMaxElementM(input), smaller / Math.Max(1e-9, input.Sizing.SizeFromSources));
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
        var solidGroups = input.Solids.Select(s => new Em3dGroup(s.Name, ++attr, 3, Em3dGroupKind.Volume, 1, AtLeast: false, s.Material)).ToList();
        groups.AddRange(solidGroups);
        var sheetGroups = input.Sheets.Select(s => new Em3dGroup(s.Name, ++attr, 2, Em3dGroupKind.Sheet, 1, AtLeast: true)).ToList();
        groups.AddRange(sheetGroups);
        var faceGroups = input.Faces.Select(f => new Em3dGroup(f.Name, ++attr, 2, Em3dGroupKind.FaceBoundary, 1, AtLeast: true)).ToList();
        groups.AddRange(faceGroups);
        Em3dGroup? exposed = input.Exposed ? new Em3dGroup(ExposedGroup, ++attr, 2, Em3dGroupKind.FaceBoundary, 0, AtLeast: true) : null;
        if (exposed is not null) groups.Add(exposed);

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
        var precedence = Em3dPrecedence.Of(input.Solids.ToList(), []);
        if (precedence.Shift > 0) L("// metal takes precedence over dielectric: every metal is ranked above every dielectric (em-3d.md §6.3a)");
        for (int i = 0; i < input.Solids.Count; i++)
        {
            var tools = new List<int>();
            int pi = precedence.Of(input.Solids[i]);
            for (int j = 0; j < input.Solids.Count; j++)
            {
                if (j == i) continue;
                int pj = precedence.Of(input.Solids[j]);
                if ((pj > pi || (pj == pi && j > i)) && Overlap(bounds[i], bounds[j])) tools.Add(j);
            }
            if (tools.Count == 0) continue;
            L($"s{i}[] = BooleanDifference{{ Volume{{s{i}[]}}; Delete; }}{{ Volume{{{string.Join(", ", tools.Select(j => $"s{j}[]"))}}}; }};");
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
            L($"own[] = Abs(Boundary{{ Volume{{s{f.Solid}[]}}; }});");
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
        if (exposed is not null)
        {
            L("// every single-sided surface no boundary face claimed");
            L("exposed[] = single[];");
            L("exposed[] -= claimed[];");
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
            double near = Round(z.Scale * ThermalSourceSizeM(input, input.Sheets[k].Sheet) * 1e6);
            int d = ++field, t = ++field;
            fields.Add(t);
            L($"Field[{d}] = Distance; Field[{d}].SurfacesList = {{w{k}[]}}; Field[{d}].Sampling = 50;");
            L($"Field[{t}] = Threshold; Field[{t}].InField = {d}; Field[{t}].SizeMin = {Num(near)}; Field[{t}].SizeMax = {Num(sizeMax)}; " +
              $"Field[{t}].DistMin = {Num(near)}; Field[{t}].DistMax = {Num(Round(near + (sizeMax - near) / (z.Grading - 1)))};");
        }
        foreach (var region in input.MeshRegions)
            L(BoxField(++field, region with { SizeM = region.SizeM * z.Scale }, sizeMax, z.Grading, fields));
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
