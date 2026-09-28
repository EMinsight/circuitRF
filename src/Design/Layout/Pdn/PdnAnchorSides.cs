// Which copper a bare coordinate anchor means — on the side parts are mounted on
// (brief-railrf-37 R-rail37-1, owner decision 2026-09-28).
//
// ── WHAT WAS WRONG ──────────────────────────────────────────────────────────────────────────────
//
// Brief 34 refused a coordinate standing on the copper of more than one net. On a six-layer board
// that is nearly every coordinate: a source dropped on a top-side trace stands over an inner plane,
// and a load over two. Both anchors of the field report's rail were refused for exactly that — and
// the answer the designer asked for, and the owner chose, is the one a board designer would give
// without thinking: a source or a load is where a part is SOLDERED, which is outer copper.
//
// ── THE RULE, IN ORDER ──────────────────────────────────────────────────────────────────────────
//
// 1. An anchor that names its layer uses it. Nothing here touches it.
// 2. A coordinate on a placed part's pad takes that pad's land layer — the part's mounting side.
//    (A PAD anchor already does: PlacedPin.Layer. A through-hole pad reaches every layer through its
//    own barrel, which the connectivity walk follows.)
// 3. Otherwise only the OUTER conductors are candidates — the stackup's first and last.
// 4. Copper on both: Top, and a note saying Bottom was there too and how to choose it.
// 5. Copper on neither, but on an inner layer: refused, naming the inner copper and the spelling that
//    selects it. An inner layer is only ever anchored on by NAME.
//
// Resolved ONCE, before the walk, into a copy of the rail every later stage reads — the walk's
// seeds, the return's resolution and the mesh attachment (PdnAttachments.RailNodes) all agree on the
// layer because there is only one answer to agree on.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>R-rail37-1 — a rail's bare coordinate anchors, resolved to the copper each means.</summary>
/// <param name="Request">The request with every resolvable anchor carrying its layer.</param>
/// <param name="Notes">One per anchor rule 4 or rule 2 settled between two outer layers.</param>
/// <param name="Refusal">Rule 5's sentence, or null.</param>
/// <param name="InnerOnly">Rule 5's anchors with the inner copper each could mean — offered by the
/// window as one click each, like brief 34's.</param>
internal sealed record PdnAnchorResolution(
    PdnExtractionRequest Request, IReadOnlyList<string> Notes, string? Refusal,
    IReadOnlyList<PdnAnchorAmbiguity> InnerOnly);

