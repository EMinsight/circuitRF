// brief-em3d-49 R-em3d49-2 — a .c3d's ports, turned into the neutral problem's Em3dPort list.
//
// PORTS BELONG TO THE DOCUMENT (R-em3d49-2a). Every setup uses all of them; a placed cell's ports are never used
// (overview §1k) — a port may NAME a conductor inside an instance (U1/pad3), which is the parent choosing where its
// signal enters.
//
// POLARITY BY CONTACT (R-em3d49-2b). A lumped port's rectangle has four edges. Each edge — its two ends pulled in by
// two DBU, so a conductor meeting the edge only at a corner does not count — is tested against every elaborated
// conductor (solids of role Conductor, every sheet, and the setup's PEC air-box faces) for contact to within ONE DBU.
// Exactly one pair of OPPOSITE edges must each touch exactly one conductor, the two different; that pair's axis is the
// port's direction. The NEGATIVE end is the one in the setup's ground set (its Ground3D net, a layout instance's
// ground-reference conductors, a PEC air-box face); failing that, the one with the larger elaborated surface. Flip
// swaps them. Stating Positive AND Negative overrides inference entirely: the contact query over all conductors is not
// run (C3dPortContext.InferenceRuns counts it, which is gate 1's counter), and only the two named objects are measured against the
// edges to find which pair they sit on.
//
// A refusal names what was found — which conductor on which edge — because "the port is invalid" gives nothing to fix.
//
// A WAVE PORT lies on an air-box face of the SETUP being run (the box is per setup), checked here because only here is
// there a box. Its two conductors are the ones whose surface meets the rectangle's region of that plane, and its
// voltage path runs across the gap between them at the positive conductor's centre — brief 23's rule (return face to
// signal face along the line's centre) stated for any geometry. A stated VoltagePath is used as stated.
//
// Geometry is metres, read from the elaboration — the model the editor draws and the solver gets. A cylinder is
// measured analytically: its facets stand up to half a percent of its radius inside the true surface, which is far
// more than a DBU.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What one port's inference found (R-em3d49-5c: <c>check</c> reports it, <c>explain</c> walks it).</summary>
/// <param name="Port">The document's record.</param>
/// <param name="Resolved">The neutral port, or null when refused.</param>
/// <param name="Refusal">Why the port cannot be built, naming what was found.</param>
/// <param name="Contacts">Each edge and the conductors that touch it (left, right, bottom, top), for a lumped port whose
/// inference ran; a wave port's candidates under "region".</param>
/// <param name="Reason">Why the negative end is the one it is — or that both ends were stated.</param>
public sealed record C3dPortResult(C3dPort Port, Em3dPort? Resolved, string? Refusal,
                                   IReadOnlyList<(string Edge, IReadOnlyList<string> Objects)> Contacts, string Reason)
{
    /// <summary>The port's label in a sentence: <c>P1</c>, or <c>port 1</c> when it has no name.</summary>
    public string Label => C3dPorts.Label(Port);
}

/// <summary>
/// The conductors of one elaboration, ready to be measured against port edges: tessellated once, bounded, with their
/// surface areas. Build one per elaboration (and air box) and ask it about every port — the editor's port tool asks on
/// every mouse move.
/// </summary>
public sealed class C3dPortContext
{
    internal sealed record Conductor(string Name, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Box,
                                     Em3dTriangleMesh? Mesh, Em3dCylinder? Cylinder, double Area, int Axis = -1, double At = 0);

    internal readonly List<Conductor> Conductors = [];
    internal readonly HashSet<string> Ground;

    /// <summary>The air box the ports are checked against, or null (no setup: wave ports cannot be placed).</summary>
    public Em3dAirBox? Box { get; }

    /// <summary>The document's DBU in metres — the contact tolerance.</summary>
    public double Dbu { get; }

    /// <summary>The document's DBU per micrometre.</summary>
    public int DbuPerMicron { get; }

    /// <summary>Contact queries over all conductors made through this context — gate 1 reads that a port with both ends
    /// stated makes none.</summary>
    public int InferenceRuns { get; internal set; }

    /// <summary>The elaborated content's centre, metres — which side of a lumped port is "into the structure".</summary>
    internal Point3 Centre { get; }

