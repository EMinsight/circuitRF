// brief-em3d-50 — a .c3d's drawn bond wires: where each end lands, what the wire resolves to, and the arithmetic the
// Wire tool and the editor's edits share with elaboration.
//
// THE PAD LOOKUP (R-em3d50-1b). An end's pad is the elaborated conductor whose TOP SURFACE contains the end's plan
// point AT THE END'S z, within 1 DBU of the document — a conductor inside an instance included, which is the whole
// point: a die pad is in `U1`, a package lead in the parent. Where tops stack there, the highest wins (series 1's
// rule: a bond lands on exposed metal). A top surface is a box's zmax, an extrusion's top, a vertical cylinder's top
// disc, a polyhedron's upward faces and a flat sheet. No pad is a refusal naming the wire and the end — never a foot
// in mid-air — and it is exactly what elaboration says when the die a wire was bonded to has moved: the wire keeps
// its points, because re-routing it silently would change its inductance.
//
// RE-SEATING is the vertical question only: the highest top surface under an end's PLAN point, and the end moved
// onto it in z. A die moved sideways leaves nothing under the end, and re-seating says so.
//
// RESOLUTION is Em3dWires.Resolve, the SAME function a .wBond's wires go through (R-em3d50-1a), so a drawn wire on
// the .wBond's points is the .wBond's wire (gate 5).
//
// THE LOOP HEIGHT A USER TYPES is the ASSEMBLY one (em-3d.md §6.6): the bottom of the lower foot — the lower pad's
// top — to the top of the wire at its apex. The axis is shaped by LoopShape (wBond's own arithmetic) to an AXIS loop
// height, and the assembly height of the resolved solid is then measured; the axis target is corrected by the
// difference until the two agree to within half a DBU. The first guess is the typed height less half the section's
// height, which is the whole difference for a flat apex; the mitre at the apex and a ball's neck are what the
// correction takes up.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.WBond;
using Point3 = CircuitRF.Engine.Em3d.Point3;
using WPoint3 = CircuitRF.WBond.Point3;

namespace CircuitRF.Design.ThreeD;

/// <summary>One conductor's top surface a wire end can land on, world metres.</summary>
/// <param name="Name">The elaborated conductor's name (<c>U1/pad3</c> inside an instance).</param>
public sealed record C3dWirePad(string Name, PlanarPolygon Poly, double TopM)
{
    public Em3dWirePad ForResolution => new(Name, Poly, TopM);
}

/// <summary>What elaboration made of one drawn wire: the resolution, the process values, and the pads it landed on — or
/// the refusal that stopped it.</summary>
public sealed record C3dWireResult(
    Em3dWireResolution? Resolution, WireBondProcessValues? StartProcess, WireBondProcessValues? EndProcess,
    C3dWirePad? StartPad, C3dWirePad? EndPad, string? Refusal)
{
    public bool Ok => Refusal is null && Resolution is { Ok: true };
}

public static class C3dWires
{
    /// <summary>wBond's own default diameter, 1 mil — what an omitted <see cref="C3dWire.DiameterUm"/> means.</summary>
    public static readonly double DefaultDiameterUm = new Wire().DiameterNm / 1000.0;

    /// <summary>The points the Wire tool's seed arch has (LoopShape's default).</summary>
    public const int SeedPoints = 7;

    /// <summary>The wire's metal: its own, or wBond's default when it states none.</summary>
    public static string MaterialOf(C3dWire w) => w.Material is { Length: > 0 } m ? m : WireMaterials.Default.Name;

    /// <summary>The diameter, nm.</summary>
    public static long DiameterNm(C3dWire w) => (long)Math.Round((w.DiameterUm ?? DefaultDiameterUm) * 1000, MidpointRounding.AwayFromZero);

    public static WireCrossSection SectionOf(C3dWire w) => w.Section ?? WireCrossSection.Hexagon;

