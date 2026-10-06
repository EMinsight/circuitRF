// brief-em3d-120 R-em3d120-2/-3 — the FDTD grid in cylindrical coordinates about one world axis.
//
// openEMS has ONE grid per run, and a cylindrical one puts a round conductor exactly on its circles: 113-a measured the
// 3D Connector's coax within 0.5 Ω and 1° only there (src/Design/RESOLVED.md § "brief-em3d-120" has this brief's own
// spike behind every choice below). The setup asks for it explicitly (C1); nothing here infers it.
//
// THE DOMAIN (C2) is the cylinder inscribed in the air box's cross-section about the axis: the box's two faces normal to
// the axis are the end faces, its four side faces become one outer radius ρ_max and must agree on one boundary kind.
// THE AXIS (C3): ρ starts at the surface of a metal solid that contains the axis along the whole box — the upstream
// examples' construction — and otherwise at 0, where openEMS treats the axis itself (§2b measured that correct, and 25×
// slower: the time step there is set by the innermost ring's arc).
// THE LINES: ρ — a required line at every coaxial radius, extremes for everything else, merged and filled by the
// Cartesian build's own Merge and Fill; α — uniform over a full 2π, §2c's rule (R-em3d120-1c); z — the Cartesian build's
// per-axis build along the world axis, so wave-port feeds and every face land exactly as they would on a Cartesian grid.
//
// openEMS's frame on such a grid has its z along the axis and x, y across it, so a world point (w_u, w_v, w_a) is written
// at (w_u − o_u, w_v − o_v, w_a) with (u, v, a) a cyclic order of (x, y, z): a rotation, never a mirror.

using System.Globalization;

namespace CircuitRF.Engine.Em3d;

/// <summary>
/// brief-em3d-120 — a cylindrical grid: the world axis it is round about, a point the axis passes through, where ρ starts and
/// ends and why, and the azimuth count and why. The mapping to openEMS's own frame is here, so the writer, the probes and the
/// overlay agree on it.
/// </summary>
/// <param name="RhoMinM">0 when the grid starts on the axis.</param>
/// <param name="RhoMinSetBy">The metal solid whose surface ρ starts on, or null on the axis.</param>
/// <param name="RhoMaxKind">The boundary at ρ_max — the side faces' common kind.</param>
/// <param name="SideFaces">The four air-box faces ρ_max replaces.</param>
/// <param name="AzimuthCells">Cells round the full circle (the written α lines are one more: 0 and 2π both).</param>
/// <param name="SmallestArcM">ρ·Δα at the innermost ring (the first dual radius when ρ starts on the axis).</param>
public sealed record FdtdCylinder(
    FdtdAxis              Axis,
    Point3                Origin,
    double                RhoMinM,
    string?               RhoMinSetBy,
    double                RhoMaxM,
    Em3dBoundaryKind      RhoMaxKind,
    IReadOnlyList<string> SideFaces,
    int                   AzimuthCells,
    string                AzimuthSetBy,
    double                SmallestArcM,
    double                SmallestArcAtM)
{
    /// <summary>The world axis index (0 x, 1 y, 2 z) the grid is round about, and the two across it, in cyclic order.</summary>
    public int A => (int)Axis;
    public int U => (A + 1) % 3;
    public int V => (A + 2) % 3;

    /// <summary>A world point in openEMS's Cartesian frame on this grid (what a <c>CoordSystem="0"</c> primitive states).</summary>
    public Point3 ToCsx(Point3 w) => new(Get(w, U) - Get(Origin, U), Get(w, V) - Get(Origin, V), Get(w, A));

    /// <summary>The openEMS axis (0, 1, 2) a world axis becomes: the axis is openEMS's z, and the cyclic order is kept.</summary>
    public int CsxAxis(int worldAxis) => (worldAxis - A + 5) % 3;

    /// <summary>The shift a world coordinate on <paramref name="worldAxis"/> takes on its way into openEMS's frame.</summary>
    public double Shift(int worldAxis) => worldAxis == A ? 0 : Get(Origin, worldAxis);

    /// <summary>The distance of a world point from the axis.</summary>
    public double RadiusOf(Point3 w) => Math.Sqrt(Sq(Get(w, U) - Get(Origin, U)) + Sq(Get(w, V) - Get(Origin, V)));

    /// <summary>The world point's angle about the axis, from the u direction towards v, in [0, 2π).</summary>
    public double AngleOf(Point3 w)
    {
        double t = Math.Atan2(Get(w, V) - Get(Origin, V), Get(w, U) - Get(Origin, U));
        return t < 0 ? t + 2 * Math.PI : t;
    }

    public bool StartsOnAxis => RhoMinM == 0;

    /// <summary>The axis in a sentence: <c>z through (0 µm, 0 µm)</c>.</summary>
    public string Describe()
        => $"{FdtdGrid.AxisName(Axis)} through ({FdtdGrid.AxisName((FdtdAxis)U)} {FdtdGrid.FormatLength(Get(Origin, U))}, " +
           $"{FdtdGrid.AxisName((FdtdAxis)V)} {FdtdGrid.FormatLength(Get(Origin, V))})";

    internal static double Get(Point3 q, int axis) => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z };
    private static double Sq(double x) => x * x;
}

