// The pads, vias and ground the four pad checks share (brief-review-3 R-rev3-1…5), built ONCE per run on
// the run's own regions, partition and evaluator — nothing re-flattened, nothing measured twice.
//
// A PAD is copper a mask opening exposes, per side: `and(outer copper, soldermask)`. It is the one
// definition that holds on a Gerber import (no parts, no pins) and on a native layout (a footprint draws
// its mask opening through LandPatternLayers). Placed parts only NAME a pad ("C209 pad 2"); they never
// change which pads exist.
//
// A VIA is a polygon of a plated drill layer — a stackup Via entry's drawing layer whose holes are metal
// (`Plated` is not false). A ViaShape's barrel and a bare circle on a drill layer arrive the same way,
// through DrcRegions.Expand. A non-plated drill is a hole, not a via. The brief named `Fill = Plated`;
// Fill is a fill MODEL and both of its values conduct (StackupLayer.Plated says why), so `Plated` is the
// field that answers "is this hole metal".
//
// A THROUGH-HOLE PIN is a pad holding exactly one plated drill with the pad's centroid within the drill's
// radius of its centre (D2): the drill is the pin's hole, and is no via for any check here. A pin is not
// an SMD pad either, so the ground checks do not ask it for a via: its own barrel is its path to ground.
//
// GROUND is on_ground's — the partition's via walk for an undrawn reference, the net of the largest copper
// on a drawn one (DrcRegionEval.GroundNets). No reference flagged, or no net read as ground: all four
// checks are not run, because the thermal-pad exemption via-in-pad honours needs ground too.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Review;

internal sealed class ReviewPads
{
    /// <summary>One pad: a connected piece of copper ∩ mask opening on one side.</summary>
    internal sealed class Pad
    {
        public required PlacementSide Side { get; init; }
        public required LayerKey Copper { get; init; }
        public required Paths64 Region { get; init; }
        public required Bbox Bounds { get; init; }
        public required double AreaDbu2 { get; init; }
        public bool Ground { get; set; }
        public bool Thermal { get; set; }

        /// <summary>A through-hole pin's pad (R-rev3-3).</summary>
        public bool Pin { get; set; }

        /// <summary>"C209 pad 2" where a placed part's pin lies in it, else null.</summary>
        public string? Name { get; set; }

        /// <summary>Indices into <see cref="Vias"/> of the plated drills that overlap or touch it.</summary>
        public List<int> Drills { get; set; } = [];
    }

    /// <summary>One plated drill.</summary>
    internal sealed class Via
    {
        public required LayerKey Layer { get; init; }
        public required Paths64 Region { get; init; }
        public required Bbox Bounds { get; init; }
        public bool Ground { get; set; }

        /// <summary>A through-hole pin's hole: no via for any check (R-rev3-3).</summary>
        public bool PinHole { get; set; }

        /// <summary>The copper indices (<see cref="ReviewCopperLayer.Index"/>) it spans, top first; null
        /// when an entry claiming its layer names no resolvable span, and then it reaches every side.</summary>
        public (int Top, int Bottom)? Span { get; init; }

        public long Diameter => Math.Max(Bounds.MaxX - Bounds.MinX, Bounds.MaxY - Bounds.MinY);
        public (long X, long Y) Centre => ((Bounds.MinX + Bounds.MaxX) / 2, (Bounds.MinY + Bounds.MaxY) / 2);
    }

    public IReadOnlyList<Pad> Pads { get; }
    public IReadOnlyList<Via> Vias { get; }

    /// <summary>The one sentence every pad check reports as not run, or null.</summary>
    public string? Refusal { get; }

    private readonly ReviewContext? _ctx;
    private readonly Dictionary<long, List<List<int>>> _groundViasNear = new();

    private ReviewPads(string refusal) { Pads = []; Vias = []; Refusal = refusal; }

    private ReviewPads(ReviewContext ctx, List<Pad> pads, List<Via> vias)
    {
        _ctx = ctx;
        Pads = pads;
        Vias = vias;
    }

    /// <summary>Whether <paramref name="via"/> reaches the copper of <paramref name="pad"/>'s side.</summary>
    public bool Reaches(Via via, Pad pad)
    {
        if (via.Span is not { } span || _ctx is null) return true;
        int at = _ctx.Layers.Copper.FirstOrDefault(c => c.DrawingLayers.Contains(pad.Copper))?.Index ?? -1;
        return at < 0 || (at >= span.Top && at <= span.Bottom);
    }

    /// <summary>The ground pads the ground-via checks judge: not thermal, not a through-hole pin.</summary>
    public IReadOnlyList<Pad> JudgedGroundPads => [.. Pads.Where(p => p.Ground && !p.Thermal && !p.Pin)];

