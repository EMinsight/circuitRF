// brief-em3d-76 R-em3d76-2 — a via field as ONE anisotropic block. A board under a flange carries hundreds of plated
// barrels, each a thin copper tube that forces tiny elements; the standard approximation replaces the region with a block
// of diagonal effective conductivity (k_xy, k_xy, k_z). It is an APPROXIMATION, reported as one, and never on by default.
//
// WHAT IS REPLACED (R-em3d76-2a), by the role and the stackup entry each solid came from (Em3dObjectOrigin): a layout
// instance's board dielectric, its conductor pieces (planes) and its via barrels — and the air bores inside the barrels,
// which are part of what the block stands for. A solid of any other kind the box cuts into (a flange, a die, a drawn
// object) is refused, naming it: the block would silently swallow part of it. A replaced solid wholly inside the box is
// dropped from the run; one reaching out of it keeps the part outside (the block outranks it, so the fragment cuts it).
//
// THE MIXTURE (R-em3d76-2b), stated once, here — docs/user's "Temperature" section repeats it:
//
//   The box is cut into LAYERS at every height where a replaced solid starts or stops. In each layer, over the box's
//   footprint and in the model's own precedence (a bore outranks its barrel, a barrel the dielectric), the area fractions
//   of COPPER (every conductor), DIELECTRIC and VOID (nothing — a bore, or a gap in a conductor layer) are measured
//   exactly, polygon by polygon — a barrel's area is its resolved annulus π(r² − r_bore²), not a nominal fraction.
//
//     through the layer:  k_z  = f_Cu·k_Cu + f_d·k_d                (parallel columns; void carries nothing)
//     in the plane:       the MATRIX is the layer's largest phase, the rest are INCLUSIONS (parallel cylinders) at their
//                         area-weighted mean k_i, fraction φ — Rayleigh's two-phase formula for aligned cylinders:
//                         k_xy = k_m·[(k_i + k_m) + φ(k_i − k_m)] / [(k_i + k_m) − φ(k_i − k_m)]
//                         (a dielectric layer: dielectric matrix, barrels the inclusions; a plane layer: copper matrix,
//                         its via holes the inclusions)
//
//   and the layers stack:  k_z of the block  = Σt / Σ(t/k_z)       (series over thickness)
//                          k_xy of the block = Σ(t·k_xy) / Σt        (parallel over thickness)
//
// Each phase's k is its material's at 25 °C: the block is a constant tensor, whatever k(T) its materials state. A layer
// with nothing in it (the box reaching above the board) or with nothing conducting through it is refused.

using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal;

namespace CircuitRF.Design.Thermal;

/// <summary>One layer of an effective block: its thickness, its phase fractions over the box's footprint and their k.</summary>
public sealed record EffectiveLayer(string Name, double ThicknessM, double CopperFraction, double KCopper, double DielectricFraction,
                                    double KDielectric, int Vias = 0)
{
    public double VoidFraction => Math.Max(0, 1 - CopperFraction - DielectricFraction);
}

/// <summary>The block's tensor and each layer's own (k_xy, k_z), W/(m·K).</summary>
public sealed record EffectiveMixture(double KXy, double KZ, IReadOnlyList<(EffectiveLayer Layer, double KXy, double KZ)> Layers);

/// <summary>What an enabled block does to a run: the solid added, the solids it replaced whole, its tensor and via count.</summary>
public sealed record EffectiveBlockLowering(string Name, Em3dSolid Solid, IReadOnlyList<string> Removed, EffectiveMixture Mixture, int Vias)
{
    /// <summary>The block's conductivity for the solver.</summary>
    public ThermalConductivity Conductivity => ThermalConductivity.Diagonal(Mixture.KXy, Mixture.KXy, Mixture.KZ);

    /// <summary>R-em3d76-2b — the run note: the tensor, the via count and each layer's fractions.</summary>
    public string Note()
        => $"Effective block '{Name}' (an approximation) replaces {Removed.Count} solid(s) with k_xy = {G(Mixture.KXy)} and " +
           $"k_z = {G(Mixture.KZ)} W/(m·K) over {Vias} via(s): " +
           string.Join("; ", Mixture.Layers.Select(l =>
               $"'{l.Layer.Name}' {G(l.Layer.ThicknessM * 1e6)} µm, copper {P(l.Layer.CopperFraction)}, dielectric {P(l.Layer.DielectricFraction)}" +
               (l.Layer.VoidFraction > 1e-9 ? $", void {P(l.Layer.VoidFraction)}" : "") +
               (l.Layer.Vias > 0 ? $", {l.Layer.Vias} via(s)" : "") + $" → ({G(l.KXy)}, {G(l.KZ)})")) + ".";

