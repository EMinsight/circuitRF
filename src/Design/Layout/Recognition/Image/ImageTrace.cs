// Trace a layout picture — brief-img-3-trace-layout-image.md R-im3-1, R-im3-5, R-im3-6, R-im3-9 (D5, D6, D7, D8, D16).
//
// A layout picture becomes LayoutShapes on technology layers, at a stated scale, sitting exactly on the picture. The
// split ArtworkRecognition already has: Trace is pure — it posts nothing and writes nothing — and Run traces and then
// writes (ImageTraceTarget.cs).
//
//   picture ─ IM-1 colour clusters ─ ImageLayerMap (Auto, or the user's) ─ per layer: coverage → contours (IM-1)
//           ─ ImageScale (D6) ─ PixelFrame ─ DBU: rectangles, circles, polygons with their holes, vias, outline paths
//           ─ the underlay: the picture itself, locked, at 35 %, at exactly the traced frame
//
// Every coordinate leaves pixel space through ONE PixelFrame, and is rounded so a snapped edge stays snapped: an axis
// edge keeps one coordinate and a 45° edge keeps x ± y, both rounded once per edge rather than once per vertex —
// rounding the two ends' x and y separately would put a 1-DBU kink in an exact 45° jog.
//
// A trace is refused only for no technology, or no shape on any copper layer with no source picture kept under the result
// (a layout of the picture alone is one to trace by hand); Run also refuses no scale.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>How a picture is traced. Every field has the dialog's default.</summary>
public sealed record ImageTraceOptions
{
    /// <summary>Douglas–Peucker tolerance on each contour, pixels.</summary>
    public double SimplifyPx { get; init; } = 0.35;

    /// <summary>Whether edges near 0°/90° (and 45°) are snapped onto them.</summary>
    public bool Snap { get; init; } = true;

    /// <summary>An edge within this many degrees of a snap angle is snapped.</summary>
    public double SnapAngleDeg { get; init; } = 3.0;

    /// <summary>Also snap onto 45° and 135°.</summary>
    public bool Snap45 { get; init; } = true;

    /// <summary>Regions and holes smaller than this many square pixels are specks and are removed.</summary>
    public double MinFeaturePx2 { get; init; } = 2.0;

    /// <summary>Keep the source picture under the result (D5; Settings ▸ Recognition).</summary>
    public bool KeepUnderlay { get; init; } = true;

    /// <summary>At most this many colours are separated (D7).</summary>
    public int MaxColours { get; init; } = 8;

    /// <summary>Colours closer than this ΔE are one colour (D7).</summary>
    public double MergeDeltaE { get; init; } = DefaultMergeDeltaE;

    /// <summary>The picture outlines every shape with a line this many pixels wide, centred on its edge, so each traced
    /// shape is pulled in by half of it; 0 when shapes are drawn as filled area alone. circuitRF's own layout view
    /// outlines each shape 2 device pixels wide: a picture of it traced at 0 is a pixel fat all round (IM-12).</summary>
    public double EdgeStrokePx { get; init; }

    public const double DefaultMergeDeltaE = 10.0;

    /// <summary>The provenance's options block. The merge distance is written only when it is not the default, so a
    /// document traced with every default reads as it always has.</summary>
    public SortedDictionary<string, string> ToProvenance() => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["simplify"] = SimplifyPx.ToString("0.###", CultureInfo.InvariantCulture) + " px",
        ["snap"] = Snap ? (Snap45 ? "0/45/90" : "0/90") : "off",
        ["snapAngle"] = SnapAngleDeg.ToString("0.###", CultureInfo.InvariantCulture) + " deg",
        ["minFeature"] = MinFeaturePx2.ToString("0.###", CultureInfo.InvariantCulture) + " px2",
        ["colours"] = MaxColours.ToString(CultureInfo.InvariantCulture),
        ["underlay"] = KeepUnderlay ? "on" : "off",
    }.WithMerge(MergeDeltaE).WithEdgeStroke(EdgeStrokePx);
}

internal static class ImageTraceOptionsProvenance
{
    public static SortedDictionary<string, string> WithMerge(this SortedDictionary<string, string> d, double merge)
    {
        if (merge != ImageTraceOptions.DefaultMergeDeltaE) d["merge"] = merge.ToString("0.###", CultureInfo.InvariantCulture) + " dE";
        return d;
    }

    /// <summary>The edge stroke, written only when there is one — a document traced without reads as it always has.</summary>
    public static SortedDictionary<string, string> WithEdgeStroke(this SortedDictionary<string, string> d, double px)
    {
        if (px > 0) d["edgeStroke"] = px.ToString("0.###", CultureInfo.InvariantCulture) + " px";
        return d;
    }
}

/// <summary>A rectangle in the picture, pixels (y down) — a trace scope.</summary>
public readonly record struct PixelRect(double Left, double Top, double Right, double Bottom);

/// <summary>What a trace reads (R-im3-1).</summary>
public sealed record ImageTraceInput
{
    /// <summary>The picture (IM-2).</summary>
    public required ImageSource Source { get; init; }

    /// <summary>The technology the user chose (D8), or null — which is a refusal.</summary>
    public required Technology? Technology { get; init; }

    /// <summary>Its file, for the written layout's technology reference.</summary>
    public string? TechnologyPath { get; init; }

    /// <summary>What the user said about the scale; Auto lets the evidence decide (D6).</summary>
    public ImageScaleStatement Scale { get; init; } = ImageScaleStatement.Auto;

    /// <summary>The colour → layer map; null is Auto. An edited map is rebound to the clusters, never replaced.</summary>
    public ImageLayerMap? LayerMap { get; init; }

    /// <summary>The part of the picture traced, pixels; null is the whole picture.</summary>
    public PixelRect? Scope { get; init; }

    public ImageTraceOptions Options { get; init; } = new();

    /// <summary>The kind as IM-2 read (or the user forced) it; null reads it again for the provenance.</summary>
    public ImageKindResult? Kind { get; init; }

    /// <summary>The trace goes into the layout the picture's bitmap sits in (D6 (1), D14). <see cref="ImageTrace.Run"/>
    /// sets it from the target.</summary>
    public bool TargetIsPlacedLayout { get; init; }

    /// <summary>The DBU per micron of a new layout. A placed bitmap's layout uses its own.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>A board photo read on its four corners (IM-13), set by <see cref="BoardPhoto.Apply"/> along with the
    /// rectified <see cref="Source"/>: its classes replace the colour separation, its scale is offered as stated, and its
    /// default map is R-im13-4's.</summary>
    public BoardPhotoReading? Photo { get; internal init; }

    /// <summary>What the user corrected by hand, in the traced picture's pixels — applied after the colour trace, so a
    /// correction never changes what is read, only what is written (brief-dfr29-1 R-dfr29-1-1).</summary>
    public IReadOnlyList<ImageCorrection> Corrections { get; init; } = [];
}

/// <summary>One layer as traced.</summary>
/// <param name="Name">The technology layer's name.</param>
/// <param name="Key">Its key, or null when the technology has no such layer (nothing is written for it).</param>
/// <param name="PixelRegions">The regions in pixel space — what the dialog's overlay draws.</param>
/// <param name="Shapes">The shapes in DBU; empty until there is a scale.</param>
public sealed record ImageTracedLayer(string Name, LayerKey? Key, ImageLayerRole Role,
                                      IReadOnlyList<ContourRegion> PixelRegions, IReadOnlyList<LayoutShape> Shapes);

/// <summary>What a trace produced.</summary>
public sealed record ImageTraceResult
{
    /// <summary>Why nothing can be made, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    public required RecognitionReport Report { get; init; }

    public ColourClusterSet? Clusters { get; init; }
    public ImageLayerMap? LayerMap { get; init; }

    /// <summary>Every piece of scale evidence, and the one chosen (null: no scale yet).</summary>
    public IReadOnlyList<ImageScaleCandidate> ScaleCandidates { get; init; } = [];
    public ImageScaleCandidate? Scale { get; init; }

    /// <summary>Pixels → DBU; null without a scale.</summary>
    public PixelFrame? Frame { get; init; }
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    public IReadOnlyList<ImageTracedLayer> Layers { get; init; } = [];

    /// <summary>The drill holes read, pixels — what the dialog's overlay draws before there is a scale.</summary>
    public IReadOnlyList<CircleFit> Drills { get; init; } = [];

    /// <summary>Every traced shape, in DBU — vias and outline paths included, the underlay not.</summary>
    public IReadOnlyList<LayoutShape> Shapes { get; init; } = [];

    /// <summary>The picture as a locked 35 % bitmap at the traced frame (D5), or null. Its path is the source file's
    /// until <see cref="ImageTrace.Run"/> keeps a copy and points it there.</summary>
    public BitmapShape? Underlay { get; init; }

    /// <summary>The silkscreen's centre lines, DBU, flat x,y — what IM-4's stroke reader takes.</summary>
    public IReadOnlyList<long[]> SilkscreenStrokes { get; init; } = [];

