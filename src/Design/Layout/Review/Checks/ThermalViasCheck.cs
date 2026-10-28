// `thermal-vias` — a thermal pad without a via farm dense enough to pull the heat into the planes
// (brief-review-3 R-rev3-9). A thermal pad is a ground pad of at least `thermal-pad-area`, the threshold
// all four pad checks share and this check declares. Vias counted are ground vias whose barrel lies at
// least half inside the pad; a thermal pad with none at all is always a finding.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class ThermalViasCheck : ReviewCheck
{
    public const string Id = "thermal-vias";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Thermal vias",
        "A thermal pad (a ground pad of at least the thermal pad area) with too few vias per mm² under it. The "
      + "thermal pad area is shared by all four pad checks.",
        [
            new("thermal-pad-area", "Thermal pad area", ReviewThresholdKind.Area, 2.0),
            new("min-via-density", "Minimum via density", ReviewThresholdKind.Density, 1.0),
        ]);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var pads = ctx.Pads;
        if (pads.Refusal is { } why) { run.NotRun(why); return; }

        double want = run.Number("min-via-density");
        double perMm2 = ReviewPads.Dbu2PerMm2(ctx.DbuPerMicron);
        var inv = CultureInfo.InvariantCulture;

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

            double area = pad.AreaDbu2 / perMm2;
            double density = counted.Count / area;
            if (counted.Count > 0 && density >= want) continue;

            var marker = new Paths64(pad.Region);
            foreach (var via in counted) marker.AddRange(via.Region);
            string measured = $"{density.ToString("0.00", inv)} per mm²";
            string what = counted.Count == 1 ? "1 via" : $"{counted.Count} vias";
            run.Add(pad.Copper,
                $"{what} under a {area.ToString("0.0", inv)} mm² thermal pad{(pad.Name is { } n ? $" ({n})" : "")} "
              + $"({measured}); the review asks for {want.ToString("0.0##", inv)} per mm²",
                measured, marker);
        }
    }
}