    /// <param name="ground">The ground set: conductors on the setup's Ground3D net and a layout instance's ground-reference
    /// conductors. A PEC air-box face is always in it.</param>
    public C3dPortContext(C3dElaboration elaboration, int dbuPerMicron, Em3dAirBox? box, IEnumerable<string> ground)
    {
        Box = box;
        DbuPerMicron = dbuPerMicron;
        Dbu = C3dLowering.Metres(1, dbuPerMicron);
        Ground = new HashSet<string>(ground, StringComparer.Ordinal);
        foreach (var s in elaboration.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            if (s.Primitive is Em3dCylinder c)
            {
                double len = Math.Sqrt(Sq(c.AxisEnd.X - c.AxisStart.X) + Sq(c.AxisEnd.Y - c.AxisStart.Y) + Sq(c.AxisEnd.Z - c.AxisStart.Z));
                Conductors.Add(new Conductor(s.Name, Em3dProblem.Bounds(c), null, c,
                                             2 * Math.PI * c.Radius * len + 2 * Math.PI * c.Radius * c.Radius));
                continue;
            }
            var mesh = Em3dTessellation.Of(s);
            Conductors.Add(new Conductor(s.Name, Em3dProblem.Bounds(s.Primitive), mesh, null, Area(mesh)));
        }
        foreach (var sh in elaboration.Sheets)
        {
            var mesh = Em3dTessellation.OfSheet(sh);
            // Both of a sheet's faces are surface: a sheet is a conductor thin enough to be one.
            Conductors.Add(new Conductor(sh.Name, sh.WorldBounds(), mesh, null, 2 * Area(mesh)));
        }
        if (box is not null)
        {
            string[] keys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
            Em3dBoundaryKind[] kinds = [box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax];
            for (int k = 0; k < 6; k++)
            {
                if (kinds[k] != Em3dBoundaryKind.Pec) continue;
                int axis = k / 2;
                double at = k % 2 == 0 ? Get(box.Min, axis) : Get(box.Max, axis);
                double[] lo = [box.Min.X, box.Min.Y, box.Min.Z], hi = [box.Max.X, box.Max.Y, box.Max.Z];
                lo[axis] = hi[axis] = at;
                double area = 1;
                for (int a = 0; a < 3; a++) if (a != axis) area *= hi[a] - lo[a];
                string name = Em3dAirBox.FaceName(keys[k]);
                Conductors.Add(new Conductor(name, (lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]), null, null, area, axis, at));
                Ground.Add(name);
            }
        }
        var e = elaboration.Extent() ?? (0, 0, 0, 0, 0, 0);
        Centre = new Point3((e.X0 + e.X1) / 2, (e.Y0 + e.Y1) / 2, (e.Z0 + e.Z1) / 2);
    }

    /// <summary>The distance from segment ab to conductor <paramref name="c"/>, metres.</summary>
    internal double Distance(Conductor c, Point3 a, Point3 b)
    {
        double tol = 2 * Dbu;
        var (x0, y0, z0, x1, y1, z1) = c.Box;
        if (Math.Min(a.X, b.X) > x1 + tol || Math.Max(a.X, b.X) < x0 - tol || Math.Min(a.Y, b.Y) > y1 + tol ||
            Math.Max(a.Y, b.Y) < y0 - tol || Math.Min(a.Z, b.Z) > z1 + tol || Math.Max(a.Z, b.Z) < z0 - tol)
            return double.PositiveInfinity;
        if (c.Axis >= 0)
        {
            // A PEC air-box face: the segment must lie on its plane.
            return Math.Max(Math.Abs(Get(a, c.Axis) - c.At), Math.Abs(Get(b, c.Axis) - c.At));
        }
        if (c.Cylinder is { } cyl) return SegmentToCylinder(a, b, cyl);
        double best = double.PositiveInfinity;
        var m = c.Mesh!;
        foreach (var t in m.Triangles)
        {
            best = Math.Min(best, Geometry3.SegmentTriangle(a, b, m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C]));
            if (best == 0) break;
        }
        return best;
    }

    /// <summary>The distance from segment ab to a solid cylinder — convex along the segment, so a ternary search.</summary>
    private static double SegmentToCylinder(Point3 a, Point3 b, Em3dCylinder c)
    {
        double lo = 0, hi = 1;
        double F(double t) => PointToCylinder(new Point3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t), c);
        for (int i = 0; i < 80; i++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (F(m1) <= F(m2)) hi = m2; else lo = m1;
        }
        return Math.Min(F((lo + hi) / 2), Math.Min(F(0), F(1)));
    }

    private static double PointToCylinder(Point3 p, Em3dCylinder c)
    {
        var ax = new Point3(c.AxisEnd.X - c.AxisStart.X, c.AxisEnd.Y - c.AxisStart.Y, c.AxisEnd.Z - c.AxisStart.Z);
        double len = Math.Sqrt(ax.X * ax.X + ax.Y * ax.Y + ax.Z * ax.Z);
        var u = new Point3(ax.X / len, ax.Y / len, ax.Z / len);
        var d = new Point3(p.X - c.AxisStart.X, p.Y - c.AxisStart.Y, p.Z - c.AxisStart.Z);
        double s = d.X * u.X + d.Y * u.Y + d.Z * u.Z;
        double rx = d.X - s * u.X, ry = d.Y - s * u.Y, rz = d.Z - s * u.Z;
        double dr = Math.Max(0, Math.Sqrt(rx * rx + ry * ry + rz * rz) - c.Radius);
        double da = Math.Max(0, Math.Max(-s, s - len));
        return Math.Sqrt(dr * dr + da * da);
    }

    private static double Area(Em3dTriangleMesh m)
    {
        double sum = 0;
        foreach (var t in m.Triangles)
        {
            var (p, q, r) = (m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C]);
            var n = Geometry3.Cross(Geometry3.Sub(q, p), Geometry3.Sub(r, p));
            sum += Math.Sqrt(Geometry3.Dot(n, n)) / 2;
        }
        return sum;
    }

    internal static double Get(Point3 q, int a) => a == 0 ? q.X : a == 1 ? q.Y : q.Z;
    private static double Sq(double v) => v * v;
}