/// <summary>R-rail37-1 — bare coordinates resolve on the mounting side.</summary>
internal static class PdnAnchorSides
{
    /// <summary>
    /// The request with each source and load anchored by a bare coordinate (no refdes, no layer)
    /// given the layer rules 2–4 choose, or refused by rule 5.
    /// </summary>
    /// <remarks>
    /// An anchor on no copper off the reference layer is left as it is: the walk then finds nothing
    /// under it and <see cref="PdnRailConnectivity.NoCopperRefusal"/> says where it did land, which
    /// is a better sentence than any this could write. So is a board whose stackup has no two outer
    /// conductors — there is no mounting side to prefer, and brief 34's refusal still answers.
    /// </remarks>
    public static PdnAnchorResolution Resolve(
        PdnExtractionRequest request, IReadOnlyDictionary<LayerKey, Paths64> layerRegions, LayerKey referenceLayer)
    {
        var rail = request.Rail;
        var tech = request.Technology;
        bool anyBare = rail.Sources.Any(s => IsBare(s.Anchor)) || rail.Loads.Any(l => IsBare(l.Anchor));
        if (!anyBare || Outer(tech) is not { } outer) return new(request, [], null, []);

        var pieces = DrcConnectivity.Extract(layerRegions, tech);
        var notes = new List<string>();
        var refusals = new List<string>();
        var innerOnly = new List<PdnAnchorAmbiguity>();

        // The return, only where a choice between two outer layers needs it: resolved from the
        // anchors as they stand, exactly as brief 34's check resolved it.
        (HashSet<int> Nets, bool Resolved)? ret = null;
        (HashSet<int> Nets, bool Resolved) Return()
        {
            if (ret is { } known) return known;
            var seeds = new List<(long X, long Y, LayerKey? Layer)>();
            foreach (var s in rail.Sources) seeds.AddRange(PdnAttachments.ResolveLands(s.Anchor, request.Pads));
            foreach (var l in rail.Loads) seeds.AddRange(PdnAttachments.ResolveLands(l.Anchor, request.Pads));
            var net = Regions.ResolveReturnNet(request.ReferenceNet, pieces, tech, request.NetPoints, referenceLayer,
                                               rail.NetName, seeds, request.DbuPerMicron);
            ret = Regions.ReturnNets(pieces, request.NetPoints, rail.NetName, referenceLayer, net.Net, net.GalvanicNet);
            return ret.Value;
        }

        RailPortAnchor Side(RailPortAnchor anchor, bool isSource, int index)
        {
            if (!IsBare(anchor)) return anchor;
            var (x, y) = anchor.Point!.Value;
            string row = $"Rail '{rail.Name}''s {(isSource ? "source" : "load")} {index + 1} at " +
                         request.LengthFormat.Point(x, y);

            var under = pieces.Where(p => p.Layer != referenceLayer
                                          && p.Bounds.Contains(x, y) && Regions.Contains(p.Paths, x, y))
                              .ToList();
            if (under.Count == 0) return anchor;

            var top = under.Where(p => outer.Top.Contains(p.Layer)).ToList();
            var bottom = under.Where(p => outer.Bottom.Contains(p.Layer)).ToList();

            // Rule 5 — inner copper only.
            if (top.Count == 0 && bottom.Count == 0)
            {
                var r = Return();
                var inner = Regions.CopperUnder(pieces, tech, request.NetPoints, x, y, referenceLayer, r.Nets, r.Resolved);
                if (inner.Count == 0) return anchor;   // all of it the return: NoCopperRefusal's sentence
                innerOnly.Add(new PdnAnchorAmbiguity(rail.Name, isSource, index, x, y, inner));
                refusals.Add(InnerOnlyRefusal(row, tech, outer, inner, request));
                return anchor;
            }

            // One outer side: it.
            if (bottom.Count == 0) return anchor with { Layer = First(top, outer.TopOrder) };
            if (top.Count == 0) return anchor with { Layer = First(bottom, outer.BottomOrder) };

            // Both. Copper joined to the return is not the rail's on either side — where only one
            // side is anything else, that side is the one meant, and nothing needs saying.
            var r2 = Return();
            if (r2.Resolved)
            {
                bool topRet = top.All(p => r2.Nets.Contains(p.Net));
                bool bottomRet = bottom.All(p => r2.Nets.Contains(p.Net));
                if (topRet && !bottomRet) return anchor with { Layer = First(bottom, outer.BottomOrder) };
                if (bottomRet && !topRet) return anchor with { Layer = First(top, outer.TopOrder) };
            }

            var topLayer = First(top, outer.TopOrder);
            var bottomLayer = First(bottom, outer.BottomOrder);

            // Rule 2 — a coordinate on a placed part's pad is on that part's side. Where both sides
            // read as a pad, the clearly smaller land is the pad and the other is copper a pad's
            // centre happens to sit on; comparable ones are a pad on each side, which is rule 4's.
            var onTop = PadUnder(request, tech, topLayer, x, y);
            var onBottom = PadUnder(request, tech, bottomLayer, x, y);
            if (onBottom is { } bottomPad && (onTop is null || bottomPad.Area < onTop.Value.Area / 2))
            {
                notes.Add($"{row} resolved to {Regions.LayerLabel(tech, bottomLayer)}, the side {bottomPad.Refdes} " +
                          $"is mounted on; {Regions.LayerLabel(tech, topLayer)} also carries copper here — set the " +
                          "anchor's layer to use it.");
                return anchor with { Layer = bottomLayer };
            }

            // Rule 4 — Top, said out loud.
            notes.Add($"{row} resolved to {Regions.LayerLabel(tech, topLayer)}" +
                      (onTop is { } topPad && (onBottom is null || topPad.Area < onBottom.Value.Area / 2)
                          ? $", the side {topPad.Refdes} is mounted on" : "") +
                      $"; {Regions.LayerLabel(tech, bottomLayer)} also carries copper here — set the anchor's " +
                      "layer to use it.");
            return anchor with { Layer = topLayer };
        }

        var resolved = rail.WithAnchors((a, i) => Side(a, true, i), (a, i) => Side(a, false, i));
        return new(request with { Rail = resolved }, notes,
                   refusals.Count > 0 ? string.Join(" ", refusals) : null, innerOnly);
    }

    /// <summary>A coordinate that names neither a part nor a layer — the one form this resolves.</summary>
    private static bool IsBare(RailPortAnchor a) =>
        a.Refdes is not { Length: > 0 } && a.Point is not null && a.Layer is null;

    /// <summary>
    /// The drawing layers of the stackup's first and last conductor, in the order the stackup lists
    /// them; null where there are not two distinct outer conductors.
    /// </summary>
    internal static OuterCopperLayers? Outer(Technology tech)
    {
        var copper = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Count > 0)
            .ToList();
        if (copper.Count < 2) return null;

