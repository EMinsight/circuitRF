// `edge-copper` — copper closer to the board edge than the pullback (brief-review-4 R-rev4-5; built in
// brief-review-2 R-rev2-9 as the frame's own proof). Any copper, on any copper layer, inside the band of
// `pullback` from the board edge, outer edge and cut-outs alike. Marker: the copper inside the band.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class EdgeCopperCheck : ReviewCheck
{
    public const string Id = "edge-copper";

    /// <summary>The ONE sentence for a stackup with no conductor bound to a layer — every copper check reports it.</summary>
    internal const string NoCopper =
        "No conductor of this technology's stackup draws on a layer, so the review cannot tell which layers are "
      + "copper — bind the copper layers to the stackup's conductors in the Technology Editor.";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Copper at the board edge",
        "Copper on any copper layer closer to the board edge than the pullback.",
        [new("pullback", "Pullback", ReviewThresholdKind.Length, 0.25e-3)]);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        if (ctx.Layers.Copper.Count == 0)
        {
            run.NotRun(NoCopper);
            return;
        }
        var board = ctx.Board;
        if (board.Refusal is { } why) { run.NotRun(why); return; }

        long pullback = run.Length("pullback");
        var band = board.Band(pullback);
        if (band.Count == 0) return;
        string asks = ctx.Length(pullback);

        foreach (var layer in ctx.Layers.CopperDrawingLayers)
        {
            if (!ctx.Regions.TryGetValue(layer, out var copper) || copper.Count == 0) continue;
            var inside = Clipper.BooleanOp(ClipType.Intersection, copper, band, LayoutClipper.Rule);
            foreach (var piece in DrcRegions.Components(inside))
            {
                long distance = board.DistanceToEdge(piece, pullback);
                string measured = ctx.Length(distance);
                run.Add(layer,
                    distance == 0
                        ? $"Copper reaches the board edge; the review asks for {asks} of pullback"
                        : $"Copper {measured} from the board edge; the review asks for {asks}",
                    measured, piece);
            }
        }
    }
}