public static class C3dPorts
{
    /// <summary>A port's label in a sentence: its name, or <c>port N</c>.</summary>
    public static string Label(C3dPort p) => p.Name is { Length: > 0 } n ? n : $"port {p.Number}";

    /// <summary>The name the neutral problem gives port <paramref name="number"/> — the generator's, so a magnetostatic
    /// terminal's <c>Source</c> resolves the same way from either container.</summary>
    public static string ProblemName(int number) => $"port/{number}";

    /// <summary>Every port of <paramref name="doc"/>, resolved — or refused, each with what was found.</summary>
    public static IReadOnlyList<C3dPortResult> Resolve(C3dDocument doc, C3dPortContext context)
    {
        var results = new List<C3dPortResult>(doc.Ports.Count);
        var numbers = new Dictionary<int, C3dPort>();
        foreach (var p in doc.Ports)
        {
            if (p.Number < 1)
                results.Add(Refuse(p, $"{Label(p)} has number {p.Number}; a port is numbered from 1."));
            else if (numbers.TryGetValue(p.Number, out var first))
                results.Add(Refuse(p, $"{Label(p)} and {Label(first)} are both port {p.Number}; each port has its own number."));
            else
                results.Add(Resolve(p, doc.DbuPerMicron, context));
            numbers.TryAdd(p.Number, p);
        }
        return results;
    }

    private static C3dPortResult Refuse(C3dPort p, string why, IReadOnlyList<(string, IReadOnlyList<string>)>? contacts = null)
        => new(p, null, why, contacts ?? [], "");

