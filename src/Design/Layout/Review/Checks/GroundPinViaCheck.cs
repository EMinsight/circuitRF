// `ground-pin-via` — a part's grounded terminal far from a via to the ground plane (brief-review-6 follow-up): on a
// die, ground-via-distance's question. A shunt capacitor or a transistor's source reaching its backside via along a
// line has that line's inductance in series with its ground, which DRC never asks about. A die has no soldermask, so
// what is judged is a placed part's PIN whose copper is ground (on_ground, the via walk to the flagged reference):
// its distance to the nearest ground via reaching its layer, straight line from the pin to the via's edge, the
// nearest one stated out to ten times the threshold. A via component's own pin is on its via, so it never fires.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class GroundPinViaCheck : ReviewCheck
{
    public const string Id = "ground-pin-via";

    private const int SearchFactor = 10;

    public override ReviewCheckDef Def { get; } = new(
        Id, "Ground far from a via",
        "A placed part's terminal on ground with no via to the ground plane within the distance — a shunt part or a "
      + "transistor's source reaching its via along a line.",
        [new("max-distance", "Maximum distance", ReviewThresholdKind.Length, 100e-6)])
    {
        Targets = [ReviewTarget.Mmic],
    };

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var layers = ctx.Layers;
        if (layers.GroundReference is null) { run.NotRun(ReviewPads.NoGroundReference); return; }
        if (!ctx.HasPlatedDrills) { run.NotRun(ReviewPads.NoPlatedDrill); return; }
        var ground = ctx.Eval.GroundNets(out string? said);
        if (ground.Count == 0) { run.NotRun(ReviewPads.NoGroundCopper(said)); return; }

        var view = ctx.Source.View;
        if (view.Instances.Count == 0) { run.Summary = "No parts are placed, so no terminal was judged."; return; }
        IReadOnlyList<PlacedPin> pins;
        var origins = new List<PlacedPinOrigin>();
        try { pins = PlacedPins.Of(view, ctx.Source.Path, ctx.Tech, PinNaming.ArtworkOnly, origins: origins); }
        catch (Exception ex) { run.NotRun($"The placed parts' terminals could not be read: {ex.Message}"); return; }

        long max = run.Length("max-distance"), search = max * SearchFactor;
        string asks = ctx.Length(max);
        var groundOn = new Dictionary<LayerKey, Paths64>();
        int judged = 0;
        for (int i = 0; i < pins.Count && i < origins.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var pin = pins[i];
            var layer = origins[i].Layer;
            if (!groundOn.TryGetValue(layer, out var g))
                groundOn[layer] = g = ctx.Eval.Evaluate(new DrcLayerExpr.OnGround(new DrcLayerExpr.Layer(layer)));
            var at = new Point64(pin.X, pin.Y);
            if (!Inside(g, at)) continue;
            judged++;

            int index = layers.Copper.FirstOrDefault(c => c.DrawingLayers.Contains(layer))?.Index ?? -1;
            var dot = new Paths64 { new Path64 { at, new(pin.X + 1, pin.Y), new(pin.X + 1, pin.Y + 1), new(pin.X, pin.Y + 1) } };
            ReviewPads.Via? nearest = null;
            long best = long.MaxValue;
            foreach (var via in ctx.Vias)
            {
                if (!via.Ground || (via.Span is { } s && index >= 0 && (index < s.Top || index > s.Bottom))) continue;
                if (!via.Bounds.Intersects(new Bbox(pin.X - search, pin.Y - search, pin.X + search, pin.Y + search))) continue;
                long d = Inside(via.Region, at) ? 0 : DrcRegions.MinDistance(dot, via.Region);
                if (d < best) { best = d; nearest = via; }
            }
            if (nearest is not null && best <= max) continue;

            string who = pin.Refdes is { Length: > 0 } r ? (pin.Pin is { Length: > 0 } p2 ? $"{r} pin {p2}" : r) : "a grounded terminal";
            long half = Math.Max(origins[i].WidthDbu / 2, max / 10);
            var marker = new Paths64 { new Path64 { new(pin.X - half, pin.Y - half), new(pin.X + half, pin.Y - half),
                                                    new(pin.X + half, pin.Y + half), new(pin.X - half, pin.Y + half) } };
            if (nearest is null || best > search)
            {
                run.Add(layer, $"No via to ground within {ctx.Length(search)} of {who}; the review asks for one within {asks}",
                        $"> {ctx.Length(search)}", marker);
                continue;
            }
            var (vx, vy) = nearest.Centre;
            marker.Add([at, new Point64(vx, vy)]);
            string measured = ctx.Length(best);
            run.Add(layer, $"Nearest via to ground {measured} from {who}; the review asks for {asks}", measured, marker);
        }
        run.Summary = judged == 0 ? "No placed part has a terminal on ground." : null;
    }

    /// <summary>Whether <paramref name="at"/> is in <paramref name="region"/>, holes counted: inside an odd number of its rings.</summary>
    private static bool Inside(Paths64 region, Point64 at) =>
        region.Count(p => Clipper.PointInPolygon(at, p) != PointInPolygonResult.IsOutside) % 2 == 1;
}
