// `shared-ground-via` — a ground via two or more ground pads depend on (brief-review-3 R-rev3-8): the ONLY
// ground via within ground-via-distance's reach of each of them. A via in a pour with several pads near it,
// each of which has another via of its own within reach, is not a finding — the point is a pad leaning on
// a via another pad also leans on. The per-pad via lists are near's own (ReviewPads.GroundViasNear), the
// ones ground-via-distance read; nothing is measured a second time.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class SharedGroundViaCheck : ReviewCheck
{
    public const string Id = "shared-ground-via";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Shared ground via",
        "A ground via that is the only one within ground-via-distance's maximum distance of two or more ground pads.",
        []);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var pads = ctx.Pads;
        if (pads.Refusal is { } why) { run.NotRun(why); return; }

        long max = (long)Math.Round(ctx.Threshold(GroundViaDistanceCheck.Id, "max-distance"));
        var judged = pads.JudgedGroundPads;
        var near = pads.GroundViasNear(max);

        var dependents = new SortedDictionary<int, List<ReviewPads.Pad>>();
        for (int k = 0; k < judged.Count; k++)
            if (near[k].Count == 1)
            {
                if (!dependents.TryGetValue(near[k][0], out var list)) dependents[near[k][0]] = list = [];
                list.Add(judged[k]);
            }

        foreach (var (v, sharing) in dependents)
        {
            if (sharing.Count < 2) continue;
            var marker = new Paths64(pads.Vias[v].Region);
            foreach (var pad in sharing) marker.AddRange(pad.Region);
            var named = sharing.Where(p => p.Name is not null).Select(p => p.Name!).ToList();
            string who = named.Count == sharing.Count ? $" ({string.Join(", ", named)})" : "";
            run.Add(sharing[0].Copper,
                $"{sharing.Count} ground pads{who} depend on this one ground via; none has another within {ctx.Length(max)}",
                $"{sharing.Count} pads", marker);
        }
    }
}