    /// <summary>One port, resolved against <paramref name="context"/>.</summary>
    public static C3dPortResult Resolve(C3dPort port, int dbuPerMicron, C3dPortContext context)
    {
        string label = Label(port);
        if (!TryParseZ0(port.Z0, out var z0))
            return Refuse(port, $"{label}'s Z0 '{port.Z0}' is not an impedance: write a number of ohms, or a complex one such as 25+j10.");
        if (!(z0.Real > 0))
            return Refuse(port, $"{label}'s Z0 is {port.Z0} Ω; a port's reference impedance has a positive real part.");
        if (port.Rect.Size.U <= 0 || port.Rect.Size.V <= 0)
            return Refuse(port, $"{label}'s rectangle has no area ({port.Rect.Size.U} × {port.Rect.Size.V} DBU).");
        if ((port.Positive is null) != (port.Negative is null))
            return Refuse(port, $"{label} states its {(port.Positive is null ? "Negative" : "Positive")} end only. State both " +
                                "ends, or neither and let what the port touches decide.");

        double M(long v) => C3dLowering.Metres(v, dbuPerMicron);
        double u0 = M(port.Rect.Min.U), v0 = M(port.Rect.Min.V);
        double u1 = M(port.Rect.Min.U + port.Rect.Size.U), v1 = M(port.Rect.Min.V + port.Rect.Size.V);
        double h = M(port.Offset);
        Point3 P(double u, double v) => C3dLowering.OnPlane(port.Plane, u, v, h);
        var corners = new[] { P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1) };
        var min = new Point3(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
        var max = new Point3(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
        int normalAxis = port.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        var (uDir, vDir) = PlaneAxes(port.Plane);

        return port.Kind == Em3dPortKind.Wave
            ? Wave(port, label, z0, context, u0, v0, u1, v1, h, normalAxis, uDir, vDir, min, max, P)
            : Lumped(port, label, z0, context, u0, v0, u1, v1, normalAxis, uDir, vDir, min, max, P);
    }

    // ── lumped ───────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] EdgeNames = ["left", "right", "bottom", "top"];

    private static C3dPortResult Lumped(C3dPort port, string label, Complex z0, C3dPortContext ctx, double u0, double v0,
                                        double u1, double v1, int normalAxis, Point3 uDir, Point3 vDir, Point3 min, Point3 max,
                                        Func<double, double, Point3> P)
    {
        // The four edges, their ends pulled in by two DBU so a corner contact does not count.
        double inset = 2 * ctx.Dbu;
        (Point3, Point3) Edge(double ua, double va, double ub, double vb)
        {
            double du = ub - ua, dv = vb - va, len = Math.Sqrt(du * du + dv * dv);
            double f = len > 2 * inset ? inset / len : 0.5;
            return (P(ua + du * f, va + dv * f), P(ub - du * f, vb - dv * f));
        }
        var edges = new[] { Edge(u0, v0, u0, v1), Edge(u1, v0, u1, v1), Edge(u0, v0, u1, v0), Edge(u0, v1, u1, v1) };

        string negative, positive, reason;
        int pair;                                   // 0: left/right (along u), 1: bottom/top (along v)
        bool negativeOnLow;
        IReadOnlyList<(string, IReadOnlyList<string>)> contacts = [];

        if (port.Positive is { } sp && port.Negative is { } sn)
        {
            // Both stated: inference is not consulted. Only the two named objects are measured, to find the pair they sit on.
            var pc = ctx.Conductors.FirstOrDefault(c => c.Name == sp);
            var nc = ctx.Conductors.FirstOrDefault(c => c.Name == sn);
            if (pc is null || nc is null)
                return Refuse(port, $"{label} names '{(pc is null ? sp : sn)}' as its {(pc is null ? "Positive" : "Negative")} end, " +
                                    "which is no conductor of the elaborated model" +
                                    (ctx.Box is null ? "" : " and no PEC face of the setup's air box") + ".");
            double best = double.PositiveInfinity;
            (pair, negativeOnLow) = (0, true);
            for (int k = 0; k < 2; k++)
                foreach (bool low in new[] { true, false })
                {
                    var (na, nb) = edges[2 * k + (low ? 0 : 1)];
                    var (pa, pb) = edges[2 * k + (low ? 1 : 0)];
                    double d = ctx.Distance(nc, na, nb) + ctx.Distance(pc, pa, pb);
                    if (d < best) (best, pair, negativeOnLow) = (d, k, low);
                }
            (negative, positive) = (sn, sp);
            reason = "both ends are stated";
        }
        else
        {
            ctx.InferenceRuns++;
            double tol = ctx.Dbu;
            var touching = edges.Select(e => ctx.Conductors.Where(c => ctx.Distance(c, e.Item1, e.Item2) <= tol)
                                                           .Select(c => c.Name).ToList()).ToArray();
            contacts = [.. EdgeNames.Select((n, i) => (n, (IReadOnlyList<string>)touching[i]))];
            var valid = new List<int>();
            for (int k = 0; k < 2; k++)
                if (touching[2 * k].Count == 1 && touching[2 * k + 1].Count == 1 && touching[2 * k][0] != touching[2 * k + 1][0])
                    valid.Add(k);
            if (valid.Count != 1)
                return Refuse(port, ContactRefusal(label, touching, valid.Count), contacts);
            pair = valid[0];
            string low = touching[2 * pair][0], high = touching[2 * pair + 1][0];
            var lc = ctx.Conductors.First(c => c.Name == low);
            var hc = ctx.Conductors.First(c => c.Name == high);
            (negative, reason) = Negative(lc, hc, ctx);
            negativeOnLow = negative == low;
            positive = negativeOnLow ? high : low;
        }
        if (port.Flip)
        {
            (negative, positive) = (positive, negative);
            negativeOnLow = !negativeOnLow;
            reason += "; Flip swaps them";
        }

        var axis = pair == 0 ? uDir : vDir;
        var direction = negativeOnLow ? axis : new Point3(-axis.X, -axis.Y, -axis.Z);
        var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var normal = Into(normalAxis, centre, ctx);
        var resolved = new Em3dPort(port.Number, ProblemName(port.Number), positive, negative, min, max, direction, z0,
                                    new Em3dReferencePlane(centre, normal, 0));
        return new C3dPortResult(port, resolved, null, contacts, reason);
    }

    /// <summary>R-em3d49-2b — the refusal, naming what each edge touches.</summary>
    private static string ContactRefusal(string label, List<string>[] touching, int validPairs)
    {
        string Names(List<string> n) => n.Count == 0 ? "nothing" : string.Join(" and ", n.Select(x => $"'{x}'"));
        const string Rule = "A lumped port lies between two conductors, one on each of two opposite edges.";
        if (validPairs > 1)
            return $"{label} touches conductors on all four edges ({Names(touching[0])} left, {Names(touching[1])} right, " +
                   $"{Names(touching[2])} bottom, {Names(touching[3])} top), so which pair it is between is not decided by " +
                   "what it touches; state Positive and Negative.";
        for (int e = 0; e < 4; e++)
            if (touching[e].Count > 1)
                return $"{label}'s {EdgeNames[e]} edge touches both {Names(touching[e])}; an end of a port is one conductor. " +
                       $"{Rule} Shrink the port, or state Positive and Negative.";
        for (int k = 0; k < 2; k++)
            if (touching[2 * k].Count == 1 && touching[2 * k + 1].Count == 1)
                return $"{label} touches {Names(touching[2 * k])} on both its {EdgeNames[2 * k]} and {EdgeNames[2 * k + 1]} edges, so " +
                       $"both of its ends are one conductor. {Rule}";
        // The pair with the most contact says the most about what is missing.
        int best = touching[0].Count + touching[1].Count >= touching[2].Count + touching[3].Count ? 0 : 1;
        var (a, b) = (touching[2 * best], touching[2 * best + 1]);
        if (a.Count + b.Count == 0)
            return $"{label} touches no conductor on any edge. {Rule} Draw it so two opposite edges lie on the metal.";
        var (lo, hi) = a.Count >= b.Count ? (2 * best, 2 * best + 1) : (2 * best + 1, 2 * best);
        return $"{label} touches {Names(touching[lo])} on its {EdgeNames[lo]} edge and nothing on its {EdgeNames[hi]}. {Rule}";
    }

    /// <summary>R-em3d49-2b — which of two ends is negative, and why.</summary>
    private static (string Negative, string Reason) Negative(C3dPortContext.Conductor a, C3dPortContext.Conductor b, C3dPortContext ctx)
    {
        bool ga = ctx.Ground.Contains(a.Name), gb = ctx.Ground.Contains(b.Name);
        if (ga != gb)
        {
            var g = ga ? a : b;
            return (g.Name, $"'{g.Name}' is in the ground set" +
                            (g.Axis >= 0 ? " (a PEC face of the air box)" : ""));
        }
        var (big, small) = a.Area >= b.Area ? (a, b) : (b, a);
        // Both in the larger one's unit, so the two numbers compare at a glance.
        bool mm = big.Area >= 1e-6;
        string Area(double m2) => (mm ? m2 * 1e6 : m2 * 1e12).ToString("0.####", CultureInfo.InvariantCulture) + (mm ? " mm²" : " µm²");
        string why = ga ? "both are in the ground set, and " : "neither is in the ground set, and ";
        return (big.Name, a.Area == b.Area
            ? why + $"their surfaces are equal ({Area(a.Area)}), so the one on the lower edge is negative"
            : why + $"'{big.Name}' has the larger surface ({Area(big.Area)} against {Area(small.Area)})");
    }

    // ── wave ─────────────────────────────────────────────────────────────────────────────────────

    private static C3dPortResult Wave(C3dPort port, string label, Complex z0, C3dPortContext ctx, double u0, double v0,
                                      double u1, double v1, double h, int normalAxis, Point3 uDir, Point3 vDir, Point3 min,
                                      Point3 max, Func<double, double, Point3> P)
    {
        if (ctx.Box is not { } box)
            return Refuse(port, $"{label} is a wave port, and a wave port lies on a face of the setup's air box; with no setup " +
                                "there is no box.");
        string? face = null;
        foreach (bool high in new[] { false, true })
        {
            double at = C3dPortContext.Get(high ? box.Max : box.Min, normalAxis);
            if (Math.Abs(at - h) <= ctx.Dbu) face = "xyz"[normalAxis] + (high ? "max" : "min");
        }
        if (face is null)
        {
            string lo = Fmt(C3dPortContext.Get(box.Min, normalAxis)), hi = Fmt(C3dPortContext.Get(box.Max, normalAxis));
            return Refuse(port, $"{label} is a wave port at {"xyz"[normalAxis]} = {Fmt(h)} µm, which is on neither of the air box's " +
                                $"{"xyz"[normalAxis]} faces ({lo} and {hi} µm) for this setup. A wave port is a region of the box's " +
                                "face; the box is each setup's own, so draw the port on its face, or make it lumped.");
        }

        // The two conductors: whatever meets the rectangle's region of the face.
        string negative, positive, reason;
        IReadOnlyList<(string, IReadOnlyList<string>)> contacts = [];
        var feet = new Dictionary<string, (double U0, double V0, double U1, double V1)>(StringComparer.Ordinal);
        foreach (var c in ctx.Conductors)
            if (Footprint(c, ctx, normalAxis, h, uDir, vDir, u0, v0, u1, v1) is { } fp) feet[c.Name] = fp;

        if (port.Positive is { } sp && port.Negative is { } sn)
        {
            (negative, positive, reason) = (sn, sp, "both ends are stated");
            if (port.VoltagePath is null && (!feet.ContainsKey(sn) || !feet.ContainsKey(sp)))
                return Refuse(port, $"{label} names '{(feet.ContainsKey(sn) ? sp : sn)}', which does not meet its region of the " +
                                    $"{face} face, so no voltage path can be inferred between its ends; state its VoltagePath.");
        }
        else
        {
            ctx.InferenceRuns++;
            var found = feet.Keys.ToList();
            contacts = [("region", (IReadOnlyList<string>)found)];
            if (found.Count > 2 && found.Count(n => !n.StartsWith("airbox/", StringComparison.Ordinal)) == 2)
                found = [.. found.Where(n => !n.StartsWith("airbox/", StringComparison.Ordinal))];
            if (found.Count != 2)
                return Refuse(port, found.Count == 0
                    ? $"{label} is a wave port on the air box's {face} face, and no conductor meets its region: a line must run to " +
                      "the face for its mode to be excited there."
                    : $"{label} is a wave port on the air box's {face} face, and {(found.Count == 1 ? "only " : "")}" +
                      $"{string.Join(", ", found.Select(n => $"'{n}'"))} meet{(found.Count == 1 ? "s" : "")} its region; a wave port's " +
                      "mode runs between two conductors. " + (found.Count > 2 ? "Shrink the region, or state Positive and Negative." : ""), contacts);
            var a = ctx.Conductors.First(c => c.Name == found[0]);
            var b = ctx.Conductors.First(c => c.Name == found[1]);
            (negative, reason) = Negative(a, b, ctx);
            positive = negative == a.Name ? b.Name : a.Name;
        }
        if (port.Flip)
        {
            (negative, positive) = (positive, negative);
            reason += "; Flip swaps them";
        }

        Point3 from, to;
        if (port.VoltagePath is { } vp)
        {
            double M(long v) => C3dLowering.Metres(v, ctx.DbuPerMicron);
            from = P(M(vp.From.U), M(vp.From.V));
            to = P(M(vp.To.U), M(vp.To.V));
        }
        else if (Path(feet[negative], feet[positive]) is var (fu, fv, tu, tv))
        {
            from = P(fu, fv);
            to = P(tu, tv);
        }
        else
            return Refuse(port, $"{label}'s two conductors '{negative}' and '{positive}' overlap across its region of the {face} face, " +
                                "so no straight path runs between them; state its VoltagePath.", contacts);

        var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var inward = new Point3(normalAxis == 0 ? (face.EndsWith("min") ? 1 : -1) : 0, normalAxis == 1 ? (face.EndsWith("min") ? 1 : -1) : 0,
                                normalAxis == 2 ? (face.EndsWith("min") ? 1 : -1) : 0);
        var d = Geometry3.Sub(to, from);
        double len = Math.Sqrt(Geometry3.Dot(d, d));
        var resolved = new Em3dPort(port.Number, ProblemName(port.Number), positive, negative, min, max,
                                    len > 0 ? new Point3(d.X / len, d.Y / len, d.Z / len) : uDir, z0,
                                    new Em3dReferencePlane(centre, inward, 0))
        {
            Kind = Em3dPortKind.Wave,
            VoltagePath = new Em3dSegment(from, to),
        };
        return new C3dPortResult(port, resolved, null, contacts, reason);
    }

    /// <summary>
    /// Where conductor <paramref name="c"/> meets the plane at <paramref name="h"/> inside the rectangle, as a (u, v) box —
    /// or null. Its vertices on the plane and its edges crossing it, clipped to the rectangle (1 DBU of slack).
    /// </summary>
    private static (double U0, double V0, double U1, double V1)? Footprint(C3dPortContext.Conductor c, C3dPortContext ctx, int axis, double h,
                                                                        Point3 uDir, Point3 vDir, double u0, double v0, double u1, double v1)
    {
        double tol = ctx.Dbu;
        var pts = new List<Point3>();
        if (c.Axis >= 0)
        {
            if (c.Axis == axis) return null;       // the port's own face, or its opposite
            // A PEC face perpendicular to the port's: it meets the port's plane along a line.
            var (x0, y0, z0, x1, y1, z1) = c.Box;
            double[] lo = [x0, y0, z0], hi = [x1, y1, z1];
            lo[axis] = hi[axis] = h;
            pts.Add(new Point3(lo[0], lo[1], lo[2]));
            pts.Add(new Point3(hi[0], hi[1], hi[2]));
        }
        else if (c.Cylinder is { } cyl)
        {
            var mesh = Em3dTessellation.Of(new Em3dSolid(c.Name, "", Em3dRole.Conductor, cyl, 0));
            Collect(mesh);
        }
        else Collect(c.Mesh!);

        void Collect(Em3dTriangleMesh m)
        {
            foreach (var t in m.Triangles)
            {
                var tri = new[] { m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C] };
                for (int i = 0; i < 3; i++)
                {
                    var p = tri[i];
                    var q = tri[(i + 1) % 3];
                    double dp = C3dPortContext.Get(p, axis) - h, dq = C3dPortContext.Get(q, axis) - h;
                    if (Math.Abs(dp) <= tol) pts.Add(p);
                    else if ((dp < -tol && dq > tol) || (dp > tol && dq < -tol))
                    {
                        double s = dp / (dp - dq);
                        pts.Add(new Point3(p.X + (q.X - p.X) * s, p.Y + (q.Y - p.Y) * s, p.Z + (q.Z - p.Z) * s));
                    }
                }
            }
        }

        double fu0 = double.PositiveInfinity, fv0 = fu0, fu1 = double.NegativeInfinity, fv1 = fu1;
        foreach (var p in pts)
        {
            double u = Geometry3.Dot(p, uDir), v = Geometry3.Dot(p, vDir);
            if (u < u0 - tol || u > u1 + tol || v < v0 - tol || v > v1 + tol) continue;
            fu0 = Math.Min(fu0, Math.Clamp(u, u0, u1)); fu1 = Math.Max(fu1, Math.Clamp(u, u0, u1));
            fv0 = Math.Min(fv0, Math.Clamp(v, v0, v1)); fv1 = Math.Max(fv1, Math.Clamp(v, v0, v1));
        }
        return double.IsInfinity(fu0) ? null : (fu0, fv0, fu1, fv1);
    }