    private static string G(double v) => v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
    private static string P(double f) => (100 * f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " %";
}

public static class ThermalEffectiveBlocks
{
    /// <summary>Segments a round via is measured with; its radius is scaled so the polygon's area is the circle's.</summary>
    private const int CircleSegments = 256;

    /// <summary>Nanometres per metre: the integer grid the areas are measured on.</summary>
    private const double Nm = 1e9;

    /// <summary>One layer's (k_xy, k_z): see the header.</summary>
    public static (double KXy, double KZ) LayerK(EffectiveLayer l)
    {
        double fc = l.CopperFraction, fd = l.DielectricFraction, fv = l.VoidFraction;
        double kz = fc * l.KCopper + fd * l.KDielectric;
        var phases = new[] { (F: fc, K: l.KCopper), (F: fd, K: l.KDielectric), (F: fv, K: 0.0) };
        int mi = 0;
        for (int i = 1; i < phases.Length; i++) if (phases[i].F > phases[mi].F) mi = i;
        double phi = 1 - phases[mi].F;
        if (phi <= 0) return (phases[mi].K, kz);
        double ki = Enumerable.Range(0, phases.Length).Where(i => i != mi).Sum(i => phases[i].F * phases[i].K) / phi, km = phases[mi].K;
        double den = ki + km - phi * (ki - km);
        double kxy = den > 0 ? km * (ki + km + phi * (ki - km)) / den : 0;
        return (kxy, kz);
    }

    /// <summary>The block's tensor: k_z in series over the layers' thicknesses, k_xy in parallel. k_z is 0 when a layer
    /// conducts nothing through its thickness.</summary>
    public static EffectiveMixture Mix(IReadOnlyList<EffectiveLayer> layers)
    {
        var per = layers.Select(l => { var (xy, z) = LayerK(l); return (l, xy, z); }).ToList();
        double t = layers.Sum(l => l.ThicknessM);
        double kz = per.Any(p => p.z <= 0) ? 0 : t / per.Sum(p => p.l.ThicknessM / p.z);
        double kxy = per.Sum(p => p.l.ThicknessM * p.xy) / t;
        return new EffectiveMixture(kxy, kz, per);
    }

    /// <summary>Whether <paramref name="s"/> is what a block may replace: board dielectric, a conductor piece or a via of a
    /// layout instance's stackup, or a via's bore.</summary>
    public static bool Replaceable(C3dElaboration e, Em3dSolid s)
        => e.Origins.TryGetValue(s.Name, out var o) && o.StackupEntry is not null &&
           o.Kind is Em3dObjectKind.Dielectric or Em3dObjectKind.Conductor or Em3dObjectKind.Via or Em3dObjectKind.Air;

    /// <summary>The box of block <paramref name="b"/>, metres.</summary>
    public static (Point3 Min, Point3 Max) Box(C3dDocument doc, C3dEffectiveBlock b)
    {
        double m = 1e-6 / doc.DbuPerMicron;
        return (new Point3(b.Min.X * m, b.Min.Y * m, b.Min.Z * m),
                new Point3((b.Min.X + b.Size.X) * m, (b.Min.Y + b.Size.Y) * m, (b.Min.Z + b.Size.Z) * m));
    }

    /// <summary>
    /// The solid of another kind the enabled block <paramref name="b"/> cuts into, or null: a meshed solid whose box overlaps
    /// the block's with volume and that is not <see cref="Replaceable"/>. What <c>check</c> and the lowering both refuse.
    /// </summary>
    public static string? Intruder(C3dDocument doc, C3dElaboration e, C3dEffectiveBlock b)
    {
        var (lo, hi) = Box(doc, b);
        double tol = 1e-6 / doc.DbuPerMicron;
        var wires = new HashSet<string>(e.Wires.Select(w => w.Name).Concat(e.DrawnWires.Keys), StringComparer.Ordinal);
        foreach (var s in e.Solids)
        {
            if (ThermalMaterials.NotMeshed(s.Role, s.Material) || wires.Contains(s.Name) || Replaceable(e, s)) continue;
            if (Cuts(Em3dProblem.Bounds(s.Primitive), lo, hi, tol)) return s.Name;
        }
        return null;
    }