    /// <summary>The silkscreen's median stroke width, pixels (IM-1's skeleton estimate); 0 without silkscreen.</summary>
    public double SilkscreenStrokeWidthPx { get; init; }

    /// <summary>The technology layer the silkscreen was mapped to, or null.</summary>
    public LayerKey? SilkscreenLayer { get; init; }

    /// <summary>The part of the picture traced, pixels: the scope, else the whole picture.</summary>
    public PixelRect Traced { get; init; }

    /// <summary>R-im3-3's one line, or null without a scale.</summary>
    public string? ResolutionLine { get; init; }

    public ImageKindResult? Kind { get; init; }

    /// <summary>The technology traced with: the input's, or its copy (<see cref="TechnologyCopy"/>).</summary>
    public Technology? Technology { get; init; }

    /// <summary>The copy of the input's technology a non-plated hole needed, written beside the new cell as its own
    /// <c>.ctech</c> (D2); null when the technology was traced with as it is.</summary>
    public Technology? TechnologyCopy { get; init; }

    /// <summary>What became of each correction, in the input's order; empty without a scale.</summary>
    public IReadOnlyList<ImageCorrectionOutcome> Corrections { get; init; } = [];

    /// <summary>Every round hole read, pixels, and what it became — what the dialog's hole picker offers.</summary>
    public IReadOnlyList<ImageTracedHole> Holes { get; init; } = [];

    /// <summary>The picture draws its copper as outlines (brief-dfr30-1 R-dfr30-1-7): what the reading panel says once.</summary>
    public bool LineArt { get; init; }
}

public static partial class ImageTrace
{
    /// <summary>The underlay's opacity (D5).</summary>
    public const double UnderlayOpacity = 0.35;

    /// <summary>The one sentence for a picture with no copper at the mapped colours.</summary>
    public const string NoCopperRefusal =
        "The picture has no copper-coloured regions at the mapped colours: map a colour to a copper layer, or check " +
        "that the picture is a layout.";

    /// <summary>The one note for a line drawing (brief-dfr30-1 R-dfr30-1-7). The reading is not changed for it: which of a
    /// drawing's closed outlines are copper — and not a hole, a part's body or a letter — is not in the picture.</summary>
    public const string LineArtNote =
        "This picture draws copper as outlines, so little copper is read from it. A coloured or filled picture of the same " +
        "board reads better.";

    public const string NoTechnologyRefusal =
        "There is no technology, so there are no layers to trace onto. Choose one — the workspace's default — and try again.";

    /// <summary>Traces <paramref name="input"/>'s picture (R-im3-1). Pure: posts nothing, writes nothing.</summary>
    /// <summary>The scale Paste Image as Layout draws a picture at when it states none and none can be inferred: a screen's
    /// 96 pixels to the inch.</summary>
    public const double PasteFallbackMetresPerPixel = 0.0254 / 96;

    /// <summary>
    /// Paste Image as Layout: <paramref name="input"/> traced with what it gives — every default but its technology and
    /// DBU — at the scale the picture states or shows, else at <see cref="PasteFallbackMetresPerPixel"/>, said in the
    /// report, since the pasted shapes are selected and scaled as one afterwards. Writes nothing.
    /// </summary>
    public static ImageTraceResult PasteTrace(ImageTraceInput input, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var result = Trace(input, control);
        if (!result.Ok || result.Scale is not null) return result;
        result = Trace(input with { Scale = ImageScaleStatement.Stated(PasteFallbackMetresPerPixel) }, control);
        if (result.Ok)
            result.Report.Add(RecognitionFindingClass.ImageScaleChosen, 1,
                "The picture states no scale and none could be inferred, so it is drawn at 96 pixels to the inch: " +
                "the pasted shapes are selected — drag a corner handle, or use Scale…, to size them.");
        return result;
    }

    public static ImageTraceResult Trace(ImageTraceInput input, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var report = new RecognitionReport();
        var token = control?.Token ?? CancellationToken.None;
        if (input.Technology is not { } given) return new ImageTraceResult { Report = report, Refusal = NoTechnologyRefusal };
        var copy = CopyFor(input, given);
        var tech = copy ?? given;
        if (copy is not null) input = input with { Technology = copy };

        control?.BeginStage("Separating colours");
        var prep = Prepare(input);
        token.ThrowIfCancellationRequested();

        // The copper corrections — Clear, Copper, Fill — act in pixels, so they act before the scale: copper painted into
        // a line drawing is a line the impedance scale can be measured on (owner, 2026-10-28). A correction never
        // changes what is read, only what is written, so the line-drawing test is made on what was read.
        var read = prep.Layers;
        var fixOutcomes = new ImageCorrectionOutcome?[input.Corrections.Count];
        var edited = EditCopper(read, input.Corrections, tech, fixOutcomes, prep.Clusters, input.Options);
        prep = prep with
        {
            Layers = edited,
            Painted = edited.Where(l => !read.Any(r => ReferenceEquals(r, l))).Select(l => l.Name).ToHashSet(StringComparer.Ordinal),
        };

        var copperNames = ImageLayerMap.CopperLayers(tech).Select(k => ImageLayerMap.NameOf(tech, k)).ToHashSet(StringComparer.Ordinal);
        bool anyCopper = prep.Layers.Any(l => copperNames.Contains(l.Name) && l.Regions.Count > 0);

        control?.BeginStage("Finding the scale");
        var candidates = ImageScale.Candidates(input, prep, out string? scaleRefusal);
        var chosen = scaleRefusal is null ? ImageScale.Choose(candidates, input.TargetIsPlacedLayout) : null;
        token.ThrowIfCancellationRequested();

        var silk = prep.Layers.FirstOrDefault(l => l.Role == ImageLayerRole.Silkscreen);
        var result = new ImageTraceResult
        {
            Report = report, Clusters = prep.Clusters, LayerMap = prep.Map, ScaleCandidates = candidates, Scale = chosen,
            Drills = prep.Drills,
            Kind = input.Kind, Technology = tech, SilkscreenStrokeWidthPx = silk?.StrokeWidth ?? 0, SilkscreenLayer = silk?.Key,
            Traced = input.Scope ?? new PixelRect(0, 0, input.Source.Raster.Width, input.Source.Raster.Height),
            Layers = [.. prep.Layers.Select(l => new ImageTracedLayer(l.Name, l.Key, l.Role, l.Regions, []))],
        };
        ReportColours(prep, report);
        bool lineArt = IsLineArt(read.Where(l => copperNames.Contains(l.Name)));
        if (lineArt) report.Add(RecognitionFindingClass.ImageLineArt, 1, LineArtNote);
        result = result with { LineArt = lineArt };
        if (input.Photo is { } photoRead) BoardPhoto.Report(photoRead, report);
        if (scaleRefusal is not null) return result with { Refusal = scaleRefusal };
        // With nothing traced, the picture under the result is still a layout worth having — one to trace by hand.
        bool pictureAlone = input.Options.KeepUnderlay && !(input.TargetIsPlacedLayout && input.Source.Placement is LayoutBitmapPlacement);
        if (!anyCopper && !pictureAlone) return result with { Refusal = NoCopperRefusal };
        if (!anyCopper)
            report.Add(RecognitionFindingClass.ImageShapesTraced, 0,
                "No copper was traced at the mapped colours, so the layout holds the source picture alone, to trace by hand.");

        report.Add(RecognitionFindingClass.ImageStackupNotRead, 1,
            $"The stackup is {tech.Name}'s: a picture says nothing about the stackup, and none was read from it.");
        if (chosen is null)
        {
            ReportPixels(prep, report);
            return result;
        }

        control?.BeginStage("Tracing");
        var built = Build(input, prep, chosen, report, fixOutcomes);
        token.ThrowIfCancellationRequested();
        return result with
        {
            Frame = built.Frame, DbuPerMicron = built.DbuPerMicron, Layers = built.Layers, Shapes = built.Shapes,
            Underlay = built.Underlay, SilkscreenStrokes = built.Strokes, ResolutionLine = built.Resolution,
            Drills = built.Drills, Holes = built.Holes, Corrections = built.Outcomes,
            TechnologyCopy = copy is not null && built.NonPlatedAdded ? copy : null,
            Technology = copy is not null && built.NonPlatedAdded ? copy : given,
        };
    }

    // ── stage 1: pixels ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One layer in pixel space.</summary>
    internal sealed record PixelLayer(string Name, LayerKey? Key, ImageLayerRole Role, List<ContourRegion> Regions, int Specks,
                                      int Snapped, List<PathD> Strokes, double StrokeWidth);

