// brief-em3d-77 R-em3d77-3 — bond wires in a thermal run: one 1D chain per wire, built from the SAME resolved centreline
// every solver and the viewer use (C3dWires.Resolve / Em3dWires.Resolve — elaboration's own sweeps), never a second wire model.
//
// THE CHAIN (R-em3d77-3b). Second-order line elements along the resolved centreline, every vertex a node and no element across
// a vertex, so its length IS the centreline's 3D arc length — a ball end's vertical neck included, never a plan length and
// never the loop's chord. Each segment takes ⌈length / (WireSizeDiameters·d)⌉ elements, at least one; a foot takes one.
//
// ONE ADJUSTMENT, AND WHY. A wedge end's foot axis sits at its pad's top plus half the section's HEIGHT, and a hexagon (the
// EM section) is 9 % lower than it is wide. Thermally the wire is round (R-em3d77-3c: πd²/4, whatever its EM section), so the
// two foot vertices of a wedge end are put at the pad's top plus d/2 — exactly where a round section's already are. That is
// what makes a hexagon wire and a round one of the same diameter the SAME thermal problem (gate 6), and it moves no vertex of a
// round wire. The viewer's arc lengths come from the displayed sweep, so on a hexagon wire its colour is placed up to ~0.05 d
// off along the first span segment — a micrometre on a 1 mil wire.
//
// THE FEET (R-em3d77-3d). A wedge foot lies on its pad from the heel outward; a ball contacts over its footprint. Each end
// gets a CONTACT PATCH — a sheet the mesher embeds in the pad's top face (brief 72 Q2), refined to PatchElementsAcross
// elements across (brief 72 Q8: two or three, not the four a heat source gets, because a 100-wire array has 200 of them) —
// and the solver couples the wire to it: a perfect bond ties the patch to the heel (the heel IS the pad); a stated bond
// resistance couples the foot along its length. A ball adds its own height as a series resistance. The balls' solids are
// part of the wire here, so they are not meshed.
//
// SIZING. The mesh round a wire is refined to WireSizeDiameters·d by a Distance field on points along its centreline: the
// well coupling reads the solid on a ring 1.5 host elements out, which should stay clear of the neighbouring wires.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using BondStyle = CircuitRF.WBond.BondStyle;

namespace CircuitRF.Design.Thermal;

/// <summary>One end of a wire as the thermal run bonds it.</summary>
/// <param name="Pad">The pad solid's name, as the elaboration names it.</param>
/// <param name="Patch">The contact patch's sheet name.</param>
/// <param name="Node">The chain node the end's contact acts on: the heel of a wedge, the ball's top.</param>
/// <param name="FootFrom">For a wedge, the foot's far node (the other end of its element); −1 for a ball.</param>
/// <param name="BallHeightM">A ball's height (its series resistance), metres; 0 for a wedge.</param>
public sealed record ThermalWireEnd(BondStyle Style, string Pad, string Patch, int Node, int FootFrom, double BallHeightM);

/// <summary>R-em3d77-3 — a wire as the thermal run solves it: its chain, its material and its two ends.</summary>
public sealed record ThermalWirePlan(
    string Name, string Material, double DiameterM, IReadOnlyList<Point3> Centreline, double[] Points, double[] S, bool[] OnPad,
    ThermalWireEnd Start, ThermalWireEnd End)
{
    public int NodeCount => S.Length;

    /// <summary>The chain's length, metres: the sum of its elements', which is the centreline's 3D arc length.</summary>
    public double ChainLengthM => S[^1] - S[0];

    /// <summary>The centreline's plan (x, y) length, metres — what a 1D wire must NOT be.</summary>
    public double PlanLengthM
    {
        get
        {
            double l = 0;
            for (int i = 1; i < Centreline.Count; i++)
                l += Math.Sqrt(Math.Pow(Centreline[i].X - Centreline[i - 1].X, 2) + Math.Pow(Centreline[i].Y - Centreline[i - 1].Y, 2));
            return l;
        }
    }
}

public static class ThermalWireLowering
{
    /// <summary>A 1D element's length and the host mesh's size round a wire, in wire diameters.</summary>
    public const double WireSizeDiameters = 2;

    /// <summary>Elements across a contact patch's smaller side (brief 72 Q8).</summary>
    public const double PatchElementsAcross = 2;

    /// <summary>Points on a ball's footprint.</summary>
    private const int FootprintSides = 16;