    /// <summary>
    /// R-em3d76-2a — enabled block <paramref name="b"/> over the elaborated model: the solid to add (of construction order
    /// <paramref name="order"/>, above every other), what it replaces whole, and its tensor; null with the reason.
    /// </summary>
    public static EffectiveBlockLowering? Lower(C3dDocument doc, C3dElaboration e, C3dEffectiveBlock b, Technology tech, int order,
                                                out string? refusal)
    {
        refusal = null;
        var (lo, hi) = Box(doc, b);
        double tol = 1e-6 / doc.DbuPerMicron;
        string label = $"Effective block '{b.Name}'";
        if (!(hi.X - lo.X > tol && hi.Y - lo.Y > tol && hi.Z - lo.Z > tol)) { refusal = $"{label} has no volume."; return null; }
        if (Intruder(doc, e, b) is { } other)
        {
            refusal = $"{label} cuts through '{other}', which is not board dielectric, a copper plane or a via barrel. A block replaces " +
                      "only those; shrink it so it stops at the other solid's face.";
            return null;
        }

        var inside = e.Solids.Where(s => Replaceable(e, s) && Cuts(Em3dProblem.Bounds(s.Primitive), lo, hi, tol)).ToList();
        if (!inside.Any(s => s.Role != Em3dRole.Air)) { refusal = $"{label} holds no board: nothing inside it is board dielectric, a plane or a via."; return null; }
        var precedence = Em3dPrecedence.Of(inside, []);
        inside = [.. inside.OrderBy(precedence.Of).ThenBy(s => s.Order)];

        // ── the layers: every height a replaced solid starts or stops, within the box ──
        var zs = new List<double> { lo.Z, hi.Z };
        foreach (var s in inside)
        {
            var bb = Em3dProblem.Bounds(s.Primitive);
            if (bb.Z0 > lo.Z + tol && bb.Z0 < hi.Z - tol) zs.Add(bb.Z0);
            if (bb.Z1 > lo.Z + tol && bb.Z1 < hi.Z - tol) zs.Add(bb.Z1);
        }
        zs.Sort();
        var cuts = new List<double> { zs[0] };
        foreach (double z in zs.Skip(1)) if (z - cuts[^1] > tol) cuts.Add(z);
        if (hi.Z - cuts[^1] <= tol) cuts[^1] = hi.Z; else cuts.Add(hi.Z);

        var box = Rect(lo, hi);
        double boxArea = Math.Abs(Clipper.Area(box));
        var layers = new List<EffectiveLayer>();
        var vias = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k + 1 < cuts.Count; k++)
        {
            double z0 = cuts[k], z1 = cuts[k + 1], mid = 0.5 * (z0 + z1);
            var painted = new List<(Em3dSolid Solid, Paths64 Area)>();
            int layerVias = 0;
            foreach (var s in inside)
            {
                var bb = Em3dProblem.Bounds(s.Primitive);
                if (mid < bb.Z0 || mid > bb.Z1) continue;
                if (Footprint(s.Primitive) is not { } fp)
                {
                    refusal = $"{label} cannot measure '{s.Name}': only upright extrusions, boxes and upright round vias can be.";
                    return null;
                }
                var mine = Clipper.Intersect(fp, box, FillRule.EvenOdd);
                if (mine.Count == 0) continue;
                for (int i = 0; i < painted.Count; i++)
                    painted[i] = (painted[i].Solid, Clipper.Difference(painted[i].Area, mine, FillRule.NonZero));
                painted.Add((s, mine));
                if (e.Origins.TryGetValue(s.Name, out var o) && o.Kind == Em3dObjectKind.Via && s.Primitive is Em3dCylinder c &&
                    c.AxisStart.X >= lo.X && c.AxisStart.X <= hi.X && c.AxisStart.Y >= lo.Y && c.AxisStart.Y <= hi.Y)
                {
                    layerVias++;
                    vias.Add(s.Name);
                }
            }
            double cu = 0, cuK = 0, di = 0, diK = 0;
            string? name = null;
            double largest = 0;
            foreach (var (s, area) in painted)
            {
                double a = Math.Abs(Clipper.Area(area)) / boxArea;
                if (a <= 0 || s.Role == Em3dRole.Air) continue;
                var rec = ThermalMaterials.For(e, s.Name, s.Material);
                if (rec is null) { refusal = $"{label} replaces '{s.Name}', whose material '{s.Material}' states no thermal conductivity."; return null; }
                double kk = ThermalProperties.ThermalKAt(rec.Material, 25)!.Value.Value;
                if (s.Role == Em3dRole.Conductor) { cu += a; cuK += a * kk; } else { di += a; diK += a * kk; }
                if (a > largest && e.Origins.TryGetValue(s.Name, out var o2)) { largest = a; name = o2.StackupEntry; }
            }
            if (cu + di <= 1e-12)
            {
                refusal = $"{label} reaches beyond the board between z = {Um(z0)} and {Um(z1)} µm, where there is nothing to replace. " +
                          "A block replaces board material only: fit it to the board's thickness.";
                return null;
            }
            var layer = new EffectiveLayer(name ?? $"z {Um(z0)}–{Um(z1)} µm", z1 - z0, Math.Min(cu, 1), cu > 0 ? cuK / cu : 0,
                                           Math.Min(di, 1), di > 0 ? diK / di : 0, layerVias);
            if (LayerK(layer).KZ <= 0)
            {
                refusal = $"{label}'s layer '{layer.Name}' conducts nothing through its thickness inside the box, so the block would be an " +
                          "insulator. Fit the block to where the board is.";
                return null;
            }
            layers.Add(layer);
        }
        var mix = Mix(layers);
        var removed = inside.Where(s => Within(Em3dProblem.Bounds(s.Primitive), lo, hi, tol)).Select(s => s.Name).ToList();
        var solid = new Em3dSolid(b.Name, EffectiveMaterial(b.Name), Em3dRole.Conductor, new Em3dBox(lo, hi), order);
        return new EffectiveBlockLowering(b.Name, solid, removed, mix, vias.Count);
    }

