// Corrections made by hand to a traced picture — brief-dfr29-1-picture-to-layout-by-hand.md R-dfr29-1-1, R-dfr29-1-5
// (D2).
//
// A correction says what a piece of the picture IS where the colours alone cannot: a hole that is a via, a hole that is
// not plated, a hole that is only an opening, copper the colours missed, copper that is a part's body, the board's
// outline. Each is kept in the picture's own pixels, so it survives a re-scale, and is applied by Build AFTER the colour
// trace: a correction never changes what is read, only what is written. One that lands on nothing — a via off the
// copper, a hole where there is no hole — is refused in place with one sentence, never dropped.
//
// The dialog and the CLI build the same records (ImageSourceViewModel, PictureFlags), so both write the same bytes.
//
// A NON-PLATED HOLE needs a non-plated via layer. A technology with none gains one, "Non-plated hole", in a COPY (D2),
// which Create writes into the new cell as <cell>.ctech, referenced by the layout — the workspace's own technology is
// never changed. The picture's stackup is the chosen technology's as it is: the dialog edits no stackup (owner decision,
// 2026-10-27, R-dfr29-1-4 withdrawn).

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Theming;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>What a correction says.</summary>
public enum ImageCorrectionKind
{
    /// <summary>A via at the point: drilled as stated, else from the hole under it, else the technology's default via.</summary>
    Via,

    /// <summary>The hole under the point is a non-plated hole; the copper opening is kept.</summary>
    Hole,

    /// <summary>The hole under the point stays an opening: no via is made of it.</summary>
    NotVia,

    /// <summary>A filled shape on a conductor, unioned with the copper traced there.</summary>
    Copper,

    /// <summary>No copper inside the shape — a part's body the colours read as copper: on every layer, or on the one it
    /// names (<i>Remove ▸ Copper read on</i>, brief-dfr30-1 R-dfr30-1-4).</summary>
    Clear,

    /// <summary>The board outline, written on the technology's outline layer.</summary>
    Outline,

    /// <summary>The area enclosed at the point, painted as copper on a conductor — a paint bucket (ImageTraceFill.cs).</summary>
    Fill,
}

/// <summary>One correction, in the traced picture's pixels (y down).</summary>
/// <param name="Points">One point for a via, a hole or a fill; a closed polygon's vertices for a shape.</param>
/// <param name="Layer">The conductor a <see cref="ImageCorrectionKind.Copper"/> fill is on, or the one a
/// <see cref="ImageCorrectionKind.Clear"/> clears (null: every layer), by layer name.</param>
/// <param name="DrillMetres">A via's drill diameter when stated (brief-dfr30-1 R-dfr30-1-2); null takes it from the hole
/// under the point. A LENGTH, not pixels: a drill chosen as 0.3 mm stays 0.3 mm when the scale is changed, where the point
/// it sits on moves with the picture.</param>
public sealed record ImageCorrection(ImageCorrectionKind Kind, IReadOnlyList<PixelPoint> Points, string? Layer = null, double? DrillMetres = null)
{
    public static ImageCorrection Via(double x, double y, double? drillMetres = null) => new(ImageCorrectionKind.Via, [new(x, y)], null, drillMetres);
    public static ImageCorrection Hole(double x, double y) => new(ImageCorrectionKind.Hole, [new(x, y)]);
    public static ImageCorrection NotVia(double x, double y) => new(ImageCorrectionKind.NotVia, [new(x, y)]);
    public static ImageCorrection Copper(IReadOnlyList<PixelPoint> polygon, string layer) => new(ImageCorrectionKind.Copper, polygon, layer);
    public static ImageCorrection Clear(IReadOnlyList<PixelPoint> polygon, string? layer = null) => new(ImageCorrectionKind.Clear, polygon, layer);
    public static ImageCorrection Outline(IReadOnlyList<PixelPoint> polygon) => new(ImageCorrectionKind.Outline, polygon);
    public static ImageCorrection Fill(double x, double y, string layer) => new(ImageCorrectionKind.Fill, [new(x, y)], layer);