        var top = copper[0].DrawingLayers.ToList();
        var bottom = copper[^1].DrawingLayers.ToList();
        if (top.Intersect(bottom).Any()) return null;
        return new OuterCopperLayers([.. top], [.. bottom], top, bottom);
    }

    /// <summary>The outer conductors' drawing layers — sets to test, lists to rank by.</summary>
    internal sealed record OuterCopperLayers(
        HashSet<LayerKey> Top, HashSet<LayerKey> Bottom, IReadOnlyList<LayerKey> TopOrder, IReadOnlyList<LayerKey> BottomOrder);

    /// <summary>The first of <paramref name="order"/> that one of <paramref name="on"/> is on.</summary>
    private static LayerKey First(List<DrcNetPiece> on, IReadOnlyList<LayerKey> order) =>
        order.First(l => on.Any(p => p.Layer == l));

    /// <summary>
    /// The placed part whose pad (x, y) is ON, on <paramref name="layer"/>, and that pad's area — or
    /// null.
    /// </summary>
    /// <remarks>
    /// "On the pad" is geometric and needs no pad outline: the SMALLEST shape on the layer under the
    /// point is the pad when it is also the smallest shape under a pad centre landed on that layer.
    /// A pour click fails that test even where the pour covers a pad, because under the pad's own
    /// centre the pad itself is smaller than the pour.
    /// </remarks>
    private static (string Refdes, double Area)? PadUnder(
        PdnExtractionRequest request, Technology tech, LayerKey layer, long x, long y)
    {
        if (!request.Pads.Any(p => p.Layer == layer && p.Refdes is { Length: > 0 })) return null;
        if (SmallestShapeAt(request.Shapes, tech, layer, x, y) is not { } at) return null;

        foreach (var pad in request.Pads)
        {
            if (pad.Layer != layer || pad.Refdes is not { Length: > 0 } refdes) continue;
            if (!at.Bounds.Contains(new Point64(pad.X, pad.Y)) || !Regions.Contains(at.Paths, pad.X, pad.Y)) continue;
            if (SmallestShapeAt(request.Shapes, tech, layer, pad.X, pad.Y) is { } own && own.Area >= at.Area)
                return (refdes, at.Area);
        }
        return null;
    }

    /// <summary>The smallest single shape whose copper on <paramref name="layer"/> covers the point —
    /// <see cref="Regions.CopperLayersAt"/>'s own expansion and containment test.</summary>
    private static (Paths64 Paths, Rect64 Bounds, double Area)? SmallestShapeAt(
        IReadOnlyList<LayoutShape> shapes, Technology tech, LayerKey layer, long x, long y)
    {
        (Paths64, Rect64, double)? best = null;
        foreach (var shape in shapes)
        {
            if (shape is not ViaShape && shape.Layer != layer) continue;
            var b = LayoutGeometry.BboxOf(shape);
            if (b.IsEmpty || x < b.MinX - 1 || x > b.MaxX + 1 || y < b.MinY - 1 || y > b.MaxY + 1) continue;

            DrcRegions.Expand(shape, tech, _ => long.MaxValue, (l, _, paths) =>
            {
                if (l != layer || !Regions.Contains(paths, x, y)) return;
                double area = Math.Abs(Clipper.Area(paths));
                if (best is null || area < best.Value.Item3) best = (paths, Clipper.GetBounds(paths), area);
            });
        }
        return best;
    }

    /// <summary>Rule 5's sentence: where the anchor is, that it stands on no outer copper, the inner
    /// copper it does stand on, and the <c>.crail</c> spelling that selects it.</summary>
    private static string InnerOnlyRefusal(
        string row, Technology tech, OuterCopperLayers outer, IReadOnlyList<PdnAnchorCopper> inner,
        PdnExtractionRequest request)
    {
        double dbuPerMm = request.DbuPerMicron * 1000.0;
        string candidates = string.Join("; ", inner.Select(c =>
            $"{c.LayerName}, {(c.Net is { Length: > 0 } net ? $"net '{net}'" : "no net named")}, " +
            $"{c.AreaSquareDbu / (dbuPerMm * dbuPerMm):0.###} mm²"));
        var first = inner[0].Layer;

        return
            $"{row} stands on no copper on {Regions.LayerLabel(tech, outer.TopOrder[0])} or " +
            $"{Regions.LayerLabel(tech, outer.BottomOrder[0])}, and a coordinate resolves on the outer " +
            "copper, where parts are mounted — an inner layer only where the anchor names it. The copper " +
            $"under it is {candidates}. Move the anchor onto the outer copper, or choose the inner layer: " +
            $"in the window, or in the .crail by giving that anchor its layer (\"Layer\": {first.Layer}, " +
            $"\"LayerDatatype\": {first.Datatype} for the first).";
    }
}
