// brief-emi-4 R-emi4-2/4 — what EMerge's lowering refuses, and how it says what it chose for each object. Every sentence
// here is a refusal, a note or an explain line; none of them is ever written into EMerge's input (EmergeScriptWriter.Emit.cs
// is the only code that writes that). The mapping each rule rests on, checked against EMerge 3.0's source and a run, is the
// table in src/Design/RESOLVED.md ("EMI-4").

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>One object of a lowered problem and what EMerge was told it is: explain's row.</summary>
/// <param name="Kind"><c>solid</c>, <c>sheet</c>, <c>port</c>, <c>lumped element</c>, <c>air box</c> or <c>mesh region</c>.</param>
public sealed record EmergeObjectMapping(string Name, string Kind, string Mapping);

internal static class EmergeLoweringRules
{
    /// <summary>
    /// EMerge 3.0's surface-impedance limit, S/m (<c>Settings.mw_3d_surfimplim</c>): a volume above it is a conductor whose
    /// faces carry its surface impedance and whose interior it does not solve, and a surface at or below it carries no
    /// boundary at all.
    /// </summary>
    public const double SurfaceImpedanceLimitSm = 1e4;

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

    private static string Mm(double m) => (m * 1e3).ToString("G4", CultureInfo.InvariantCulture) + " mm";

    /// <summary>The first thing of <paramref name="problem"/> EMerge cannot be told, as the run refuses it; null when it can
    /// be told all of it.</summary>
    public static string? Refusal(Em3dProblem problem, EmergeSettings settings)
    {
        if (problem.Type == Em3dProblemType.Thermal)
            return "A thermal problem is solved by circuitRF's own thermal solver, never by an EM solver: it cannot be lowered for EMerge.";
        if (problem.IsStatic)
            return $"{(problem.Type == Em3dProblemType.Electrostatic ? "An electrostatic" : "A magnetostatic")} problem cannot be lowered for EMerge: circuitRF runs EMerge for " +
                   "driven and eigenmode problems. Set the setup's Solver3D to Palace.";
        if (settings.Problems() is { Count: > 0 } bad) return string.Join(" ", bad);
        // What EMerge cannot be told at all is named before what is merely wrong, so a wave port is refused as one.
        foreach (var p in problem.Ports)
            if (KindRefusal(problem, p) is { } kind) return kind;
        if (problem.FirstCylindrical() is { } curved) return Em3dCylinderGap.NotHere(curved, "EMerge");
        if (GmshGeoWriter.ImportedOpenEmsOnly(problem.Solids, "EMerge") is { } imported) return imported;
        if (problem.Solids.FirstOrDefault(s => s.Voxel is not null) is { } voxel)
            return $"'{voxel.Voxel!.Phantom}' is a voxelized phantom: each of its cells takes the layer its own centre lies in, which " +
                   "openEMS's grid states and a tetrahedral mesh does not. Run the setup on openEMS, or set the phantom's voxel size " +
                   "to 0 to solve its smooth layers.";
        if (problem.FaceBoundaries.Count > 0)
            return $"The boundary on face '{problem.FaceBoundaries[0].Face}' of '{problem.FaceBoundaries[0].Object}' is a face " +
                   "boundary, which circuitRF lowers for Palace and openEMS and not yet for EMerge. Run the setup on Palace, or " +
                   "remove the face boundary.";
        if (FaceKinds(problem).FirstOrDefault(f => f.Kind == Em3dBoundaryKind.Symmetry) is { Key: not null } symmetric)
            return $"The air box's {symmetric.Key} face is a Symmetry face, which does not say whether the field's electric or " +
                   "magnetic wall lies there. Set it to Pec (an electric wall) or Pmc (a magnetic one) in the setup's AirBox.";
        if (problem.Validate() is { Count: > 0 } invalid)
            return "The 3D problem cannot be lowered for EMerge: " + string.Join(" ", invalid);
        if (problem.Ports.Count == 0 && problem.Type == Em3dProblemType.Driven)
            return "The 3D problem has no ports, so there is nothing for EMerge to excite.";
        foreach (var p in problem.Ports)
            if (PortRefusal(p) is { } port) return port;
        foreach (var le in problem.LumpedElements)
            if (Inexpressible(le) is { } element) return element;
        return MaterialRefusal(problem);
    }