    /// <summary>The voltage path from the negative footprint to the positive one across the axis they are apart on, at the
    /// positive's centre on the other — or null when they overlap on both.</summary>
    private static (double FU, double FV, double TU, double TV)? Path((double U0, double V0, double U1, double V1) n,
                                                                      (double U0, double V0, double U1, double V1) p)
    {
        double gapV = Math.Max(p.V0 - n.V1, n.V0 - p.V1), gapU = Math.Max(p.U0 - n.U1, n.U0 - p.U1);
        if (gapV <= 0 && gapU <= 0) return null;
        if (gapV >= gapU)
        {
            double u = (p.U0 + p.U1) / 2;
            return p.V0 >= n.V1 ? (u, n.V1, u, p.V0) : (u, n.V0, u, p.V1);
        }
        double v = (p.V0 + p.V1) / 2;
        return p.U0 >= n.U1 ? (n.U1, v, p.U0, v) : (n.U0, v, p.U1, v);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A drawing plane's u and v directions in the world (C3dPlane's note).</summary>
    public static (Point3 U, Point3 V) PlaneAxes(C3dPlane plane) => plane switch
    {
        C3dPlane.YZ => (new Point3(0, 1, 0), new Point3(0, 0, 1)),
        C3dPlane.XZ => (new Point3(1, 0, 0), new Point3(0, 0, 1)),
        _           => (new Point3(1, 0, 0), new Point3(0, 1, 0)),
    };

