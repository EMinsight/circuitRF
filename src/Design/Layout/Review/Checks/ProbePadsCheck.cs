// `probe-pads` — whether the die's RF probe pads can be probed (brief-review-6 follow-up). DRC checks a pad's
// geometry; it never asks whether one probe lands on every RF pad, whether its ground tips reach ground, or whether
// the probe can get there. Those are a reviewer's questions about the placed GSGPADs:
//
//   - the probe's pitch: on-wafer probes come in fixed pitches, and a pad laid out at another one cannot be probed
//     with the probe the lab has; each pad whose pitch is not `probe-pitch` is a finding (which also catches pads
//     of two pitches on one die);
//   - grounded: each ground pad's copper is ground (on_ground, the via walk to the flagged reference) — NVia = 0
//     with nothing else joining it leaves a ground tip on floating metal;
//   - a clear approach: the probe comes in from the side away from the line, so metal within `approach` in front
//     of the pads — on the pads' conductor or any above it, which a tip or the probe's body meets — is in its way.
//
// A probe pad is a GSGPAD placed in this layout (its PCell snapshot names the generator), read through
// GsgPadPCell.Resolve, the geometry the cell draws. A pad inside another cell, or in an array, is not judged.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class ProbePadsCheck : ReviewCheck
{
    public const string Id = "probe-pads";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Probe pads",
        "The placed GSG probe pads: at the probe's pitch, their ground pads on ground, and nothing in front of them where "
      + "the probe comes in.",
        [
            new("probe-pitch", "Probe pitch", ReviewThresholdKind.Length, 150e-6),
            new("approach", "Clear approach", ReviewThresholdKind.Length, 100e-6),
        ])
    {
        Targets = [ReviewTarget.Mmic],
    };

    private sealed record Placed(string Name, LayoutInstance Instance, GsgPadPCell.Geometry Pad, LayerKey Layer, Paths64 Pads);

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        var view = ctx.Source.View;
        var placed = new List<Placed>();
        foreach (var inst in view.Instances)
        {
            string folder = Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            if (!view.PCellSnapshots.TryGetValue(folder, out var snap)
                || !string.Equals(snap.GeneratorId, GsgPadPCell.GeneratorId, StringComparison.OrdinalIgnoreCase)) continue;
            var selection = new PCellLayerSelection(snap.SignalLayerNameOverride, snap.GroundLayerNameOverride);
            var pad = GsgPadPCell.Resolve(snap.Parameters, ctx.Tech, selection, ctx.DbuPerMicron);
            var layer = SubstrateResolver.ResolveSignalLayerKey(ctx.Tech, selection, out _);
            string name = inst.SchematicId is { Length: > 0 } id ? id : inst.RefDes is { Length: > 0 } rd ? rd : "a probe pad";
            var pads = new Paths64 { World(inst, pad.SignalPad) };
            pads.AddRange(pad.GroundPads.Select(b => World(inst, b)));
            placed.Add(new Placed(name, inst, pad, layer, pads));
        }
        if (placed.Count == 0) { run.Summary = "No GSG probe pads are placed in this layout."; return; }

        // ── the probe's pitch ────────────────────────────────────────────────────────────────
        // Within half a micrometre: a pitch typed in another unit (5.9 mil) is the same probe.
        long probe = run.Length("probe-pitch");
        long slack = Math.Max(1, ctx.DbuPerMicron / 2);
        foreach (var p in placed.Where(p => Math.Abs(p.Pad.Pitch - probe) > slack))
            run.Add(p.Layer, $"{p.Name} has a {ctx.Length(p.Pad.Pitch)} pitch and the probe {ctx.Length(probe)}: the probe "
                           + "will not land on it", ctx.Length(p.Pad.Pitch), p.Pads);

        // ── grounded ─────────────────────────────────────────────────────────────────────────
        if (ctx.Layers.GroundReference is null || ctx.Eval.GroundNets(out _).Count == 0)
            ctx.Diagnostics.Add("probe-pads: no copper is ground, so whether the probe pads' ground pads reach ground was not judged.");
        else
        {
            var groundOn = new Dictionary<LayerKey, Paths64>();
            foreach (var p in placed)
            {
                if (!groundOn.TryGetValue(p.Layer, out var g))
                    groundOn[p.Layer] = g = ctx.Eval.Evaluate(new DrcLayerExpr.OnGround(new DrcLayerExpr.Layer(p.Layer)));
                for (int k = 0; k < p.Pad.GroundPads.Count; k++)
                {
                    var b = p.Pad.GroundPads[k];
                    var (cx, cy) = LayoutInstanceTransform.TransformPoint((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, p.Instance, 0, 0);
                    if (g.Count(r => Clipper.PointInPolygon(new Point64(cx, cy), r) != PointInPolygonResult.IsOutside) % 2 == 1) continue;
                    run.Add(p.Layer, $"A ground pad of {p.Name} is not on ground: no via or copper takes it to the ground "
                                   + "reference, so the probe's ground tip lands on floating metal", null, new Paths64 { World(p.Instance, b) });
                }
            }
        }

        // ── a clear approach ─────────────────────────────────────────────────────────────────
        long approach = run.Length("approach");
        if (approach <= 0) return;
        foreach (var p in placed)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var s = p.Pad.SignalPad;
            long half = p.Pad.Pitch + (s.MaxY - s.MinY) / 2;
            // A via pad wider than its ground pad overhangs it; the strip starts past that, so a pad never meets itself.
            long overhang = Math.Max(0, (p.Pad.ViaPad - (s.MaxY - s.MinY)) / 2);
            var front = new Paths64 { World(p.Instance, new Bbox(s.MinX - overhang - approach, -half, s.MinX - overhang, half)) };
            int padIndex = ctx.Layers.Copper.FirstOrDefault(c => c.DrawingLayers.Contains(p.Layer))?.Index ?? int.MaxValue;
            foreach (var copper in ctx.Layers.Copper.Where(c => c.Index <= padIndex))
                foreach (var layer in copper.DrawingLayers)
                {
                    if (!ctx.Regions.TryGetValue(layer, out var metal) || metal.Count == 0) continue;
                    var inWay = Clipper.BooleanOp(ClipType.Intersection, metal, front, LayoutClipper.Rule);
                    if (inWay.Count == 0) continue;
                    // How far in front of the pads the nearest of it is, in the pad's own frame.
                    double nearest = inWay.SelectMany(r => r).Max(q => LayoutInstanceTransform.InverseTransformPoint(q.X, q.Y, p.Instance, 0, 0).X);
                    string measured = ctx.Length(Math.Max(0, s.MinX - (long)Math.Round(nearest)));
                    run.Add(layer, $"Metal {measured} in front of {p.Name}, where the probe comes in; the review asks for "
                                 + $"{ctx.Length(approach)} clear", measured, inWay);
                }
        }
    }

    /// <summary>A box of the pad's own frame, placed in the layout.</summary>
    private static Path64 World(LayoutInstance inst, Bbox b)
    {
        var path = new Path64(4);
        foreach (var (x, y) in new[] { (b.MinX, b.MinY), (b.MaxX, b.MinY), (b.MaxX, b.MaxY), (b.MinX, b.MaxY) })
        {
            var (wx, wy) = LayoutInstanceTransform.TransformPoint(x, y, inst, 0, 0);
            path.Add(new Point64(wx, wy));
        }
        if (!Clipper.IsPositive(path)) path.Reverse();
        return path;
    }
}