    /// <summary>The air box's six faces by key, in the order the input lists them.</summary>
    public static IReadOnlyList<(string Key, Em3dBoundaryKind Kind)> FaceKinds(Em3dProblem problem)
    {
        var f = problem.Boundary.Faces;
        return [("xmin", f.XMin), ("xmax", f.XMax), ("ymin", f.YMin), ("ymax", f.YMax), ("zmin", f.ZMin), ("zmax", f.ZMax)];
    }

    private static string? KindRefusal(Em3dProblem problem, Em3dPort p)
    {
        int n = p.SourceNumber ?? p.Number;
        // brief-emw-2 R-emw2-6 — a wave port of one terminal runs; a terminal group and a hollow waveguide do not yet
        if (p.Kind == Em3dPortKind.Wave && p.FaceGroup is { } group)
        {
            int terminals = problem.Ports.Count(q => q.FaceGroup == group);
            return $"Port '{p.FaceGroupLabel ?? n.ToString(CultureInfo.InvariantCulture)}' has {Count(terminals)} terminals. EMerge runs wave " +
                   "ports with one conductor on the face in this version; openEMS runs terminal ports. Set the setup's Solver3D to OpenEms.";
        }
        if (p.Kind == Em3dPortKind.Wave && problem.Type == Em3dProblemType.Eigenmode)
            return $"Port {n} is a wave port, and circuitRF runs EMerge's eigenmode solve with lumped ports only: a wave port's " +
                   "condition is a driven problem's. Make the port lumped, or solve a driven problem.";
        if (p.Kind == Em3dPortKind.Wave && FdtdWavePorts.IsHollow(problem, p))
            return $"Port {n} is a hollow waveguide: one conductor meets its region. EMerge's wave port keeps TEM modes only in " +
                   "this version, and a hollow waveguide carries none. Palace runs it: set the setup's Solver3D to Palace.";
        if (p.Annulus is not null)
            return $"Port {n} is a coaxial lumped port. EMerge's lumped port drives one uniform field direction across its sheet, " +
                   "and a coaxial annulus is fed radially, which it cannot state. Run the setup on Palace or openEMS, or draw a " +
                   "rectangular port.";
        return null;
    }

    private static string? PortRefusal(Em3dPort p)
    {
        int n = p.SourceNumber ?? p.Number;
        if (p.Kind == Em3dPortKind.Wave)
            return p.Z0.Imaginary != 0 || !(p.Z0.Real > 0)
                ? $"Port {n}'s reference impedance is {Ohms(p.Z0)}. EMerge's wave port refers its S-parameters to the mode, and " +
                  "circuitRF renormalises them to the port's Z0, which must be real and positive: set the port's Z0 to a positive real value."
                : null;
        if (p.Z0.Imaginary != 0 || !(p.Z0.Real > 0))
            return $"Port {n}'s reference impedance is {Ohms(p.Z0)}. EMerge's lumped port is terminated in a real resistance, " +
                   "which must be positive: set the port's Z0 to a positive real value.";
        if (Rect(p.Min, p.Max, p.Direction) is null)
            return $"Port {n}'s direction is not along a side of its sheet, so its height (the side the voltage is across) " +
                   "cannot be told to EMerge. Draw the port with its direction along one of the sheet's sides.";
        return null;
    }

    /// <summary>Why EMerge cannot state <paramref name="le"/>, or null when it can. EMerge takes an element as a surface
    /// impedance Z(f)·width/height over its sheet, so each value must be finite and the impedance neither zero nor
    /// infinite at a frequency of the run.</summary>
    public static string? Inexpressible(Em3dLumpedElement le)
    {
        string who = $"Lumped element '{le.Name}'";
        if (le.R is null && le.L is null && le.C is null)
            return $"{who} states no R, L or C: an element with none is nothing. Give it a value.";
        foreach (var (key, v) in new[] { ("R", le.R), ("L", le.L), ("C", le.C) })
            if (v is { } x && (!double.IsFinite(x) || x < 0))
                return $"{who}'s {key} is {G(x)}: a lumped value is a finite number of 0 or more.";
        if (Rect(le.Min, le.Max, le.Direction) is null)
            return $"{who}'s direction is not along a side of its sheet, so its height cannot be told to EMerge. Draw it with " +
                   "its direction along one of the sheet's sides.";
        if (le.Topology == Em3dRlcTopology.Parallel)
        {
            if (le.R == 0 || le.L == 0)
                return $"{who} is parallel with {(le.R == 0 ? "R" : "L")} = 0, a short circuit, which EMerge's surface impedance " +
                       "cannot be (it divides by it). Draw a conductor for a short, or remove the zero value.";
        }
        else
        {
            if (le.C == 0)
                return $"{who} is series with C = 0, an open circuit, which EMerge's surface impedance cannot be. Remove C, or give it a value.";
            if ((le.R ?? 0) == 0 && (le.L ?? 0) == 0 && le.C is null)
                return $"{who} is series with nothing but a zero R or L, a short circuit, which EMerge's surface impedance cannot " +
                       "be. Draw a conductor for a short.";
        }
        return null;
    }