    /// <summary>The unit normal of a lumped port's plane pointing toward the structure's centre (into the structure).</summary>
    private static Point3 Into(int axis, Point3 centre, C3dPortContext ctx)
    {
        double sign = C3dPortContext.Get(ctx.Centre, axis) >= C3dPortContext.Get(centre, axis) ? 1 : -1;
        return new Point3(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0);
    }

    private static string Fmt(double m) => (m * 1e6).ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>A port's Z0 as the <c>.cem</c> spells a complex impedance: <c>50</c>, <c>25+j10</c>, <c>25-10j</c>, <c>j5</c>.</summary>
    public static bool TryParseZ0(string? text, out Complex z)
    {
        z = default;
        string s = (text ?? "").Replace(" ", "").Replace("Ω", "").Replace("ohm", "", StringComparison.OrdinalIgnoreCase);
        if (s.Length == 0) return false;
        int j = s.IndexOfAny(['j', 'i']);
        if (j < 0) return Real(s, out double re) && Finish(re, 0, out z);
        int split = -1;
        for (int i = 1; i < s.Length; i++)
            if (s[i] is '+' or '-' && s[i - 1] is not ('e' or 'E')) split = i;
        string rePart = split < 0 ? "0" : s[..split];
        string imPart = (split < 0 ? s : s[split..]).Replace("j", "").Replace("i", "");
        if (imPart is "" or "+") imPart = "1";
        if (imPart is "-") imPart = "-1";
        return Real(rePart, out double r) && Real(imPart, out double im) && Finish(r, im, out z);

        static bool Real(string t, out double v) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        static bool Finish(double re, double im, out Complex c) { c = new Complex(re, im); return true; }
    }