    /// <summary>The axis-aligned rectangle between two corners, as a polygon.</summary>
    public static IReadOnlyList<PixelPoint> Rectangle(double x0, double y0, double x1, double y1) =>
    [
        new(Math.Min(x0, x1), Math.Min(y0, y1)), new(Math.Max(x0, x1), Math.Min(y0, y1)),
        new(Math.Max(x0, x1), Math.Max(y0, y1)), new(Math.Min(x0, x1), Math.Max(y0, y1)),
    ];

    /// <summary>Whether it is a point (a via or a hole) rather than a shape.</summary>
    public bool IsPoint => Kind is ImageCorrectionKind.Via or ImageCorrectionKind.Hole or ImageCorrectionKind.NotVia;

    public PixelPoint At => Points[0];

    /// <summary>The CLI flag that states it, as <c>convert</c> takes it.</summary>
    public string Spelling
    {
        get
        {
            string P(PixelPoint p) => $"{N(p.X)}px,{N(p.Y)}px";
            string Poly() => string.Join(";", Points.Select(P));
            return Kind switch
            {
                ImageCorrectionKind.Via => $"--via {P(At)}" + (DrillMetres is { } d ? $",{N(d * 1e3, "0.####")}mm" : ""),
                ImageCorrectionKind.Hole => $"--hole {P(At)}",
                ImageCorrectionKind.NotVia => $"--not-via {P(At)}",
                ImageCorrectionKind.Copper => $"--copper \"{Poly()}={Layer}\"",
                ImageCorrectionKind.Fill => $"--fill \"{P(At)}={Layer}\"",
                ImageCorrectionKind.Clear => Layer is null ? $"--clear \"{Poly()}\"" : $"--clear \"{Poly()}={Layer}\"",
                _ => $"--outline \"{Poly()}\"",
            };
        }
    }

    internal PathD Path() => new(Points.Select(p => new PointD(p.X, p.Y)));

    private static string N(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>What became of one correction: applied, or the one sentence saying why not.</summary>
public sealed record ImageCorrectionOutcome(ImageCorrection Correction, string? Refusal)
{
    public bool Applied => Refusal is null;
}

/// <summary>What a round hole in the picture was written as.</summary>
public enum ImageHoleKind { Via, NonPlated, Opening }

/// <summary>A round hole read in the picture, pixels, and what it became.</summary>
public sealed record ImageTracedHole(CircleFit Circle, ImageHoleKind Kind);

public static partial class ImageTrace
{
    /// <summary>The non-plated via layer a technology without one gains in its copy (D2).</summary>
    public const string NonPlatedHoleLayerName = "Non-plated hole";

    /// <summary>The layer names a <see cref="ImageCorrectionKind.Copper"/> fill may name: each conductor's drawing layer,
    /// top first — the dialog's picker and the CLI's check.</summary>
    public static IReadOnlyList<string> ConductorLayerNames(Technology tech) =>
        [.. ImageLayerMap.CopperLayers(tech).Select(k => ImageLayerMap.NameOf(tech, k))];

    /// <summary>A point within this many pixels beyond a hole's radius is on the hole.</summary>
    internal const double HoleReachPx = 1.0;

    /// <summary><paramref name="input"/> with the rectangle its board photo's corners name as the board outline, unless an
    /// outline is drawn already — Board corners…'s <i>Use as the board outline</i>, and <c>--outline corners</c>.</summary>
    public static ImageTraceInput WithPhotoOutline(ImageTraceInput input)
    {
        if (input.Photo?.Rectangle() is not { } rect || input.Corrections.Any(c => c.Kind == ImageCorrectionKind.Outline)) return input;
        return input with { Corrections = [ImageCorrection.Outline(rect), .. input.Corrections] };
    }

    /// <summary>A copy of <paramref name="tech"/> when a non-plated hole may have to be placed on a technology with no
    /// layer for one (D2), else null. The layer is added to it only when a hole is in fact placed.</summary>
    private static Technology? CopyFor(ImageTraceInput input, Technology tech) =>
        input.Corrections.Any(c => c.Kind == ImageCorrectionKind.Hole) && NonPlatedLayer(tech) is null ? TechPersistence.Clone(tech) : null;

    /// <summary>A non-plated via layer's drawing layer, or null.</summary>
    internal static LayerKey? NonPlatedLayer(Technology tech) =>
        tech.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Via && l.Plated == false && l.DrawingLayers.Count > 0)?.DrawingLayers[0];

    /// <summary>Adds <see cref="NonPlatedHoleLayerName"/> to <paramref name="copy"/>, spanning the board (D2).</summary>
    private static LayerKey AddNonPlatedLayer(Technology copy)
    {
        var key = new LayerKey(copy.Layers.Select(l => l.Key.Layer).DefaultIfEmpty(0).Max() + 1, 0);
        copy.Layers.Add(new LayerDef
        {
            Key = key, Name = NonPlatedHoleLayerName, Color = new Rgba(0x80, 0x80, 0x80), Purpose = "drawing",
            ZOrder = copy.Layers.Select(l => l.ZOrder).DefaultIfEmpty(0).Max() + 1,
        });
        var conductors = Conductors.Of(copy);
        copy.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = NonPlatedHoleLayerName, DrawingLayers = [key], Plated = false,
            SpanFromLayer = conductors.Count > 0 ? conductors[0].StackupName : null,
            SpanToLayer = conductors.Count > 0 ? conductors[^1].StackupName : null,
        });
        return key;
    }