    /// <summary>
    /// For each of <see cref="JudgedGroundPads"/>, the indices into <see cref="Vias"/> of the ground
    /// vias reaching its side within <paramref name="distance"/> — at most two, which is all either
    /// ground-via check asks (none; exactly one). <c>near</c>'s own routine with the identities kept
    /// (<see cref="DrcRegionEval.WithinEach"/>), asked once per distance and shared by both checks.
    /// </summary>
    public List<List<int>> GroundViasNear(long distance)
    {
        if (_groundViasNear.TryGetValue(distance, out var hit)) return hit;
        var pads = JudgedGroundPads;
        var result = new List<List<int>>(pads.Count);
        // Pads are listed side by side (top, then bottom), so the groups concatenate in their order.
        foreach (var side in pads.GroupBy(p => p.Side))
        {
            var sidePads = side.ToList();
            var candidates = Enumerable.Range(0, Vias.Count)
                .Where(i => Vias[i].Ground && !Vias[i].PinHole && Reaches(Vias[i], sidePads[0])).ToList();
            var near = _ctx!.Eval.WithinEach([.. sidePads.Select(p => p.Region)],
                                             [.. candidates.Select(i => Vias[i].Region)], distance, stopAt: 2);
            foreach (var list in near) result.Add([.. list.Select(j => candidates[j])]);
        }
        _groundViasNear[distance] = result;
        return result;
    }

    internal static ReviewPads Build(ReviewContext ctx)
    {
        var layers = ctx.Layers;
        if (layers.GroundReference is null)
            return new("No ground reference is flagged in this technology's stackup — flag the ground plane in "
                     + "the Technology Editor.");

        var sides = new List<(PlacementSide Side, LayerKey Copper, LayerKey Mask)>();
        foreach (var (side, copper, mask) in new[]
                 {
                     (PlacementSide.Top, layers.TopCopper, layers.TopMask),
                     (PlacementSide.Bottom, layers.BottomCopper, layers.BottomMask),
                 })
        {
            if (copper is not { } cu) continue;
            if (mask is { } m) { sides.Add((side, cu, m)); continue; }
            ctx.Diagnostics.Add($"The {side.ToString().ToLowerInvariant()} side has no soldermask layer, so the pad "
                              + "checks found no pads there.");
        }
        if (sides.Count == 0)
            return new("No soldermask layer of this technology says which side it is on, so the review cannot tell a pad "
                     + "from copper — name the mask layers \"Soldermask Top\" / \"Soldermask Bottom\", or give them the "
                     + "interchange names F.Mask / B.Mask, in the Technology Editor.");

        var drills = layers.Drills.Where(d => d.Entry.Plated != false).ToList();
        if (drills.Count == 0)
            return new("No via entry of this technology's stackup is a plated drill layer, so the review cannot tell a via "
                     + "from a hole — add the via entries, with their drawing layers, in the Technology Editor.");

        var eval = ctx.Eval;
        var ground = eval.GroundNets(out string? said);
        if (ground.Count == 0)
            return new($"No copper on this board is ground: {said}");
        if (eval.GroundPlanePiece is >= 0 and var plane)
        {
            double mm2 = DrcRegionEval.AreaOfComponent(ctx.Partition.Pieces[plane].Paths) / Dbu2PerMm2(ctx.DbuPerMicron);
            ctx.Diagnostics.Add($"Ground is the net of the largest copper on the ground reference "
                              + $"\"{layers.GroundReference.Name}\" ({mm2:0.#} mm²), and everything joined to it.");
        }

        // ── vias ─────────────────────────────────────────────────────────────────────────────────
        // One via per drill polygon, however many entries claim its layer: an imported technology can
        // bind two entries to one drill layer, and the connectivity walk then reads that layer's barrels
        // under BOTH spans — so here a via spans their union.
        var vias = new List<Via>();
        foreach (var claimed in drills.GroupBy(d => d.Layer))
        {
            var layer = claimed.Key;
            if (!ctx.Regions.TryGetValue(layer, out var region) || region.Count == 0) continue;
            var groundBoxes = Boxes(eval.Evaluate(new DrcLayerExpr.OnGround(new DrcLayerExpr.Layer(layer))));
            int lo = int.MaxValue, hi = int.MinValue;
            bool unresolved = false;
            foreach (var d in claimed)
            {
                int t = d.Top is { } top ? IndexOf(layers, top) : -1;
                int b = d.Bottom is { } bottom ? IndexOf(layers, bottom) : -1;
                if (t < 0 || b < 0) { unresolved = true; break; }
                lo = Math.Min(lo, Math.Min(t, b));
                hi = Math.Max(hi, Math.Max(t, b));
            }
            (int, int)? span = unresolved ? null : (lo, hi);
            foreach (var c in DrcRegions.Components(region))
            {
                var bounds = DrcRegions.BoundsOf(c);
                vias.Add(new Via { Layer = layer, Region = c, Bounds = bounds, Ground = groundBoxes.Contains(bounds), Span = span });
            }
        }

        // ── pads, side by side ───────────────────────────────────────────────────────────────────
        var result = new ReviewPads(ctx, [], vias);
        var pads = (List<Pad>)result.Pads;
        double thermal = ctx.Threshold("thermal-vias", "thermal-pad-area") * Dbu2PerMm2(ctx.DbuPerMicron);
        foreach (var (side, copper, mask) in sides)
        {
            var exposed = new DrcLayerExpr.And(new DrcLayerExpr.Layer(copper), new DrcLayerExpr.Layer(mask));
            var region = eval.Evaluate(exposed);
            if (region.Count == 0) continue;
            var groundBoxes = Boxes(eval.Evaluate(new DrcLayerExpr.OnGround(exposed)));

            var sidePads = DrcRegions.Components(region).Select(c =>
            {
                var bounds = DrcRegions.BoundsOf(c);
                return new Pad
                {
                    Side = side, Copper = copper, Region = c, Bounds = bounds,
                    AreaDbu2 = DrcRegionEval.AreaOfComponent(c), Ground = groundBoxes.Contains(bounds),
                };
            }).ToList();

            var candidates = Enumerable.Range(0, vias.Count).Where(i => result.Reaches(vias[i], sidePads[0])).ToList();
            var touching = eval.WithinEach([.. sidePads.Select(p => p.Region)], [.. candidates.Select(i => vias[i].Region)], 0);
            for (int k = 0; k < sidePads.Count; k++)
            {
                var pad = sidePads[k];
                pad.Drills = [.. touching[k].Select(j => candidates[j])];
                if (pad.Drills.Count == 1 && vias[pad.Drills[0]] is var hole && IsCentredOn(pad, hole))
                {
                    pad.Pin = true;
                    hole.PinHole = true;
                }
                pad.Thermal = pad.Ground && !pad.Pin && pad.AreaDbu2 >= thermal;
            }
            pads.AddRange(sidePads);
        }

        Name(ctx, pads);
        return result;
    }