    private static long? FootNm(C3dWireEnd e) => e.FootLengthUm is { } f ? (long)Math.Round(f * 1000, MidpointRounding.AwayFromZero) : null;

    // ── pads ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every conductor top surface of <paramref name="solids"/> and <paramref name="sheets"/> — world metres — whose
    /// name starts with <paramref name="prefix"/> (a wire inside an instance lands on its own document's conductors),
    /// leaving out <paramref name="exclude"/> (the wires themselves: a wire does not bond onto a wire).
    /// </summary>
    public static List<C3dWirePad> Pads(IEnumerable<Em3dSolid> solids, IEnumerable<Em3dSheet> sheets, string prefix = "",
                                        ISet<string>? exclude = null)
    {
        var pads = new List<C3dWirePad>();
        bool Take(string name) => name.StartsWith(prefix, StringComparison.Ordinal) && exclude?.Contains(name) != true;
        foreach (var s in solids)
        {
            if (s.Role != Em3dRole.Conductor || !Take(s.Name)) continue;
            switch (s.Primitive)
            {
                case Em3dBox b:
                    pads.Add(new(s.Name, new PlanarPolygon([new(b.Min.X, b.Min.Y), new(b.Max.X, b.Min.Y), new(b.Max.X, b.Max.Y),
                                                            new(b.Min.X, b.Max.Y)]), b.Max.Z));
                    break;
                case Em3dExtrudedPolygon e:
                    pads.Add(new(s.Name, Poly(e.Outline, e.Holes), Math.Max(e.ZTop, e.ZBottom)));
                    break;
                case Em3dCylinder c when c.AxisStart.X == c.AxisEnd.X && c.AxisStart.Y == c.AxisEnd.Y:
                {
                    const int sides = 72;
                    var ring = new List<EmPoint>(sides);
                    for (int k = 0; k < sides; k++)
                    {
                        double a = 2 * Math.PI * k / sides;
                        ring.Add(new(c.AxisStart.X + c.Radius * Math.Cos(a), c.AxisStart.Y + c.Radius * Math.Sin(a)));
                    }
                    pads.Add(new(s.Name, new PlanarPolygon(ring), Math.Max(c.AxisStart.Z, c.AxisEnd.Z)));
                    break;
                }
                case Em3dPolyhedron p:
                    foreach (var f in p.Faces)
                        if (UpwardZ(p.Vertices, f.Outer) is { } z)
                            pads.Add(new(s.Name, new PlanarPolygon([.. f.Outer.Select(i => new EmPoint(p.Vertices[i].X, p.Vertices[i].Y))],
                                                                   [.. f.Holes.Select(h => (IReadOnlyList<EmPoint>)[.. h.Select(i => new EmPoint(p.Vertices[i].X, p.Vertices[i].Y))])]),
                                         z));
                    break;
            }
        }
        foreach (var sh in sheets)
            if (sh.Frame is null && Take(sh.Name)) pads.Add(new(sh.Name, Poly(sh.Outline, sh.Holes), sh.Z));
        return pads;
    }

    private static PlanarPolygon Poly(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
        => new([.. outline.Select(q => new EmPoint(q.X, q.Y))], [.. holes.Select(h => (IReadOnlyList<EmPoint>)[.. h.Select(q => new EmPoint(q.X, q.Y))])]);

    /// <summary>The face's z when it is flat and faces +z; null otherwise.</summary>
    private static double? UpwardZ(IReadOnlyList<Point3> v, IReadOnlyList<int> loop)
    {
        if (loop.Count < 3) return null;
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            var a = v[loop[j]]; var b = v[loop[i]];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(len > 0) || nz / len < 1 - 1e-9) return null;
        double z0 = v[loop[0]].Z;
        foreach (int i in loop) if (Math.Abs(v[i].Z - z0) > 1e-12 * Math.Max(1, Math.Abs(z0)) + 1e-15) return null;
        return z0;
    }

