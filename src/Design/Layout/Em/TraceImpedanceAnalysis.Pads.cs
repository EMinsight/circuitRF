// A ROUND PAD HANGING OFF A LINE (brief-dfr26-7 R-dfr26-7-5).
//
// A test point on an RF line is a round pad joined to the trace — directly, or through a via — and it loads the line
// with the pad's capacitance to the nearest plane and the via's inductance. The review measures Z0 along traces and
// would otherwise say nothing about it, though on the board that asked for this it was the 6 dB loss.
//
// ONE IMPLEMENTATION OF THE FORMULAS. The pad, the planes it faces and the via are resolved with the TESTPAD's own
// stackup walk (SubstrateResolver.ResolveTestPad) and the numbers come out of the TestPadGeometry its circuit model
// is built from — what differs is only how the geometry is learned: from the ARTWORK here (copper under the pad's
// centre, the clearance actually drawn round the via), from ClearLayers in the model.

using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.Em;

/// <summary>A round pad hanging off one end of a trace, reported as the shunt element it is (brief-dfr26-7
/// R-dfr26-7-5). Lengths DBU; electrical values SI.</summary>
/// <param name="AtStart">The pad is on the trace's start end.</param>
/// <param name="X">The pad's centre.</param>
/// <param name="Y">The pad's centre.</param>
/// <param name="Diameter">The pad's diameter, DBU.</param>
/// <param name="PadLayer">The conductor the pad is on.</param>
/// <param name="ReferencePlanes">The conductors its capacitance is taken to, one a side at most.</param>
/// <param name="Capacitance">The pad's capacitance to them, F.</param>
/// <param name="ViaInductance">Through a via, the via's inductance, H; null for a pad joined directly.</param>
/// <param name="BarrelCapacitance">The via barrel's capacitance to the planes it passes, F.</param>
/// <param name="ResonanceHz">Where the via's inductance and the capacitance below it resonate; null with no via.</param>
/// <param name="Text">The sentence the report and panel show.</param>
/// <param name="UnusedPads">The lands the via carries on the planes it passes, each with its share (brief-dfr26-8
/// R-dfr26-8-2).</param>
public sealed record TracePadShunt(
    bool AtStart, long X, long Y, long Diameter, string PadLayer, IReadOnlyList<string> ReferencePlanes,
    double Capacitance, double? ViaInductance, double BarrelCapacitance, double? ResonanceHz, string Text,
    IReadOnlyList<TraceUnusedPad>? UnusedPads = null)
{
    /// <summary>What the unused pads add between them, F.</summary>
    public double UnusedPadCapacitance => UnusedPads?.Sum(p => p.Capacitance) ?? 0.0;
}

/// <summary>A land a test pad's via carries on a plane it passes, joined to nothing there (brief-dfr26-8
/// R-dfr26-8-2). Lengths DBU; the capacitance SI.</summary>
/// <param name="Layer">The conductor it is on.</param>
/// <param name="Diameter">The land's diameter, as drawn.</param>
/// <param name="Antipad">The clearance round it, as drawn.</param>
/// <param name="Capacitance">What it adds to the line over the bare barrel, F.</param>
public sealed record TraceUnusedPad(string Layer, long Diameter, long Antipad, double Capacitance);

public static partial class TraceImpedanceAnalysis
{
    /// <summary>A disc no wider than this many of the via's own land is the via's land, not a pad hanging off it.</summary>
    private const double PadOverLand = 1.25;

    /// <summary>The round pads hanging off <paramref name="run"/>'s two ends.</summary>
    private static IReadOnlyList<TracePadShunt> PadShunts(TraceRun run, IReadOnlyList<LayoutShape> shapes, TraceStack ctx,
                                                          TraceImpedanceReport fmt)
    {
        var shunts = new List<TracePadShunt>();
        if (!ctx.BandOf.TryGetValue(run.Layer, out var band)) return shunts;
        foreach (bool atStart in new[] { true, false })
        {
            string kind = atStart ? run.StartsAt : run.EndsAt;
            if (kind is not ("pad" or "via")) continue;
            long ex = atStart ? run.StartX : run.EndX, ey = atStart ? run.StartY : run.EndY;
            if (PadShuntAt(ex, ey, run.WidthMax, band, shapes, ctx, fmt) is { } s)
                shunts.Add(s with { AtStart = atStart });
        }
        return shunts;
    }