    /// <summary>A conductor whose σ is too low for EMerge to treat as one, or a dielectric whose σ is high enough that it
    /// would; null when every material is on its own side of EMerge's limit.</summary>
    private static string? MaterialRefusal(Em3dProblem problem)
    {
        var byName = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        foreach (var s in problem.Solids)
        {
            double sigma = byName[s.Material].SigmaSm;
            if (s.Role == Em3dRole.Conductor && sigma > 0 && double.IsFinite(sigma) && sigma <= SurfaceImpedanceLimitSm)
                return $"Conductor '{s.Name}' is {s.Material}, σ {G(sigma)} S/m, at or below EMerge's surface-impedance limit of " +
                       $"{G(SurfaceImpedanceLimitSm)} S/m: EMerge would solve it as a lossy dielectric, not as the conductor Palace " +
                       "and openEMS treat it as. Give it a conductivity above that, or make it a dielectric with that loss.";
            if (s.Role != Em3dRole.Conductor && sigma > SurfaceImpedanceLimitSm)
                return $"'{s.Name}' is a {(s.Role == Em3dRole.Air ? "fill" : "dielectric")} of {s.Material}, σ {G(sigma)} S/m, above " +
                       $"EMerge's surface-impedance limit of {G(SurfaceImpedanceLimitSm)} S/m: EMerge would treat it as a conductor. " +
                       "Make it a conductor, or give it the dielectric's own loss.";
        }
        foreach (var sh in problem.Sheets)
        {
            double sigma = byName[sh.Material].SigmaSm;
            if (sigma > 0 && double.IsFinite(sigma) && sigma <= SurfaceImpedanceLimitSm)
                return $"Sheet '{sh.Name}' is {sh.Material}, σ {G(sigma)} S/m, at or below EMerge's surface-impedance limit of " +
                       $"{G(SurfaceImpedanceLimitSm)} S/m, where EMerge puts no boundary on a surface at all. Give it a conductivity " +
                       "above that, or a perfect conductor.";
        }
        return null;
    }

    /// <summary>
    /// A port or element sheet as EMerge's lumped boundary takes it: an origin and two side vectors spanning the rectangle,
    /// and its width and height — the height being its extent along <paramref name="direction"/>, which must lie along one
    /// of its two sides (to 1e-9). Null when it does not.
    /// </summary>
    public static (Point3 Origin, Point3 U, Point3 V, double Width, double Height)? Rect(Point3 min, Point3 max, Point3 direction)
    {
        double[] d = [max.X - min.X, max.Y - min.Y, max.Z - min.Z];
        double[] dir = [direction.X, direction.Y, direction.Z];
        int flat = Array.IndexOf(d, d.Min());
        int a = flat == 0 ? 1 : 0, b = flat == 2 ? 1 : 2;
        static Point3 Along(int axis, double length) => axis switch
        {
            0 => new Point3(length, 0, 0),
            1 => new Point3(0, length, 0),
            _ => new Point3(0, 0, length),
        };
        double tol = 1e-9;
        bool alongA = Math.Abs(Math.Abs(dir[a]) - 1) <= tol && Math.Abs(dir[b]) <= tol && Math.Abs(dir[flat]) <= tol;
        bool alongB = Math.Abs(Math.Abs(dir[b]) - 1) <= tol && Math.Abs(dir[a]) <= tol && Math.Abs(dir[flat]) <= tol;
        if (!alongA && !alongB) return null;
        return (min, Along(a, d[a]), Along(b, d[b]), alongA ? d[b] : d[a], alongA ? d[a] : d[b]);
    }

    /// <summary>
    /// brief-emw-2 — a wave port's region as the driver takes it: an origin and two side vectors spanning the rectangle on its
    /// face, and its width and height (the two sides' lengths, in that order).
    /// </summary>
    public static (Point3 Origin, Point3 U, Point3 V, double Width, double Height) Region(Point3 min, Point3 max)
    {
        double[] d = [max.X - min.X, max.Y - min.Y, max.Z - min.Z];
        int flat = Array.IndexOf(d, d.Min());
        int a = flat == 0 ? 1 : 0, b = flat == 2 ? 1 : 2;
        static Point3 Along(int axis, double length) => axis switch
        {
            0 => new Point3(length, 0, 0),
            1 => new Point3(0, length, 0),
            _ => new Point3(0, 0, length),
        };
        return (min, Along(a, d[a]), Along(b, d[b]), d[a], d[b]);
    }

