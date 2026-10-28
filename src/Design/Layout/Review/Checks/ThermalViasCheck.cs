// `thermal-vias` — a thermal pad without a via farm dense enough to pull the heat into the planes
// (brief-review-3 R-rev3-9). A thermal pad is a ground pad of at least `thermal-pad-area`, the threshold
// all four pad checks share and this check declares. Vias counted are ground vias whose barrel lies at
// least half inside the pad; a thermal pad with none at all is always a finding.
//
// How dense is said as a PITCH, a length in the layout's own unit, not as vias per mm²: a density has no
// readable spelling in µm or mil (1 per mm² is 0.000001 per µm²). A pitch p asks for one via per p × p of pad,
// so a pad passes when its area over its via count is at most p² — the same test as density ≥ 1/p², and the
// measured value is the pad's own average pitch, √(area / vias).

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class ThermalViasCheck : ReviewCheck
{
    public const string Id = "thermal-vias";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Thermal vias",
        "A thermal pad (a ground pad of at least the thermal pad area) with fewer vias under it than one per maximum "
      + "via pitch squared. The thermal pad area is shared by all four pad checks.",
        [
            new("thermal-pad-area", "Thermal pad area", ReviewThresholdKind.Area, 2.0),
            new("max-via-pitch", "Maximum via pitch", ReviewThresholdKind.Length, 1.0e-3),
        ]);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var pads = ctx.Pads;
        if (pads.Refusal is { } why) { run.NotRun(why); return; }

        long pitch = run.Length("max-via-pitch");
        double perMm2 = ReviewPads.Dbu2PerMm2(ctx.DbuPerMicron);

        foreach (var pad in pads.Pads.Where(p => p.Thermal))
        {
            var counted = new List<ReviewPads.Via>();
            foreach (int v in pad.Drills)
            {
                var via = pads.Vias[v];
                if (!via.Ground || via.PinHole) continue;
                var inside = Clipper.BooleanOp(ClipType.Intersection, via.Region, pad.Region, LayoutClipper.Rule);
                if (DrcRegionEval.AreaOfComponent(inside) * 2 >= DrcRegionEval.AreaOfComponent(via.Region)) counted.Add(via);
            }

            if (counted.Count > 0 && pad.AreaDbu2 / counted.Count <= (double)pitch * pitch) continue;

            var marker = new Paths64(pad.Region);
            foreach (var via in counted) marker.AddRange(via.Region);
            string area = ReviewText.Area(pad.AreaDbu2 / perMm2, ctx.Unit);
            string under = $"under a {area} thermal pad{(pad.Name is { } n ? $" ({n})" : "")}";
            string asks = $"the review asks for one per {ctx.Length(pitch)} pitch";
            if (counted.Count == 0)
            {
                run.Add(pad.Copper, $"No vias {under}; {asks}", "no vias", marker);
                continue;
            }
            string measured = $"{ctx.Length((long)Math.Round(Math.Sqrt(pad.AreaDbu2 / counted.Count)))} pitch";
            string what = counted.Count == 1 ? "1 via" : $"{counted.Count} vias";
            run.Add(pad.Copper, $"{what} {under}, one per {measured}; {asks}", measured, marker);
        }
    }
}