    /// <summary>
    /// The round pad at one trace end, or null. When a drilled via stands at the end, the pad is a disc on another
    /// conductor the via reaches, wider than the via's land and with no other copper joined to it — and a via with no
    /// such disc is a layer change, not a pad. Otherwise the pad is a disc on the trace's own conductor that the end
    /// runs into. (A via whose land is drawn as a disc reads as a "pad" end to the trace walk, so the end's kind is
    /// not trusted to say which.)
    /// </summary>
    internal static TracePadShunt? PadShuntAt(long ex, long ey, double width, CrossSectionExtractor.Band band,
                                              IReadOnlyList<LayoutShape> shapes, TraceStack ctx, TraceImpedanceReport fmt)
    {
        var tech = ctx.Tech;
        static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        CircleShape? pad = null;
        CrossSectionExtractor.Band? padBand = null;
        var via = shapes.OfType<ViaShape>()
                        .Where(v => v.DrillSize > 0 && Dist(v.X, v.Y, ex, ey) <= Math.Max(v.PadSize, v.DrillSize) + width)
                        .MinBy(v => Dist(v.X, v.Y, ex, ey));
        if (via is not null)
        {
            double land = Math.Max(via.PadSize, via.DrillSize);
            foreach (var b in BandsSpanned(via, tech, ctx.Bands))
            {
                if (b.Index == band.Index) continue;
                var disc = shapes.OfType<CircleShape>()
                                 .Where(c => ctx.BandOf.TryGetValue(c.Layer, out var cb) && cb.Index == b.Index
                                             && 2 * c.R > PadOverLand * land && Dist(c.Cx, c.Cy, via.X, via.Y) <= c.R)
                                 .MaxBy(c => c.R);
                if (disc is null || !Isolated(disc, ctx.Copper.GetValueOrDefault(b.Index))) continue;
                pad = disc;
                padBand = b;
                break;
            }
            if (pad is null) return null;
        }
        else
        {
            pad = shapes.OfType<CircleShape>()
                        .Where(c => ctx.BandOf.TryGetValue(c.Layer, out var cb) && cb.Index == band.Index
                                    && 2 * c.R > PadOverLand * width && Dist(c.Cx, c.Cy, ex, ey) <= c.R + width)
                        .MinBy(c => Dist(c.Cx, c.Cy, ex, ey));
            padBand = band;
        }
        if (pad is null || padBand is null) return null;

        // What the artwork says: a conductor faces the pad where its copper covers at least half the pad's area — a
        // via's own antipad in it does not clear a pad much wider than the antipad — or where it is a ground reference
        // with nothing drawn on it at all (taken as a solid plane, as the cross-section takes it). The area is sampled
        // on four equal-area rings of eight points.
        bool Drawn(CrossSectionExtractor.Band b) => ctx.Copper.TryGetValue(b.Index, out var cu) && cu.Paths.Count > 0;
        // By NAME: the bands are built over the effective stackup (patterned films settled), not the technology's own objects.
        // A conductor with no band is one the cross-section does not model: nothing drawn on it, as far as this reads.
        CrossSectionExtractor.Band? BandOfLayer(StackupLayer l) => ctx.Bands.FirstOrDefault(b => b.Layer.Name == l.Name);
        bool Faces(StackupLayer l)
        {
            if (BandOfLayer(l) is not { } b || !Drawn(b)) return l.IsGroundReference;
            var cu = ctx.Copper[b.Index];
            int hit = 0;
            for (int ring = 0; ring < 4; ring++)
            {
                double r = Math.Sqrt((ring + 0.5) / 4) * pad.R;
                for (int k = 0; k < 8; k++)
                    if (cu.Contains(pad.Cx + r * Math.Cos(k * Math.PI / 4), pad.Cy + r * Math.Sin(k * Math.PI / 4))) hit++;
            }
            return hit >= 16;
        }

        // The planes the via passes: those whose drawn copper rings the barrel, at the clearance drawn — measured
        // outside the land when the via carries one there (brief-dfr26-8), since the land is copper too and would
        // otherwise read as a clearance no wider than the drill.
        var clearance = new Dictionary<string, double>(StringComparer.Ordinal);
        var lands = new Dictionary<string, double>(StringComparer.Ordinal);
        if (via is not null)
            foreach (var b in BandsSpanned(via, tech, ctx.Bands))
            {
                if (b.Index == band.Index || b.Index == padBand.Index || !Drawn(b)) continue;
                var cu = ctx.Copper[b.Index];
                double rDrill = Math.Max(via.DrillSize, 1) / 2.0;
                double? land = MeasuredLand(cu, via.X, via.Y, rDrill, ctx.DbuPerMicron);
                double from = land is { } edge ? edge + 5.0 * ctx.DbuPerMicron : rDrill;   // past the land's own rim
                if (MeasuredClearance(cu, via.X, via.Y, from, ctx.DbuPerMicron) is not { } d) continue;
                clearance[b.Layer.Name] = d;
                if (land is { } l) lands[b.Layer.Name] = 2 * l;
            }

        var (stack, _, _) = SubstrateResolver.ResolveTestPad(tech, padBand.Layer.Name, band.Layer.Name, via is not null,
                                                             [.. clearance.Keys], Faces);
        if (stack is null) return null;

        double m = ctx.MetresPerDbu;
        double antipad = (via is null ? 0 : Math.Max(via.PadSize, via.DrillSize) * m) + ComponentModelFactory.DefaultViaAntipadRingMeters;
        var planes = new List<TestPadPlane>();
        foreach (var p in stack.Planes)
        {
            double? b = clearance.TryGetValue(p.Plane.Name, out var dc) ? dc * m
                      : (BandOfLayer(p.Plane) is not { } pb || !Drawn(pb)) && p.Plane.IsGroundReference ? antipad : null;
            if (b is { } bb) planes.Add(new TestPadPlane(p.PositionMeters, p.LengthMeters, p.EpsR, bb));
        }
        // The unused lands, each with what is next to it, through the same slots the TESTPAD's own model uses.
        var unused = new List<(TestPadInnerPad Pad, string Layer, long D, long B)>();
        if (stack.Via is { } span && lands.Count > 0)
            foreach (var slot in SubstrateResolver.InnerPadSlots(tech, span.From, stack.Pad, l => lands.ContainsKey(l.Name)))
            {
                double dl = lands[slot.Conductor.Name], bl = clearance[slot.Conductor.Name];
                unused.Add((new TestPadInnerPad(slot.PositionMeters, slot.ThicknessMeters, slot.EpsR, dl * m, bl * m,
                                                slot.GapAMeters, slot.SideA, slot.GapBMeters, slot.SideB),
                            slot.Conductor.Name, (long)Math.Round(dl), (long)Math.Round(bl)));
            }
        var geometry = new TestPadGeometry(
            Diameter: 2 * pad.R * m,
            Faces: [.. stack.Faces.Select(f => new TestPadFacing(f.HeightMeters, f.EpsR))],
            Drill: via is null ? 0 : via.DrillSize * m,
            ViaLength: stack.ViaLengthMeters,
            ViaEpsR: stack.Via?.MaxRelativePermittivity ?? 1,
            Planes: planes,
            InnerPads: [.. unused.Select(u => u.Pad)]);
        var unusedPads = unused.Select(u => new TraceUnusedPad(u.Layer, u.D, u.B, geometry.InnerPadCapacitanceOf(u.Pad))).ToList();

        string G(double v) => v.ToString("G3", System.Globalization.CultureInfo.InvariantCulture);
        var refs = stack.Faces.Select(f => f.Plane.Name).ToList();
        string text =
            $"A round pad {fmt.Len(2 * pad.R)} across on '{padBand.Layer.Name}' hangs off this end at {fmt.Pt(pad.Cx, pad.Cy)}" +
            (via is not null ? $", through a via from '{band.Layer.Name}'" : "") + ": " +
            (refs.Count > 0 ? $"{G(geometry.PadCapacitance * 1e12)} pF to {string.Join(" and ", refs.Select(r => $"'{r}'"))}"
                            : "no plane faces it") +
            (via is not null
                ? $", the via {G(geometry.ViaInductance * 1e9)} nH" +
                  (geometry.BarrelCapacitance > 0 ? $" with {G(geometry.BarrelCapacitance * 1e12)} pF to the planes it passes" : "") +
                  (unusedPads.Count > 0
                      ? $", {unusedPads.Count} unused pad{(unusedPads.Count == 1 ? "" : "s")} {G(geometry.InnerPadCapacitance * 1e12)} pF (" +
                        string.Join(", ", unusedPads.Select(u => $"'{u.Layer}' {fmt.Len(u.Diameter)} in {fmt.Len(u.Antipad)}, {G(u.Capacitance * 1e12)} pF")) + ")"
                      : "") +
                  (double.IsFinite(geometry.ResonanceHz) ? $", resonating at {G(geometry.ResonanceHz / 1e9)} GHz" : "")
                : "") +
            ". A shunt element on the line that the impedance review does not include: an estimate, of which an EM run is the reference." +
            (unusedPads.Count > 0 ? " Removing unused pads (a CAM option) removes their share." : "");
        return new TracePadShunt(false, pad.Cx, pad.Cy, 2 * pad.R, padBand.Layer.Name, refs, geometry.PadCapacitance,
                                 via is null ? null : geometry.ViaInductance, geometry.BarrelCapacitance,
                                 via is null || !double.IsFinite(geometry.ResonanceHz) ? null : geometry.ResonanceHz, text,
                                 unusedPads);
    }