    internal static double Dbu2PerMm2(int dbuPerMicron) => (1000.0 * dbuPerMicron) * (1000.0 * dbuPerMicron);

    private static int IndexOf(ReviewLayers layers, StackupLayer row) =>
        layers.Copper.FirstOrDefault(c => ReferenceEquals(c.Row, row))?.Index ?? -1;

    /// <summary>The bounding boxes of a selection's polygons — how a whole-polygon selection of a
    /// region is matched back to that region's own components (they are the same polygons).</summary>
    private static HashSet<Bbox> Boxes(Paths64 selected) =>
        [.. DrcRegions.Components(selected).Select(DrcRegions.BoundsOf)];

    /// <summary>R-rev3-3: the pad's area centroid within the drill's radius of the drill's centre.</summary>
    private static bool IsCentredOn(Pad pad, Via hole)
    {
        double sx = 0, sy = 0, sa = 0;
        foreach (var path in pad.Region)
            for (int i = 0; i < path.Count; i++)
            {
                var p = path[i];
                var q = path[(i + 1) % path.Count];
                double cross = (double)p.X * q.Y - (double)q.X * p.Y;
                sa += cross;
                sx += (p.X + q.X) * cross;
                sy += (p.Y + q.Y) * cross;
            }
        if (sa == 0) return false;
        double cx = sx / (3 * sa), cy = sy / (3 * sa);
        var (hx, hy) = hole.Centre;
        double dx = cx - hx, dy = cy - hy;
        return Math.Sqrt(dx * dx + dy * dy) <= hole.Diameter / 2.0;
    }

    /// <summary>Names each pad a placed part's pin lies in — for the sentence only (R-rev3-1).</summary>
    private static void Name(ReviewContext ctx, List<Pad> pads)
    {
        var view = ctx.Source.View;
        if (view.Instances.Count == 0 || pads.Count == 0) return;

        IReadOnlyList<PlacedPin> pins;
        var origins = new List<PlacedPinOrigin>();
        try
        {
            pins = PlacedPins.Of(view, ctx.Source.Path, ctx.Tech, PinNaming.ArtworkOnly, origins: origins);
        }
        catch (Exception ex)
        {
            ctx.Diagnostics.Add($"The pads are not named by part: {ex.Message}");
            return;
        }

        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin.Refdes is not { Length: > 0 } refdes) continue;
            LayerKey? layer = i < origins.Count ? origins[i].Layer : null;
            var at = new Point64(pin.X, pin.Y);
            foreach (var pad in pads)
            {
                if (pad.Name is not null || (layer is { } l && l != pad.Copper)) continue;
                if (!pad.Bounds.Contains(pin.X, pin.Y)) continue;
                if (pad.Region.Count == 0 || Clipper.PointInPolygon(at, pad.Region[0]) == PointInPolygonResult.IsOutside) continue;
                pad.Name = pin.Pin is { Length: > 0 } p ? $"{refdes} pad {p}" : refdes;
                break;
            }
        }
    }
}