    /// <summary>The copper corrections — Clear, then Copper — applied to the layers read (R-dfr29-1-1). A layer no
    /// correction touches is the very list read, so a trace with none is unchanged to the byte.</summary>
    private static List<PixelLayer> EditCopper(List<PixelLayer> read, IReadOnlyList<ImageCorrection> fixes, Technology tech,
                                               ImageCorrectionOutcome?[] outcomes, ColourClusterSet clusters, ImageTraceOptions options)
    {
        if (!fixes.Any(f => f.Kind is ImageCorrectionKind.Copper or ImageCorrectionKind.Clear or ImageCorrectionKind.Fill)) return read;
        var layers = new List<PixelLayer>(read);
        var copperOrder = ImageLayerMap.CopperLayers(tech);

        for (int i = 0; i < fixes.Count; i++)
        {
            if (fixes[i].Kind != ImageCorrectionKind.Clear) continue;
            var cut = fixes[i].Path();
            if (fixes[i].Points.Count < 3 || Math.Abs(Clipper.Area(cut)) < 1) { outcomes[i] = new(fixes[i], "An area to clear needs at least three corners around something."); continue; }
            LayerKey? only = null;
            if (fixes[i].Layer is { } named)
            {
                if (ImageLayerMap.KeyOf(tech, named) is not { } k0 || !copperOrder.Contains(k0)) { outcomes[i] = new(fixes[i], NotAConductor(tech, named, copperOrder)); continue; }
                only = k0;
            }
            bool hit = false;
            for (int k = 0; k < layers.Count; k++)
            {
                var l = layers[k];
                if (l.Role != ImageLayerRole.Layer || l.Regions.Count == 0 || (only is { } o && l.Key != o)) continue;
                var (kept, touched) = Split(l.Regions, cut);
                if (touched.Count == 0) continue;
                double before = touched.Sum(r => Math.Abs(r.Area));
                var after = Regions(ClipType.Difference, touched, cut);
                if (Math.Abs(before - after.Sum(r => Math.Abs(r.Area))) < 0.5) continue;
                hit = true;
                layers[k] = l with { Regions = [.. kept, .. after] };
            }
            outcomes[i] = new(fixes[i], hit ? null : only is null ? "There is no traced copper inside the area to clear."
                                                                  : $"There is no traced copper on {fixes[i].Layer} inside the area to clear.");
        }

        var contour = new ContourOptions
        {
            Simplify = options.SimplifyPx, Snap = options.Snap, SnapAngleDeg = options.SnapAngleDeg, Snap45 = options.Snap45, MinArea = 0,
        };
        for (int i = 0; i < fixes.Count; i++)
        {
            if (fixes[i].Kind is not (ImageCorrectionKind.Copper or ImageCorrectionKind.Fill)) continue;
            PathsD fill;
            if (fixes[i].Kind == ImageCorrectionKind.Fill)
            {
                var painted = FillRegions(clusters, fixes[i].At, contour, options.MinFeaturePx2, out bool background);
                if (painted.Count == 0)
                {
                    outcomes[i] = new(fixes[i], background ? FillBackgroundRefusal : $"There is nothing to fill at {fixes[i].At.X.ToString("0.#", CultureInfo.InvariantCulture)}px,{fixes[i].At.Y.ToString("0.#", CultureInfo.InvariantCulture)}px.");
                    continue;
                }
                fill = [.. painted.SelectMany(r => r.Paths)];
            }
            else
            {
                var drawn = fixes[i].Path();
                if (fixes[i].Points.Count < 3 || Math.Abs(Clipper.Area(drawn)) < 1) { outcomes[i] = new(fixes[i], "A copper fill needs at least three corners around an area."); continue; }
                fill = [drawn];
            }
            string name = fixes[i].Layer ?? "";
            if (ImageLayerMap.KeyOf(tech, name) is not { } key || !copperOrder.Contains(key))
            {
                outcomes[i] = new(fixes[i], NotAConductor(tech, name, copperOrder));
                continue;
            }
            string layerName = ImageLayerMap.NameOf(tech, key);
            int at = layers.FindIndex(l => l.Role == ImageLayerRole.Layer && l.Key == key);
            if (at < 0)
            {
                // A conductor the picture put nothing on: placed in the technology's copper order, as Prepare orders them.
                var empty = new PixelLayer(layerName, key, ImageLayerRole.Layer, [], 0, 0, [], 0);
                int order = copperOrder.IndexOf(key);
                at = layers.FindIndex(l => l.Role != ImageLayerRole.Layer || l.Key is not { } lk || !copperOrder.Contains(lk) || copperOrder.IndexOf(lk) > order);
                if (at < 0) at = layers.Count;
                layers.Insert(at, empty);
            }
            var (kept, touched) = Split(layers[at].Regions, fill);
            layers[at] = layers[at] with { Regions = [.. kept, .. Regions(ClipType.Union, touched, fill)] };
            outcomes[i] = new(fixes[i], null);
        }
        return layers;
    }

    private static string NotAConductor(Technology tech, string name, List<LayerKey> copperOrder) =>
        $"'{name}' is not one of {tech.Name}'s conductors: " + string.Join(", ", copperOrder.Select(k => ImageLayerMap.NameOf(tech, k))) + ".";

    /// <summary>The regions whose bounds meet <paramref name="shape"/>'s, and the rest.</summary>
    private static (List<ContourRegion> Kept, List<ContourRegion> Touched) Split(List<ContourRegion> regions, PathD shape) =>
        Split(regions, new PathsD { shape });

    private static (List<ContourRegion> Kept, List<ContourRegion> Touched) Split(List<ContourRegion> regions, PathsD shape)
    {
        var b = Clipper.GetBounds(shape);
        var kept = new List<ContourRegion>();
        var touched = new List<ContourRegion>();
        foreach (var r in regions)
        {
            var rb = Clipper.GetBounds(r.Outer);
            (rb.right < b.left || rb.left > b.right || rb.bottom < b.top || rb.top > b.bottom ? kept : touched).Add(r);
        }
        return (kept, touched);
    }

    /// <summary><paramref name="regions"/> combined with <paramref name="shape"/>, as regions with their holes.</summary>
    private static List<ContourRegion> Regions(ClipType op, List<ContourRegion> regions, PathD shape) => Regions(op, regions, new PathsD { shape });

    /// <summary><paramref name="regions"/> combined with <paramref name="shape"/> — rings read even-odd, so a shape's holes
    /// stay holes.</summary>
    private static List<ContourRegion> Regions(ClipType op, List<ContourRegion> regions, PathsD shape)
    {
        var c = new ClipperD(4);
        foreach (var r in regions) c.AddSubject(r.Paths);
        c.AddClip(shape);
        var tree = new PolyTreeD();
        c.Execute(op, FillRule.EvenOdd, tree);
        var result = new List<ContourRegion>();
        void Walk(PolyPathD node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var outer = node[i];
                var holes = new List<PathD>();
                for (int j = 0; j < outer.Count; j++)
                {
                    holes.Add(outer[j].Polygon!);
                    Walk(outer[j]);   // islands in the hole
                }
                result.Add(new ContourRegion(outer.Polygon!, holes));
            }
        }
        Walk(tree);
        return result;
    }