public static partial class FdtdGrid
{
    private static readonly string[] CylFaceKeys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    /// <summary>
    /// R-em3d120-1c — the azimuth rule's cell count: the arc at the outermost conductor radius no longer than the radial
    /// cell there, rounded up to a multiple of this (so the grid has lines on both world axes across the axis).
    /// </summary>
    public const int AzimuthMultiple = 4;

    private static FdtdGridResult BuildCylindrical(Em3dProblem problem, OpenEmsGridSettings settings, OpenEmsCylindrical cyl,
                                                   long? availableMemoryBytes)
    {
        int a = (int)cyl.Axis, u = (a + 1) % 3, v = (a + 2) % 3;
        var ctx = new Context(problem, settings);
        var wavePorts = FdtdWavePorts.Plan(problem, settings, ctx.MinCell);
        if (wavePorts.Feeds.Count > 0)
        {
            problem = FdtdWavePorts.WithFeedFacesAbsorbing(problem, wavePorts);
            ctx = new Context(problem, settings);
        }
        var merges = new List<FdtdMerge>();
        var warnings = new List<string>();
        var box = problem.Boundary;
        double span = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        double tol = CoincidenceTolerance(span);
        double ou = FdtdCylinder.Get(cyl.Origin, u), ov = FdtdCylinder.Get(cyl.Origin, v);
        var kinds = new[] { box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax };
        string axisText = $"{AxisName(cyl.Axis)} through ({AxisName((FdtdAxis)u)} {FormatLength(ou)}, {AxisName((FdtdAxis)v)} {FormatLength(ov)})";

        // ── C2: the domain, and what a cylindrical grid cannot state ──────────────────────────────
        string[] sides = [CylFaceKeys[2 * u], CylFaceKeys[2 * u + 1], CylFaceKeys[2 * v], CylFaceKeys[2 * v + 1]];
        var sideKinds = sides.Select(f => kinds[Array.IndexOf(CylFaceKeys, f)]).ToList();
        double rhoMax = Math.Min(Math.Min(ou - FdtdCylinder.Get(box.Min, u), FdtdCylinder.Get(box.Max, u) - ou),
                                 Math.Min(ov - FdtdCylinder.Get(box.Min, v), FdtdCylinder.Get(box.Max, v) - ov));
        string? refusal = null;
        if (sideKinds.Distinct().Count() > 1)
            refusal = $"A cylindrical grid about {axisText} replaces the air box's {string.Join(", ", sides)} faces by one outer radius, " +
                      $"and they are {string.Join(", ", sides.Select((f, i) => $"{f} {sideKinds[i]}"))}. Give the four one boundary kind in " +
                      "the setup's AirBox, or set OpenEms.Grid to Cartesian.";
        else if (!(rhoMax > tol))
            refusal = $"The cylindrical grid's axis, {axisText}, does not pass through the air box's cross-section, so there is no " +
                      "circle to inscribe in it. Set OpenEms.AxisOriginUm to a point inside the box.";
        refusal ??= CylindricalPorts(problem, wavePorts, cyl, axisText, ou, ov, tol);
        if (refusal is null)
            foreach (var s in problem.Solids)
            {
                if (s.Role is Em3dRole.Air or Em3dRole.Conductor) continue;
                var (_, rmax) = RadialRange(s.Primitive, a, u, v, ou, ov, tol);
                if (rmax > rhoMax + tol)
                {
                    refusal = $"'{s.Name}' reaches {FormatLength(rmax)} from the axis, past the cylindrical grid's outer radius " +
                              $"{FormatLength(rhoMax)} (the circle inscribed in the air box's cross-section about {axisText}). Only " +
                              "metal may cross it, as a shield ending on the wall does. Widen the air box's side faces, or set " +
                              "OpenEms.Grid to Cartesian.";
                    break;
                }
            }

        // ── C3: where ρ starts ────────────────────────────────────────────────────────────────────
        var (rhoMin, rhoMinBy) = AxisMetal(problem, a, u, v, ou, ov, tol);

        // ── ρ: required lines at every coaxial radius; extremes for the rest ──────────────────────
        var required = new List<FdtdRequiredLine>
        {
            new(rhoMin, true, [new FdtdLineSource(rhoMinBy ?? "axis", rhoMinBy is null ? FdtdLineKind.AirBoxFace : FdtdLineKind.MetalEdge, rhoMin)]),
            new(rhoMax, true, [new FdtdLineSource(Em3dAirBox.FaceName("ρmax"), FdtdLineKind.AirBoxFace, rhoMax)]),
        };
        var radial = new List<(double Lo, double Hi, double Index)>();
        var conductorRadii = new List<(double R, string Name)>();
        if (rhoMinBy is not null) conductorRadii.Add((rhoMin, rhoMinBy));
        void Want(double r, Em3dSolid s, FdtdLineKind kind)
        {
            if (r > rhoMin + tol && r < rhoMax - tol) required.Add(new FdtdRequiredLine(r, false, [new FdtdLineSource(s.Name, kind, r)]));
        }
        var shapes = ctx.Shapes.ToDictionary(sh => sh.Name, StringComparer.Ordinal);
        // A polygon's circle is known only to its vertices' rounding: it takes an exact radius (a cylinder's) within a
        // thousandth of it, rather than putting a second line a nanometre from that one.
        var exactRadii = problem.Solids.SelectMany(s => CoaxialRadii(s.Primitive, a, u, v, ou, ov, tol, exactOnly: true)).ToList();
        double Exact(double r) => exactRadii.Where(x => Math.Abs(x - r) <= 1e-3 * r).DefaultIfEmpty(r).MinBy(x => Math.Abs(x - r));
        foreach (var s in problem.Solids)
        {
            bool metal = s.Role == Em3dRole.Conductor;
            var exact = CoaxialRadii(s.Primitive, a, u, v, ou, ov, tol).Select(Exact).ToList();
            foreach (double r in exact)
            {
                Want(r, s, metal ? FdtdLineKind.MetalEdge : FdtdLineKind.MaterialFace);
                if (metal && r <= rhoMax + tol) conductorRadii.Add((r, s.Name));
            }
            var (lo, hi) = RadialRange(s.Primitive, a, u, v, ou, ov, tol);
            if (exact.Count == 0)
            {
                Want(lo, s, metal ? FdtdLineKind.MetalExtreme : FdtdLineKind.MaterialFace);
                Want(hi, s, metal ? FdtdLineKind.MetalExtreme : FdtdLineKind.MaterialFace);
            }
            if (!metal && s.Role != Em3dRole.Air && shapes.TryGetValue(s.Name, out var sh)) radial.Add((lo, hi, sh.Index));
        }
        foreach (var sh in problem.Sheets)
        {
            var (x0, y0, z0, x1, y1, z1) = sh.WorldBounds();
            var (lo, hi) = RectRadial((x0, y0, z0, x1, y1, z1), u, v, ou, ov);
            foreach (double r in new[] { lo, hi })
                if (r > rhoMin + tol && r < rhoMax - tol)
                    required.Add(new FdtdRequiredLine(r, false, [new FdtdLineSource(sh.Name, FdtdLineKind.MetalExtreme, r)]));
        }
        double MaxRadialCell(double lo, double hi)
        {
            double n = 1;
            foreach (var (r0, r1, index) in radial)
                if (r0 < hi - tol && r1 > lo + tol) n = Math.Max(n, index);
            return C0 / (problem.Frequency.StopHz * n * settings.CellsPerWavelength);
        }
        var rhoRequired = Merge(required, FdtdAxis.Rho, ctx.MinCell, merges, warnings);
        var rhoLines = Fill(rhoRequired, iv => MaxRadialCell(iv.Lo, iv.Hi), settings.GradingRatio, 0,
                            sideKinds[0] == Em3dBoundaryKind.Absorbing ? settings.PmlCells : 0);
        var rho = Describe(FdtdAxis.Rho, rhoLines, rhoRequired, rhoMin, rhoMax);

        // ── α: §2c's rule, or the setup's count ───────────────────────────────────────────────────
        int cells;
        string azimuthBy;
        if (cyl.AzimuthLines is { } stated)
        {
            cells = stated - 1;
            azimuthBy = $"OpenEms.AzimuthLines ({stated} lines, {cells} cells)";
        }
        else
        {
            var (rOut, rOutBy) = conductorRadii.Count > 0 ? conductorRadii.MaxBy(c => c.R) : (rhoMax, Em3dAirBox.FaceName("ρmax"));
            int i = rhoLines.FindIndex(x => Math.Abs(x - rOut) <= tol);
            double cell = i > 0 ? rhoLines[i] - rhoLines[i - 1] : rhoLines[1] - rhoLines[0];
            cells = (int)Math.Ceiling(2 * Math.PI * rOut / cell - 1e-9);
            cells = Math.Max(2 * AzimuthMultiple, (cells + AzimuthMultiple - 1) / AzimuthMultiple * AzimuthMultiple);
            azimuthBy = $"the arc at the outermost conductor radius ('{rOutBy}', {FormatLength(rOut)}) no longer than the radial cell " +
                        $"there ({FormatLength(cell)}), rounded up to a multiple of {AzimuthMultiple}: {cells} cells";
        }
        var alphaLines = new List<double>(cells + 1);
        for (int k = 0; k < cells; k++) alphaLines.Add(2 * Math.PI * k / cells);
        alphaLines.Add(2 * Math.PI);
        double first = rhoMin > 0 ? rhoMin : (rhoLines[0] + rhoLines[1]) / 2;
        double arc = first * 2 * Math.PI / cells;
        var arcBy = new FdtdLineSource(rhoMinBy ?? "axis", rhoMinBy is null ? FdtdLineKind.AirBoxFace : FdtdLineKind.MetalEdge, rhoMin);
        var alpha = new FdtdAxisGrid(FdtdAxis.Alpha, alphaLines,
                                     [new FdtdRequiredLine(0, true, [arcBy]), new FdtdRequiredLine(2 * Math.PI, true, [arcBy])],
                                     arc, first, [arcBy], 0, 0);

        // ── z: the Cartesian build along the world axis, feeds and all ───────────────────────────
        var axial = (FdtdAxis)a;
        var zRequired = Merge([.. CollectRequired(problem, axial, settings, ctx.MinCell), .. wavePorts.ExtraLines(axial)], axial, ctx.MinCell,
                              merges, warnings);
        var (zLo, zHi) = ctx.Faces(axial);
        var zLines = Fill(zRequired, iv => ctx.MaxCell(axial, iv.Lo, iv.Hi), settings.GradingRatio,
                          zLo == Em3dBoundaryKind.Absorbing ? settings.PmlCells : 0, zHi == Em3dBoundaryKind.Absorbing ? settings.PmlCells : 0);
        var z = Describe(axial, zLines, zRequired, ctx.BoxMin(axial), ctx.BoxMax(axial));

        // The Courant limit on the smallest arc (R-em3d120-3), in the medium every cell of the innermost ring lies in: openEMS's
        // time step is local to the cell (method 3; and on the axis method 1, which reads each cell's material too).
        double index = InnermostIndex(problem, a, u, v, ou, ov, first, tol);
        long cellsTotal = checked((long)rho.Lines.Count * cells * z.Lines.Count);
        double dt = index * CourantTimeStep(rho.SmallestCellM, arc, z.SmallestCellM);
        double pulse = ExcitationLength(problem.Frequency);
        long steps = (long)Math.Ceiling(pulse * (1 + RingDownPulses) / dt);
        long memory = Em3dSizeEstimate.OpenEmsMemoryBytes(cellsTotal);
        long limit = availableMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var frame = new FdtdCylinder(cyl.Axis, cyl.Origin, rhoMin, rhoMinBy, rhoMax, sideKinds[0], sides, cells, azimuthBy, arc, first);
        var result = new FdtdGridResult(rho, alpha, z, ctx.MinCell, cellsTotal, dt, pulse, steps, memory, merges, warnings, refusal)
                     { WavePorts = wavePorts, Cylinder = frame };
        if (rhoMinBy is null && refusal is null)
            warnings.Add($"No metal solid contains the cylindrical grid's axis ({axisText}) along the whole box, so the grid starts ON " +
                         "the axis and openEMS treats it there. That is correct, and slow: the innermost ring's arc sets the time step " +
                         $"({FormatLength(arc)} here). A coax measured the same with ρ starting at its pin's surface, at a 25× larger time step.");
        return refusal is null && memory > limit ? result with { Refusal = MemoryRefusal(result, limit) } : result;
    }

