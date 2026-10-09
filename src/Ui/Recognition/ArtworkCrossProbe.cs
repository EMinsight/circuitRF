// Cross-probing between a recognised schematic and its artwork — brief-artsch-8-gui-command.md R-as8-3, R-as8-5;
// overview D12.
//
// ── WHAT THIS FILE IS ALLOWED TO KNOW ────────────────────────────────────────────────────────────────
//
// Where a thing is on the artwork, and how to turn that into rings the layout editor's probe overlay draws. It
// recognises nothing. The places come from what the recognition already stated: a component's ArtworkAnchor and its
// schematic's ArtworkSource block (both written by AS-6), a parts-table row's measured pads, a report finding's
// anchors. A part's mark is RailPartMarks' resolution — the one railRF's board, its exports and `circuitrf rail` all
// draw — so a part is outlined the same size here as everywhere else.
//
// ── EVERY ANSWER SAYS SOMETHING ──────────────────────────────────────────────────────────────────────
//
// A probe that quietly highlights nothing is indistinguishable from one that found the place off screen (the LVS
// cross-probe's rule, R-lvs12-3b). So Resolve returns a refusal sentence wherever it cannot point.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Schematic;
using CircuitRF.Render;

namespace CircuitRF.Ui.Recognition;

/// <summary>Where a cross-probe points, or why it cannot.</summary>
/// <param name="ClayPath">The source layout, full path; null on a refusal.</param>
/// <param name="Anchor">The anchor's points, DBU.</param>
/// <param name="Rings">What the probe overlay draws, DBU.</param>
/// <param name="Focus">What to bring on screen.</param>
/// <param name="Refusal">Why nothing can be shown, or null.</param>
public sealed record ArtworkProbeTarget(
    string? ClayPath, IReadOnlyList<(long X, long Y)> Anchor, IReadOnlyList<long[]> Rings, Bbox Focus, string? Refusal)
{
    public bool Ok => Refusal is null && ClayPath is not null;

    internal static ArtworkProbeTarget Refused(string why) => new(null, [], [], Bbox.Empty, why);
}

/// <summary>Resolves cross-probes. <b>Resolves; draws nothing.</b></summary>
public static class ArtworkCrossProbe
{
    /// <summary>R-as8-5: whether a component's context menu has a Show in Artwork row — only a <c>FromArtwork</c> one.</summary>
    public static bool Offers(EditableComponent? component) => component is { FromArtwork: true };

    /// <summary>
    /// R-as8-5: Show in Artwork for <paramref name="component"/> of the schematic at <paramref name="schematicPath"/> —
    /// the provenance's <c>.clay</c>, resolved against the schematic's folder as it was written, and the component's
    /// anchor as rings: a line's centre line, a part's pads.
    /// </summary>
    public static ArtworkProbeTarget Resolve(string? schematicPath, SchematicEditModel model, EditableComponent component)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(component);
        if (!Offers(component))
            return ArtworkProbeTarget.Refused($"{component.InstanceName} was not created from artwork, so there is nowhere to show it.");
        if (model.ArtworkSource is not { Layout: { Length: > 0 } stored })
            return ArtworkProbeTarget.Refused("This schematic does not say which layout it was created from.");
        if (schematicPath is not { Length: > 0 } csch)
            return ArtworkProbeTarget.Refused("Save the schematic first: the layout it came from is stated relative to where it is saved.");

        string clay = RefPath.Resolve(Path.GetDirectoryName(Path.GetFullPath(csch))!, stored);
        if (!File.Exists(clay))
            return ArtworkProbeTarget.Refused($"The layout this schematic was created from is not on disk: {clay}.");
        if (component.ArtworkAnchor.Count == 0)
            return ArtworkProbeTarget.Refused($"{component.InstanceName} carries no artwork anchor.");

        var anchor = component.ArtworkAnchor.ToList();
        var rings = AnchorRings(component.InstanceName, anchor);
        return new ArtworkProbeTarget(clay, anchor, rings, Extent(rings), null);
    }

    /// <summary>
    /// An anchor as rings: two or more points are a line's centre line, as a ring that runs out and back so it strokes
    /// as the line itself; one point is a part (or a discontinuity, a via, a port) and is marked as
    /// <see cref="RailPartMarks"/> marks a part with no body box — the point grown by its pad reach.
    /// </summary>
    public static IReadOnlyList<long[]> AnchorRings(string label, IReadOnlyList<(long X, long Y)> anchor)
    {
        if (anchor.Count == 0) return [];
        if (anchor.Count == 1) return PartRings(new RailPartMark(label, [anchor[0]], null));
        var ring = new long[anchor.Count * 4];
        int k = 0;
        foreach (var (x, y) in anchor) { ring[k++] = x; ring[k++] = y; }
        for (int i = anchor.Count - 1; i >= 0; i--) { ring[k++] = anchor[i].X; ring[k++] = anchor[i].Y; }
        return [ring];
    }

    /// <summary>
    /// R-as8-3: a parts-table row's mark — its measured pads (or its centre, for a part with more than two), with the
    /// body box a placement file states, through <see cref="RailPartMarks.For"/> exactly as the part reading itself
    /// lands a placement row.
    /// </summary>
    public static IReadOnlyList<long[]> PartRings(PartRow row, PlacementTable? placement)
    {
        ArgumentNullException.ThrowIfNull(row);
        IReadOnlyList<(long X, long Y)> pads = row.Terminals.Count > 0 ? [.. row.Terminals.Select(t => (t.X, t.Y))] : [(row.X, row.Y)];
        var body = placement is null ? null
            : RailPartMarks.For(new RailPortAnchor { Refdes = row.Refdes }, row.Refdes, [], placement)?.Body;
        return PartRings(new RailPartMark(row.Refdes, pads, body));
    }

    /// <summary>The outline <see cref="RailPartHighlight"/> draws for a mark, and each pad as its own (crosshair-sized) mark.</summary>
    private static IReadOnlyList<long[]> PartRings(RailPartMark mark)
    {
        var rings = new List<long[]>();
        if (RailPartHighlight.Of(mark).Outline is { IsEmpty: false } outline) rings.Add(Ring(outline));
        foreach (var (x, y) in mark.Pads) rings.Add(Ring(new Bbox(x - 1, y - 1, x + 1, y + 1)));
        return rings;
    }

    /// <summary>A report finding's anchor as rings — the mark a double-click in Messages draws (R-as8-5).</summary>
    public static IReadOnlyList<long[]> FindingRings(RecognitionAnchor anchor) =>
        PartRings(new RailPartMark("", [(anchor.X, anchor.Y)], null));

    /// <summary>The box round every ring.</summary>
    public static Bbox Extent(IReadOnlyList<long[]> rings)
    {
        var box = Bbox.Empty;
        foreach (var ring in rings)
            for (int i = 0; i + 1 < ring.Length; i += 2)
                box = box.Union(new Bbox(ring[i], ring[i + 1], ring[i], ring[i + 1]));
        return box;
    }

    private static long[] Ring(Bbox b) => [b.MinX, b.MinY, b.MaxX, b.MinY, b.MaxX, b.MaxY, b.MinX, b.MaxY];
}
