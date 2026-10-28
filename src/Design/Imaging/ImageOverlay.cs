// What is drawn over a picture — brief-img-5-dialog.md R-im5-3; below the firewall since brief-img-11 R-im11-3, so the
// dialog's canvas and the CLI's --overlay picture draw the same marks from the same result (ImageOverlay.Picture.cs).
//
// Built ONLY from what the reading returned (the trace, and the recognition when a schematic is made) and carried into
// pixel space through the trace's own PixelFrame, so every item lies on the pixels it was read from. Nothing here reads
// the picture or decides anything: a part is boxed because the recognition found it, amber because the recognition
// left it unknown. The canvas draws these items and nothing else.
//
// The items are also the cross-probe: a parts-table row and an overlay item name each other by the board's designator,
// and a report anchor finds the item nearest to it — a pure mapping, so it is tested without rendering.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Design.Imaging;

/// <summary>The overlay's classes, each toggled from the legend. A schematic picture's are its wires (junction dots and
/// dangling ends among them), its symbols (as parts), its ports, its words and its crossings (IM-10), and its
/// annotation strokes — arrows and dashed lines, with what each was read as (IMF-3). A plot picture's are its frames,
/// its traces, its markers and its words (IM-14).</summary>
public enum ImageOverlayClass { Copper, Drill, Ignored, Part, Line, Port, Unknown, Wire, Text, Crossing, Plot, Trace, Marker, Annotation }

/// <summary>A rectangle in the picture, pixels.</summary>
public readonly record struct PixelBounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CentreX => (Left + Right) / 2;
    public double CentreY => (Top + Bottom) / 2;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public PixelBounds Inflate(double d) => new(Left - d, Top - d, Right + d, Bottom + d);

    public static PixelBounds Of(IEnumerable<(double X, double Y)> points)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var (x, y) in points)
        {
            l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
        }
        return l > r ? default : new(l, t, r, b);
    }
}

/// <summary>One thing drawn over the picture.</summary>
/// <param name="Id">Unique within the overlay: <c>part:C1</c>, <c>line:MLIN2</c>, <c>port:1</c>, <c>copper:TOP:3</c>.</param>
/// <param name="Paths">Pixel paths — closed rings for copper, a box for a part, a centre line for a line.</param>
/// <param name="Closed">Whether the paths are rings.</param>
/// <param name="Label">What is written beside it, or null.</param>
/// <param name="Rgb">Its colour (a layer's own), 0xRRGGBB, or null for the class colour.</param>
/// <param name="Unknown">Something about it was not read: drawn in amber whatever its class.</param>
/// <param name="PartKey">The board designator of the part it is, for the parts-table cross-probe.</param>
/// <param name="Circle">For a drill or a port: its centre and radius, pixels.</param>
public sealed record ImageOverlayItem(
    string Id, ImageOverlayClass Class, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Paths, bool Closed,
    string? Label, int? Rgb, bool Unknown, string? PartKey, PixelBounds Bounds, (double X, double Y, double R)? Circle = null);

/// <summary>Everything the canvas draws over one reading.</summary>
public sealed partial class ImageOverlay
{
    private ImageOverlay(IReadOnlyList<ImageOverlayItem> items, ColourClusterSet? clusters, IReadOnlyList<int> ignored,
                         IReadOnlyList<int> unmapped, PixelFrame? frame, bool pixelAnchors = false)
    {
        Items = items;
        Clusters = clusters;
        IgnoredClusters = ignored;
        UnmappedClusters = unmapped;
        Frame = frame;
        PixelAnchors = pixelAnchors;
    }

    /// <summary>The report's anchors are the picture's own pixels — a schematic picture's — rather than DBU.</summary>
    public bool PixelAnchors { get; }

    /// <summary>What the reading measured of the picture, one line each — a schematic picture's scales (IMF-2, R-imf2-1):
    /// written in the flattened picture's corner, the legend of what every mark was read at.</summary>
    public IReadOnlyList<string> Legend { get; private init; } = [];

    public static ImageOverlay Empty { get; } = new([], null, [], [], null);

    /// <summary>This overlay with <paramref name="items"/> in place of its items — a label the user typed shown in place of
    /// what was read (PTU-7).</summary>
    public ImageOverlay WithItems(IReadOnlyList<ImageOverlayItem> items) =>
        new(items, Clusters, IgnoredClusters, UnmappedClusters, Frame, PixelAnchors) { Legend = Legend };

