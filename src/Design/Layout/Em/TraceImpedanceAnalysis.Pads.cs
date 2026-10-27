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
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Extraction;
using Clipper2Lib;
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

    /// <summary>The frequency it was judged at — the check's <see cref="TraceImpedanceOptions.MaxFrequencyHz"/> — or
    /// null when none was given and it was not judged (brief-dfr29-2 R-dfr29-2-1).</summary>
    public double? JudgedAtHz { get; init; }

    /// <summary>The Z0 of the line it loads, Ω: the trace's own mean, else the target.</summary>
    public double? LineOhms { get; init; }

    /// <summary>|S11| of the shunt on that line at <see cref="JudgedAtHz"/>, dB; null when not judged.</summary>
    public double? S11Db { get; init; }

    /// <summary>The finding's severity, or null when it is a note — not judged, or below
    /// <see cref="TraceImpedanceAnalysis.PadShuntWarnDb"/> with its resonance clear of twice the band.</summary>
    public IssueSeverity? Severity { get; init; }
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

    /// <summary>A pad shunt whose |S11| on its line at the highest frequency is above this is a warning (owner
    /// decision D1, brief-dfr29-2); so is one resonating below twice that frequency.</summary>
    public const double PadShuntWarnDb = -20;

    /// <summary>Above this it fails; so does one resonating inside the band.</summary>
    public const double PadShuntFailDb = -10;

    /// <summary>The round pads hanging off <paramref name="run"/>'s two ends, each judged at the check's highest
    /// frequency against the trace's own Z0.</summary>
    private static IReadOnlyList<TracePadShunt> PadShunts(TraceRun run, PadSearch search, TraceStack ctx,
                                                          TraceImpedanceReport fmt, TraceImpedanceOptions options)
    {
        var shunts = new List<TracePadShunt>();
        if (!ctx.BandOf.TryGetValue(run.Layer, out var band)) return shunts;
        double z0 = run.Z0Mean ?? options.TargetOhms;
        foreach (bool atStart in new[] { true, false })
        {
            string kind = atStart ? run.StartsAt : run.EndsAt;
            if (kind is not ("pad" or "via")) continue;
            long ex = atStart ? run.StartX : run.EndX, ey = atStart ? run.StartY : run.EndY;
            if (PadShuntAt(ex, ey, run.WidthMax, band, search, ctx, fmt, options.MaxFrequencyHz, z0, "this end") is { } s)
                shunts.Add(s with { AtStart = atStart });
        }
        return shunts;
    }

    /// <summary><paramref name="run"/> with <paramref name="shunts"/> added: each judged one a finding whose box is the
    /// pad's bounds, each other a note (brief-dfr29-2 R-dfr29-2-1), and the verdict taken again.</summary>
    private static TraceRun WithPadShunts(TraceRun run, IReadOnlyList<TracePadShunt> shunts)
    {
        if (shunts.Count == 0) return run;
        var found = shunts.Where(sh => sh.Severity is not null)
                          .Select(sh => new TraceIssue(TraceIssueKind.PadShunt, sh.Severity!.Value,
                                                       sh.X - sh.Diameter / 2, sh.Y - sh.Diameter / 2,
                                                       sh.X + sh.Diameter / 2, sh.Y + sh.Diameter / 2, sh.Text)
                                          { S11Db = sh.S11Db })
                          .ToList();
        var issues = found.Count == 0 ? run.Issues : [.. run.Issues, .. found];
        return run with
        {
            PadShunts = [.. run.PadShunts, .. shunts],
            Issues = issues,
            Notes = [.. run.Notes, .. shunts.Where(sh => sh.Severity is null).Select(sh => sh.Text)],
            Verdict = TraceRun.VerdictOf(run.Z0Min is not null, issues),
        };
    }

    /// <summary>
    /// The pads reached from a trace's SIDE (brief-dfr29-2 R-dfr29-2-4): a drilled via on this layer, at no trace's
    /// end, joined to a trace by copper all the way from its centre line over less than <see cref="MinAspect"/> of the
    /// trace's widths — a stub too short to be a trace of its own, which the end-by-end search never sees, and which
    /// the via keep-out leaves unchecked as well. A stub long enough to be a trace ends at the via and is found there.
    /// <see cref="PadShuntAt"/> then decides whether there is a pad through it, by the same rule. A bare drill circle
    /// is a via here as everywhere in the search (brief-dfr29-3 R-dfr29-3-1); its land is measured only for a hole the
    /// copper already joins to a trace, since measuring is the costly part.
    /// </summary>
    private static IEnumerable<(int Run, TracePadShunt Shunt)> SidePadShunts(
        IReadOnlyList<TraceRun> runs, CrossSectionExtractor.Band band, PadSearch search, TraceStack ctx,
        TraceImpedanceReport fmt, TraceImpedanceOptions options)
    {
        if (runs.Count == 0 || ctx.Copper.GetValueOrDefault(band.Index) is not { } cu) yield break;
        static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        foreach (var via in search.Holes)
        {
            if (!via.Bands.Any(b => b.Index == band.Index) || !cu.Contains(via.X, via.Y)) continue;

            // The nearest trace, and the foot of the perpendicular on its centre line.
            int best = -1;
            double bestD = double.MaxValue, fx = 0, fy = 0;
            for (int ri = 0; ri < runs.Count; ri++)
                foreach (var (x0, y0, x1, y1, _) in runs[ri].Pieces)
                {
                    double dx = x1 - x0, dy = y1 - y0, l2 = dx * dx + dy * dy;
                    double t = l2 > 0 ? Math.Clamp(((via.X - x0) * dx + (via.Y - y0) * dy) / l2, 0, 1) : 0;
                    double px = x0 + t * dx, py = y0 + t * dy, d = Dist(px, py, via.X, via.Y);
                    if (d < bestD) (best, bestD, fx, fy) = (ri, d, px, py);
                }
            if (best < 0) continue;
            var run = runs[best];
            double w = run.WidthMax;
            if (!Joined(cu, fx, fy, via.X, via.Y, w)) continue;
            double land = via.Land;
            if (bestD - w / 2 - land / 2 > MinAspect * w) continue;
            if (runs.Any(r => Dist(r.StartX, r.StartY, via.X, via.Y) <= land + r.WidthMax
                              || Dist(r.EndX, r.EndY, via.X, via.Y) <= land + r.WidthMax)) continue;

            string where = bestD > w / 2 ? $"this trace through a {fmt.Len(bestD - w / 2)} stub" : "this trace";
            if (PadShuntAt(via.X, via.Y, w, band, search, ctx, fmt, options.MaxFrequencyHz, run.Z0Mean ?? options.TargetOhms, where) is { } s)
                yield return (best, s);
        }
    }

    /// <summary>
    /// |S11| of a pad hanging off a matched line of <paramref name="z0"/>, at <paramref name="hz"/>: the shunt's
    /// admittance Y gives S11 = −YZ0 / (2 + YZ0). Through a via, Y is what the TESTPAD's own two-port presents at its
    /// line pin with the pad pin open — the via's inductance in series with the barrel, the unused lands and the pad,
    /// so the series resonance is in it; a pad joined directly is its capacitance alone.
    /// </summary>
    internal static double PadShuntS11(TestPadGeometry g, bool viaFed, double hz, double z0)
    {
        double w = 2 * Math.PI * hz;
        System.Numerics.Complex y;
        if (!viaFed) y = new System.Numerics.Complex(0, w * g.PadCapacitance);
        else
        {
            var (y11, y12, y22) = new TestPadModel(g).Admittance(w);
            y = y11 - y12 * y12 / y22;
        }
        var yz = y * z0;
        return (yz / (2 + yz)).Magnitude;
    }

    /// <summary>
    /// The round pad at one trace end, or null. When a drilled via stands at the end, the pad is a disc on another
    /// conductor the via reaches, wider than the via's land and with no other copper joined to it — and a via with no
    /// such disc is a layer change, not a pad. Otherwise the pad is a disc on the trace's own conductor that the end
    /// runs into. (A via whose land is drawn as a disc reads as a "pad" end to the trace walk, so the end's kind is
    /// not trusted to say which.) Through a via, the pad may also be read off the copper: a round outline on the
    /// via's own island, over the via or joined to its land by a stub too short to be a trace (brief-dfr29-3).
    /// </summary>
    internal static TracePadShunt? PadShuntAt(long ex, long ey, double width, CrossSectionExtractor.Band band,
                                              PadSearch search, TraceStack ctx, TraceImpedanceReport fmt, double? fMax, double lineOhms,
                                              string where)
    {
        var tech = ctx.Tech;
        static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        RoundPad? pad = null;
        CrossSectionExtractor.Band? padBand = null;
        double stub = 0;
        double landReach = LandReachMicrons * ctx.DbuPerMicron;
        // A bare drill's land is measured, which costs a scan on every conductor it passes: only one the copper joins to
        // this end is measured — not every via in the pour beside it.
        var endCopper = ctx.Copper.GetValueOrDefault(band.Index);
        var via = search.Holes.Where(v => !v.Bare
                                          || Dist(v.X, v.Y, ex, ey) <= landReach + width && Joined(endCopper, ex, ey, v.X, v.Y, width))
                              .Where(v => Dist(v.X, v.Y, ex, ey) <= v.Land + width)
                              .MinBy(v => Dist(v.X, v.Y, ex, ey));
        if (via is not null)
        {
            double land = via.Land;
            foreach (var b in via.Bands)
            {
                if (b.Index == band.Index) continue;
                var cu = ctx.Copper.GetValueOrDefault(b.Index);
                var disc = search.DiscsOn(b.Index)
                                 .Where(c => 2 * c.R > PadOverLand * land && Dist(c.Cx, c.Cy, via.X, via.Y) <= c.R)
                                 .MaxBy(c => c.R);
                if (disc is not null && Isolated(disc.Cx, disc.Cy, disc.R, cu))
                    pad = new RoundPad(disc.Cx, disc.Cy, disc.R, Fitted: false);
                else if (cu is not null && IslandPad(via, b.Index, cu, search, ctx) is { } found)
                    (pad, stub) = found;
                else continue;
                padBand = b;
                break;
            }
            if (pad is null) return null;
        }
        else
        {
            pad = search.DiscsOn(band.Index)
                        .Where(c => 2 * c.R > PadOverLand * width && Dist(c.Cx, c.Cy, ex, ey) <= c.R + width)
                        .MinBy(c => Dist(c.Cx, c.Cy, ex, ey)) is { } c0
                ? new RoundPad(c0.Cx, c0.Cy, c0.R, Fitted: false) : null;
            padBand = band;
        }
        if (pad is not { } p0 || padBand is null) return null;
        var (padX, padY, padR) = (p0.Cx, p0.Cy, p0.R);

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
                double r = Math.Sqrt((ring + 0.5) / 4) * padR;
                for (int k = 0; k < 8; k++)
                    if (cu.Contains(padX + r * Math.Cos(k * Math.PI / 4), padY + r * Math.Sin(k * Math.PI / 4))) hit++;
            }
            return hit >= 16;
        }

        // The planes the via passes: those whose drawn copper rings the barrel, at the clearance drawn — measured
        // outside the land when the via carries one there (brief-dfr26-8), since the land is copper too and would
        // otherwise read as a clearance no wider than the drill.
        var clearance = new Dictionary<string, double>(StringComparer.Ordinal);
        var lands = new Dictionary<string, double>(StringComparer.Ordinal);
        if (via is not null)
            foreach (var b in via.Bands)
            {
                if (b.Index == band.Index || b.Index == padBand.Index || !Drawn(b)) continue;
                var (land, cleared) = search.Ring(via, b.Index, () =>
                {
                    var cu = ctx.Copper[b.Index];
                    double rDrill = Math.Max(via.Drill, 1) / 2.0;
                    double? land = MeasuredLand(cu, via.X, via.Y, rDrill, ctx.DbuPerMicron);
                    double from = land is { } edge ? edge + 5.0 * ctx.DbuPerMicron : rDrill;   // past the land's own rim
                    return (land, MeasuredClearance(cu, via.X, via.Y, from, ctx.DbuPerMicron));
                });
                if (cleared is not { } d) continue;
                clearance[b.Layer.Name] = d;
                if (land is { } l) lands[b.Layer.Name] = 2 * l;
            }

        var (stack, _, _) = SubstrateResolver.ResolveTestPad(tech, padBand.Layer.Name, band.Layer.Name, via is not null,
                                                             [.. clearance.Keys], Faces);
        if (stack is null) return null;

        double m = ctx.MetresPerDbu;
        double antipad = (via is null ? 0 : via.Land * m) + ComponentModelFactory.DefaultViaAntipadRingMeters;
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
            Diameter: 2 * padR * m,
            Faces: [.. stack.Faces.Select(f => new TestPadFacing(f.HeightMeters, f.EpsR))],
            Drill: via is null ? 0 : via.Drill * m,
            ViaLength: stack.ViaLengthMeters,
            ViaEpsR: stack.Via?.MaxRelativePermittivity ?? 1,
            Planes: planes,
            InnerPads: [.. unused.Select(u => u.Pad)]);
        var unusedPads = unused.Select(u => new TraceUnusedPad(u.Layer, u.D, u.B, geometry.InnerPadCapacitanceOf(u.Pad))).ToList();

        string G(double v) => v.ToString("G3", System.Globalization.CultureInfo.InvariantCulture);
        var refs = stack.Faces.Select(f => f.Plane.Name).ToList();
        double? resonance = via is null || !double.IsFinite(geometry.ResonanceHz) ? null : geometry.ResonanceHz;
        // brief-dfr29-3 R-dfr29-3-4: a hole and a pad READ off an imported board say so; owner decision D1 (a): the stub
        // joining a pad to its via's land stays out of the numbers, as it does in the TESTPAD, and is named instead.
        long cx = (long)Math.Round(padX), cy = (long)Math.Round(padY), diameter = (long)Math.Round(2 * padR);
        string head =
            $"A round pad {fmt.Len(diameter)} across{(p0.Fitted ? ", fitted to its outline," : "")} on '{padBand.Layer.Name}' " +
            $"hangs off {where} at {fmt.Pt(cx, cy)}" +
            (via is null ? "" : via.Bare ? $", through a {fmt.Len(via.Drill)} drill hole from '{band.Layer.Name}'"
                                         : $", through a via from '{band.Layer.Name}'") +
            (stub > 0 ? $", joined by a {fmt.Len(stub)} trace on '{padBand.Layer.Name}' the estimate leaves out" : "") + ": " +
            (refs.Count > 0 ? $"{G(geometry.PadCapacitance * 1e12)} pF to {string.Join(" and ", refs.Select(r => $"'{r}'"))}"
                            : "no plane faces it") +
            (via is not null
                ? $", the via {G(geometry.ViaInductance * 1e9)} nH" +
                  (geometry.BarrelCapacitance > 0 ? $" with {G(geometry.BarrelCapacitance * 1e12)} pF to the planes it passes" : "") +
                  (unusedPads.Count > 0
                      ? $", {unusedPads.Count} unused pad{(unusedPads.Count == 1 ? "" : "s")} {G(geometry.InnerPadCapacitance * 1e12)} pF (" +
                        string.Join(", ", unusedPads.Select(u => $"'{u.Layer}' {fmt.Len(u.Diameter)} in {fmt.Len(u.Antipad)}, {G(u.Capacitance * 1e12)} pF")) + ")"
                      : "") +
                  (resonance is { } fr ? $", resonating at {G(fr / 1e9)} GHz" : "")
                : "");

        // brief-dfr29-2 R-dfr29-2-1: judged at the highest frequency asked, against the line it loads (owner D1).
        double? s11Db = null;
        IssueSeverity? severity = null;
        string verdict;
        if (fMax is { } f)
        {
            s11Db = 20 * Math.Log10(Math.Max(PadShuntS11(geometry, via is not null, f, lineOhms), 1e-12));
            bool inBand = resonance < f, nearBand = resonance < 2 * f;
            severity = s11Db > PadShuntFailDb || inBand ? IssueSeverity.Fail
                     : s11Db > PadShuntWarnDb || nearBand ? IssueSeverity.Warning
                     : null;
            verdict = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                          $": |S11| {s11Db:0.0} dB at {TraceImpedanceReport.Hz(f)} on the {lineOhms:0.#} Ω line") +
                      (inBand ? ", resonating inside the band" : nearBand ? ", resonating below twice that" : "") + ".";
        }
        else verdict = ". Not judged: no highest frequency was given.";
        string text = head + verdict +
            " An estimate, of which an EM run is the reference." +
            (unusedPads.Count > 0 ? " Removing unused pads (a CAM option) removes their share." : "");
        return new TracePadShunt(false, cx, cy, diameter, padBand.Layer.Name, refs, geometry.PadCapacitance,
                                 via is null ? null : geometry.ViaInductance, geometry.BarrelCapacitance, resonance, text,
                                 unusedPads)
        {
            JudgedAtHz = fMax, LineOhms = lineOhms, S11Db = s11Db, Severity = severity,
        };
    }

    /// <summary>No copper on the disc's conductor just outside it, all round: nothing else is joined to the pad.</summary>
    private static bool Isolated(double cx, double cy, double r, TraceCopper? copper)
    {
        if (copper is null) return true;
        for (int k = 0; k < 16; k++)
        {
            double a = k * Math.PI / 8;
            foreach (double f in new[] { 1.2, 1.6 })
                if (copper.Contains(cx + f * r * Math.Cos(a), cy + f * r * Math.Sin(a))) return false;
        }
        return true;
    }

    /// <summary>A drilled hole the pad search looks through (brief-dfr29-3 R-dfr29-3-1): a <see cref="ViaShape"/>, or a
    /// bare circle on a drawing layer some <see cref="StackupKind.Via"/> entry claims — R-via-5's identity for a barrel,
    /// the one DRC connectivity and every export already use, which is how an imported drill file arrives when its hole
    /// was left unpaired. Lengths DBU.</summary>
    internal sealed class DrillHole(long x, long y, double drill, bool bare, IReadOnlyList<CrossSectionExtractor.Band> bands,
                                    Func<double> land)
    {
        private double? _land;
        public long X { get; } = x;
        public long Y { get; } = y;
        public double Drill { get; } = drill;
        /// <summary>A bare drill circle, not a drawn via.</summary>
        public bool Bare { get; } = bare;
        /// <summary>The conductors the barrel passes, the span its via entry states.</summary>
        public IReadOnlyList<CrossSectionExtractor.Band> Bands { get; } = bands;
        /// <summary>The land's diameter: a via's PadSize; for a bare circle, measured on first use.</summary>
        public double Land => _land ??= land();
    }

    /// <summary>A round pad, drawn as a disc or fitted to an outline. DBU.</summary>
    private readonly record struct RoundPad(double Cx, double Cy, double R, bool Fitted);

    /// <summary>How far from a trace end a bare drill circle is looked at before its land is measured: the reach of
    /// <see cref="MeasuredLand"/>.</summary>
    private const double LandReachMicrons = 5_000;

    /// <summary>A via's island wider or taller than this is not a land, a stub and a pad — it is a pour or a route — and
    /// is not fitted.</summary>
    private const double IslandReachMicrons = 10_000;

    /// <summary>A run of outline vertices is round when every vertex and every edge's midpoint is within this fraction
    /// of the radius of the circle fitted to them (brief-dfr29-3 R-dfr29-3-2). The midpoints are what keep a rectangle
    /// out: its four corners lie on one circle exactly, its sides nowhere near it.</summary>
    private const double RoundTolerance = 0.02;

    /// <summary>…and when it sweeps at least this much: a pad joined by a stub is round everywhere but where the stub meets
    /// it.</summary>
    private const double MinPadSweepDeg = 240;

    /// <summary>What the pad search reads, gathered once a run rather than once a trace end: every drilled hole, every
    /// disc by conductor, and each conductor's ring bounds as the island search first asks for them.</summary>
    internal sealed class PadSearch(List<DrillHole> holes, Dictionary<int, List<CircleShape>> discs)
    {
        private readonly Dictionary<int, (Rect64 Box, double Area)[]> _rings = [];
        private readonly Dictionary<(DrillHole, int), (double? Land, double? Clearance)> _round = [];
        public List<DrillHole> Holes { get; } = holes;
        public IEnumerable<CircleShape> DiscsOn(int band) => discs.TryGetValue(band, out var l) ? l : [];

        /// <summary>The land and clearance measured round <paramref name="hole"/> on one conductor — a radial scan each,
        /// taken once however many traces reach the pad.</summary>
        public (double? Land, double? Clearance) Ring(DrillHole hole, int band, Func<(double?, double?)> measure)
        {
            if (!_round.TryGetValue((hole, band), out var r)) _round[(hole, band)] = r = measure();
            return r;
        }

        /// <summary><paramref name="cu"/>'s rings' bounds and areas, index for index with its paths.</summary>
        public (Rect64 Box, double Area)[] RingsOf(int band, TraceCopper cu)
        {
            if (!_rings.TryGetValue(band, out var r))
                _rings[band] = r = [.. cu.Paths.Select(p => (Clipper.GetBounds(p), Math.Abs(Clipper.Area(p))))];
            return r;
        }
    }

    /// <summary>The pad search over <paramref name="shapes"/>.</summary>
    private static PadSearch PadSearchOf(IReadOnlyList<LayoutShape> shapes, TraceStack ctx)
    {
        var drillLayers = ViaSpanResolver.DrillLayerKeys(ctx.Tech);
        var holes = new List<DrillHole>();
        var discs = new Dictionary<int, List<CircleShape>>();
        foreach (var shape in shapes)
        {
            if (shape is CircleShape d && ctx.BandOf.TryGetValue(d.Layer, out var db))
                (discs.TryGetValue(db.Index, out var l) ? l : discs[db.Index] = []).Add(d);
            if (shape is ViaShape v && v.DrillSize > 0)
                holes.Add(new DrillHole(v.X, v.Y, v.DrillSize, false, [.. BandsSpanned(v.Layer, ctx.Tech, ctx.Bands)],
                                        () => Math.Max(v.PadSize, v.DrillSize)));
            else if (shape is CircleShape c && c.R > 0 && drillLayers.Contains(c.Layer))
            {
                List<CrossSectionExtractor.Band> bands = [.. BandsSpanned(c.Layer, ctx.Tech, ctx.Bands)];
                holes.Add(new DrillHole(c.Cx, c.Cy, 2 * c.R, true, bands, () => MeasuredHoleLand(c.Cx, c.Cy, c.R, bands, ctx)));
            }
        }
        return new PadSearch(holes, discs);
    }

    /// <summary>Copper all the way from (x0, y0) to (x1, y1), sampled every quarter of <paramref name="width"/>.</summary>
    private static bool Joined(TraceCopper? cu, double x0, double y0, double x1, double y1, double width)
    {
        if (cu is null) return false;
        double d = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int steps = Math.Max(2, (int)Math.Ceiling(d / Math.Max(width / 4, 1)));
        for (int k = 0; k <= steps; k++)
            if (!cu.Contains(x0 + (x1 - x0) * k / steps, y0 + (y1 - y0) * k / steps)) return false;
        return true;
    }

    /// <summary>A bare drill's land: the largest <see cref="MeasuredLand"/> finds on the conductors its barrel passes
    /// between its two ends — at an end the land may be part of a pad, a stub or a trace. With nothing between them (a
    /// blind laser drill spans two adjacent conductors), the smaller of the two ends' lands, since a pad is on one end at
    /// most; else twice the drill. Guessing twice the drill there read a blind drill's own land at its far end, a little
    /// wider than that, as a test pad.</summary>
    private static double MeasuredHoleLand(long x, long y, double rDrill, List<CrossSectionExtractor.Band> bands, TraceStack ctx)
    {
        double? Land(CrossSectionExtractor.Band b) =>
            ctx.Copper.GetValueOrDefault(b.Index) is { Paths.Count: > 0 } cu && MeasuredLand(cu, x, y, rDrill, ctx.DbuPerMicron) is { } r
                ? 2 * r : null;
        double land = 0;
        for (int i = 1; i < bands.Count - 1; i++)
            if (Land(bands[i]) is { } l) land = Math.Max(land, l);
        if (land > 0) return land;
        var ends = bands.Count == 0 ? [] : new[] { bands[0], bands[^1] }.Distinct().Select(Land).OfType<double>().ToList();
        return ends.Count > 0 ? ends.Min() : 4 * rDrill;
    }

    /// <summary>
    /// The test pad on <paramref name="hole"/>'s copper island on one conductor, or null (brief-dfr29-3 R-dfr29-3-3).
    /// The island is the copper joined to the hole's land there. It is a test pad when its outline holds exactly one
    /// round pad wider than <see cref="PadOverLand"/> × the land; that pad is over the land or its edge is within
    /// <see cref="MinAspect"/> × the joining stub's width of the land's edge; no other hole is in the island; and the
    /// island holds nothing but the land, the stub and the pad — all of it inside the convex hull of the land's circle
    /// and the pad's, so a trace this run reviews, or would review, leaving either one is never a test pad.
    /// Returns the pad and the stub's length (0 over the land).
    /// </summary>
    private static (RoundPad Pad, double Stub)? IslandPad(DrillHole hole, int band, TraceCopper cu, PadSearch search,
                                                         TraceStack ctx)
    {
        if (!cu.Contains(hole.X, hole.Y)) return null;
        // The island is the innermost ring round the hole — the smallest that holds it, the hole being in copper. A ring
        // wider than the reach is never one, so a via in a pour is settled without walking the pour's outline.
        var at = new Point64(hole.X, hole.Y);
        double reach = IslandReachMicrons * ctx.DbuPerMicron;
        Path64? island = null;
        double least = double.MaxValue;
        var rings = search.RingsOf(band, cu);
        for (int k = 0; k < rings.Length; k++)
        {
            var (box, area) = rings[k];
            if (area >= least || cu.Paths[k].Count < 3 || box.Width > reach || box.Height > reach
                || at.X < box.left || at.X > box.right || at.Y < box.top || at.Y > box.bottom) continue;
            if (Clipper.PointInPolygon(at, cu.Paths[k]) == PointInPolygonResult.IsOutside) continue;
            (island, least) = (cu.Paths[k], area);
        }
        if (island is null) return null;
        var bounds = Clipper.GetBounds(island);

        var pads = RoundOutlines(island).Where(c => 2 * c.R > PadOverLand * hole.Land).ToList();
        if (pads.Count != 1) return null;
        var pad = pads[0];
        foreach (var other in search.Holes)
            if (!ReferenceEquals(other, hole) && other.X >= bounds.left && other.X <= bounds.right
                && other.Y >= bounds.top && other.Y <= bounds.bottom
                && Clipper.PointInPolygon(new Point64(other.X, other.Y), island) != PointInPolygonResult.IsOutside)
                return null;

        // The land here, as drawn on this conductor when it is wider than the barrel's own.
        double rLand = Math.Max(hole.Land / 2, MeasuredLand(cu, hole.X, hole.Y, hole.Drill / 2, ctx.DbuPerMicron) ?? 0);
        double dx = pad.Cx - hole.X, dy = pad.Cy - hole.Y, d = Math.Sqrt(dx * dx + dy * dy);
        double stub = 0;
        if (d > pad.R && d - pad.R - rLand > 0)
        {
            stub = d - pad.R - rLand;
            if (stub > MinAspect * StubWidth(island, hole.X, hole.Y, rLand, pad)) return null;
        }
        double tol = 1.5 * RoundTolerance * pad.R + 10.0 * ctx.DbuPerMicron;
        foreach (var v in island)
            if (HullExcess(v.X, v.Y, hole.X, hole.Y, rLand, pad.Cx, pad.Cy, pad.R) > tol) return null;
        return (new RoundPad(pad.Cx, pad.Cy, pad.R, Fitted: true), stub);
    }

    /// <summary>The width of the copper joining a land to a pad: what is left of the island outside the two circles,
    /// eroded until nothing is left — twice the erosion is its width (or its length, where that is the less, and then the
    /// stub is short whatever its width). A stub may bend, so no one chord across it is trusted to find it.</summary>
    private static double StubWidth(Path64 island, double hx, double hy, double rLand, CircleFit pad)
    {
        double slack = RoundTolerance * pad.R + 1;
        var discs = Clipper.Union([Clipper.Ellipse(new Point64(hx, hy), rLand + slack, rLand + slack, 64),
                                   Clipper.Ellipse(new Point64(pad.Cx, pad.Cy), pad.R + slack, pad.R + slack, 64)],
                                  FillRule.NonZero);
        var rest = Clipper.Difference([island], discs, FillRule.NonZero);
        if (rest.Count == 0) return 0;
        double lo = 0, hi = pad.R;
        for (int i = 0; i < 16; i++)
        {
            double r = (lo + hi) / 2;
            if (Clipper.InflatePaths(rest, -r, JoinType.Miter, EndType.Polygon).Count > 0) lo = r; else hi = r;
        }
        return 2 * hi;
    }

    /// <summary>How far (px, py) lies outside the convex hull of two circles, DBU (≤ 0 inside). That hull is the union of
    /// the discs whose centre and radius run linearly from one circle to the other, so the excess is the least, over that
    /// run, of the distance to the centre less the radius — a convex function of the run's parameter.</summary>
    private static double HullExcess(double px, double py, double ax, double ay, double ra, double bx, double by, double rb)
    {
        double F(double t)
        {
            double cx = ax + t * (bx - ax), cy = ay + t * (by - ay);
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - (ra + t * (rb - ra));
        }
        double lo = 0, hi = 1;
        for (int i = 0; i < 60; i++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (F(m1) <= F(m2)) hi = m2; else lo = m1;
        }
        return Math.Min(F((lo + hi) / 2), Math.Min(F(0), F(1)));
    }

    /// <summary>
    /// The circles an outline holds (brief-dfr29-3 R-dfr29-3-2): each run of consecutive vertices that lie on one circle
    /// — least squares, every vertex and every edge's midpoint within <see cref="RoundTolerance"/> of its radius — and
    /// sweep at least <see cref="MinPadSweepDeg"/>. A whole-region fit (<c>ImageTrace.CircleOf</c>) would not fit a pad
    /// with a stub on it; this finds the pad's arc either side of where the stub joins.
    /// </summary>
    internal static List<CircleFit> RoundOutlines(Path64 ring)
    {
        var found = new List<(CircleFit Fit, double Sweep)>();
        int n = ring.Count;
        if (n < 8) return [];
        PointD P(int k) => new(ring[k % n].X, ring[k % n].Y);
        static PointD Mid(PointD a, PointD b) => new((a.x + b.x) / 2, (a.y + b.y) / 2);
        static bool On(double cx, double cy, double r, PointD p)
            => Math.Abs(Math.Sqrt((p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy)) - r) <= Math.Max(RoundTolerance * r, 1);

        // The circle through three points, or null when they are in a line.
        static (double Cx, double Cy, double R)? Through(PointD a, PointD b, PointD c)
        {
            double det = 2 * ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
            if (Math.Abs(det) < 1e-9) return null;
            double a2 = a.x * a.x + a.y * a.y, b2 = b.x * b.x + b.y * b.y, c2 = c.x * c.x + c.y * c.y;
            double cx = (a2 * (b.y - c.y) + b2 * (c.y - a.y) + c2 * (a.y - b.y)) / det;
            double cy = (a2 * (c.x - b.x) + b2 * (a.x - c.x) + c2 * (b.x - a.x)) / det;
            return (cx, cy, Math.Sqrt((a.x - cx) * (a.x - cx) + (a.y - cy) * (a.y - cy)));
        }
        static double SweepDeg(List<PointD> pts, double cx, double cy)
        {
            double sweep = 0;
            for (int k = 1; k < pts.Count; k++)
            {
                double ux = pts[k - 1].x - cx, uy = pts[k - 1].y - cy, vx = pts[k].x - cx, vy = pts[k].y - cy;
                sweep += Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            }
            return Math.Abs(sweep) * 180 / Math.PI;
        }

        int i = 0;
        while (i < n)
        {
            if (Through(P(i), P(i + 1), P(i + 2)) is not var (cx, cy, r)
                || !On(cx, cy, r, Mid(P(i), P(i + 1))) || !On(cx, cy, r, Mid(P(i + 1), P(i + 2)))) { i++; continue; }

            // Grow while the next edge stays on the circle — re-taken through the run's first, middle and last vertices
            // as the run doubles, which costs nothing; the least-squares fit is left to a run that may qualify.
            int j = i + 2, retake = 6;
            while (j + 1 <= i + n && On(cx, cy, r, P(j + 1)) && On(cx, cy, r, Mid(P(j), P(j + 1))))
            {
                j++;
                if (j - i + 1 < retake) continue;
                if (Through(P(i), P((i + j) / 2), P(j)) is var (tx, ty, tr)) (cx, cy, r) = (tx, ty, tr);
                retake *= 2;
            }

            var pts = Enumerable.Range(i, j - i + 1).Select(P).ToList();
            if (SweepDeg(pts, cx, cy) < MinPadSweepDeg - 10) { i = Math.Max(i + 1, j - 1); continue; }
            var fit = Fit.Circle(pts);
            bool round = pts.All(p => On(fit.Cx, fit.Cy, fit.R, p))
                         && pts.Zip(pts.Skip(1), Mid).All(p => On(fit.Cx, fit.Cy, fit.R, p));
            double sweep = SweepDeg(pts, fit.Cx, fit.Cy);
            if (round && sweep >= MinPadSweepDeg - 1e-6)
            {
                int same = found.FindIndex(o => Math.Abs(o.Fit.R - fit.R) <= RoundTolerance * fit.R
                                                && Math.Sqrt((o.Fit.Cx - fit.Cx) * (o.Fit.Cx - fit.Cx) + (o.Fit.Cy - fit.Cy) * (o.Fit.Cy - fit.Cy))
                                                   <= RoundTolerance * fit.R);
                if (same < 0) found.Add((fit, sweep));
                else if (sweep > found[same].Sweep) found[same] = (fit, sweep);
            }
            if (j >= i + n) break;   // the whole outline is one circle
            i = Math.Max(i + 1, j - 1);
        }
        return [.. found.Select(f => f.Fit)];
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