    /// <summary>The last point correction of <paramref name="kinds"/> on the hole at (<paramref name="cx"/>,
    /// <paramref name="cy"/>) of radius <paramref name="r"/>, pixels — the last one wins; any earlier one on the same hole
    /// is answered by it, and said so.</summary>
    private static int LastOn(IReadOnlyList<ImageCorrection> fixes, ImageCorrectionOutcome?[] outcomes, double cx, double cy, double r)
    {
        int last = -1;
        for (int i = 0; i < fixes.Count; i++)
        {
            if (!fixes[i].IsPoint) continue;
            var p = fixes[i].At;
            if ((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) > (r + HoleReachPx) * (r + HoleReachPx)) continue;
            if (last >= 0 && outcomes[last] is null) outcomes[last] = new(fixes[last], "A later correction of the same hole answers it.");
            last = i;
        }
        return last;
    }

    /// <summary>The report's one line on the corrections (R-dfr29-1-1), refusals named after it.</summary>
    private static void ReportCorrections(IReadOnlyList<ImageCorrectionOutcome> outcomes, RecognitionReport report)
    {
        if (outcomes.Count == 0) return;
        var applied = outcomes.Where(o => o.Applied).Select(o => o.Correction).ToList();
        var parts = new List<string>();
        void Say(ImageCorrectionKind kind, string one, string many)
        {
            int n = applied.Count(c => c.Kind == kind);
            if (n > 0) parts.Add(Plural(n, one, many));
        }
        Say(ImageCorrectionKind.Via, "via added", "vias added");
        Say(ImageCorrectionKind.Hole, "hole not plated", "holes not plated");
        Say(ImageCorrectionKind.NotVia, "hole kept as an opening", "holes kept as openings");
        foreach (var g in applied.Where(c => c.Kind == ImageCorrectionKind.Copper).GroupBy(c => c.Layer ?? ""))
            parts.Add($"{Plural(g.Count(), "copper fill", "copper fills")} on {g.Key}");
        foreach (var g in applied.Where(c => c.Kind == ImageCorrectionKind.Fill).GroupBy(c => c.Layer ?? ""))
            parts.Add($"{Plural(g.Count(), "area", "areas")} painted as copper on {g.Key}");
        int clears = applied.Count(c => c.Kind == ImageCorrectionKind.Clear && c.Layer is null);
        if (clears > 0) parts.Add(Plural(clears, "area cleared of copper", "areas cleared of copper"));
        foreach (var g in applied.Where(c => c.Kind == ImageCorrectionKind.Clear && c.Layer is not null).GroupBy(c => c.Layer!))
            parts.Add($"{Plural(g.Count(), "area", "areas")} cleared of copper on {g.Key}");
        if (applied.Any(c => c.Kind == ImageCorrectionKind.Outline)) parts.Add("the board outline drawn");
        string line = $"{Plural(outcomes.Count, "correction", "corrections")}" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "") + ".";
        foreach (var o in outcomes.Where(o => !o.Applied))
            line += $" Not applied ({o.Correction.Spelling}): {o.Refusal}";
        report.Add(RecognitionFindingClass.ImageCorrections, applied.Count, line);
    }
}
