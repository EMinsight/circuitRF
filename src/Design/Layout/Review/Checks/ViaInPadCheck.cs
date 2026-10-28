// `via-in-pad` — a via inside an SMD pad (brief-review-3 R-rev3-6). Production does not want a drill in an
// SMD pad: solder wicks down the barrel. A via in a THERMAL pad is thermal-vias' business, not this
// check's (D3), and a through-hole pin's own hole is no via (D2). Marker: the via's barrel and the pad.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class ViaInPadCheck : ReviewCheck
{
    public const string Id = "via-in-pad";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Via in pad",
        "A via inside an SMD pad's mask opening. Thermal pads and through-hole pins are exempt.",
        []);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var pads = ctx.Pads;
        if (pads.Refusal is { } why) { run.NotRun(why); return; }

        foreach (var pad in pads.Pads)
        {
            if (pad.Thermal || pad.Pin) continue;
            foreach (int v in pad.Drills)
            {
                var via = pads.Vias[v];
                if (via.PinHole) continue;
                string drill = $"⌀{ctx.Length(via.Diameter)}";
                string size = ReviewText.Size(pad.Bounds.MaxX - pad.Bounds.MinX, pad.Bounds.MaxY - pad.Bounds.MinY, ctx.Unit, ctx.DbuPerMicron);
                var marker = new Paths64(via.Region);
                marker.AddRange(pad.Region);
                run.Add(pad.Copper,
                    pad.Name is { } name
                        ? $"Via in {name} (drill {drill} inside a {size} pad)"
                        : $"Via in pad (drill {drill} inside a {size} pad)",
                    drill, marker);
            }
        }
    }
}