    /// <summary>brief-emw-2 R-emw2-1 — the materials meeting a wave port's region, by the Engine's own sampling of the face's
    /// cross-section; more than one makes the port's mode solve per frequency (EMerge's <c>mixed_materials</c>).</summary>
    public static IReadOnlyList<string> RegionMaterials(Em3dProblem problem, Em3dPort p)
        => FdtdWavePorts.RegionMaterials(problem, p,
                                         problem.Boundary.Material ?? GmshGeoWriter.BackgroundMaterial(problem.Solids, problem.Materials));

    private static string Count(int n) => n switch
    {
        2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string Ohms(Complex z)
        => z.Imaginary == 0 ? $"{G(z.Real)} Ω" : $"{G(z.Real)}{(z.Imaginary < 0 ? "-" : "+")}j{G(Math.Abs(z.Imaginary))} Ω";

    // ── explain's rows ────────────────────────────────────────────────────────────────────────

    /// <summary>R-emi4-4 — every object of <paramref name="problem"/> and what EMerge is told it is, in the input's order.</summary>
    public static IReadOnlyList<EmergeObjectMapping> Mappings(Em3dProblem problem, EmergeSettings settings, double edgeSizeM)
    {
        var rows = new List<EmergeObjectMapping>();
        var byName = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var priority = Priorities(problem);
        foreach (var s in problem.Solids)
        {
            var m = byName[s.Material];
            string what = s.Primitive switch
            {
                Em3dShapeSolid => "the kernel's own B-rep, imported as it is (the same file Palace's Gmsh reads)",
                Em3dExtrudedPolygon => "a polygon extruded",
                Em3dSweep w => w.Section == Em3dSection.Circle ? "a round wire, lofted through its sections" : "a hexagonal wire, lofted through its sections",
                Em3dPolyhedron => "a polyhedron of plane faces",
                Em3dTruncatedSphere => "a sphere cut by a box",
                Em3dEllipsoid => "a unit sphere dilated and turned onto its axes",
                Em3dBox => "a box",
                Em3dCylinder => "a cylinder",
                Em3dSphere => "a sphere",
                _ => "a solid",
            };
            string material = s.Role == Em3dRole.Conductor
                ? IsPerfect(m) ? "a perfect conductor (EMerge's PEC)"
                               : $"a conductor, σ {G(m.SigmaSm)} S/m: surface impedance on its faces, its interior not solved"
                : $"{(s.Role == Em3dRole.Air ? "a fill" : "a dielectric")}, εr {(m.EpsrTensor is { Count: 3 } t ? $"[{G(t[0])}, {G(t[1])}, {G(t[2])}]" : G(m.Epsr))}" +
                  (m.TanD > 0 ? $", tanδ {G(m.TanD)}" : "") + (m.Mur != 1 ? $", μr {G(m.Mur)}" : "") + (m.SigmaSm > 0 ? $", σ {G(m.SigmaSm)} S/m" : "");
            rows.Add(new(s.Name, "solid", $"{what}; {material}; priority {priority[s]} (higher wins where solids overlap)"));
        }
        foreach (var sh in problem.Sheets)
        {
            var m = byName[sh.Material];
            rows.Add(new(sh.Name, "sheet", IsPerfect(m)
                ? "a plate, perfect conductor (EMerge's PEC)"
                : $"a plate of {sh.Material}, σ {G(m.SigmaSm)} S/m, {Mm(sh.ThicknessM)} thick: EMerge's thin-conductor boundary"));
        }
        foreach (var p in problem.Ports)
        {
            if (p.Kind == Em3dPortKind.Wave)
            {
                rows.Add(new(p.Name, "port", WaveMapping(problem, p)));
                continue;
            }
            var r = Rect(p.Min, p.Max, p.Direction)!.Value;
            rows.Add(new(p.Name, "port",
                $"a lumped port, number {p.Number}: {Mm(r.Width)} wide, {Mm(r.Height)} high, voltage along ({G(p.Direction.X)}, " +
                $"{G(p.Direction.Y)}, {G(p.Direction.Z)}) as circuitRF's port direction states it, Z0 {G(p.Z0.Real)} Ω"));
        }
        foreach (var le in problem.LumpedElements)
        {
            var r = Rect(le.Min, le.Max, le.Direction)!.Value;
            string values = string.Join(", ", new[] { ("R", le.R, "Ω"), ("L", le.L, "H"), ("C", le.C, "F") }
                                                 .Where(x => x.Item2 is not null).Select(x => $"{x.Item1} {G(x.Item2!.Value)} {x.Item3}"));
            rows.Add(new(le.Name, "lumped element",
                $"a lumped element ({le.Topology.ToString().ToLowerInvariant()} {values}): its impedance as a function of frequency " +
                $"over a {Mm(r.Width)} × {Mm(r.Height)} sheet"));
        }
        rows.Add(new("air box", "air box", string.Join(", ", FaceKinds(problem).Select(f => $"{f.Key} {FaceMapping(f.Kind)}"))));
        foreach (var region in problem.MeshRegions)
            rows.Add(new(region.Name, "mesh region", $"a box size field: elements at most {Mm(region.SizeM)} inside it"));
        rows.Add(new("mesh", "mesh",
            $"each material at most {G(settings.MaxElementWavelengths)} of its wavelength (EMerge's resolution); {Mm(edgeSizeM)} along " +
            "the edges of conductors and sheets, growing by 3 away from them (EMerge's own rate); a port is not refined" +
            (problem.HasWavePorts ? "; a wave port's region is meshed to about 50 triangles, as EMerge meshes a port face" : "")));
        return rows;
    }

    /// <summary>brief-emw-2 R-emw2-5 — explain's row for a wave port of one terminal.</summary>
    private static string WaveMapping(Em3dProblem problem, Em3dPort p)
    {
        var r = Region(p.Min, p.Max);
        bool mixed = RegionMaterials(problem, p).Count > 1;
        string path = p.VoltagePath is { } vp
            ? $"voltage path from ({Mm3(vp.From)}) to ({Mm3(vp.To)}), negative object to positive"
            : "no voltage path";
        double shift = p.ReferencePlane.ShiftM;
        return $"a wave port, number {p.Number}: EMerge's full wave-port condition on the {problem.FaceOf(p.Min, p.Max)} face's " +
               $"{G(r.Width * 1e3)} × {G(r.Height * 1e3)} mm region, its mode solved at every frequency (the condition is built " +
               "from it) " + (mixed ? "on a face of several materials, so its impedance varies with frequency"
                                    : "on a face of one material, so its impedance is constant") +
               $"; {path}; renormalised from the mode's impedance to {G(p.Z0.Real)} Ω" +
               (shift != 0 ? $"; reference plane {Mm(Math.Abs(shift))} {(shift > 0 ? "into the structure" : "out of it")}" : "");
    }

    private static string Mm3(Point3 q)
    {
        static string C(double m) => G(Math.Abs(m) < 1e-12 ? 0 : m * 1e3);     // a coordinate's round-off reads as 0
        return $"{C(q.X)}, {C(q.Y)}, {C(q.Z)} mm";
    }

    private static string FaceMapping(Em3dBoundaryKind kind) => kind switch
    {
        Em3dBoundaryKind.Pec => "PEC (EMerge's default outer boundary)",
        Em3dBoundaryKind.Pmc => "PMC",
        Em3dBoundaryKind.Absorbing => "absorbing boundary (second order)",
        _ => kind.ToString(),
    };

    /// <summary>A conductor's material that is a PERFECT one: no finite positive σ, as Palace reads it.</summary>
    public static bool IsPerfect(Em3dMaterial m) => !(m.SigmaSm > 0) || double.IsInfinity(m.SigmaSm);

    /// <summary>
    /// Each solid's EMerge priority, 1 … N: its rank by circuitRF's precedence (metal over dielectric, then construction
    /// order; em-3d.md §6.3a), ties by problem order. EMerge gives a region two solids share to the higher priority, so the
    /// ranks state "later wins" exactly; the air box is 0, below every solid.
    /// </summary>
    public static IReadOnlyDictionary<Em3dSolid, int> Priorities(Em3dProblem problem)
    {
        var precedence = Em3dPrecedence.Of(problem);
        var ranked = problem.Solids.Select((s, i) => (s, i)).OrderBy(x => precedence.Of(x.s)).ThenBy(x => x.i).ToList();
        var priority = new Dictionary<Em3dSolid, int>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < ranked.Count; k++) priority[ranked[k].s] = k + 1;
        return priority;
    }
}