    /// <summary>Whether (x, y) is inside <paramref name="poly"/>, or within <paramref name="tol"/> of its boundary.</summary>
    public static bool Covers(PlanarPolygon poly, double x, double y, double tol)
    {
        if (poly.Contains(x, y)) return true;
        bool Near(IReadOnlyList<EmPoint> ring)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double ax = ring[j].X, ay = ring[j].Y, dx = ring[i].X - ax, dy = ring[i].Y - ay;
                double l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1) : 0;
                double ex = ax + t * dx - x, ey = ay + t * dy - y;
                if (ex * ex + ey * ey <= tol * tol) return true;
            }
            return false;
        }
        return Near(poly.Outer) || poly.HoleRings.Any(Near);
    }

    /// <summary>R-em3d50-1b — the pad whose top surface holds <paramref name="p"/>: containing its plan point and at its
    /// z, each within <paramref name="tolM"/>; the highest where several do. Null when none.</summary>
    public static C3dWirePad? PadAt(IEnumerable<C3dWirePad> pads, Point3 p, double tolM)
        => pads.Where(q => Math.Abs(q.TopM - p.Z) <= tolM && Covers(q.Poly, p.X, p.Y, tolM))
               .OrderByDescending(q => q.TopM).FirstOrDefault();

    /// <summary>The first pad top a ray meets (a point on an upward-facing conductor face under the cursor), with where it
    /// meets it; null when it meets none.</summary>
    public static (C3dWirePad Pad, Point3 At)? PadHit(IEnumerable<C3dWirePad> pads, Point3 origin, Point3 direction)
    {
        if (!(direction.Z < 0)) return null;                    // a top is only seen from above
        (C3dWirePad, Point3)? best = null;
        double bestT = double.PositiveInfinity;
        foreach (var pad in pads)
        {
            double t = (pad.TopM - origin.Z) / direction.Z;
            if (!(t > 0) || t >= bestT) continue;
            double x = origin.X + t * direction.X, y = origin.Y + t * direction.Y;
            if (!pad.Poly.Contains(x, y)) continue;
            bestT = t;
            best = (pad, new Point3(x, y, pad.TopM));
        }
        return best;
    }

    /// <summary>Re-seating's question: the highest top surface under the plan point, at any height.</summary>
    public static C3dWirePad? PadUnder(IEnumerable<C3dWirePad> pads, double x, double y, double tolM)
        => pads.Where(q => Covers(q.Poly, x, y, tolM)).OrderByDescending(q => q.TopM).FirstOrDefault();

    // ── one wire ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="w"/> resolved under <paramref name="world"/> (its document's metres to the world) against
    /// <paramref name="pads"/>. <paramref name="name"/> is its elaborated name; <paramref name="dbuPerMicron"/> is its
    /// document's and <paramref name="tolM"/> one DBU of the TOP document, in metres.
    /// </summary>
    public static C3dWireResult Resolve(C3dWire w, string name, C3dTransform world, int dbuPerMicron,
                                        IReadOnlyList<C3dWirePad> pads, double tolM, WireBondWorkspace workspace, string unit)
    {
        if (!w.Placement.IsDefault)
            return new(null, null, null, null, null, C3dDiagnostics.WirePlacement(name).Render());
        if (w.Points.Count < 2 || w.Points.Distinct().Count() < 2)
            return new(null, null, null, null, null, $"The wire '{name}' needs at least two distinct points.");
        long dNm = DiameterNm(w);
        if (dNm <= 0) return new(null, null, null, null, null, $"The wire '{name}' has a diameter that is not positive.");

        var axis = w.Points.Select(q => C3dLowering.Apply(world, new Point3(C3dLowering.Metres(q.X, dbuPerMicron),
                                                                            C3dLowering.Metres(q.Y, dbuPerMicron),
                                                                            C3dLowering.Metres(q.Z, dbuPerMicron)))).ToList();
        var startPad = PadAt(pads, axis[0], tolM);
        var endPad   = PadAt(pads, axis[^1], tolM);
        foreach (var (pad, which, q) in new[] { (startPad, "start", axis[0]), (endPad, "end", axis[^1]) })
            if (pad is null)
                return new(null, null, null, startPad, endPad, NoPad(name, which, q, pads, tolM, unit));

        var startProcess = WireBondProcess.Resolve(dNm, FootNm(w.Start), workspace);
        var endProcess   = WireBondProcess.Resolve(dNm, FootNm(w.End), workspace);
        var section = SectionOf(w) == WireCrossSection.Round ? Em3dSection.Circle : Em3dSection.Hexagon;
        long zMin = w.Points.Min(q => q.Z), zMax = w.Points.Max(q => q.Z);
        // The report carries one set of process values: the wedge end's, where there is one (the start's when both).
        var reported = w.Start.Style == BondStyle.Wedge || w.End.Style != BondStyle.Wedge ? startProcess : endProcess;
        var resolution = Em3dWires.Resolve(
            new Em3dWireInput(name, axis, section, dNm * 1e-9, w.Start.Style, w.End.Style, startProcess.FootLength.Nm,
                              endProcess.FootLength.Nm, reported,
                              C3dLowering.Metres(zMax, dbuPerMicron) - C3dLowering.Metres(zMin, dbuPerMicron)),
            startPad!.ForResolution, endPad!.ForResolution);
        return new(resolution, startProcess, endProcess, startPad, endPad, resolution.Refusal);
    }

    /// <summary>R-em3d50-3c — the sentence an end that is on no pad gets: which wire, which end, and what is under it.</summary>
    public static string NoPad(string name, string which, Point3 q, IReadOnlyList<C3dWirePad> pads, double tolM, string unit)
    {
        string where = $"({Length(q.X, unit)}, {Length(q.Y, unit)}, {Length(q.Z, unit)}) {unit}";
        string under = PadUnder(pads, q.X, q.Y, tolM) is { } p
            ? $" '{p.Name}' is under it with its top at {Length(p.TopM, unit)} {unit}: Re-seat Wire Ends moves the end onto it."
            : " Nothing conductive is under it: move the end onto a pad.";
        return $"{name}'s {which} is no longer on a pad: no conductor's top surface is at {where}.{under} A wire is not " +
               "re-routed when what it was bonded to moves, because that would change its inductance.";
    }

    private static string Length(double m, string unit)
    {
        double per = unit switch { "mil" => 25.4e-6, "mm" => 1e-3, "nm" => 1e-9, "in" => 25.4e-3, _ => 1e-6 };
        return (m / per).ToString("0.####", CultureInfo.InvariantCulture);
    }

    // ── re-seating and editing ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d50-3c — <paramref name="w"/> with each end moved in z onto the highest top surface under its plan point,
    /// where there is one. <paramref name="unseated"/> names the ends with nothing under them (left as they were).
    /// The pads are the document's own frame (a wire's points are its document's world points).
    /// </summary>
    public static C3dWire Reseat(C3dWire w, IReadOnlyList<C3dWirePad> pads, int dbuPerMicron, out List<string> unseated)
    {
        var copy = (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(w));
        unseated = [];
        double per = C3dLowering.Metres(1, dbuPerMicron);
        for (int e = 0; e < 2 && copy.Points.Count > 0; e++)
        {
            int i = e == 0 ? 0 : copy.Points.Count - 1;
            var q = copy.Points[i];
            if (PadUnder(pads, C3dLowering.Metres(q.X, dbuPerMicron), C3dLowering.Metres(q.Y, dbuPerMicron), per) is { } pad)
                copy.Points[i] = q with { Z = (long)Math.Round(pad.TopM / per, MidpointRounding.AwayFromZero) };
            else unseated.Add(e == 0 ? "start" : "end");
        }
        return copy;
    }

    /// <summary>
    /// brief-em3d-50 — a wire's placement applied to its points, and the placement cleared: what every operation that
    /// composes into a placement (move, rotate, mirror, duplicate, array, flatten) ends with for a wire, whose points are
    /// where it is. False when a point was not a whole DBU and was rounded. Anything else is left alone (true).
    /// </summary>
    public static bool BakePlacement(C3dObject o)
    {
        if (o is not C3dWire w || w.Placement.IsDefault) return true;
        var t = w.Placement.ToTransform();
        bool exact = true;
        for (int i = 0; i < w.Points.Count; i++)
        {
            var (x, y, z) = t.Apply(w.Points[i]);
            long rx = R(x), ry = R(y), rz = R(z);
            if (Math.Abs(x - rx) > 1e-6 || Math.Abs(y - ry) > 1e-6 || Math.Abs(z - rz) > 1e-6) exact = false;
            w.Points[i] = new C3dPoint3(rx, ry, rz);
        }
        // A mirror reverses the handedness of nothing a wire has: the axis is the shape, so the points are all of it.
        w.Placement = new C3dPlacement();
        return exact;
    }

    private static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    // ── the Wire tool's shape ────────────────────────────────────────────────────────────────────

    /// <summary>LoopShape's seed arch between two feet (DBU) with an AXIS loop height of <paramref name="axisLoopDbu"/>
    /// (the axis's highest point above its lower foot) — wBond's own arithmetic, feet written exactly.</summary>
    public static List<C3dPoint3> Arch(C3dPoint3 start, C3dPoint3 end, long axisLoopDbu, int points = SeedPoints)
    {
        var wire = new Wire();
        LoopShape.Write(wire, new WPoint3(start.X, start.Y, start.Z), new WPoint3(end.X, end.Y, end.Z),
                        LoopShape.Seed(points), Math.Max(0, axisLoopDbu));
        return [.. wire.Points.Select(p => new C3dPoint3(p.X, p.Y, p.Z))];
    }

    /// <summary>The half-height of a section of diameter <paramref name="dNm"/>, DBU.</summary>
    public static double HalfHeightDbu(WireCrossSection section, long dNm, int dbuPerMicron)
        => Em3dWireSection.Of(section == WireCrossSection.Round ? Em3dSection.Circle : Em3dSection.Hexagon, dNm * 1e-9).Height / 2
           / C3dLowering.Metres(1, dbuPerMicron);

    /// <summary>
    /// R-em3d50-3a — the arch whose RESOLVED assembly loop height is <paramref name="assemblyDbu"/>: shaped by
    /// <see cref="Arch"/> to an axis target, measured by <paramref name="measure"/> (the resolved solid's assembly height,
    /// DBU, or null when it does not resolve), and corrected until the two agree to half a DBU. The best arch found is
    /// returned either way, with its measured height.
    /// </summary>
    public static (List<C3dPoint3> Points, double? MeasuredDbu) ForAssemblyHeight(
        C3dPoint3 start, C3dPoint3 end, long assemblyDbu, double halfHeightDbu, Func<List<C3dPoint3>, double?> measure)
    {
        double axis = assemblyDbu - halfHeightDbu;
        List<C3dPoint3>? best = null;
        double? bestMeasured = null;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            var points = Arch(start, end, (long)Math.Round(axis, MidpointRounding.AwayFromZero));
            var m = measure(points);
            if (m is not { } got) return (best ?? points, bestMeasured);
            if (bestMeasured is null || Math.Abs(got - assemblyDbu) < Math.Abs(bestMeasured.Value - assemblyDbu))
                (best, bestMeasured) = (points, got);
            if (Math.Abs(got - assemblyDbu) <= 0.5) break;
            double next = axis + (assemblyDbu - got);
            if (Math.Abs(next - axis) < 0.5) next = axis + Math.Sign(assemblyDbu - got);
            axis = next;
        }
        return (best!, bestMeasured);
    }
}
