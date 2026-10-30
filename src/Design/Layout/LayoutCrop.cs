// Crop to Rectangle (brief-dfr26-2 R-dfr26-2-1) — the whole board cut down to one rectangle, on every
// layer, in one result. Framework-free like LayoutBooleans: no undo, no selection, no Messages; the
// layout editor turns the result into one undo entry and says what it did.

using Clipper2Lib;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout;

/// <summary>What a crop does to one layer. <see cref="Inside"/> + <see cref="Cut"/> + <see cref="Removed"/> is
/// <see cref="Shapes"/>, except on the board outline, which is rebuilt rather than clipped:
/// <see cref="OutlineAfter"/> is how many shapes it is drawn with afterwards, and the three counts are zero.</summary>
public readonly record struct LayoutCropLayerCount(
    LayerKey Layer, int Shapes, int Inside, int Cut, int Removed, bool IsOutline = false, int OutlineAfter = 0);

/// <summary>One crop, computed before anything is changed so the editor can show it first.</summary>
/// <param name="ReplacedIndices">Every shape index the crop replaces, ascending — all of them but the bitmaps.</param>
/// <param name="Shapes">What replaces them, in their order; a shape inside the rectangle is the SAME object.</param>
/// <param name="Originals">The shapes at <see cref="ReplacedIndices"/> when the crop was computed, so a caller can
/// tell the layout has not changed since.</param>
/// <param name="RemovedInstances">Instance indices whose origin is outside the rectangle, ascending.</param>
/// <param name="EdgeCutVias">Vias kept (their anchor is inside) whose drill circle crosses the new edge: half a barrel
/// outside the board, which the 3D view builds only the board's part of (R-dfr28-1-2).</param>
public sealed record LayoutCropResult(
    Bbox Rect,
    IReadOnlyList<int> ReplacedIndices,
    IReadOnlyList<LayoutShape> Originals,
    IReadOnlyList<LayoutShape> Shapes,
    IReadOnlyList<LayoutCropLayerCount> Layers,
    IReadOnlyList<int> RemovedInstances,
    int KeptInstances,
    int BitmapsUntouched,
    bool AnyCurvedOperand,
    int EdgeCutVias = 0);

public static class LayoutCrop
{
    /// <summary>
    /// Clips every shape to <paramref name="rect"/> with <see cref="LayoutBooleans.Clip"/>'s own rule — each shape
    /// on its own, keeping its layer and net, holes and all; a via or label kept when its anchor is inside — on every
    /// layer, whatever the editor shows or locks. Instances follow their origin. A bitmap is a picture, not
    /// geometry, and is left alone (the caller reports it).
    ///
    /// <para><b>The board outline is rebuilt, not clipped.</b> An imported outline is a STROKE drawn round the
    /// board, and clipping that stroke keeps only the bits of line that cross the rectangle — on a crop well inside
    /// the board, none at all, and the 3D view then has no board to build. The outline becomes the board the 3D
    /// view reads (<see cref="Em3dLayoutSolids.Builder.BoardRegions"/>, the same rule) intersected with the
    /// rectangle, drawn the way it was: a line along the new edge at the old line's width, or a region.</para>
    /// </summary>
    public static LayoutCropResult Crop(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances, Bbox rect, Technology? tech)
    {
        var stencil = new RectShape { X1 = rect.MinX, Y1 = rect.MinY, X2 = rect.MaxX, Y2 = rect.MaxY };
        var outlineKeys = tech is null ? [] : Em3dGenerator.BoardOutlineLayers(tech);
        bool IsOutline(LayoutShape s) => outlineKeys.Contains(s.Layer) && LayoutBooleans.IsClipperOperand(s);

        var outline = shapes.Where(IsOutline).ToList();
        var newOutline = outline.Count > 0 ? RebuildOutline(outline, rect, tech!) : [];

        var replaced = new List<int>(shapes.Count);
        var originals = new List<LayoutShape>(shapes.Count);
        var result = new List<LayoutShape>(shapes.Count);
        var counts = new Dictionary<LayerKey, (int Shapes, int Inside, int Cut, int Removed)>();
        var order = new List<LayerKey>();
        bool outlinePlaced = false, anyCurved = false;
        int bitmaps = 0, edgeCutVias = 0;

        for (int i = 0; i < shapes.Count; i++)
        {
            var s = shapes[i];
            if (s is BitmapShape) { bitmaps++; continue; }
            replaced.Add(i);
            originals.Add(s);
            if (!counts.TryGetValue(s.Layer, out var c)) order.Add(s.Layer);

            if (IsOutline(s))
            {
                // The rebuilt outline takes the first outline shape's place in the drawing order.
                if (!outlinePlaced) { result.AddRange(newOutline); outlinePlaced = true; }
                counts[s.Layer] = (c.Shapes + 1, 0, 0, 0);
                continue;
            }

            if (!LayoutBooleans.IsClipOperand(s))
            {
                // A kind neither clip nor anchor test knows — kept as it is rather than guessed at.
                result.Add(s);
                counts[s.Layer] = (c.Shapes + 1, c.Inside + 1, c.Cut, c.Removed);
                continue;
            }

            if (s is ViaShape v && CrossesEdge(v, rect)) edgeCutVias++;
            var clip = LayoutBooleans.Clip([s], stencil, tech);
            result.AddRange(clip.Shapes);
            anyCurved |= clip.AnyCurvedOperand;
            counts[s.Layer] = (c.Shapes + 1, c.Inside + clip.OperandsUntouched, c.Cut + clip.OperandsChanged,
                               c.Removed + clip.OperandsRemoved);
        }

        var layers = order.Select(k =>
        {
            var c = counts[k];
            bool isOutline = outline.Any(s => s.Layer == k);
            return isOutline
                ? new LayoutCropLayerCount(k, c.Shapes, 0, 0, 0, IsOutline: true, newOutline.Count(s => s.Layer == k))
                : new LayoutCropLayerCount(k, c.Shapes, c.Inside, c.Cut, c.Removed);
        }).ToList();

        var removedInstances = new List<int>();
        for (int i = 0; i < instances.Count; i++)
            if (!rect.Contains(instances[i].X, instances[i].Y)) removedInstances.Add(i);

        return new LayoutCropResult(rect, replaced, originals, result, layers, removedInstances,
                                    instances.Count - removedInstances.Count, bitmaps, anyCurved, edgeCutVias);
    }