    /// <summary>The material name an effective block's region carries (no technology record has it).</summary>
    public static string EffectiveMaterial(string block) => $"effective block {block}";

    private static bool Cuts((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a, Point3 lo, Point3 hi, double tol)
        => a.X0 < hi.X - tol && a.X1 > lo.X + tol && a.Y0 < hi.Y - tol && a.Y1 > lo.Y + tol && a.Z0 < hi.Z - tol && a.Z1 > lo.Z + tol;

    private static bool Within((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a, Point3 lo, Point3 hi, double tol)
        => a.X0 >= lo.X - tol && a.X1 <= hi.X + tol && a.Y0 >= lo.Y - tol && a.Y1 <= hi.Y + tol && a.Z0 >= lo.Z - tol && a.Z1 <= hi.Z + tol;

    private static string Um(double m) => (m * 1e6).ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

    private static Paths64 Rect(Point3 lo, Point3 hi)
        => [[P(lo.X, lo.Y), P(hi.X, lo.Y), P(hi.X, hi.Y), P(lo.X, hi.Y)]];

    private static Point64 P(double x, double y) => new((long)Math.Round(x * Nm), (long)Math.Round(y * Nm));

    /// <summary>A solid's plan-view outline, or null when it is not upright.</summary>
    private static Paths64? Footprint(Em3dPrimitive p)
    {
        switch (p)
        {
            case Em3dExtrudedPolygon ep:
                var paths = new Paths64 { new Path64(ep.Outline.Select(q => P(q.X, q.Y))) };
                foreach (var h in ep.Holes) paths.Add(new Path64(h.Select(q => P(q.X, q.Y))));
                return paths;
            case Em3dBox bx:
                return Rect(bx.Min, bx.Max);
            case Em3dCylinder c when Math.Abs(c.AxisStart.X - c.AxisEnd.X) < 1e-12 && Math.Abs(c.AxisStart.Y - c.AxisEnd.Y) < 1e-12:
                // the polygon's area is the circle's: r' = r·sqrt(2π / (n·sin(2π/n)))
                double r = c.Radius * Math.Sqrt(2 * Math.PI / (CircleSegments * Math.Sin(2 * Math.PI / CircleSegments)));
                return [new Path64(Enumerable.Range(0, CircleSegments).Select(i =>
                    P(c.AxisStart.X + r * Math.Cos(2 * Math.PI * i / CircleSegments), c.AxisStart.Y + r * Math.Sin(2 * Math.PI * i / CircleSegments))))];
            default:
                return null;
        }
    }
}