    /// <summary>
    /// Every wire of <paramref name="e"/> as a thermal chain, the names of the solids it stands for (its sweep and its balls, never
    /// meshed), and its patches — or null with the refusal. <paramref name="meshed"/> are the solids the run meshes (by name).
    /// </summary>
    public static List<ThermalWirePlan>? Plan(C3dElaboration e, ISet<string> meshed, List<GmshThermalSheet> patches, List<string> notes,
                                              out string? refusal)
    {
        refusal = null;
        var plans = new List<ThermalWirePlan>();
        var unbonded = new List<string>();
        foreach (var report in e.Wires)
        {
            if (e.Solids.FirstOrDefault(s => s.Name == report.Name)?.Primitive is not Em3dSweep sweep) continue;
            var plan = Chain(report, sweep);
            if (plan is null) { refusal = $"Wire '{report.Name}' has fewer than two distinct centreline points."; return null; }
            foreach (var end in new[] { plan.Start, plan.End })
            {
                if (!meshed.Contains(end.Pad)) { unbonded.Add($"{report.Name} ({end.Pad})"); continue; }
                patches.Add(new GmshThermalSheet(end.Patch, Patch(plan, report, end, end == plan.Start)) { ElementsAcross = PatchElementsAcross });
            }
            plans.Add(plan);
        }
        if (unbonded.Count > 0)
            notes.Add($"{unbonded.Count} wire end(s) land on a pad this thermal run does not mesh, so they touch nothing: " +
                      string.Join(", ", unbonded.Take(6)) + (unbonded.Count > 6 ? $" and {unbonded.Count - 6} more" : "") + ".");
        return plans;
    }

    /// <summary>The names of the solids a wire stands for: its sweep and its balls.</summary>
    public static IEnumerable<string> SolidsOf(Em3dWireReport report)
    {
        yield return report.Name;
        yield return report.Name + "/ball/start";
        yield return report.Name + "/ball/end";
    }

    /// <summary>R-em3d77-3b — the chain along <paramref name="sweep"/>'s centreline (a wedge's foot vertices put at pad + d/2).</summary>
    public static ThermalWirePlan? Chain(Em3dWireReport report, Em3dSweep sweep)
    {
        double d = report.DiameterM;
        var path = sweep.Path.ToList();
        if (path.Count < 2) return null;
        bool wedgeStart = report.Start.Style == BondStyle.Wedge && path.Count >= 3;
        bool wedgeEnd = report.End.Style == BondStyle.Wedge && path.Count >= 3;
        if (wedgeStart)
        {
            path[0] = path[0] with { Z = report.Start.PadTopM + d / 2 };
            path[1] = path[1] with { Z = report.Start.PadTopM + d / 2 };
        }
        if (wedgeEnd)
        {
            path[^1] = path[^1] with { Z = report.End.PadTopM + d / 2 };
            path[^2] = path[^2] with { Z = report.End.PadTopM + d / 2 };
        }
        double target = WireSizeDiameters * d;
        var pts = new List<double>();
        var arc = new List<double>();
        var onPad = new List<bool>();
        var vertexNode = new int[path.Count];
        double s = 0;
        pts.AddRange([path[0].X, path[0].Y, path[0].Z]);
        arc.Add(0);
        for (int g = 0; g + 1 < path.Count; g++)
        {
            var (p, q) = (path[g], path[g + 1]);
            double len = Dist(p, q);
            bool foot = wedgeStart && g == 0 || wedgeEnd && g == path.Count - 2;
            int n = foot ? 1 : Math.Max(1, (int)Math.Ceiling(len / target - 1e-9));
            for (int j = 1; j <= 2 * n; j++)
            {
                double f = (double)j / (2 * n);
                // the segment's own end exactly, so the chain's vertices are the centreline's
                var at = j == 2 * n ? q : new Point3(p.X + f * (q.X - p.X), p.Y + f * (q.Y - p.Y), p.Z + f * (q.Z - p.Z));
                pts.AddRange([at.X, at.Y, at.Z]);
                arc.Add(j == 2 * n ? s + len : s + f * len);
            }
            for (int k = 0; k < n; k++) onPad.Add(foot);
            s += len;
            vertexNode[g + 1] = arc.Count - 1;
        }
        // s is 0 at the start heel: the wedge's vertex 1, the ball's top (vertex 0)
        double s0 = wedgeStart ? arc[vertexNode[1]] : 0;
        var sArr = arc.Select(v => v - s0).ToArray();
        int last = arc.Count - 1;
        string Patch(string which) => $"*wire*:{report.Name}:{which}";
        var ballH = report.Process.BallHeight.Nm * 1e-9;
        var start = wedgeStart
            ? new ThermalWireEnd(BondStyle.Wedge, report.Start.Pad, Patch("start"), vertexNode[1], 0, 0)
            : new ThermalWireEnd(BondStyle.Ball, report.Start.Pad, Patch("start"), 0, -1, ballH);
        var end = wedgeEnd
            ? new ThermalWireEnd(BondStyle.Wedge, report.End.Pad, Patch("end"), vertexNode[^2], last, 0)
            : new ThermalWireEnd(BondStyle.Ball, report.End.Pad, Patch("end"), last, -1, ballH);
        return new ThermalWirePlan(report.Name, report.Material, d, path, [.. pts], sArr, [.. onPad], start, end);
    }