    /// <summary>A via the crop keeps (anchor inside, boundary included) whose drill circle reaches past the rectangle.</summary>
    private static bool CrossesEdge(ViaShape v, Bbox rect)
    {
        if (!rect.Contains(v.X, v.Y)) return false;
        double r = (v.DrillSize > 0 ? v.DrillSize : v.PadSize) / 2.0;
        return Math.Min(Math.Min(v.X - rect.MinX, rect.MaxX - v.X), Math.Min(v.Y - rect.MinY, rect.MaxY - v.Y)) < r;
    }

    /// <summary>
    /// The outline after the crop: the board <paramref name="outline"/> describes, intersected with
    /// <paramref name="rect"/>. A stroked outline's board is the region its CENTRE LINE encloses — a Gerber
    /// profile is drawn on the edge — so the 3D reading of the new line (its inside, as for the old one) stands
    /// in the same relation to the copper as before.
    /// </summary>
    internal static List<LayoutShape> RebuildOutline(List<LayoutShape> outline, Bbox rect, Technology tech)
    {
        var layer = outline[0].Layer;
        var merged = LayoutBooleans.Union(outline, tech).Shapes;
        var drawn = merged.SelectMany(s => PlanarExtractor.ToPolygons(s, tech, 1.0)).ToList();
        var boards = Em3dLayoutSolids.Builder.BoardRegions(drawn, out int lines);

        var paths = new Paths64();
        foreach (var b in boards)
        {
            paths.Add(Oriented(b.Outer, positive: true));
            foreach (var h in b.HoleRings) paths.Add(Oriented(h, positive: false));
        }

        var line = lines > 0 ? outline.OfType<PathShape>().MaxBy(p => p.Width) : null;
        if (line is not null)
            paths = Clipper.InflatePaths(paths, line.Width / 2.0, JoinType.Miter, EndType.Polygon);

        var box = new Paths64
        {
            new Path64 { new(rect.MinX, rect.MinY), new(rect.MaxX, rect.MinY), new(rect.MaxX, rect.MaxY), new(rect.MinX, rect.MaxY) },
        };
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Intersection, paths, box, tree, LayoutClipper.Rule);

        if (line is null) return [.. LayoutClipper.FromClipperTree(tree, layer, null)];

        var rings = new List<LayoutShape>();
        void Walk(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var ring = node[i].Polygon!;
                if (ring.Count >= 3)
                {
                    var xy = new long[(ring.Count + 1) * 2];
                    for (int k = 0; k <= ring.Count; k++)
                    {
                        xy[2 * k] = ring[k % ring.Count].X;
                        xy[2 * k + 1] = ring[k % ring.Count].Y;
                    }
                    rings.Add(new PathShape { Layer = layer, Xy = xy, Width = line.Width, End = line.End });
                }
                Walk(node[i]);
            }
        }
        Walk(tree);
        return rings;
    }

    private static Path64 Oriented(IReadOnlyList<EmPoint> ring, bool positive)
    {
        var path = new Path64(ring.Count);
        foreach (var p in ring) path.Add(new Point64((long)Math.Round(p.X), (long)Math.Round(p.Y)));
        if (Clipper.IsPositive(path) != positive) path.Reverse();
        return path;
    }
}