    public IReadOnlyList<ImageOverlayItem> Items { get; }

    /// <summary>The picture's colours — the canvas hatches the ignored ones and flashes a hovered layer row's.</summary>
    public ColourClusterSet? Clusters { get; }

    /// <summary>Colours read as Ignore: hatched.</summary>
    public IReadOnlyList<int> IgnoredClusters { get; }

    /// <summary>Colours mapped to a layer the technology does not have: amber.</summary>
    public IReadOnlyList<int> UnmappedClusters { get; }

    /// <summary>Pixels → DBU, or null without a scale.</summary>
    public PixelFrame? Frame { get; }

    /// <summary>The item of the part the board calls <paramref name="boardRefdes"/>, or null.</summary>
    public ImageOverlayItem? ItemForPart(string? boardRefdes) =>
        boardRefdes is null ? null
        : Items.FirstOrDefault(i => i.Class == ImageOverlayClass.Part && string.Equals(i.PartKey, boardRefdes, StringComparison.OrdinalIgnoreCase));

    /// <summary>The topmost item under a pixel, the selectable classes first (a part over the copper it sits on). With
    /// <paramref name="corrections"/> a correction counts too — what the pointer is over, rather than what a click means.</summary>
    public ImageOverlayItem? HitTest(double x, double y, double tolerancePx, Func<ImageOverlayClass, bool> shown, bool corrections = false)
    {
        ImageOverlayItem? best = null;
        double bestArea = double.MaxValue;
        foreach (var item in Items)
        {
            if (!shown(item.Class) && !(item.Unknown && shown(ImageOverlayClass.Unknown))) continue;
            if (!item.Bounds.Inflate(tolerancePx).Contains(x, y)) continue;
            // A trace's fitted curve is drawn over the trace, which is what a click there means (brief-pdg-2 R-pdg2-4).
            if (item.Id.StartsWith(FitPrefix, StringComparison.Ordinal)) continue;
            // A correction is drawn over what it corrects, which is what a click there means (brief-dfr29-1 R-dfr29-1-2).
            if (!corrections && item.Id.StartsWith(CorrectionPrefix, StringComparison.Ordinal)) continue;
            int rank = item.Class switch
            {
                ImageOverlayClass.Crossing => 0,
                ImageOverlayClass.Part or ImageOverlayClass.Port => 1,
                ImageOverlayClass.Line or ImageOverlayClass.Drill or ImageOverlayClass.Text => 2,
                _ => 3,
            };
            double area = rank * 1e12 + Math.Max(1, item.Bounds.Width * item.Bounds.Height);
            // A line, a wire or a plot's trace is hit on its stroke, not anywhere in its box: two traces across one plot
            // share nearly the whole box (R-ptu3-1).
            if (item.Class is ImageOverlayClass.Line or ImageOverlayClass.Wire or ImageOverlayClass.Trace && item.Circle is null
                && DistanceToPaths(item, x, y) > tolerancePx) continue;
            if (area < bestArea) { bestArea = area; best = item; }
        }
        return best;
    }

    /// <summary>The item a report anchor points at: the nearest part, port or line within a few pixels, else null.</summary>
    public ImageOverlayItem? ItemForAnchor(RecognitionAnchor anchor)
    {
        if (ToPixel(anchor.X, anchor.Y) is not { } at) return null;
        var (x, y) = at;
        return Items.Where(i => i.Class is ImageOverlayClass.Part or ImageOverlayClass.Port or ImageOverlayClass.Line or ImageOverlayClass.Drill)
                    .Select(i => (Item: i, D: i.Bounds.Contains(x, y) ? 0 : DistanceToBounds(i.Bounds, x, y)))
                    .Where(t => t.D <= 6)
                    .OrderBy(t => t.D).ThenBy(t => t.Item.Bounds.Width * t.Item.Bounds.Height)
                    .Select(t => t.Item).FirstOrDefault();
    }

    /// <summary>A DBU point in pixels, or null without a scale.</summary>
    public (double X, double Y)? ToPixel(long x, long y) => PixelAnchors ? (x, y) : Frame is { } f ? f.ToPixel(x, y) : null;