    /// <summary>No copper on the disc's conductor just outside it, all round: nothing else is joined to the pad.</summary>
    private static bool Isolated(CircleShape disc, TraceCopper? copper)
    {
        if (copper is null) return true;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI / 8;
            foreach (double f in new[] { 1.2, 1.6 })
                if (copper.Contains(disc.Cx + f * disc.R * Math.Cos(a), disc.Cy + f * disc.R * Math.Sin(a))) return false;
        }
        return true;
    }

    /// <summary>The radius of a land at (x, y) on a plane's conductor, DBU — copper all round just outside the drill,
    /// ending in a gap before the plane — or null when there is none there, or when the copper runs on into the plane
    /// within 5 mm (the via is joined to it, not passing it).</summary>
    private static double? MeasuredLand(TraceCopper copper, long x, long y, double rDrill, int dbuPerMicron)
    {
        int Hits(double r)
        {
            int hit = 0;
            for (int k = 0; k < 16; k++)
            {
                double a = k * Math.PI / 8;
                if (copper.Contains(x + r * Math.Cos(a), y + r * Math.Sin(a))) hit++;
            }
            return hit;
        }
        double step = 5.0 * dbuPerMicron;
        if (Hits(rDrill + step) < 12) return null;
        for (double r = rDrill + 2 * step; r <= 5_000.0 * dbuPerMicron; r += step)
            if (Hits(r) <= 4) return r - step / 2;
        return null;
    }

    /// <summary>The diameter of the clearance a plane's copper leaves round a barrel at (x, y), DBU — the smallest
    /// radius at which copper surrounds it in at least three quarters of sixteen directions — or null when no copper
    /// rings it within 10 mm (the conductor is not a plane there).</summary>
    private static double? MeasuredClearance(TraceCopper copper, long x, long y, double rMin, int dbuPerMicron)
    {
        double step = 10.0 * dbuPerMicron, rMax = 10_000.0 * dbuPerMicron;
        for (double r = rMin; r <= rMax; r += step)
        {
            int hit = 0;
            for (int k = 0; k < 16; k++)
            {
                double a = k * Math.PI / 8;
                if (copper.Contains(x + r * Math.Cos(a), y + r * Math.Sin(a))) hit++;
            }
            if (hit >= 12) return 2 * r;
        }
        return null;
    }
}
