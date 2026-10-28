// `stacked-traces` — two traces on adjacent copper layers running on top of each other (brief-review-4
// R-rev4-4): broadside coupling between two signals. A trace is the trace finder's (R-rev4-1); ADJACENT is
// two consecutive conductors of the stackup (D2), since a conductor between two signal layers shields them
// and checking across it would report exactly the stackups chosen to prevent this. The overlap in plan of
// an upper trace and a lower one, each grown by `lateral-margin`, on DIFFERENT nets (the partition's, as
// same_net reads them) is a finding where it runs at least `min-length`. Only overlap between pieces of the
// two running the same way (within the finder's corner angle) counts: area over width alone makes a
// crossing a run — a 1 mm trace crossed at 45° by a 0.1 mm one measures 1.41 mm. Its length is its area
// over the narrower width plus twice the margin, which is what the growth added to that width, so a
// margin does not lengthen the overlap it widens. A doubled supply trace is one net and is not a finding.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class StackedTracesCheck : ReviewCheck
{
    public const string Id = "stacked-traces";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Stacked traces",
        "Two traces on adjacent copper layers, on different nets, running on top of each other for at least the "
      + "minimum length. Each trace is grown by the lateral margin first; 0 means directly on top of each other.",
        [
            new("lateral-margin", "Lateral margin", ReviewThresholdKind.Length, 0),
            new("min-length", "Minimum length", ReviewThresholdKind.Length, 1.0e-3),
        ]) { SpansAdjacentCopper = true };

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        if (ctx.Layers.Copper.Count == 0) { run.NotRun(EdgeCopperCheck.NoCopper); return; }
        if (ctx.Traces.Refusal is { } why) { run.NotRun(why); return; }

        long margin = run.Length("lateral-margin");
        long minLength = run.Length("min-length");
        var nets = new Dictionary<TraceImpedanceAnalysis.FoundTrace, HashSet<int>>(ReferenceEqualityComparer.Instance);
        HashSet<int> NetsOf(TraceImpedanceAnalysis.FoundTrace t, ReviewCopperLayer layer)
        {
            if (!nets.TryGetValue(t, out var n)) nets[t] = n = ctx.Eval.NetsOf(t.Region, layer.DrawingLayers);
            return n;
        }

        double parallel = Math.Cos(TraceImpedanceAnalysis.CornerMinDeg * Math.PI / 180);
        foreach (var (upper, lower) in ctx.Layers.AdjacentCopper)
        {
            var above = Grown(ctx.TracesOn(upper), margin);
            var below = Grown(ctx.TracesOn(lower), margin);
            if (above.Count == 0 || below.Count == 0) continue;

            foreach (var (upKey, up, upStrips, upBox) in above)
            {
                ctx.Token.ThrowIfCancellationRequested();
                foreach (var (downKey, down, downStrips, downBox) in below)
                {
                    if (!upBox.Intersects(downBox)) continue;
                    // Running together, not crossing: only the overlap of two pieces running the same way
                    // (within the finder's own corner angle) counts.
                    var parts = new Paths64();
                    foreach (var (a, aBox, ax, ay) in upStrips)
                        foreach (var (b, bBox, bx, by) in downStrips)
                            if (aBox.Intersects(bBox) && Math.Abs(ax * bx + ay * by) >= parallel)
                                parts.AddRange(Clipper.BooleanOp(ClipType.Intersection, a, b, LayoutClipper.Rule));
                    if (parts.Count == 0) continue;
                    if (NetsOf(up, upper).Overlaps(NetsOf(down, lower))) continue;

                    double narrower = Math.Min(up.Width, down.Width) + 2.0 * margin;
                    foreach (var piece in DrcRegions.Components(parts))
                    {
                        long length = (long)Math.Round(DrcRegionEval.AreaOfComponent(piece) / narrower);
                        if (length < minLength) continue;
                        string measured = ctx.Length(length);
                        run.Add(upKey,
                            $"{ctx.Layers.NameOf(upKey)} and {ctx.Layers.NameOf(downKey)} traces run on top of each other "
                          + $"for {measured} ({ctx.Length((long)Math.Round(up.Width))} and "
                          + $"{ctx.Length((long)Math.Round(down.Width))} wide, different nets)",
                            measured, piece);
                    }
                }
            }
        }
    }

    private static List<(LayerKey Key, TraceImpedanceAnalysis.FoundTrace Trace,
                         List<(Paths64 Strip, Bbox Box, double Dx, double Dy)> Strips, Bbox Box)> Grown(
        IEnumerable<(LayerKey Layer, TraceImpedanceAnalysis.FoundTrace Trace)> traces, long margin) =>
        [.. traces.Select(t =>
        {
            var strips = t.Trace.Strips.Select(s => margin > 0
                ? (Clipper.InflatePaths([s.Strip], margin, JoinType.Round, EndType.Polygon), DrcRegions.Grow(s.Bounds, margin), s.Dx, s.Dy)
                : (new Paths64 { s.Strip }, s.Bounds, s.Dx, s.Dy)).ToList();
            return (t.Layer, t.Trace, strips, margin > 0 ? DrcRegions.Grow(t.Trace.Bounds, margin) : t.Trace.Bounds);
        })];
}