    private static double DistanceToBounds(PixelBounds b, double x, double y)
    {
        double dx = Math.Max(0, Math.Max(b.Left - x, x - b.Right)), dy = Math.Max(0, Math.Max(b.Top - y, y - b.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToPaths(ImageOverlayItem item, double x, double y)
    {
        double best = double.MaxValue;
        foreach (var path in item.Paths)
            for (int i = 1; i < path.Count; i++)
            {
                var (ax, ay) = path[i - 1];
                var (bx, by) = path[i];
                double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
                double t = len2 == 0 ? 0 : Math.Clamp(((x - ax) * vx + (y - ay) * vy) / len2, 0, 1);
                double px = ax + t * vx - x, py = ay + t * vy - y;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }
        return best;
    }

    // ── building ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The overlay of a trace and, when a schematic is being made, the recognition over it.</summary>
    public static ImageOverlay Build(ImageTraceResult trace, RecognitionResult? recognition, Technology? technology)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var items = new List<ImageOverlayItem>();

        // Copper: each traced region outlined in its layer's own colour.
        foreach (var layer in trace.Layers.Where(l => l.Role == ImageLayerRole.Layer))
        {
            int? rgb = technology?.Layers.FirstOrDefault(l => string.Equals(l.Name, layer.Name, StringComparison.Ordinal)) is { } def
                ? (def.Color.R << 16) | (def.Color.G << 8) | def.Color.B : null;
            for (int r = 0; r < layer.PixelRegions.Count; r++)
            {
                var region = layer.PixelRegions[r];
                var paths = new List<IReadOnlyList<(double X, double Y)>> { Points(region.Outer) };
                paths.AddRange(region.Holes.Select(Points));
                items.Add(new ImageOverlayItem($"copper:{layer.Name}:{r}", ImageOverlayClass.Copper, paths, true, null, rgb,
                                               layer.Key is null, null, PixelBounds.Of(paths[0])));
            }
        }

        // Drills, as the circles read.
        for (int d = 0; d < trace.Drills.Count; d++)
        {
            var c = trace.Drills[d];
            items.Add(new ImageOverlayItem($"drill:{d}", ImageOverlayClass.Drill, [], true, null, null, false, null,
                                           new PixelBounds(c.Cx - c.R, c.Cy - c.R, c.Cx + c.R, c.Cy + c.R), (c.Cx, c.Cy, c.R)));
        }

        // Round holes kept as openings or made non-plated — what the hole picker answers (brief-dfr29-1 R-dfr29-1-2). One
        // read in the copper is drawn as its own outline, never as the circle of its area: a shape that is not round can
        // then never show as a circle no one drew (brief-dfr30-3 R-dfr30-3-2). The circle stays, for the picker.
        for (int h = 0; h < trace.Holes.Count; h++)
        {
            var (hole, kind) = (trace.Holes[h].Circle, trace.Holes[h].Kind);
            if (kind == ImageHoleKind.Via) continue;   // drawn as a drill above
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> outline = trace.Holes[h].Outline is { Count: >= 3 } o ? [Points(o)] : [];
            items.Add(new ImageOverlayItem($"{HolePrefix}{h}", ImageOverlayClass.Drill, outline, true,
                                           kind == ImageHoleKind.NonPlated ? "not plated" : "opening", Colours.Ignored, false, null,
                                           new PixelBounds(hole.Cx - hole.R, hole.Cy - hole.R, hole.Cx + hole.R, hole.Cy + hole.R),
                                           (hole.Cx, hole.Cy, hole.R)));
        }

        // The corrections, each drawn as what it makes and dashed over the reading; a refused one amber, its sentence
        // its label (R-dfr29-1-3).
        for (int i = 0; i < trace.Corrections.Count; i++)
        {
            var o = trace.Corrections[i];
            var fix = o.Correction;
            string id = $"{CorrectionPrefix}{i}";
            if (fix.IsPoint)
            {
                var (x, y) = (fix.At.X, fix.At.Y);
                double r = trace.Holes.Where(h => (h.Circle.Cx - x) * (h.Circle.Cx - x) + (h.Circle.Cy - y) * (h.Circle.Cy - y) <= (h.Circle.R + 1) * (h.Circle.R + 1))
                                      .Select(h => h.Circle.R).DefaultIfEmpty(4).First() + 2;
                items.Add(new ImageOverlayItem(id, ImageOverlayClass.Drill, [], true, o.Refusal, null, !o.Applied, null,
                                               new PixelBounds(x - r, y - r, x + r, y + r), (x, y, r)));
                continue;
            }
            // A painted area is its copper, drawn with the layer it is on; the point clicked is marked.
            if (fix.Kind == ImageCorrectionKind.PaintFill)
            {
                var (fx, fy) = (fix.At.X, fix.At.Y);
                int? layerRgb = technology?.Layers.FirstOrDefault(l => string.Equals(l.Name, fix.Layer, StringComparison.Ordinal)) is { } fd
                    ? (fd.Color.R << 16) | (fd.Color.G << 8) | fd.Color.B : null;
                items.Add(new ImageOverlayItem(id, ImageOverlayClass.Marker, [], true, o.Refusal, layerRgb, !o.Applied, null,
                                               new PixelBounds(fx - 4, fy - 4, fx + 4, fy + 4), (fx, fy, 4)));
                continue;
            }
            // The copper read on one conductor removed covers the whole picture: its row says so, a box round it would not.
            if (fix is { Kind: ImageCorrectionKind.Clear, Layer: not null }) continue;
            IReadOnlyList<(double X, double Y)> ring = [.. fix.Points.Select(p => (p.X, p.Y))];
            int? rgb = fix.Kind == ImageCorrectionKind.Copper
                       && technology?.Layers.FirstOrDefault(l => string.Equals(l.Name, fix.Layer, StringComparison.Ordinal)) is { } def
                ? (def.Color.R << 16) | (def.Color.G << 8) | def.Color.B
                : fix.Kind == ImageCorrectionKind.Copper ? null : fix.Kind == ImageCorrectionKind.Clear ? Colours.Ignored : Colours.Line;
            // An outline is a line round the board, never a fill over it.
            var cls = fix.Kind switch
            {
                ImageCorrectionKind.Clear => ImageOverlayClass.Ignored, ImageCorrectionKind.Outline => ImageOverlayClass.Line, _ => ImageOverlayClass.Copper,
            };
            items.Add(new ImageOverlayItem(id, cls, [ring], true, o.Refusal, rgb, !o.Applied, null, PixelBounds.Of(ring)));
        }

        // What the recognition found, carried back onto the picture through the trace's frame.
        if (recognition is not null && trace.Frame is { } frame)
        {
            (double X, double Y) Px(long x, long y) => frame.ToPixel(x, y);

            foreach (var row in recognition.Parts.Rows)
            {
                var pads = row.Terminals.Count > 0 ? row.Terminals.Select(t => Px(t.X, t.Y)).ToList() : [Px(row.X, row.Y)];
                var b = PixelBounds.Of(pads);
                double pad = Math.Max(5, 0.25 * Math.Max(b.Width, b.Height));
                b = b.Inflate(pad);
                var box = new List<(double X, double Y)> { (b.Left, b.Top), (b.Right, b.Top), (b.Right, b.Bottom), (b.Left, b.Bottom) };
                bool unknown = row.Kind == PartKind.Unknown || (row.Value is null && row.Model != PartModelKind.SnP);
                items.Add(new ImageOverlayItem($"part:{row.BoardRefdes}", ImageOverlayClass.Part, [box], true,
                                               WithValue(row.Refdes, recognition.Parts, row), null, unknown, row.BoardRefdes, b));
            }

            foreach (var line in recognition.Lines.Elements)
            {
                if (line.Anchor.Count == 0) continue;
                var pts = line.Anchor.Select(a => Px(a.X, a.Y)).ToList();
                string label = line.Width is { } w
                    ? $"{line.Type} W={ImageScale.FormatLength(w)}"
                    : line.Type.ToString();
                var b = PixelBounds.Of(pts);
                if (pts.Count == 1) b = b.Inflate(4);
                items.Add(new ImageOverlayItem($"line:{line.Name}", ImageOverlayClass.Line, [pts], false, label, null,
                                               false, null, b));
            }

            if (recognition.Board is { } board)
                foreach (var port in board.Ports)
                {
                    var (x, y) = Px(port.X, port.Y);
                    const double r = 7;
                    items.Add(new ImageOverlayItem($"port:{port.Number}", ImageOverlayClass.Port, [], true,
                                                   $"P{port.Number.ToString(CultureInfo.InvariantCulture)}", null, false, null,
                                                   new PixelBounds(x - r, y - r, x + r, y + r), (x, y, r)));
                }
        }

        var map = trace.LayerMap;
        var ignored = map?.Rows.Where(r => r.Role == ImageLayerRole.Ignore).Select(r => r.Cluster).ToList() ?? [];
        var unmapped = map?.Rows.Where(r => r.Role is ImageLayerRole.Layer or ImageLayerRole.Drill or ImageLayerRole.Silkscreen or ImageLayerRole.BoardOutline
                                            && r.Layers.Count == 0).Select(r => r.Cluster).ToList() ?? [];
        return new ImageOverlay(items, trace.Clusters, ignored, unmapped, trace.Frame);
    }

    /// <summary>A part's label with its value after it (R-im11-3: refdes and value), when the row has one.</summary>
    private static string WithValue(string label, PartsTable parts, PartRow? row) =>
        row is not null && PartsTableCsv.Cell(parts, row, "Value") is { Length: > 0 } value ? $"{label} {value}" : label;

    private static IReadOnlyList<(double X, double Y)> Points(Clipper2Lib.PathD path) => [.. path.Select(p => (p.x, p.y))];

    // ── a schematic picture (brief-img-10 R-im10-9) ─────────────────────────────────────────────────

    /// <summary>A layout picture's round hole that is no via (an opening, or not plated), and a correction made by hand
    /// (brief-dfr29-1).</summary>
    public const string HolePrefix = "hole:", CorrectionPrefix = "fix:";

    /// <summary>The id prefixes of the items a user corrects (R-im10-8): a crossing is clicked, a dangling end dragged.</summary>
    public const string CrossingPrefix = "crossing:", EndPrefix = "end:";

    /// <summary>
    /// The overlay of a schematic picture's reading (R-im5-3's schematic classes), in the SOURCE picture's pixels: the
    /// wires, junction dots and crossings — each crossing drawn as it was read, so a click can flip it — every symbol
    /// boxed with its kind and orientation, the ports, every word boxed with what was read, and in amber whatever was not
    /// read: a dangling wire end, a `?` part, an unread value, an unread word.
    /// </summary>
    public static ImageOverlay BuildSchematic(SchematicImageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Read is not { } read) return Empty;
        var items = new List<ImageOverlayItem>();
        (double X, double Y) S(double x, double y) => read.ToSource(x, y);
        IReadOnlyList<(double X, double Y)> Box(PixelBox b, double pad = 0) =>
            [S(b.Left - pad, b.Top - pad), S(b.Right + 1 + pad, b.Top - pad), S(b.Right + 1 + pad, b.Bottom + 1 + pad), S(b.Left - pad, b.Bottom + 1 + pad)];
        var graph = read.Wires;

        foreach (var seg in graph.Segments)
        {
            var path = new List<(double X, double Y)> { S(seg.A.x, seg.A.y), S(seg.B.x, seg.B.y) };
            items.Add(new ImageOverlayItem($"wire:{seg.Id}", ImageOverlayClass.Wire, [path], false, null, null, false, null,
                                           PixelBounds.Of(path).Inflate(1)));
        }
        foreach (var node in graph.Nodes.Where(n => n.Dot))
        {
            var (x, y) = S(node.X, node.Y);
            double r = Math.Max(2, read.StrokeWidth / read.Scale * 1.5);
            items.Add(new ImageOverlayItem($"dot:{node.Id}", ImageOverlayClass.Wire, [], true, null, null, false, null,
                                           new PixelBounds(x - r, y - r, x + r, y + r), (x, y, r)));
        }
        foreach (var end in read.Dangling)
        {
            var (x, y) = S(graph.Nodes[end].X, graph.Nodes[end].Y);
            double r = Math.Max(3, read.StrokeWidth / read.Scale * 2);
            items.Add(new ImageOverlayItem($"{EndPrefix}{end}", ImageOverlayClass.Wire, [], true, null, null, true, null,
                                           new PixelBounds(x - r, y - r, x + r, y + r), (x, y, r)));
        }
        foreach (var c in graph.Crossings)
        {
            var (x, y) = S(c.X, c.Y);
            double r = Math.Max(5, read.StrokeWidth / read.Scale * 3);
            string label = c.Reading switch
            {
                CrossingReading.Connected => "connected",
                CrossingReading.Hop => "hop",
                _ => "not connected",
            } + (c.Overridden ? " (yours)" : "");
            items.Add(new ImageOverlayItem($"{CrossingPrefix}{c.Id}", ImageOverlayClass.Crossing, [], true, label,
                                           c.Connected ? Colours.Connected : Colours.Ignored, false, null, new PixelBounds(x - r, y - r, x + r, y + r), (x, y, r)));
        }

        // Annotation strokes (R-imf3-3, R-imf3-4): an arrow from its tail to its tip, a dashed line end to end, named.
        for (int i = 0; i < read.Annotations.Count; i++)
        {
            var a = read.Annotations[i];
            var path = a.Path.Select(p => S(p.x, p.y)).ToList();
            items.Add(new ImageOverlayItem($"annotation:{i}", ImageOverlayClass.Annotation, [path], false, AnnotationLabel(a.Kind), null, false, null,
                                           PixelBounds.Of(path).Inflate(2)));
        }

        // Symbols, as parts: the parts-table row's designator where there is one, so a row and its box name each other.
        // A transformer is three rows on one symbol — its two coils and its coupling — and its box names the coupling's,
        // the transformer's own designator (the shortest of the three).
        var rowOf = result.Circuit?.SymbolOfRow.GroupBy(kv => kv.Value)
                          .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).MinBy(k => k.Length)!) ?? [];
        var rows = result.Parts;
        var labels = result.Labels?.Symbols.ToDictionary(l => l.Symbol) ?? [];
        var syms = result.Symbols?.Symbols ?? [];
        for (int i = 0; i < syms.Count; i++)
        {
            var s = syms[i];
            if (s.Kind == ImageSymbolKind.Port) continue;
            var box = Box(s.Region.Box, 2);
            string? key = rowOf.TryGetValue(i, out var k) ? k : null;
            var row = key is null ? null : rows.Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, key, StringComparison.OrdinalIgnoreCase));
            string name = row?.Refdes ?? (labels.TryGetValue(i, out var l) ? l.Designator : null) ?? "";
            string label = WithValue($"{name} {s.KindLabel}{(s.Template is null ? "" : " " + s.Orientation)}".Trim(), rows, row);
            bool unknown = s.Disposition is SymbolDisposition.Unknown or SymbolDisposition.UnreadTerminal
                           || (row is not null && row.IsModelled && row.TakesValue && row.Value is null && row.Model != PartModelKind.SnP);
            items.Add(new ImageOverlayItem($"part:{key ?? "symbol" + i}", ImageOverlayClass.Part, [box], true, label, null, unknown, key,
                                           PixelBounds.Of(box)));
        }
        foreach (var p in result.Circuit?.Ports ?? [])
        {
            const double r = 7;
            items.Add(new ImageOverlayItem($"port:{p.Number}", ImageOverlayClass.Port, [], true,
                                           $"P{p.Number.ToString(CultureInfo.InvariantCulture)}", null, false, null,
                                           new PixelBounds(p.X - r, p.Y - r, p.X + r, p.Y + r), (p.X, p.Y, r)));
        }
        foreach (var w in result.Words)
        {
            var box = Box(w.Box, 1);
            items.Add(new ImageOverlayItem($"text:{w.Id}", ImageOverlayClass.Text, [box], true,
                                           w.Class == WordClass.Unread ? null : w.Text, null, w.Class == WordClass.Unread, null, PixelBounds.Of(box)));
        }
        return new ImageOverlay(items, null, [], [], null, pixelAnchors: true) { Legend = read.Profile?.Lines() ?? [] };
    }

    /// <summary>What an annotation stroke was read as, in the overlay's words.</summary>
    public static string AnnotationLabel(StrokeAnnotationKind kind) => kind switch
    {
        StrokeAnnotationKind.DimensionArrow => "dimension arrow",
        StrokeAnnotationKind.CurrentArrow => "current arrow",
        StrokeAnnotationKind.VoltageArrow => "voltage arrow",
        StrokeAnnotationKind.TerminalArrow => "arrow into a terminal",
        StrokeAnnotationKind.SupplyArrow => "supply arrow",
        StrokeAnnotationKind.SymbolArrow => "arrow on a symbol",
        StrokeAnnotationKind.DashedBox => "dashed boundary",
        _ => "dashed wire",
    };
}