    /// <summary>The contact patch of <paramref name="end"/> on its pad's top face: the foot's footprint (heel to far, the round
    /// wire's width), or the ball's (a polygon of its footprint circle).</summary>
    private static Em3dSheet Patch(ThermalWirePlan plan, Em3dWireReport report, ThermalWireEnd end, bool start)
    {
        double top = start ? report.Start.PadTopM : report.End.PadTopM;
        var frame = new Em3dPlaneFrame(new Point3(0, 0, top), new Point3(1, 0, 0), new Point3(0, 1, 0));
        List<Point2> outline;
        if (end.Style == BondStyle.Wedge)
        {
            var heel = plan.Centreline[start ? 1 : ^2];
            var far = plan.Centreline[start ? 0 : ^1];
            double dx = far.X - heel.X, dy = far.Y - heel.Y, l = Math.Sqrt(dx * dx + dy * dy);
            double ux = l > 0 ? dx / l : 1, uy = l > 0 ? dy / l : 0, w = plan.DiameterM / 2;
            outline = [new(heel.X - uy * w, heel.Y + ux * w), new(far.X - uy * w, far.Y + ux * w),
                       new(far.X + uy * w, far.Y - ux * w), new(heel.X + uy * w, heel.Y - ux * w)];
        }
        else
        {
            var c = plan.Centreline[start ? 0 : ^1];
            double dd = report.Process.BallDiameter.Nm * 1e-9, h = report.Process.BallHeight.Nm * 1e-9;
            double r = Math.Sqrt(Math.Max(dd * dd / 4 - h * h / 4, plan.DiameterM * plan.DiameterM / 4));
            outline = [.. Enumerable.Range(0, FootprintSides).Select(k => new Point2(c.X + r * Math.Cos(2 * Math.PI * k / FootprintSides),
                                                                                      c.Y + r * Math.Sin(2 * Math.PI * k / FootprintSides)))];
        }
        return new Em3dSheet(end.Patch, "", outline, [], 0, 0, 0) { Frame = frame };
    }

    /// <summary>Points along every wire's centreline, at most half a target size apart, for the mesher's Distance field.</summary>
    public static IReadOnlyList<(IReadOnlyList<Point3> Points, double SizeM)> SizeLines(IEnumerable<ThermalWirePlan> plans)
    {
        var lines = new List<(IReadOnlyList<Point3>, double)>();
        foreach (var p in plans)
        {
            double size = WireSizeDiameters * p.DiameterM;
            var pts = new List<Point3> { p.Centreline[0] };
            for (int i = 1; i < p.Centreline.Count; i++)
            {
                var (a, b) = (p.Centreline[i - 1], p.Centreline[i]);
                int n = Math.Max(1, (int)Math.Ceiling(Dist(a, b) / (size / 2)));
                for (int k = 1; k <= n; k++) pts.Add(new Point3(a.X + (b.X - a.X) * k / n, a.Y + (b.Y - a.Y) * k / n, a.Z + (b.Z - a.Z) * k / n));
            }
            lines.Add((pts, size));
        }
        return lines;
    }

    /// <summary>R-em3d77-3 — the note a run carries about its wires.</summary>
    public static string Note(IReadOnlyList<ThermalWirePlan> plans, IReadOnlyList<(int InSolid, int InAir, int OnPad)>? hosting = null)
    {
        string L(double m) => (m * 1e6).ToString("0.#", CultureInfo.InvariantCulture);
        var lengths = plans.Select(p => p.ChainLengthM).ToList();
        string hosted = hosting is null ? "" :
            $"; {hosting.Sum(h => h.InSolid)} span element(s) lie in a solid (coupled to it by the well conductance) and {hosting.Sum(h => h.InAir)} " +
            "in air";
        return $"{plans.Count} bond wire(s) are solved as 1D conduction elements along their resolved centrelines, with area πd²/4 whatever " +
               $"their EM section: 3D lengths {L(lengths.Min())}–{L(lengths.Max())} µm (feet included), " +
               $"{plans.Sum(p => (p.NodeCount - 1) / 2)} element(s){hosted}.";
    }

    private static double Dist(Point3 a, Point3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
