// `coupled-lines` — two traces of different nets on one copper layer running side by side, close for long enough
// to couple (brief-review-6 follow-up). DRC answers "is this gap at least the process minimum"; this asks whether
// a reviewer would want a look at it, which is a question of the gap AGAINST THE SUBSTRATE: a line couples to a
// neighbour within a couple of substrate heights of it. So both thresholds are in heights (h, the dielectric
// between the trace's conductor and the nearest ground reference), and one default suits a die and a board.
//
// A trace is the trace finder's (brief-review-4 R-rev4-1); nets are the partition's (same_net's routine), and a
// pair on one net is not a finding. Only pieces of the two running the same way count — two lines crossing near
// each other are not a run — and each pair of pieces is measured exactly: its overlap along the run, and its edge
// gap across it. Where the gap is under `spacing` the overlaps add up per pair of traces; at least `min-length`
// of them is one finding, marked on the copper-free strip between the two. Lines coupled on purpose — a coupler,
// a coupled-line filter, a differential pair — are findings to accept.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Layout.Review.Checks;

internal sealed class CoupledLinesCheck : ReviewCheck
{
    public const string Id = "coupled-lines";

    public override ReviewCheckDef Def { get; } = new(
        Id, "Coupled lines",
        "Two traces of different nets on one copper layer running side by side closer than the spacing, for at least "
      + "the minimum length. Both are in substrate heights (h): the dielectric between the layer and its ground "
      + "reference.",
        [
            new("spacing", "Spacing", ReviewThresholdKind.Heights, 2),
            new("min-length", "Minimum length", ReviewThresholdKind.Heights, 5),
        ])
    {
        Targets = [ReviewTarget.Pcb, ReviewTarget.Mmic],
    };

    public override void Run(ReviewContext ctx, ReviewCheckRun run)
    {
        if (ctx.Layers.Copper.Count == 0) { run.NotRun(EdgeCopperCheck.NoCopper); return; }
        if (ctx.Layers.GroundReference is null) { run.NotRun(ReviewPads.NoGroundReference); return; }
        if (ctx.Traces.Refusal is { } why) { run.NotRun(why); return; }

        double spacingH = run.Number("spacing"), lengthH = run.Number("min-length");
        double parallel = Math.Cos(TraceImpedanceAnalysis.CornerMinDeg * Math.PI / 180);
        foreach (var copper in ctx.Layers.Copper)
        {
            if (HeightOf(ctx, copper) is not { } h || h <= 0) continue;
            long gap = (long)Math.Round(spacingH * h), minLength = (long)Math.Round(lengthH * h);
            var traces = ctx.TracesOn(copper).ToList();
            var nets = new Dictionary<int, HashSet<int>>();
            HashSet<int> NetsOf(int i) => nets.TryGetValue(i, out var n) ? n : nets[i] = ctx.Eval.NetsOf(traces[i].Trace.Region, copper.DrawingLayers);

            for (int i = 0; i < traces.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var (key, a) = traces[i];
                var reach = DrcRegions.Grow(a.Bounds, gap);
                for (int j = i + 1; j < traces.Count; j++)
                {
                    var b = traces[j].Trace;
                    if (!reach.Intersects(b.Bounds)) continue;

                    long length = 0, closest = long.MaxValue;
                    var marker = new Paths64();
                    foreach (var sa in a.Strips)
                        foreach (var sb in b.Strips)
                        {
                            if (!DrcRegions.Grow(sa.Bounds, gap).Intersects(sb.Bounds)) continue;
                            if (Math.Abs(sa.Dx * sb.Dx + sa.Dy * sb.Dy) < parallel) continue;
                            if (Beside(sa, sb, gap) is not { } run2) continue;
                            length += run2.Length;
                            closest = Math.Min(closest, run2.Gap);
                            marker.Add(run2.Between);
                        }
                    if (length < minLength || length == 0) continue;
                    if (NetsOf(i).Overlaps(NetsOf(j))) continue;

                    string measured = ctx.Length(length);
                    run.Add(key,
                        $"{ctx.Layers.NameOf(key)} traces of two nets run side by side for {measured}, {ctx.Length(closest)} "
                      + $"apart at the closest; the review asks for {ctx.Length(gap)} ({spacingH:0.##} h, h = {ctx.Length(h)}) "
                      + $"over {ctx.Length(minLength)} or more",
                        measured, Clipper.Union(marker, LayoutClipper.Rule));
                }
            }
        }
    }

    /// <summary>
    /// Two parallel pieces measured in the frame of <paramref name="a"/>'s direction: how far they run side by side
    /// (the overlap of their spans along it), their edge gap across it, and the strip between their facing edges
    /// over that overlap — or null when they overlap in plan, do not overlap along the run, or are further apart
    /// than <paramref name="gap"/>.
    /// </summary>
    internal static (long Length, long Gap, Path64 Between)? Beside(
        TraceImpedanceAnalysis.FoundStrip a, TraceImpedanceAnalysis.FoundStrip b, long gap)
    {
        double dx = a.Dx, dy = a.Dy, nx = -dy, ny = dx;
        (double Min, double Max) Span(Path64 p, double ux, double uy)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var q in p) { double v = q.X * ux + q.Y * uy; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            return (lo, hi);
        }
        var (aAlong0, aAlong1) = Span(a.Strip, dx, dy);
        var (bAlong0, bAlong1) = Span(b.Strip, dx, dy);
        var (aAcross0, aAcross1) = Span(a.Strip, nx, ny);
        var (bAcross0, bAcross1) = Span(b.Strip, nx, ny);

        double s0 = Math.Max(aAlong0, bAlong0), s1 = Math.Min(aAlong1, bAlong1);
        if (s1 <= s0) return null;
        bool bAbove = bAcross0 >= aAcross1;
        double e0 = bAbove ? aAcross1 : bAcross1, e1 = bAbove ? bAcross0 : aAcross0;
        double g = e1 - e0;
        if (g <= 0 || g > gap) return null;

        Point64 P(double along, double across) =>
            new((long)Math.Round(along * dx + across * nx), (long)Math.Round(along * dy + across * ny));
        return ((long)Math.Round(s1 - s0), (long)Math.Round(g), [P(s0, e0), P(s1, e0), P(s1, e1), P(s0, e1)]);
    }

    /// <summary>h for <paramref name="copper"/>: the dielectric between it and the nearest conductor flagged as a ground
    /// reference, either way, in the layout's DBU; null when no flagged conductor is reachable.</summary>
    internal static long? HeightOf(ReviewContext ctx, ReviewCopperLayer copper)
    {
        var rows = ctx.Tech.Stackup.Layers;
        int at = -1;
        for (int i = 0; i < rows.Count; i++) if (ReferenceEquals(rows[i], copper.Row)) { at = i; break; }
        if (at < 0) return null;
        long? best = null;
        foreach (int step in new[] { 1, -1 })
        {
            long sum = 0;
            for (int i = at + step; i >= 0 && i < rows.Count; i += step)
            {
                var r = rows[i];
                if (r.Kind == StackupKind.Dielectric) sum += r.ThicknessDbu;
                else if (r.Kind == StackupKind.Conductor && r.IsGroundReference)
                {
                    best = best is { } b ? Math.Min(b, sum) : sum;
                    break;
                }
            }
        }
        // The stackup states thicknesses at the default resolution.
        return best is { } h ? (long)Math.Round((double)h * ctx.DbuPerMicron / LayoutUnits.DefaultDbuPerMicron) : null;
    }
}