    /// <summary>Everything read from the picture before the scale is known.</summary>
    internal sealed record Prepared(ColourClusterSet Clusters, ImageLayerMap Map, List<PixelLayer> Layers,
                                    Dictionary<string, CoverageMap> Coverage, List<PixelPad> Pads, List<CircleFit> Drills,
                                    double NarrowestPx, bool Photograph = false)
    {
        /// <summary>The layers a copper correction changed — measured, by the impedance scale, as they will be written.</summary>
        public IReadOnlySet<string> Painted { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The clusters traced onto each layer.</summary>
        public IReadOnlyDictionary<string, List<int>> ClustersByLayer { get; init; } = new Dictionary<string, List<int>>();

        /// <summary><paramref name="layer"/>'s coverage over the WHOLE picture, the scope ignored — what a scale is measured
        /// on, since a scale is the picture's and not the region's: a line picked for its impedance and then left outside
        /// a region dragged later still says how big a pixel is, and one crossing the region's edge is not narrowed by it.
        /// <see cref="Coverage"/> itself when there is no scope.</summary>
        public CoverageMap? WholeCoverage(string layer, PixelRect? scope)
        {
            if (scope is null) return Coverage.GetValueOrDefault(layer);
            if (!ClustersByLayer.TryGetValue(layer, out var ks)) return null;
            var v = new float[Clusters.Width * Clusters.Height];
            foreach (int k in ks)
            {
                var c = Clusters.Coverage(k).Values;
                for (int i = 0; i < v.Length; i++) v[i] = Math.Min(1f, v[i] + c[i]);
            }
            return new CoverageMap(Clusters.Width, Clusters.Height, v);
        }
    }

    internal static Prepared Prepare(ImageTraceInput input)
    {
        var tech = input.Technology!;
        var o = input.Options;
        // A photograph (not a board photo read square on its corners, which classes its own pixels) is clustered with
        // its light taken out, so a bare laminate under a warm lamp reads grey and the copper reads copper.
        var raster = input.Source.Raster;
        bool photograph = input.Photo is null && ImageKind.IsPhotograph(raster);
        var clusters = input.Photo?.Clusters
                       ?? ColourClusters.Find(photograph ? WhiteBalance.Correct(raster) : raster, Math.Max(1, o.MaxColours),
                                              new ColourClusterOptions { MergeDeltaE = o.MergeDeltaE });
        var map = input.LayerMap is { Edited: true } edited ? edited.Rebind(clusters)
                : input.Photo is { } photo ? photo.DefaultMap(tech)
                : ImageLayerMap.Auto(clusters, tech, photograph);
        var contour = new ContourOptions
        {
            Simplify = o.SimplifyPx, Snap = o.Snap, SnapAngleDeg = o.SnapAngleDeg, Snap45 = o.Snap45, MinArea = 0,
        };

        CoverageMap Sum(IEnumerable<int> ks)
        {
            int w = clusters.Width, h = clusters.Height;
            var v = new float[w * h];
            foreach (int k in ks)
            {
                var c = clusters.Coverage(k).Values;
                for (int i = 0; i < v.Length; i++) v[i] = Math.Min(1f, v[i] + c[i]);
            }
            if (input.Scope is { } s) ClipToScope(v, w, h, s);
            return new CoverageMap(w, h, v);
        }

        // Drills first: their circles are the holes the copper is not cut by.
        var drills = new List<CircleFit>();
        var drillRows = map.Rows.Where(r => r.Role == ImageLayerRole.Drill).Select(r => r.Cluster).ToList();
        if (drillRows.Count > 0)
            foreach (var region in Despeckle(Contours.Trace(Sum(drillRows), contour with { Snap = false }), o.MinFeaturePx2, out _))
                if (CircleOf(region) is { } fit) drills.Add(fit);

        var layers = new List<PixelLayer>();
        var coverage = new Dictionary<string, CoverageMap>(StringComparer.Ordinal);
        var byLayer = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var r in map.Rows.Where(r => r.Role == ImageLayerRole.Layer))
            foreach (string name in r.Layers)
                (byLayer.TryGetValue(name, out var l) ? l : byLayer[name] = []).Add(r.Cluster);

        // Copper in the technology's own order, top first; other layers after, by name.
        var copperOrder = ImageLayerMap.CopperLayers(tech).Select(k => ImageLayerMap.NameOf(tech, k)).ToList();
        var names = byLayer.Keys.OrderBy(n => copperOrder.IndexOf(n) is var i and >= 0 ? i : int.MaxValue).ThenBy(n => n, StringComparer.Ordinal);
        foreach (string name in names)
        {
            var cov = Sum(byLayer[name]);
            coverage[name] = cov;
            var regions = Deblip(Despeckle(Contours.Trace(cov, contour), o.MinFeaturePx2, out int specks));
            if (o.EdgeStrokePx > 0) regions = Inset(regions, o.EdgeStrokePx / 2, clusters.Width, clusters.Height);
            layers.Add(new PixelLayer(name, ImageLayerMap.KeyOf(tech, name), ImageLayerRole.Layer, regions, specks,
                                      o.Snap ? CountSnapped(regions, o.Snap45) : 0, [], 0));
        }

        foreach (var role in new[] { ImageLayerRole.Silkscreen, ImageLayerRole.BoardOutline })
        {
            var rows = map.Rows.Where(r => r.Role == role).ToList();
            if (rows.Count == 0) continue;
            var cov = Sum(rows.Select(r => r.Cluster));
            string name = rows.SelectMany(r => r.Layers).FirstOrDefault() ?? (role == ImageLayerRole.Silkscreen ? "silkscreen" : "outline");
            var regions = role == ImageLayerRole.Silkscreen ? Despeckle(Contours.Trace(cov, contour), o.MinFeaturePx2, out int sp) : [];
            var graph = SkeletonGraph.Build(cov.Threshold());
            layers.Add(new PixelLayer(name, rows.SelectMany(r => r.Layers).Select(n => ImageLayerMap.KeyOf(tech, n)).FirstOrDefault(),
                                      role, regions, 0, 0, [.. graph.Edges.Select(e => e.Polyline)], graph.StrokeWidth));
        }

        // Pads for the scale search: the rectangles on each copper layer.
        var pads = new List<PixelPad>();
        for (int li = 0; li < layers.Count; li++)
        {
            if (layers[li].Role != ImageLayerRole.Layer) continue;
            foreach (var region in layers[li].Regions)
                if (region.Holes.Count == 0 && RectOf(region.Outer) is { } r) pads.Add(new PixelPad(r.left, r.top, r.right, r.bottom, li + 1));
        }

        return new Prepared(clusters, map, layers, coverage, pads, drills, Narrowest(layers, copperOrder), photograph)
        {
            ClustersByLayer = byLayer.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>The copper read is under this share of the box it spans, and drawn in lines no wider on average than
    /// <see cref="LineArtWidthShare"/> of that box's narrower side (or <see cref="LineArtWidthPx"/>): a line drawing.</summary>
    internal const double LineArtFilledShare = 0.2, LineArtWidthShare = 0.01, LineArtWidthPx = 3;

    /// <summary>Whether the copper read is a line drawing's (brief-dfr30-1 R-dfr30-1-7): thin strokes — the mean width of
    /// everything read, twice its area over its edge length — covering a small share of the box they span. Measured on
    /// the strokes alone, not on whether they close: a picture's anti-aliasing breaks a drawn outline into pieces, and an
    /// outline broken into pieces encloses nothing.</summary>
    internal static bool IsLineArt(IEnumerable<PixelLayer> copper)
    {
        double ink = 0, edge = 0, left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in copper.SelectMany(l => l.Regions))
        {
            ink += Math.Abs(r.Area);
            edge += Perimeter(r.Outer) + r.Holes.Sum(Perimeter);
            var b = Clipper.GetBounds(r.Outer);
            (left, top, right, bottom) = (Math.Min(left, b.left), Math.Min(top, b.top), Math.Max(right, b.right), Math.Max(bottom, b.bottom));
        }
        if (ink <= 0 || edge <= 0) return false;
        double box = (right - left) * (bottom - top), width = 2 * ink / edge;
        return ink < LineArtFilledShare * box && width <= Math.Max(LineArtWidthPx, LineArtWidthShare * Math.Min(right - left, bottom - top));
    }

    private static void ClipToScope(float[] v, int w, int h, PixelRect s)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x + 0.5 < s.Left || x + 0.5 > s.Right || y + 0.5 < s.Top || y + 0.5 > s.Bottom) v[y * w + x] = 0;
    }

    /// <summary>Regions and holes under the minimum feature removed, and counted.</summary>
    private static List<ContourRegion> Despeckle(List<ContourRegion> regions, double minArea, out int specks)
    {
        specks = 0;
        var kept = new List<ContourRegion>(regions.Count);
        foreach (var r in regions)
        {
            if (Math.Abs(Clipper.Area(r.Outer)) < minArea) { specks++; continue; }
            var holes = r.Holes.Where(hp => Math.Abs(Clipper.Area(hp)) >= minArea).ToList();
            specks += r.Holes.Count - holes.Count;
            kept.Add(holes.Count == r.Holes.Count ? r : r with { Holes = holes });
        }
        return kept;
    }

    /// <summary>An excursion off a snapped edge no further than this, and no longer than <see cref="BlipLengthPx"/>, is a
    /// blip.</summary>
    internal const double BlipPx = 0.5, BlipLengthPx = 4.0;

    /// <summary>
    /// Removes the blips a colour junction leaves on a straight edge. Where three or four colours meet in one pixel — a
    /// bottom layer's corner on a top layer's edge — the pixel is no two-colour mix, and the edge picks up an excursion
    /// of a fraction of a pixel that splits one snapped edge into two, each snapped about its own centroid a hair apart.
    /// Two edges on one snap direction whose lines are within <see cref="BlipPx"/>, joined by a few short edges that stay
    /// within it, are one edge: both are moved onto their length-weighted common line (their outer ends sliding along
    /// the edges before and after, so those keep their directions) and the excursion is dropped.
    /// </summary>
    /// <summary>Every region pulled in by <paramref name="d"/> pixels, square corners kept square: the outline a
    /// picture draws on each edge taken back off (<see cref="ImageTraceOptions.EdgeStrokePx"/>) — except where a region
    /// runs off the picture, whose edge cut the shape and drew no outline there: a line running off it keeps its
    /// length. A region narrower than twice that is gone; one with a waist that narrow comes back in two.</summary>
    internal static List<ContourRegion> Inset(List<ContourRegion> regions, double d, int width, int height)
    {
        var all = new PathsD();
        foreach (var r in regions) all.AddRange(r.Paths);
        var inset = Clipper.InflatePaths(all, -d, JoinType.Miter, EndType.Polygon, 4, 4);
        var inner = new PathD { new(d, d), new(width - d, d), new(width - d, height - d), new(d, height - d) };
        var frame = new PathD { new(-1, -1), new(width + 1, -1), new(width + 1, height + 1), new(-1, height + 1) };
        var band = Clipper.Difference(new PathsD { frame }, new PathsD { inner }, FillRule.NonZero);
        var border = Clipper.Intersect(all, band, FillRule.NonZero);
        var tree = new PolyTreeD();
        var c = new ClipperD(4);
        c.AddSubject(inset);
        // What lay within d of the edge comes back only where it meets the inset shape: a piece the inset lost
        // altogether is not restored by the band alone.
        c.AddSubject(Clipper.Intersect(border, Clipper.InflatePaths(inset, 2 * d + 1, JoinType.Miter, EndType.Polygon, 4, 4), FillRule.NonZero));
        c.Execute(ClipType.Union, FillRule.NonZero, tree);
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

    private static List<ContourRegion> Deblip(List<ContourRegion> regions) =>
        [.. regions.Select(r => r with { Outer = Deblip(r.Outer), Holes = [.. r.Holes.Select(Deblip).Where(h => h.Count >= 3)] })];

    internal static PathD Deblip(PathD ring)
    {
        var p = new PathD(ring);
        for (bool changed = true; changed;)
        {
            changed = false;
            int n = p.Count;
            for (int i = 0; i < n && !changed; i++)
                for (int g = 2; g <= 5 && g + 3 <= n && !changed; g++)
                {
                    var a0 = p[i];
                    var a1 = p[(i + 1) % n];
                    var b0 = p[(i + g) % n];
                    var b1 = p[(i + g + 1) % n];
                    if (SnapDirection(a0, a1) is not { } d || SnapDirection(b0, b1) is not { } e || d != e) continue;
                    double ca = Cross(d, a0), cb = Cross(d, b0);
                    if (Math.Abs(ca - cb) > BlipPx) continue;
                    double run = 0;
                    bool within = true;
                    for (int k = 1; k < g && within; k++)
                    {
                        var v = p[(i + k + 1) % n];
                        within = Math.Min(Math.Abs(Cross(d, v) - ca), Math.Abs(Cross(d, v) - cb)) <= BlipPx;
                    }
                    for (int k = 1; k < g; k++) run += Dist(p[(i + k) % n], p[(i + k + 1) % n]);
                    if (!within || run > BlipLengthPx) continue;

                    double la = Dist(a0, a1), lb = Dist(b0, b1);
                    double c = (ca * la + cb * lb) / (la + lb);
                    var before = p[(i - 1 + n) % n];
                    var after = p[(i + g + 2) % n];
                    if (OnLine(d, c, before, a0) is not { } na || OnLine(d, c, after, b1) is not { } nb) continue;

                    var drop = new HashSet<int>();
                    for (int k = 1; k <= g; k++) drop.Add((i + k) % n);
                    var q = new PathD(n - g);
                    for (int k = 0; k < n; k++)
                    {
                        if (drop.Contains(k)) continue;
                        q.Add(k == i ? na : k == (i + g + 1) % n ? nb : p[k]);
                    }
                    p = q;
                    changed = true;
                }
        }
        return p;
    }

    /// <summary>The unit direction of an edge lying exactly on 0°, 90° or ±45° (up to sign), or null.</summary>
    private static (double X, double Y)? SnapDirection(PointD a, PointD b)
    {
        double dx = b.x - a.x, dy = b.y - a.y, len = Math.Max(Math.Abs(dx), Math.Abs(dy)), tol = 1e-9 * Math.Max(1, len);
        if (len <= 0) return null;
        if (Math.Abs(dy) <= tol) return (1, 0);
        if (Math.Abs(dx) <= tol) return (0, 1);
        if (Math.Abs(Math.Abs(dx) - Math.Abs(dy)) <= tol) return Math.Sign(dx) == Math.Sign(dy) ? (Math.Sqrt(0.5), Math.Sqrt(0.5)) : (Math.Sqrt(0.5), -Math.Sqrt(0.5));
        return null;
    }

    /// <summary>The offset of the line of direction <paramref name="d"/> through <paramref name="p"/>.</summary>
    private static double Cross((double X, double Y) d, PointD p) => d.X * p.y - d.Y * p.x;

    private static double Dist(PointD a, PointD b) => Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));

    /// <summary>Where the line through <paramref name="from"/> and <paramref name="to"/> meets the line of direction
    /// <paramref name="d"/> and offset <paramref name="c"/>; null when they are near parallel.</summary>
    private static PointD? OnLine((double X, double Y) d, double c, PointD from, PointD to)
    {
        double ex = to.x - from.x, ey = to.y - from.y, den = d.X * ey - d.Y * ex;
        if (Math.Abs(den) < 1e-9 * Math.Max(1, Math.Sqrt(ex * ex + ey * ey))) return null;
        double t = (c - Cross(d, from)) / den;
        return new PointD(from.x + t * ex, from.y + t * ey);
    }

    /// <summary>Edges lying exactly on a snap angle — what snapping produced.</summary>
    private static int CountSnapped(List<ContourRegion> regions, bool snap45)
    {
        int n = 0;
        foreach (var r in regions)
            foreach (var p in r.Paths)
                for (int i = 0; i < p.Count; i++)
                {
                    var a = p[i];
                    var b = p[(i + 1) % p.Count];
                    double dx = Math.Abs(b.x - a.x), dy = Math.Abs(b.y - a.y), len = Math.Max(dx, dy);
                    if (len <= 0) continue;
                    if (dx == 0 || dy == 0 || (snap45 && Math.Abs(dx - dy) <= 1e-7 * len)) n++;
                }
        return n;
    }

    /// <summary>A region that is a circle: a ring of at least eight vertices on one circle, enclosing its area.</summary>
    internal static CircleFit? CircleOf(ContourRegion region)
    {
        var outer = region.Outer;
        if (outer.Count < 8) return null;
        var fit = Fit.Circle(outer);
        if (!(fit.R > 1) || fit.Residual > Math.Max(0.35, 0.04 * fit.R)) return null;
        double area = Math.Abs(Clipper.Area(outer)), disc = Math.PI * fit.R * fit.R;
        return Math.Abs(area / disc - 1) <= 0.06 ? fit : null;
    }

    /// <summary>
    /// A via's drill or pad as a designer states one (owner, designer round 29): a picture measures it to a fraction of a
    /// pixel, and 0.4876 mm is noise in its last three digits. Rounded in the technology's own unit — to 1 mil (½ mil below
    /// 4 mil) on an inch board, 0.05 mm (0.01 mm below 0.2 mm) on a metric one, 1 µm on a chip — and never to nothing.
    /// </summary>
    internal static long Tidy(long dbu, LayoutUnit unit, int dbuPerMicron)
    {
        double um = dbu / (double)dbuPerMicron;
        double step = unit switch
        {
            LayoutUnit.Mil or LayoutUnit.Inch => um >= 4 * 25.4 ? 25.4 : 12.7,
            LayoutUnit.Mm => um >= 200 ? 50 : 10,
            _ => 1,
        };
        return (long)Math.Round(Math.Max(step, Math.Round(um / step) * step) * dbuPerMicron);
    }

    /// <summary>The widest round hole in copper read as a via when the picture has no drill colour: wider is a mounting
    /// hole's clearance and stays an opening.</summary>
    internal const double MaxHoleViaMetres = 1.0e-3;

    /// <summary>Wider than this a round hole is no hole of the board's — a mounting hole is a few millimetres.</summary>
    internal const double MaxHoleMetres = 10e-3;

    /// <summary>Narrower than this a round hole is a speck, not a drill.</summary>
    internal const double MinHoleViaMetres = 0.1e-3;

    /// <summary>A hole drawn round — a circle, or a drawing's rounded square — as the circle of its area, centred on its
    /// bounds; null for anything elongated or ragged. Looser than <see cref="CircleOf"/>: a hole is drawn small, so a
    /// pixel of edge noise is a large share of its radius.</summary>
    internal static CircleFit? RoundHole(PathD hole)
    {
        if (hole.Count < 4) return null;
        var b = Clipper.GetBounds(hole);
        double w = b.Width, h = b.Height;
        if (!(w > 2 && h > 2) || Math.Max(w, h) / Math.Min(w, h) > 1.3) return null;
        double area = Math.Abs(Clipper.Area(hole));
        if (area / (w * h) < 0.6) return null;   // a disc fills 0.785 of its box, a rounded square more
        return new CircleFit((b.left + b.right) / 2, (b.top + b.bottom) / 2, Math.Sqrt(area / Math.PI), 0);
    }

    /// <summary>The bounds of a ring that is an axis-aligned rectangle, or null.</summary>
    internal static RectD? RectOf(PathD ring)
    {
        if (ring.Count != 4) return null;
        for (int i = 0; i < 4; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % 4];
            if (Math.Abs(a.x - b.x) > 1e-6 && Math.Abs(a.y - b.y) > 1e-6) return null;
        }
        return Clipper.GetBounds(ring);
    }

    /// <summary>The narrowest traced copper line, pixels: the thinnest skeleton edge at least three widths long, else
    /// the narrower side of the smallest region.</summary>
    private static double Narrowest(List<PixelLayer> layers, List<string> copper)
    {
        double best = double.MaxValue;
        foreach (var l in layers.Where(l => l.Role == ImageLayerRole.Layer && copper.Contains(l.Name)))
            foreach (var r in l.Regions)
            {
                var b = Clipper.GetBounds(r.Outer);
                best = Math.Min(best, Math.Min(b.Width, b.Height));
                // A strip of length L and width W: L·W = area, L + W = half the perimeter. A shape no strip fits (a
                // disc) has no real root and is not a line.
                double area = Math.Abs(r.Area), half = Perimeter(r.Outer) / 2, disc = half * half - 4 * area;
                if (disc < 0) continue;
                double w = (half - Math.Sqrt(disc)) / 2;
                if (w > 0 && half - w >= 3 * w) best = Math.Min(best, w);
            }
        return best == double.MaxValue ? 0 : best;
    }

    private static double Perimeter(PathD p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            s += Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
        }
        return s;
    }

    // ── stage 2: DBU ────────────────────────────────────────────────────────────────────────────────

    /// <param name="Drills">Every via written, pixels — drawn on the overlay as drills.</param>
    /// <param name="NonPlatedAdded">The technology's copy gained a non-plated hole layer (D2).</param>
    private sealed record Built(PixelFrame Frame, int DbuPerMicron, List<ImageTracedLayer> Layers, List<LayoutShape> Shapes,
                                BitmapShape? Underlay, List<long[]> Strokes, string Resolution, List<CircleFit> Drills,
                                List<ImageTracedHole> Holes, List<ImageCorrectionOutcome> Outcomes, bool NonPlatedAdded);

    private static Built Build(ImageTraceInput input, Prepared prep, ImageScaleCandidate scale, RecognitionReport report,
                               ImageCorrectionOutcome?[] outcomes)
    {
        var tech = input.Technology!;
        var raster = input.Source.Raster;
        int w = raster.Width, h = raster.Height;
        var placement = input.Source.Placement as LayoutBitmapPlacement;

        // ── the frame: on the placed bitmap's rect, or with the picture's top-left at the origin ──────────
        PixelFrame frame;
        int dbuPerMicron = input.DbuPerMicron;
        if (scale is { Kind: ImageScaleKind.Placement, DbuPerPixel: var (sx, sy) } && placement is not null)
        {
            dbuPerMicron = scale.DbuPerMicron ?? dbuPerMicron;
            frame = new PixelFrame(sx, sy, placement.X, placement.Y + placement.H, true);
            if (Math.Abs(sx / sy - 1) > ImageScale.AspectTolerance)
                report.Add(RecognitionFindingClass.ImageBitmapStretched, 1,
                    $"The placed bitmap is stretched ({(sx / sy - 1) * 100:+0.#;-0.#} % across against down); the trace follows its rect.");
        }
        else
        {
            if (input.TargetIsPlacedLayout && placement is not null)
                dbuPerMicron = LayoutPersistence.TryReadUnits(placement.Document)?.DbuPerMicron ?? dbuPerMicron;
            double s = scale.MetresPerPixel * 1e6 * dbuPerMicron;
            double ox = input.TargetIsPlacedLayout && placement is not null ? placement.X : 0;
            double oy = input.TargetIsPlacedLayout && placement is not null ? placement.Y + placement.H : 0;
            frame = new PixelFrame(s, s, ox, oy, true);
        }
        double scaleDbu = 0.5 * (frame.ScaleX + frame.ScaleY);
        double techToLayout = dbuPerMicron / (double)LayoutUnits.DefaultDbuPerMicron;

        var copperOrder = ImageLayerMap.CopperLayers(tech);
        var shapes = new List<LayoutShape>();
        var traced = new List<ImageTracedLayer>();
        var consumed = new HashSet<ContourRegion>();

        // ── the copper corrections (R-dfr29-1-1): applied to what was read, never to the reading ─────────
        var fixes = input.Corrections;
        var layers = prep.Layers;   // the copper corrections applied already, before the scale
        var copperLayers = layers.Where(l => l.Role == ImageLayerRole.Layer && l.Key is { } k && copperOrder.Contains(k))
                                 .OrderBy(l => copperOrder.IndexOf(l.Key!.Value)).ToList();
        long Px(double px) => (long)Math.Round(px * scaleDbu);
        long Metres(double m) => (long)Math.Round(m * 1e6 * dbuPerMicron);
        long fallbackPad = (long)Math.Round(tech.DefaultViaPadDbu * techToLayout);
        var holes = new List<ImageTracedHole>();
        var nonPlated = new List<CircleFit>();
        var seenHoles = new List<CircleFit>();
        bool Seen(CircleFit c) => seenHoles.Any(p => Math.Abs(p.Cx - c.Cx) <= p.R && Math.Abs(p.Cy - c.Cy) <= p.R);
        void Open(CircleFit c, bool plated)
        {
            if (Seen(c)) return;
            seenHoles.Add(c);
            if (plated) holes.Add(new ImageTracedHole(c, ImageHoleKind.Opening));
            else nonPlated.Add(c);
        }

        // ── drills → vias ──────────────────────────────────────────────────────────────────────────
        var barrel = ImageLayerMap.ViaBarrelLayer(tech);
        var vias = new List<LayoutShape>();
        var viaDrills = new List<CircleFit>();
        foreach (var d in prep.Drills)
        {
            // A drill circle a correction says is an opening, or not plated, is no via: its hole in the copper stays.
            int fix = LastOn(fixes, outcomes, d.Cx, d.Cy, d.R);
            var said = fix >= 0 ? fixes[fix].Kind : (ImageCorrectionKind?)null;
            if (said is ImageCorrectionKind.NotVia or ImageCorrectionKind.Hole)
            {
                outcomes[fix] = new(fixes[fix], null);
                Open(d, plated: said == ImageCorrectionKind.NotVia);
                continue;
            }
            if (fix >= 0) outcomes[fix] = new(fixes[fix], barrel is null ? $"{tech.Name} has no via layer to place it on." : null);
            viaDrills.Add(d);
            if (barrel is null) continue;
            LayerKey? landing = null;
            long pad = 0;
            foreach (var l in copperLayers)
            {
                var holder = l.Regions.FirstOrDefault(r => Clipper.PointInPolygon(new PointD(d.Cx, d.Cy), r.Outer) != PointInPolygonResult.IsOutside);
                if (holder is null) continue;
                landing ??= l.Key;
                if (pad == 0 && holder.Holes.Count <= 1 && CircleOf(holder with { Holes = [] }) is { } ring
                    && Math.Abs(ring.Cx - d.Cx) <= 0.5 * d.R && Math.Abs(ring.Cy - d.Cy) <= 0.5 * d.R && ring.R > d.R)
                {
                    pad = (long)Math.Round(2 * ring.R * scaleDbu);
                    consumed.Add(holder);
                }
            }
            long drill = Tidy(fix >= 0 && fixes[fix].DrillMetres is { } stated ? Metres(stated) : (long)Math.Round(2 * d.R * scaleDbu),
                              tech.DefaultDisplayUnit, dbuPerMicron);
            if (pad > drill) pad = Tidy(pad, tech.DefaultDisplayUnit, dbuPerMicron);
            if (pad <= drill) pad = fallbackPad > drill ? fallbackPad : 2 * drill;
            var (cx, cy) = frame.ToTarget(d.Cx, d.Cy);
            vias.Add(new ViaShape
            {
                X = (long)Math.Round(cx), Y = (long)Math.Round(cy), PadSize = pad, DrillSize = drill,
                Layer = barrel.Value, LandingLayer = landing ?? (copperOrder.Count > 0 ? copperOrder[0] : null),
            });
        }
        // ── round holes in the copper → vias, when the picture draws no drill colour ─────────────────
        // A datasheet's test board draws its holes in the background's own colour, so no drill row exists and every via
        // was a hole cut in the pour (designer round 29). A small round hole inside a copper region is a via: the
        // copper is written whole under it and the via opens it. A larger one (a mounting hole's clearance) stays an
        // opening — whether it is plated is not in the picture. A correction on a hole says what it is, drill colour or
        // not (R-dfr29-1-1).
        //
        // A ring around a round hole is not copper to make whole (brief-dfr30-1 R-dfr30-1-8): a datasheet draws a hole
        // with a dark rim, and a ring read on its own layer is either that rim — sitting in the pour's hole on another
        // layer, so the hole is the one drawn, its drill the rim's centre line — or the hole's pad, which is the via's
        // as a drill circle's ring is. Made whole, either one was a solid copper disc on a layer no one drew one on.
        var holeVias = new HashSet<PathD>(ReferenceEqualityComparer.Instance);
        var holeDrills = new List<CircleFit>();
        int openings = 0, autoPlaced = 0;
        bool auto = prep.Drills.Count == 0;
        {
            long maxDrill = (long)Math.Round(MaxHoleViaMetres * 1e6 * dbuPerMicron), minDrill = (long)Math.Round(MinHoleViaMetres * 1e6 * dbuPerMicron);
            var placed = new List<(double X, double Y, double R)>();
            var (rings, rimmed) = Rings(copperLayers);
            foreach (var l in copperLayers)
                foreach (var region in l.Regions)
                    foreach (var hole in region.Holes)
                    {
                        if (RoundHole(hole) is not { } c) continue;
                        var ring = rings.GetValueOrDefault(region);
                        if (ring is { Rim: true }) continue;   // drawn as its pour's hole, settled there
                        if (rimmed.TryGetValue(hole, out var drawn)) c = drawn;
                        if (prep.Drills.Any(d => Within(hole, d))) continue;   // a drill circle's own hole, settled above
                        int fix = LastOn(fixes, outcomes, c.Cx, c.Cy, c.R);
                        var said = fix >= 0 ? fixes[fix].Kind : (ImageCorrectionKind?)null;
                        if (said is ImageCorrectionKind.NotVia or ImageCorrectionKind.Hole)
                        {
                            outcomes[fix] = new(fixes[fix], null);
                            Open(c, plated: said == ImageCorrectionKind.NotVia);
                            continue;
                        }
                        long drill = (long)Math.Round(2 * c.R * scaleDbu);
                        if (said == ImageCorrectionKind.Via)
                        {
                            outcomes[fix] = new(fixes[fix], barrel is null ? $"{tech.Name} has no via layer to place it on." : null);
                            if (fixes[fix].DrillMetres is { } stated) drill = Metres(stated);
                        }
                        else if (!auto) { Open(c, plated: true); continue; }
                        // Wider than any drilled hole, a "round" hole is the board inside a drawn outline (a rounded
                        // square passes RoundHole): the copper's hole as read, never a hole of the board's.
                        else if (drill > MaxHoleMetres / MaxHoleViaMetres * maxDrill) continue;
                        // Compared as stated: a 1 mm hole read a few microns over is a 1 mm via, not a mounting hole.
                        else if (Tidy(drill, tech.DefaultDisplayUnit, dbuPerMicron) > maxDrill) { openings++; Open(c, plated: true); continue; }
                        else if (drill < minDrill) continue;
                        holeVias.Add(hole);
                        if (barrel is null || placed.Any(p => Math.Abs(p.X - c.Cx) <= p.R && Math.Abs(p.Y - c.Cy) <= p.R)) continue;
                        drill = Tidy(drill, tech.DefaultDisplayUnit, dbuPerMicron);
                        placed.Add((c.Cx, c.Cy, c.R));
                        holeDrills.Add(c);
                        if (said is null) autoPlaced++;
                        long pad = 0;
                        if (ring is { } pr)
                        {
                            consumed.Add(region);   // the via's pad, not a disc of copper as well
                            pad = Tidy((long)Math.Round(2 * pr.Outer.R * scaleDbu), tech.DefaultDisplayUnit, dbuPerMicron);
                        }
                        foreach (var rim in rings.Where(kv => kv.Value.Rim && kv.Value.In == hole)) consumed.Add(rim.Key);
                        if (pad <= drill) pad = fallbackPad > drill ? fallbackPad : 2 * drill;
                        var (cx, cy) = frame.ToTarget(c.Cx, c.Cy);
                        vias.Add(new ViaShape
                        {
                            X = (long)Math.Round(cx), Y = (long)Math.Round(cy), DrillSize = drill,
                            PadSize = pad, Layer = barrel.Value, LandingLayer = l.Key,
                        });
                    }
            if (barrel is null) holeVias.Clear();
            var said2 = new List<string>();
            if (auto && autoPlaced > 0)
                said2.Add($"{Plural(autoPlaced, "round hole in the copper became a via", "round holes in the copper became vias")} on " +
                          $"{ImageLayerMap.NameOf(tech, barrel!.Value)}, the copper whole under each — the picture draws no drill colour.");
            if (auto && openings > 0)
                said2.Add($"{Plural(openings, "round hole", "round holes")} wider than {MaxHoleViaMetres * 1e3:0.#} mm " +
                          $"{(openings == 1 ? "was kept as an opening" : "were kept as openings")} in the copper: whether a mounting hole is plated is not in the picture.");
            if (said2.Count > 0) report.Add(RecognitionFindingClass.ImageViasPlaced, autoPlaced, string.Join(" ", said2));
        }

        if (prep.Drills.Count > 0)
        {
            if (barrel is null)
                report.Add(RecognitionFindingClass.ImageDrillNoViaLayer, prep.Drills.Count,
                    $"{Plural(prep.Drills.Count, "drill hole was", "drill holes were")} read, but {tech.Name} has no via layer, " +
                    "so they were written on no layer.");
            else
                report.Add(RecognitionFindingClass.ImageViasPlaced, viaDrills.Count,
                    $"{Plural(viaDrills.Count, "drill circle became a via", "drill circles became vias")} on {ImageLayerMap.NameOf(tech, barrel.Value)}.");
        }

        // ── a via placed by hand where there is no hole: on the copper under the point ─────────────────
        for (int i = 0; i < fixes.Count; i++)
        {
            if (outcomes[i] is not null || !fixes[i].IsPoint) continue;
            var p = fixes[i].At;
            string at = $"{p.X.ToString("0.#", CultureInfo.InvariantCulture)}px,{p.Y.ToString("0.#", CultureInfo.InvariantCulture)}px";
            if (fixes[i].Kind != ImageCorrectionKind.Via) { outcomes[i] = new(fixes[i], $"There is no round hole at {at}."); continue; }
            if (barrel is null) { outcomes[i] = new(fixes[i], $"{tech.Name} has no via layer to place it on."); continue; }
            if (viaDrills.Concat(holeDrills).Any(d => (d.Cx - p.X) * (d.Cx - p.X) + (d.Cy - p.Y) * (d.Cy - p.Y) <= (d.R + HoleReachPx) * (d.R + HoleReachPx)))
            {
                outcomes[i] = new(fixes[i], null);   // a via already
                continue;
            }
            var pt = new PointD(p.X, p.Y);
            var landing = copperLayers.FirstOrDefault(l => l.Regions.Any(r => Clipper.PointInPolygon(pt, r.Outer) != PointInPolygonResult.IsOutside
                                                                               && r.Holes.All(hp => Clipper.PointInPolygon(pt, hp) != PointInPolygonResult.IsInside)));
            if (landing is null) { outcomes[i] = new(fixes[i], $"There is no copper at {at} for a via to land on."); continue; }
            long drill = fixes[i].DrillMetres is { } stated ? Tidy(Metres(stated), tech.DefaultDisplayUnit, dbuPerMicron)
                       : tech.DefaultViaDrillDbu > 0 ? (long)Math.Round(tech.DefaultViaDrillDbu * techToLayout)
                       : Tidy((long)Math.Round(3 * MinHoleViaMetres * 1e6 * dbuPerMicron), tech.DefaultDisplayUnit, dbuPerMicron);
            var (cx, cy) = frame.ToTarget(p.X, p.Y);
            vias.Add(new ViaShape
            {
                X = (long)Math.Round(cx), Y = (long)Math.Round(cy), DrillSize = drill,
                PadSize = fallbackPad > drill ? fallbackPad : 2 * drill, Layer = barrel.Value, LandingLayer = landing.Key,
            });
            holeDrills.Add(new CircleFit(p.X, p.Y, drill / scaleDbu / 2, 0));
            outcomes[i] = new(fixes[i], null);
        }

        // ── holes that are not plated (R-dfr29-1-5) ──────────────────────────────────────────────────
        var holeShapes = new List<LayoutShape>();
        bool nonPlatedAdded = false;
        if (nonPlated.Count > 0)
        {
            // Trace made a copy for a technology with no non-plated layer (CopyFor): the layer is added to that copy (D2).
            if (NonPlatedLayer(tech) is not { } npth)
            {
                npth = AddNonPlatedLayer(tech);
                nonPlatedAdded = true;
                report.Add(RecognitionFindingClass.ImageCorrections, 1,
                    $"{tech.Name} has no non-plated hole layer, so the copy of it written beside the cell gains one, " +
                    $"'{NonPlatedHoleLayerName}', spanning the board.");
            }
            foreach (var c in nonPlated)
            {
                long drill = Tidy(Px(2 * c.R), tech.DefaultDisplayUnit, dbuPerMicron);
                var (cx, cy) = frame.ToTarget(c.Cx, c.Cy);
                holeShapes.Add(new CircleShape { Layer = npth, Cx = (long)Math.Round(cx), Cy = (long)Math.Round(cy), R = drill / 2 });
                holes.Add(new ImageTracedHole(c, ImageHoleKind.NonPlated));
            }
        }
        holes.InsertRange(0, viaDrills.Concat(holeDrills).Select(c => new ImageTracedHole(c, ImageHoleKind.Via)));

        // ── each layer ─────────────────────────────────────────────────────────────────────────────
        int circles = 0;
        var strokes = new List<long[]>();
        foreach (var l in layers)
        {
            var these = new List<LayoutShape>();
            if (l.Key is { } key)
            {
                foreach (var region in l.Regions)
                {
                    if (consumed.Contains(region)) continue;
                    var kept = region.Holes.Where(hp => !holeVias.Contains(hp) && !viaDrills.Any(d => Within(hp, d))).ToList();
                    if (kept.Count == 0 && CircleOf(region) is { } c)
                    {
                        var (cx, cy) = frame.ToTarget(c.Cx, c.Cy);
                        these.Add(new CircleShape { Layer = key, Cx = (long)Math.Round(cx), Cy = (long)Math.Round(cy), R = (long)Math.Round(c.R * scaleDbu) });
                        circles++;
                        continue;
                    }
                    these.Add(ShapeOf(region.Outer, kept, frame, key));
                }
                if (l.Role == ImageLayerRole.BoardOutline || l.Role == ImageLayerRole.Silkscreen)
                {
                    long width = Math.Max(1, (long)Math.Round(Math.Max(1, l.StrokeWidth) * scaleDbu));
                    foreach (var stroke in l.Strokes)
                    {
                        var xy = Quantise(frame.ToTarget(stroke), closed: false);
                        if (l.Role == ImageLayerRole.Silkscreen) strokes.Add(xy);
                        else if (xy.Length >= 4) these.Add(new PathShape { Layer = key, Xy = xy, Width = width });
                    }
                }
            }
            else if (l.Role == ImageLayerRole.Silkscreen)
                foreach (var stroke in l.Strokes) strokes.Add(Quantise(frame.ToTarget(stroke), closed: false));

            traced.Add(new ImageTracedLayer(l.Name, l.Key, l.Role, l.Regions, these));
            shapes.AddRange(these);
        }

        // ── the board outline drawn by hand: one, the last drawn (R-dfr29-1-3) ─────────────────────────
        int outlineFix = -1;
        for (int i = 0; i < fixes.Count; i++)
        {
            if (fixes[i].Kind != ImageCorrectionKind.Outline) continue;
            if (outlineFix >= 0) outcomes[outlineFix] = new(fixes[outlineFix], "A later outline replaces it.");
            outlineFix = i;
        }
        if (outlineFix >= 0)
        {
            var f = fixes[outlineFix];
            if (f.Points.Count < 3 || Math.Abs(Clipper.Area(f.Path())) < 1)
                outcomes[outlineFix] = new(f, "An outline needs at least three corners around the board.");
            else if (ImageLayerMap.OutlineLayer(tech) is not { } outlineKey)
                outcomes[outlineFix] = new(f, $"{tech.Name} has no board outline layer to write it on.");
            else
            {
                // A closed line along the outline, as a board outline is drawn: read along its centre line (Em3d, Crop).
                var xy = Quantise(frame.ToTarget(f.Path()), closed: true);
                var edge = new PathShape { Layer = outlineKey, Xy = [.. xy, xy[0], xy[1]], Width = Math.Max(1, (long)Math.Round(scaleDbu)) };
                int t = traced.FindIndex(t => t.Role == ImageLayerRole.BoardOutline);
                if (t >= 0) traced[t] = traced[t] with { Shapes = [.. traced[t].Shapes, edge] };
                else traced.Add(new ImageTracedLayer(ImageLayerMap.NameOf(tech, outlineKey), outlineKey, ImageLayerRole.BoardOutline, [], [edge]));
                shapes.Add(edge);
                outcomes[outlineFix] = new(f, null);
            }
        }
        shapes.AddRange(vias);
        shapes.AddRange(holeShapes);

        // ── the underlay (D5) ──────────────────────────────────────────────────────────────────────
        BitmapShape? underlay = null;
        bool alreadyThere = input.TargetIsPlacedLayout && placement is not null;
        if (input.Options.KeepUnderlay && !alreadyThere)
        {
            var doc = ImageLayerMap.DocumentationLayer(tech);
            var layer = doc ?? traced.Where(t => t.Key is { } k && copperOrder.Contains(k))
                                     .OrderByDescending(t => t.PixelRegions.Sum(r => Math.Abs(r.Area))).Select(t => t.Key).FirstOrDefault()
                             ?? (copperOrder.Count > 0 ? copperOrder[0] : default);
            var (x0, yTop) = frame.ToTarget(0, 0);
            var (x1, yBottom) = frame.ToTarget(w, h);
            underlay = new BitmapShape
            {
                Layer = layer, ImagePathRef = input.Source.Path ?? "",
                X = (long)Math.Round(x0), Y = (long)Math.Round(yBottom),
                W = (long)Math.Round(x1 - x0), H = (long)Math.Round(yTop - yBottom),
                Opacity = UnderlayOpacity,   // an ordinary picture: selected, moved, scaled or deleted like any other
            };
            if (doc is null)
                report.Add(RecognitionFindingClass.ImageUnderlayOnCopper, 1,
                    $"{tech.Name} has no documentation layer, so the source picture lies on {ImageLayerMap.NameOf(tech, layer)}: " +
                    "hiding that layer hides the picture.");
        }

        // ── the report ─────────────────────────────────────────────────────────────────────────────
        report.Add(RecognitionFindingClass.ImageScaleChosen, 1, $"Scale {scale.Display} — {scale.Evidence}.");
        string resolution = ImageScale.ResolutionLine(scale.MetresPerPixel, prep.NarrowestPx);
        report.Add(RecognitionFindingClass.ImageResolution, 1, resolution + ".");
        var perLayer = traced.Where(t => t.Shapes.Count > 0).Select(t => $"{t.Name}: {t.Shapes.Count}").ToList();
        if (shapes.Count > 0)
            report.Add(RecognitionFindingClass.ImageShapesTraced, shapes.Count,
                $"{Plural(shapes.Count, "shape", "shapes")} traced — {string.Join("; ", perLayer)}" + (vias.Count > 0 ? $"; vias: {vias.Count}" : "")
                + (holeShapes.Count > 0 ? $"; non-plated holes: {holeShapes.Count}" : "") + ".");
        report.Add(RecognitionFindingClass.ImageCirclesFitted, circles, $"{Plural(circles, "region was fitted as a circle", "regions were fitted as circles")}.");
        ReportPixels(prep, report);
        if (strokes.Count > 0)
            report.Add(RecognitionFindingClass.ImageSilkscreenStrokes, strokes.Count,
                $"{Plural(strokes.Count, "silkscreen stroke was", "silkscreen strokes were")} kept as centre lines for reading text.");

        var outcomeList = outcomes.Select((o, i) => o ?? new ImageCorrectionOutcome(fixes[i], "It was not applied.")).ToList();
        ReportCorrections(outcomeList, report);

        return new Built(frame, dbuPerMicron, traced, shapes, underlay, strokes, resolution, [.. viaDrills, .. holeDrills], holes,
                         outcomeList, nonPlatedAdded);
    }

    /// <summary>A region that is one ring round one round hole.</summary>
    /// <param name="Outer">The ring's outer circle.</param>
    /// <param name="Rim">It fills <paramref name="In"/>, a round hole in another layer's copper: a hole's drawn rim.</param>
    private sealed record Ring(CircleFit Outer, bool Rim, PathD? In);

    /// <summary>Every ring round a round hole on <paramref name="copper"/> (R-dfr30-1-8), and each pour hole a rim fills,
    /// with the circle drawn: the rim's centre line, half-way between its outer and inner edges.</summary>
    private static (Dictionary<ContourRegion, Ring> Rings, Dictionary<PathD, CircleFit> Rimmed) Rings(List<PixelLayer> copper)
    {
        var rings = new Dictionary<ContourRegion, Ring>(ReferenceEqualityComparer.Instance);
        var rimmed = new Dictionary<PathD, CircleFit>(ReferenceEqualityComparer.Instance);
        var found = new List<(PixelLayer Layer, ContourRegion Region, CircleFit Inner, CircleFit Outer)>();
        foreach (var l in copper)
            foreach (var r in l.Regions)
                if (r.Holes.Count == 1 && RoundHole(r.Holes[0]) is { } inner && RoundHole(r.Outer) is { } outer
                    && outer.R > inner.R && Math.Abs(outer.Cx - inner.Cx) <= 0.5 * inner.R && Math.Abs(outer.Cy - inner.Cy) <= 0.5 * inner.R)
                    found.Add((l, r, inner, outer));
        foreach (var (layer, region, inner, outer) in found)
        {
            PathD? hole = null;
            foreach (var l in copper.Where(l => l != layer))
                foreach (var r in l.Regions)
                    foreach (var h in r.Holes)
                        if (hole is null && RoundHole(h) is { } c && Math.Abs(c.Cx - outer.Cx) <= 0.5 * inner.R
                            && Math.Abs(c.Cy - outer.Cy) <= 0.5 * inner.R && Math.Abs(c.R - outer.R) <= Math.Max(1.5, 0.15 * outer.R))
                        {
                            hole = h;
                            rimmed[h] = new CircleFit(c.Cx, c.Cy, 0.5 * (c.R + inner.R), 0);
                        }
            rings[region] = new Ring(outer, hole is not null, hole);
        }
        return (rings, rimmed);
    }

    private static bool Within(PathD hole, CircleFit d)
    {
        var b = Clipper.GetBounds(hole);
        double cx = (b.left + b.right) / 2, cy = (b.top + b.bottom) / 2;
        return (cx - d.Cx) * (cx - d.Cx) + (cy - d.Cy) * (cy - d.Cy) <= (d.R + 1) * (d.R + 1);
    }

    /// <summary>A region as a shape: an axis-aligned rectangle as a <see cref="RectShape"/>, anything else as a polygon
    /// with its holes.</summary>
    private static LayoutShape ShapeOf(PathD outer, List<PathD> holes, PixelFrame frame, LayerKey layer)
    {
        long[] xy = Quantise(frame.ToTarget(outer), closed: true);
        if (holes.Count == 0 && xy.Length == 8 && IsAxisRect(xy))
            return new RectShape
            {
                Layer = layer, X1 = Math.Min(xy[0], xy[4]), Y1 = Math.Min(xy[1], xy[5]), X2 = Math.Max(xy[0], xy[4]), Y2 = Math.Max(xy[1], xy[5]),
            };
        return new PolygonShape
        {
            Layer = layer, Xy = xy,
            Holes = holes.Count == 0 ? null : [.. holes.Select(hp => Quantise(frame.ToTarget(hp), closed: true))],
        };
    }

    private static bool IsAxisRect(long[] xy)
    {
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            if (xy[2 * i] != xy[2 * j] && xy[2 * i + 1] != xy[2 * j + 1]) return false;
        }
        return true;
    }

    private enum EdgeKind { Free, H, V, DPlus, DMinus }

    /// <summary>
    /// Rounds a path to DBU so every snapped edge stays snapped: each edge's invariant — y for a horizontal edge, x for a
    /// vertical one, x − y or x + y for a 45° one — is rounded ONCE, and each vertex is solved from the invariants of the
    /// two edges meeting at it. Rounding x and y per vertex would leave a 45° jog a DBU off 45°.
    /// </summary>
    internal static long[] Quantise(PathD p, bool closed)
    {
        int n = p.Count;
        var result = new long[2 * n];
        if (n == 0) return result;
        int edges = closed ? n : n - 1;
        var kind = new EdgeKind[Math.Max(0, edges)];
        var inv = new long[Math.Max(0, edges)];
        for (int e = 0; e < edges; e++)
        {
            var a = p[e];
            var b = p[(e + 1) % n];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Max(Math.Abs(dx), Math.Abs(dy)), tol = 1e-7 * Math.Max(len, 1);
            if (Math.Abs(dy) <= tol && Math.Abs(dx) > tol) { kind[e] = EdgeKind.H; inv[e] = (long)Math.Round((a.y + b.y) / 2); }
            else if (Math.Abs(dx) <= tol && Math.Abs(dy) > tol) { kind[e] = EdgeKind.V; inv[e] = (long)Math.Round((a.x + b.x) / 2); }
            else if (Math.Abs(Math.Abs(dx) - Math.Abs(dy)) <= tol && len > tol)
            {
                bool plus = Math.Sign(dx) == Math.Sign(dy);
                kind[e] = plus ? EdgeKind.DPlus : EdgeKind.DMinus;
                inv[e] = (long)Math.Round(plus ? ((a.x - a.y) + (b.x - b.y)) / 2 : ((a.x + a.y) + (b.x + b.y)) / 2);
            }
        }
        for (int i = 0; i < n; i++)
        {
            int eIn = closed ? (i - 1 + n) % n : i - 1, eOut = closed || i < n - 1 ? i : -1;
            long? x = null, y = null;
            void Apply(int e)
            {
                if (e < 0 || e >= edges) return;
                switch (kind[e])
                {
                    case EdgeKind.H: y ??= inv[e]; break;
                    case EdgeKind.V: x ??= inv[e]; break;
                }
            }
            Apply(eIn);
            Apply(eOut);
            void Diagonal(int e)
            {
                if (e < 0 || e >= edges || kind[e] is not (EdgeKind.DPlus or EdgeKind.DMinus)) return;
                bool plus = kind[e] == EdgeKind.DPlus;
                if (x is { } xv && y is null) y = plus ? xv - inv[e] : inv[e] - xv;
                else if (y is { } yv && x is null) x = plus ? inv[e] + yv : inv[e] - yv;
            }
            Diagonal(eIn);
            Diagonal(eOut);
            if (x is null && y is null && eIn >= 0 && eOut >= 0 && eIn < edges && eOut < edges
                && kind[eIn] is EdgeKind.DPlus or EdgeKind.DMinus && kind[eOut] is EdgeKind.DPlus or EdgeKind.DMinus && kind[eIn] != kind[eOut])
            {
                long cp = kind[eIn] == EdgeKind.DPlus ? inv[eIn] : inv[eOut], cm = kind[eIn] == EdgeKind.DMinus ? inv[eIn] : inv[eOut];
                x = (long)Math.Round((cp + cm) / 2.0);
                y = cm - x;
            }
            if (x is null && y is null)
            {
                x = (long)Math.Round(p[i].x);
                Diagonal(eIn);
                Diagonal(eOut);
            }
            result[2 * i] = x ?? (long)Math.Round(p[i].x);
            result[2 * i + 1] = y ?? (long)Math.Round(p[i].y);
        }
        return result;
    }

    // ── the report ──────────────────────────────────────────────────────────────────────────────────

    private static void ReportColours(Prepared prep, RecognitionReport report)
    {
        var rows = prep.Map.Rows;
        report.Add(RecognitionFindingClass.ImageColoursMapped, rows.Count,
            $"{Plural(rows.Count, "colour", "colours")}: " +
            string.Join(", ", rows.Select(r => $"{r.Hex} → {(r.Overlap ? r.Spelling + " (overlap)" : r.Spelling)} ({r.Share * 100:0.#} %)")) + "." +
            (prep.Photograph ? " The picture is a photograph: its colours are read with the light's cast taken out, and " +
                               "unless the map is edited the copper-coloured ones are the top copper and the rest is the board." : ""));
        var ignored = rows.Where(r => r.Role == ImageLayerRole.Ignore || (r.Role != ImageLayerRole.Background && r.Layers.Count == 0 && r.Role != ImageLayerRole.Drill)).ToList();
        report.Add(RecognitionFindingClass.ImageIgnored, ignored.Count,
            $"{Plural(ignored.Count, "colour was", "colours were")} not traced: {string.Join(", ", ignored.Select(r => $"{r.Hex} ({r.Share * 100:0.#} %)"))}.");
    }

    private static void ReportPixels(Prepared prep, RecognitionReport report)
    {
        int specks = prep.Layers.Sum(l => l.Specks), snapped = prep.Layers.Sum(l => l.Snapped);
        report.Add(RecognitionFindingClass.ImageSpecksRemoved, specks,
            $"{Plural(specks, "speck", "specks")} below the minimum feature {(specks == 1 ? "was" : "were")} removed.");
        report.Add(RecognitionFindingClass.ImageEdgesSnapped, snapped,
            $"{Plural(snapped, "edge lies", "edges lie")} on 0°, 45° or 90°.");
    }

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
