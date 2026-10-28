// `ground-via-distance` — a ground pad with no ground via close by (brief-review-3 R-rev3-7). A long path
// from a ground pad to a via is inductance in series with the part's ground. The closest approach is pad
// edge to barrel edge, `near(pad, vias, d, 0, 0)` by near's own routine; a pad in a ground pour is judged
// the same way, because the question is the path to a via and not what the copper between is called.
// Thermal pads are thermal-vias' business; a through-hole pin's barrel is its own path.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class GroundViaDistanceCheck : ReviewCheck
{
    public const string Id = "ground-via-distance";

    /// <summary>How far past the threshold the nearest via is still searched for and stated.</summary>
    private const int SearchFactor = 10;

    public override ReviewCheckDef Def { get; } = new(
        Id, "Ground via distance",
        "A ground pad with no ground via within the distance, pad edge to barrel edge. The distance is also "
      + "shared-ground-via's reach.",
        [new("max-distance", "Maximum distance", ReviewThresholdKind.Length, 0.5e-3)]);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var pads = ctx.Pads;
        if (pads.Refusal is { } why) { run.NotRun(why); return; }

        long max = run.Length("max-distance");
        long search = max * SearchFactor;
        string asks = ctx.Length(max);
        var judged = pads.JudgedGroundPads;
        var near = pads.GroundViasNear(max);

        for (int k = 0; k < judged.Count; k++)
        {
            if (near[k].Count > 0) continue;
            var pad = judged[k];
            string who = pad.Name ?? "this ground pad";

            // The measured value: the nearest ground via out to ten times the threshold.
            var reach = DrcRegions.Grow(pad.Bounds, search);
            ReviewPads.Via? nearest = null;
            long best = long.MaxValue;
            foreach (var via in pads.Vias)
            {
                if (!via.Ground || via.PinHole || !via.Bounds.Intersects(reach) || !pads.Reaches(via, pad)) continue;
                long d = DrcRegions.MinDistance(pad.Region, via.Region);
                if (d <= search && d < best) { best = d; nearest = via; }
            }

            var marker = new Paths64(pad.Region);
            if (nearest is null)
            {
                run.Add(pad.Copper, $"No ground via within {ctx.Length(search)} of {who}; the review asks for one within {asks}",
                        $"> {ctx.Length(search)}", marker);
                continue;
            }
            var (vx, vy) = nearest.Centre;
            marker.Add([new Point64((pad.Bounds.MinX + pad.Bounds.MaxX) / 2, (pad.Bounds.MinY + pad.Bounds.MaxY) / 2), new Point64(vx, vy)]);
            string measured = ctx.Length(best);
            run.Add(pad.Copper, $"Nearest ground via {measured} from {who}; the review asks for {asks}", measured, marker);
        }
    }
}