    /// <summary>The wave and lumped ports a cylindrical grid cannot state (R-em3d120-5), or null.</summary>
    private static string? CylindricalPorts(Em3dProblem problem, FdtdWavePortPlan plan, OpenEmsCylindrical cyl, string axisText,
                                            double ou, double ov, double tol)
    {
        int a = (int)cyl.Axis;
        if (problem.Ports.FirstOrDefault(p => p.Kind == Em3dPortKind.Lumped) is { } lumped)
            return $"Port {lumped.SourceNumber ?? lumped.Number} is a lumped port, and circuitRF writes openEMS's lumped port on a Cartesian grid only. " +
                   $"On a cylindrical grid make it a wave port on a face the axis crosses ({AxisName(cyl.Axis)}min or {AxisName(cyl.Axis)}max), " +
                   "or set OpenEms.Grid to Cartesian.";
        if (plan.Refusal is not null) return null;              // the writer says it, in the plan's words
        foreach (var feed in plan.Feeds)
        {
            if ((int)feed.Axis != a)
                return $"{string.Join(", ", feed.Ports.Select(x => $"'{x}'"))} {(feed.Ports.Count == 1 ? "is" : "are")} on the {feed.Face} face, " +
                       $"which a cylindrical grid about {axisText} replaces by its outer radius. It feeds wave ports on its two end " +
                       $"faces only ({AxisName(cyl.Axis)}min, {AxisName(cyl.Axis)}max); set OpenEms.Axis to {AxisName(feed.Axis).ToUpperInvariant()}, " +
                       "or OpenEms.Grid to Cartesian.";
            foreach (var t in feed.Terminals)
            {
                if (t.Shape != FdtdTerminalShape.Coaxial)
                    return $"Terminal {t.Label} on the {feed.Face} face is not coaxial (openEMS reads it as {ShapeName(t.Shape)}), and a " +
                           $"cylindrical grid about {axisText} feeds a coaxial terminal only: the axis is not coaxial with that wave port's " +
                           "conductors. A cylindrical grid suits a problem round about one axis; set OpenEms.Grid to Cartesian.";
                double cu = t.Centre(t.U), cv = t.Centre(t.V);
                double du = (t.U == (a + 1) % 3 ? cu - ou : cv - ou), dv = (t.U == (a + 1) % 3 ? cv - ov : cu - ov);
                double off = Math.Sqrt(du * du + dv * dv);
                if (off > Math.Max(tol, 1e-6 * t.InnerRadiusM))
                    return $"Terminal {t.Label}'s conductor on the {feed.Face} face is centred {FormatLength(off)} off the cylindrical grid's " +
                           $"axis ({axisText}): the axis is not coaxial with that wave port's conductors. Set OpenEms.AxisOriginUm to the " +
                           "conductor's centre, or set OpenEms.Grid to Cartesian.";
            }
        }
        return null;
    }