    /// <summary>A Z0 written as a port record states it: <c>50</c>, or <c>25+j10</c>.</summary>
    public static string FormatZ0(Complex z)
    {
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return z.Imaginary == 0 ? R(z.Real) : $"{R(z.Real)}{(z.Imaginary < 0 ? "-" : "+")}j{R(Math.Abs(z.Imaginary))}";
    }
}

/// <summary>Distances between 3D primitives, metres — the contact query's arithmetic (Ericson, Real-Time Collision
/// Detection, §5.1.5 and §5.1.9).</summary>
public static class Geometry3
{
    public static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static Point3 Add(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point3 Mul(Point3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    private static double Dist(Point3 a, Point3 b) { var d = Sub(a, b); return Math.Sqrt(Dot(d, d)); }

    /// <summary>The distance from segment pq to triangle abc: zero when they meet.</summary>
    public static double SegmentTriangle(Point3 p, Point3 q, Point3 a, Point3 b, Point3 c)
    {
        if (Crosses(p, q, a, b, c)) return 0;
        double d = Math.Min(Dist(p, ClosestOnTriangle(p, a, b, c)), Dist(q, ClosestOnTriangle(q, a, b, c)));
        d = Math.Min(d, SegmentSegment(p, q, a, b));
        d = Math.Min(d, SegmentSegment(p, q, b, c));
        return Math.Min(d, SegmentSegment(p, q, c, a));
    }

    /// <summary>True when segment pq passes through the triangle's interior.</summary>
    private static bool Crosses(Point3 p, Point3 q, Point3 a, Point3 b, Point3 c)
    {
        var n = Cross(Sub(b, a), Sub(c, a));
        double dp = Dot(Sub(p, a), n), dq = Dot(Sub(q, a), n);
        if (dp * dq > 0 || dp == dq) return false;
        var x = Add(p, Mul(Sub(q, p), dp / (dp - dq)));
        var cp = ClosestOnTriangle(x, a, b, c);
        return Dist(cp, x) <= 1e-15 * Math.Max(1, Math.Sqrt(Dot(n, n)));
    }

    private static Point3 ClosestOnTriangle(Point3 p, Point3 a, Point3 b, Point3 c)
    {
        var ab = Sub(b, a); var ac = Sub(c, a); var ap = Sub(p, a);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = Sub(p, b);
        double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Add(a, Mul(ab, d1 / (d1 - d3)));
        var cp = Sub(p, c);
        double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Add(a, Mul(ac, d2 / (d2 - d6)));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Add(b, Mul(Sub(c, b), (d4 - d3) / (d4 - d3 + (d5 - d6))));
        double den = 1 / (va + vb + vc);
        return Add(a, Add(Mul(ab, vb * den), Mul(ac, vc * den)));
    }

    /// <summary>The distance between segments p1q1 and p2q2.</summary>
    public static double SegmentSegment(Point3 p1, Point3 q1, Point3 p2, Point3 q2)
    {
        var d1 = Sub(q1, p1); var d2 = Sub(q2, p2); var r = Sub(p1, p2);
        double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r);
        double s, t;
        if (a <= 0 && e <= 0) return Dist(p1, p2);
        if (a <= 0) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = Dot(d1, r);
            if (e <= 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = Dot(d1, d2), den = a * e - b * b;
                s = den != 0 ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return Dist(Add(p1, Mul(d1, s)), Add(p2, Mul(d2, t)));
    }
}