    private static string ShapeName(FdtdTerminalShape s) => s switch
    {
        FdtdTerminalShape.Stripline  => "a strip between two reference planes",
        FdtdTerminalShape.Microstrip => "a strip over one reference plane",
        _                            => "a line fed along its voltage path",
    };

    /// <summary>
    /// C3 — the radius ρ starts at: the smallest, along the box, of the largest radius a metal solid containing the axis has
    /// there (a coaxial cylinder's own; a box's inscribed one), with the solid that sets it; (0, null) when some stretch of
    /// the axis inside the box lies in no metal.
    /// </summary>
    private static (double Rho, string? By) AxisMetal(Em3dProblem problem, int a, int u, int v, double ou, double ov, double tol)
    {
        double z0 = FdtdCylinder.Get(problem.Boundary.Min, a), z1 = FdtdCylinder.Get(problem.Boundary.Max, a);
        var pieces = new List<(double Lo, double Hi, double R, string Name)>();
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
            if (Inscribed(s.Primitive, a, u, v, ou, ov, tol) is var (lo, hi, r) && r > tol)
                pieces.Add((Math.Max(lo, z0), Math.Min(hi, z1), r, s.Name));
        if (pieces.Count == 0) return (0, null);
        var cuts = pieces.SelectMany(p => new[] { p.Lo, p.Hi }).Append(z0).Append(z1).Where(x => x >= z0 - tol && x <= z1 + tol)
                         .Distinct().OrderBy(x => x).ToList();
        double best = double.PositiveInfinity;
        string? by = null;
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            if (cuts[i + 1] - cuts[i] <= tol) continue;
            double mid = (cuts[i] + cuts[i + 1]) / 2;
            var here = pieces.Where(p => p.Lo <= mid && p.Hi >= mid).ToList();
            if (here.Count == 0) return (0, null);
            var widest = here.MaxBy(p => p.R);
            if (widest.R < best) { best = widest.R; by = widest.Name; }
        }
        return by is null ? (0, null) : (best, by);
    }

    /// <summary>A metal primitive's stretch along the axis and the radius of the largest circle about the axis inside it, when
    /// it contains the axis: a coaxial cylinder or a box around the axis. Null otherwise.</summary>
    private static (double Lo, double Hi, double R)? Inscribed(Em3dPrimitive p, int a, int u, int v, double ou, double ov, double tol)
    {
        switch (p)
        {
            case Em3dCylinder c when IsCoaxial(c, a, u, v, ou, ov, tol):
            {
                double s0 = FdtdCylinder.Get(c.AxisStart, a), s1 = FdtdCylinder.Get(c.AxisEnd, a);
                return (Math.Min(s0, s1), Math.Max(s0, s1), c.Radius);
            }
            case Em3dBox b:
            {
                double r = Math.Min(Math.Min(ou - FdtdCylinder.Get(b.Min, u), FdtdCylinder.Get(b.Max, u) - ou),
                                    Math.Min(ov - FdtdCylinder.Get(b.Min, v), FdtdCylinder.Get(b.Max, v) - ov));
                return r > tol ? (FdtdCylinder.Get(b.Min, a), FdtdCylinder.Get(b.Max, a), r) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>Whether a cylinder's axis is the grid's: parallel to world axis <paramref name="a"/> and through the origin.</summary>
    public static bool IsCoaxial(Em3dCylinder c, int a, int u, int v, double ou, double ov, double tol)
        => Math.Abs(FdtdCylinder.Get(c.AxisStart, u) - FdtdCylinder.Get(c.AxisEnd, u)) <= tol &&
           Math.Abs(FdtdCylinder.Get(c.AxisStart, v) - FdtdCylinder.Get(c.AxisEnd, v)) <= tol &&
           Math.Abs(FdtdCylinder.Get(c.AxisStart, u) - ou) <= tol && Math.Abs(FdtdCylinder.Get(c.AxisStart, v) - ov) <= tol;

    /// <summary>
    /// The radii about the axis a solid's surface lies exactly on: a coaxial cylinder's; a boolean's coaxial operands
    /// (<see cref="Em3dShapeSolid.Operands"/>); a kernel solid's full cylindrical faces centred on the axis.
    /// </summary>
    private static List<double> CoaxialRadii(Em3dPrimitive p, int a, int u, int v, double ou, double ov, double tol, bool exactOnly = false)
    {
        var r = new List<double>();
        switch (p)
        {
            case Em3dCylinder c when IsCoaxial(c, a, u, v, ou, ov, tol):
                r.Add(c.Radius);
                break;
            // A prism along the axis whose outline or hole is a polygon inscribed in a circle about it (a bore drawn as a
            // many-sided hole): that circle.
            case Em3dExtrudedPolygon e when a == 2 && !exactOnly:
                foreach (var ring in e.Holes.Prepend(e.Outline))
                    if (Circumradius(ring, u, v, ou, ov) is { } rc) r.Add(rc);
                break;
            case Em3dShapeSolid k:
                if (k.Operands is { } ops)
                    foreach (var q in ops.Tools.Select(t => t.Primitive).Prepend(ops.Blank))
                        if (q is Em3dCylinder oc && IsCoaxial(oc, a, u, v, ou, ov, tol)) r.Add(oc.Radius);
                foreach (var f in k.Faces)
                {
                    if (f.Kind != "cylinder" || !(f.MinRadiusM > 0)) continue;
                    double[] lo = [f.Box.X0, f.Box.Y0, f.Box.Z0], hi = [f.Box.X1, f.Box.Y1, f.Box.Z1];
                    double cu = (lo[u] + hi[u]) / 2, cv = (lo[v] + hi[v]) / 2, ru = (hi[u] - lo[u]) / 2, rv = (hi[v] - lo[v]) / 2;
                    double t = Math.Max(tol, 1e-6 * f.MinRadiusM);
                    if (Math.Abs(cu - ou) <= t && Math.Abs(cv - ov) <= t && Math.Abs(ru - f.MinRadiusM) <= t && Math.Abs(rv - f.MinRadiusM) <= t
                        && !r.Any(x => Math.Abs(x - f.MinRadiusM) <= t))
                        r.Add(f.MinRadiusM);
                }
                break;
        }
        return r;
    }

    /// <summary>The radius of the circle about the axis a ring's vertices all lie on (to a thousandth of it), or null.</summary>
    private static double? Circumradius(IReadOnlyList<Point2> ring, int u, int v, double ou, double ov)
    {
        if (ring.Count < 8) return null;
        double lo = double.PositiveInfinity, hi = 0;
        foreach (var q in ring)
        {
            double du = (u == 0 ? q.X : q.Y) - ou, dv = (v == 1 ? q.Y : q.X) - ov;
            double d = Math.Sqrt(du * du + dv * dv);
            lo = Math.Min(lo, d);
            hi = Math.Max(hi, d);
        }
        return hi > 0 && hi - lo <= 1e-3 * hi ? hi : null;
    }

    /// <summary>The nearest and farthest a primitive reaches from the axis: a coaxial cylinder exactly, anything else by its box.</summary>
    public static (double Lo, double Hi) RadialRange(Em3dPrimitive p, int a, int u, int v, double ou, double ov, double tol)
        => p is Em3dCylinder c && IsCoaxial(c, a, u, v, ou, ov, tol) ? (0, c.Radius) : RectRadial(Em3dProblem.Bounds(p), u, v, ou, ov);

    private static (double Lo, double Hi) RectRadial((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, int u, int v,
                                                     double ou, double ov)
    {
        double[] lo = [b.X0, b.Y0, b.Z0], hi = [b.X1, b.Y1, b.Z1];
        double nu = Math.Max(0, Math.Max(lo[u] - ou, ou - hi[u])), nv = Math.Max(0, Math.Max(lo[v] - ov, ov - hi[v]));
        double fu = Math.Max(Math.Abs(lo[u] - ou), Math.Abs(hi[u] - ou)), fv = Math.Max(Math.Abs(lo[v] - ov), Math.Abs(hi[v] - ov));
        return (Math.Sqrt(nu * nu + nv * nv), Math.Sqrt(fu * fu + fv * fv));
    }

    /// <summary>The refractive index every cell of the innermost ring lies in: the least, along the box, of the densest
    /// dielectric at that radius; 1 where none is.</summary>
    private static double InnermostIndex(Em3dProblem problem, int a, int u, int v, double ou, double ov, double rho, double tol)
    {
        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        double z0 = FdtdCylinder.Get(problem.Boundary.Min, a), z1 = FdtdCylinder.Get(problem.Boundary.Max, a);
        var pieces = new List<(double Lo, double Hi, double N)>();
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && materials.ContainsKey(s.Material)))
        {
            if (Inscribed(s.Primitive, a, u, v, ou, ov, tol) is not var (lo, hi, r) || rho > r + tol) continue;
            var m = materials[s.Material];
            pieces.Add((lo, hi, Math.Sqrt(Math.Max(m.Epsr, 1) * Math.Max(m.Mur, 1))));
        }
        double least = double.PositiveInfinity;
        var cuts = pieces.SelectMany(p => new[] { p.Lo, p.Hi }).Append(z0).Append(z1).Where(x => x >= z0 - tol && x <= z1 + tol)
                         .Distinct().OrderBy(x => x).ToList();
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            if (cuts[i + 1] - cuts[i] <= tol) continue;
            double mid = (cuts[i] + cuts[i + 1]) / 2;
            double n = pieces.Where(p => p.Lo <= mid && p.Hi >= mid).Select(p => p.N).DefaultIfEmpty(1).Max();
            least = Math.Min(least, n);
        }
        return double.IsFinite(least) ? least : 1;
    }
}
