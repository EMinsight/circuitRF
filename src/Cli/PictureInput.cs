// A picture as `convert` and `recognize` read it — brief-img-11-cli-and-mcp.md R-im11-1 … R-im11-4, R-im11-6.
//
// THIS FILE OWNS NO IMAGE PROCESSING, and it must never start (R-im11-6). The decode is ImageSource's, the kind
// ImageKind's, the trace ImageTrace's, the scale evidence ImageScale's, the overlay ImageOverlay's — the functions the
// Create from Image dialog calls. What is here is the flags those functions' inputs are spelled with, the refusals a
// command line makes where the dialog would have asked, and the projection of what came back into the result
// document. A comment-stripped source scan holds it (ImageCliSourceScanTests).
//
// THE SCALE IS NEVER A SILENT GUESS (D6). The dialog may open on an inferred scale because the user sees it and can
// change it with one click; a build machine sees nothing. So with no scale flag a picture is refused — the dialog's own
// sentence, then the flags that state one — and an inference is taken only when asked for with --scale auto, and then
// only on D6's own terms (at least ImageScale.MinPartsSupport parts agreeing), the refusal listing every candidate.
//
// A PHOTO OF A BOARD IS PUT SQUARE FIRST (IM-13): --corners names four corners and the rectangle's size, which IS the
// scale — so it is refused beside any other scale flag, and beside --region, whose pixels are the photo's and not the
// rectified picture the trace reads. BoardPhoto does the reading; this file only spells it.
//
// EVERY PICTURE COORDINATE IS IN PIXELS AND SAYS SO: --region takes `px` on each number, render --window's rule — a
// bare number is a refusal, because on a layout the same flag takes lengths and the two must not be confused.

using System.Globalization;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic.Recognition;
using RfCore.Export;

namespace CircuitRF.Cli;

internal sealed class PictureFlags
{
    public enum ScaleMode { Unstated, Stated, Points, Auto, Corners }

    public ScaleMode Mode = ScaleMode.Unstated;
    private double _metresPerPixel;
    private (double X0, double Y0, double X1, double Y1, string Distance) _points;
    private BoardPhotoCorners? _corners;

    /// <summary>--corners was given: the picture is a photo of a board, whatever IM-2 read it as.</summary>
    public bool HasCorners => _corners is not null;
    public string? LayerMap, Preset, Overlay;
    public double? MinFeature, Simplify, SnapDeg, EdgeStroke;
    public bool? Snap45, Underlay;

    /// <summary>brief-dfr29-1 R-dfr29-1-6: the corrections the dialog makes by hand, the same records, in FILE pixels (the
    /// rectified picture's with --corners).</summary>
    public readonly List<ImageCorrection> Corrections = [];

    /// <summary><c>--outline corners</c>: the rectangle --corners names is the board outline (the dialog's <i>Use as the
    /// board outline</i>).</summary>
    public bool OutlineCorners;

    // A schematic picture's (recognize only).
    public DrawingKind? Kind;
    public CrossingRule? Crossings;
    public SchematicArrange? Arrange;
    public double? Threshold;
    /// <summary>The picture's scales, stated in its pixels (IMF-2, R-imf2-5): each replaces only its own measurement.</summary>
    public double? Pen, TextHeight, SymbolScale;
    /// <summary>IMF-9, R-imf9-7: <c>auto</c> (null) reads the candidate readings and keeps the most plausible; a candidate's
    /// name reads that one alone.</summary>
    public string? Profile;
    public readonly List<ConnectionOverride> Overrides = [];

    /// <summary>The layout-picture flags given, by spelling — refused on a schematic picture or a document.</summary>
    public readonly List<string> LayoutFlags = [];
    /// <summary>The schematic-picture flags given, by spelling — refused on a layout picture or a document.</summary>
    public readonly List<string> SchematicFlags = [];

    /// <summary>The flags both kinds of picture take (<c>--overlay</c>, <c>--underlay</c>, <c>--image-kind</c>).</summary>
    private readonly List<string> _common = [];

    /// <summary>Any picture flag at all, by spelling — refused when the input is not a picture.</summary>
    public IEnumerable<string> All => _common.Concat(LayoutFlags).Concat(SchematicFlags);

    /// <summary>
    /// Reads <c>args[i]</c> when it is a picture flag, advancing past its value. Returns null when it is not one (the
    /// verb's own loop goes on), 0 when it was taken, or the exit code of a refusal. <paramref name="schematic"/>: the
    /// schematic-picture flags and <c>--image-kind</c> are accepted too (recognize).
    /// </summary>
    public int? Take(string verb, string[] args, ref int i, bool schematic)
    {
        string a = args[i];
        if (i + 1 >= args.Length) return null;
        string v = args[i + 1];
        int? Bad(string expected) => JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, a, v, expected));
        // The corners state the scale; a second statement beside them is refused, never ordered.
        if (a is "--scale" or "--dpi" or "--scale-points" && Mode == ScaleMode.Corners)
            return Bad("nothing beside --corners, which states the scale already");

        switch (a)
        {
            case "--corners":
            {
                if (Mode is not (ScaleMode.Unstated or ScaleMode.Corners)) return Bad("nothing beside another scale flag: the corners state the scale");
                if (BoardPhotoCorners.Parse(v, out string? why) is not { } corners)
                    return why is null or BoardPhotoCorners.Shape ? Bad(BoardPhotoCorners.Shape) : JsonRun.Fail(CliDiagnostics.PictureRefused(verb, why));
                Mode = ScaleMode.Corners;
                _corners = corners;
                { LayoutFlags.Add(a); i++; return 0; }
            }
            case "--overlay": Overlay = v; _common.Add(a); i++; return 0;
            case "--scale":
                if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) { Mode = ScaleMode.Auto; LayoutFlags.Add(a); i++; return 0; }
                string len = v.EndsWith("/px", StringComparison.OrdinalIgnoreCase) ? v[..^3] : v;
                if (len.Length == 0 || !char.IsLetter(len[^1]) || !LayoutUnits.TryParse(len, LayoutUnit.Um, 1000, out long nm) || nm <= 0)
                    return Bad("a length per pixel with its unit (25.4um/px, 0.1mm/px, 1mil/px) or auto");
                Mode = ScaleMode.Stated;
                _metresPerPixel = nm * 1e-9;
                { LayoutFlags.Add(a); i++; return 0; }
            case "--dpi":
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dpi) || !(dpi > 0) || !double.IsFinite(dpi))
                    return Bad("a positive number of pixels per inch");
                Mode = ScaleMode.Stated;
                _metresPerPixel = 0.0254 / dpi;
                { LayoutFlags.Add(a); i++; return 0; }
            case "--scale-points":
            {
                int eq = v.IndexOf('=');
                var xy = eq < 0 ? null : Numbers(v[..eq], 4);
                if (xy is null || v[(eq + 1)..].Trim().Length == 0)
                    return Bad("x0,y0,x1,y1=<length>: two points in pixels and the distance between them (10,10,510,10=12.7mm)");
                Mode = ScaleMode.Points;
                _points = (xy[0], xy[1], xy[2], xy[3], v[(eq + 1)..].Trim());
                { LayoutFlags.Add(a); i++; return 0; }
            }
            case "--layer-map": LayerMap = v; { LayoutFlags.Add(a); i++; return 0; }
            case "--preset": Preset = v; { LayoutFlags.Add(a); i++; return 0; }
            case "--min-feature":
                if (Number(v) is not { } mf || mf < 0) return Bad("an area in square pixels, 0 or more");
                MinFeature = mf;
                { LayoutFlags.Add(a); i++; return 0; }
            case "--simplify":
                if (Number(v) is not { } sp || sp < 0) return Bad("a tolerance in pixels, 0 or more");
                Simplify = sp;
                { LayoutFlags.Add(a); i++; return 0; }
            case "--edge-stroke":
                if (Number(v) is not { } es || es < 0) return Bad("a line width in pixels, 0 or more");
                EdgeStroke = es;
                { LayoutFlags.Add(a); i++; return 0; }
            case "--snap-deg":
                if (Number(v) is not { } sd || sd is < 0 or > 45) return Bad("an angle in degrees, 0 (no snapping) to 45");
                SnapDeg = sd;
                { LayoutFlags.Add(a); i++; return 0; }
            case "--snap45":
                if (OnOff(v) is not { } s45) return Bad("on or off");
                Snap45 = s45;
                { LayoutFlags.Add(a); i++; return 0; }
            case "--via" or "--hole" or "--not-via":
            {
                // A via's third number is its drill, a length with its unit (brief-dfr30-1 R-dfr30-1-2): --via 120px,80px,0.6mm.
                string at = v;
                double? drill = null;
                if (a == "--via" && v.Split(',') is { Length: 3 } three)
                {
                    at = three[0] + "," + three[1];
                    string d = three[2].Trim();
                    if (d.Length == 0 || !char.IsLetter(d[^1]) || d.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                        || !LayoutUnits.TryParse(d, LayoutUnit.Um, 1000, out long drillNm) || drillNm <= 0)
                        return Bad("x,y[,drill]: the point in pixels with px, the drill a length with its unit (120px,80px,0.6mm)");
                    drill = drillNm * 1e-9;
                }
                if (PointPx(verb, a, at) is not { } p) return _pxRefusal;
                Corrections.Add(a switch
                {
                    "--via" => ImageCorrection.Via(p.X, p.Y, drill), "--hole" => ImageCorrection.Hole(p.X, p.Y), _ => ImageCorrection.NotVia(p.X, p.Y),
                });
                { LayoutFlags.Add(a); i++; return 0; }
            }
            case "--copper":
            {
                int eq = v.LastIndexOf('=');
                if (eq < 0 || v[(eq + 1)..].Trim().Length == 0)
                    return Bad("\"x,y;x,y;x,y=<layer>\": three or more corners in pixels with px, then the conductor");
                if (PolygonPx(verb, a, v[..eq]) is not { } poly) return _pxRefusal;
                Corrections.Add(ImageCorrection.Copper(poly, v[(eq + 1)..].Trim()));
                { LayoutFlags.Add(a); i++; return 0; }
            }
            case "--fill":
            {
                // A paint bucket (the dialog's Fill tool): the area enclosed at the point is copper on the conductor.
                int eq = v.LastIndexOf('=');
                if (eq < 0 || v[(eq + 1)..].Trim().Length == 0)
                    return Bad("\"x,y=<conductor>\": a point in pixels with px inside the area, then the conductor");
                if (PointPx(verb, a, v[..eq]) is not { } p) return _pxRefusal;
                Corrections.Add(ImageCorrection.Fill(p.X, p.Y, v[(eq + 1)..].Trim()));
                { LayoutFlags.Add(a); i++; return 0; }
            }
            case "--clear":
            {
                // "x,y;...=<conductor>" clears that conductor only (brief-dfr30-1 R-dfr30-1-4); without it, every layer.
                int eq = v.LastIndexOf('=');
                string? layer = eq < 0 ? null : v[(eq + 1)..].Trim();
                if (layer is { Length: 0 }) return Bad("\"x,y;x,y;x,y\" or \"x,y;x,y;x,y=<conductor>\": three or more corners in pixels with px");
                if (PolygonPx(verb, a, eq < 0 ? v : v[..eq]) is not { } poly) return _pxRefusal;
                Corrections.Add(ImageCorrection.Clear(poly, layer));
                { LayoutFlags.Add(a); i++; return 0; }
            }
            case "--outline":
            {
                if (v.Trim().Equals("corners", StringComparison.OrdinalIgnoreCase)) { OutlineCorners = true; LayoutFlags.Add(a); i++; return 0; }
                if (PolygonPx(verb, a, v) is not { } poly) return _pxRefusal;
                Corrections.Add(ImageCorrection.Outline(poly));
                { LayoutFlags.Add(a); i++; return 0; }
            }
            case "--underlay":
                // Both kinds take it (D5): a layout picture's trace and a schematic picture's drawing lay the picture under.
                if (OnOff(v) is not { } under) return Bad("on or off");
                Underlay = under;
                _common.Add(a);
                i++;
                return 0;
        }
        if (!schematic) return null;
        switch (a)
        {
            case "--image-kind":
                if (!ImageKind.TryParseOverride(v, out var kind)) return Bad("auto, schematic, layout or plot");
                Kind = kind;
                _common.Add(a);
                i++;
                return 0;
            case "--crossings":
                Crossings = v.ToLowerInvariant() switch
                {
                    "never" => CrossingRule.Never, "dot" => CrossingRule.WithDot, "always" => CrossingRule.Always, _ => null,
                };
                if (Crossings is null) return Bad("never, dot or always");
                { SchematicFlags.Add(a); i++; return 0; }
            case "--arrange":
                Arrange = v.ToLowerInvariant() switch { "drawn" => SchematicArrange.AsDrawn, "tidy" => SchematicArrange.Tidy, _ => null };
                if (Arrange is null) return Bad("drawn or tidy");
                { SchematicFlags.Add(a); i++; return 0; }
            case "--threshold":
                if (Number(v) is not { } k || !(k > 0) || k >= 1) return Bad("Sauvola's k, between 0 and 1");
                Threshold = k;
                { SchematicFlags.Add(a); i++; return 0; }
            case "--profile":
            {
                string p = v.ToLowerInvariant();
                if (p != "auto" && !SchematicImageReadings.All.Contains(p)) return Bad("auto, " + string.Join(", ", SchematicImageReadings.All));
                Profile = p == "auto" ? null : p;
                { SchematicFlags.Add(a); i++; return 0; }
            }
            case "--pen" or "--text-height" or "--symbol-scale":
            {
                string p = v.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? v[..^2] : v;
                if (Number(p) is not { } px || !(px > 0)) return Bad("a size in pixels above zero");
                if (a == "--pen") Pen = px; else if (a == "--text-height") TextHeight = px; else SymbolScale = px;
                { SchematicFlags.Add(a); i++; return 0; }
            }
            case "--connect" or "--disconnect":
                if (ConnectionOverride.Parse(a, v, out string? error) is not { } o) return Bad(error ?? "x,y in pixels");
                Overrides.Add(o);
                { SchematicFlags.Add(a); i++; return 0; }
        }
        return null;
    }

    /// <summary>The refusal a pixel coordinate's parse made.</summary>
    private int? _pxRefusal;

    /// <summary><c>x,y</c>, each with <c>px</c>, or null with <see cref="_pxRefusal"/> set — <c>--region</c>'s rule.</summary>
    private PixelPoint? PointPx(string verb, string option, string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 2)
        {
            _pxRefusal = JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, option, text, "x,y, each in pixels with px (120px,80px)"));
            return null;
        }
        var v = new double[2];
        for (int k = 0; k < 2; k++)
        {
            string p = parts[k].Trim();
            if (p.Length > 0 && !char.IsLetter(p[^1])) { _pxRefusal = JsonRun.Fail(CliDiagnostics.PictureCoordinateNeedsPx(verb, option, p)); return null; }
            if (!p.EndsWith("px", StringComparison.OrdinalIgnoreCase) || Number(p[..^2]) is not { } d)
            {
                _pxRefusal = JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, option, text, "x,y, each in pixels with px (120px,80px)"));
                return null;
            }
            v[k] = d;
        }
        return new PixelPoint(v[0], v[1]);
    }

    /// <summary><c>x,y;x,y;x,y…</c>, three or more corners, each number with <c>px</c>.</summary>
    private IReadOnlyList<PixelPoint>? PolygonPx(string verb, string option, string text)
    {
        var corners = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (corners.Length < 3)
        {
            _pxRefusal = JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, option, text,
                "three or more corners x,y;x,y;x,y, each number in pixels with px (10px,10px;200px,10px;200px,90px;10px,90px)"));
            return null;
        }
        var points = new List<PixelPoint>(corners.Length);
        foreach (string c in corners)
        {
            if (PointPx(verb, option, c) is not { } p) return null;
            points.Add(p);
        }
        return points;
    }

    private static double? Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;

    private static bool? OnOff(string s) => s.ToLowerInvariant() switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, _ => null };

    /// <summary><paramref name="count"/> comma-separated pixel numbers, each optionally suffixed <c>px</c>.</summary>
    private static double[]? Numbers(string text, int count)
    {
        var parts = text.Split(',');
        if (parts.Length != count) return null;
        var v = new double[count];
        for (int k = 0; k < count; k++)
        {
            string p = parts[k].Trim();
            if (p.EndsWith("px", StringComparison.OrdinalIgnoreCase)) p = p[..^2];
            if (Number(p) is not { } d) return null;
            v[k] = d;
        }
        return v;
    }

    /// <summary>R-im11-1's <c>--region</c>: four pixel coordinates, each with <c>px</c>.</summary>
    public static (PixelRect? Region, int? Refusal) Region(string verb, string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 4)
            return (null, JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, "--region", text, "x0,y0,x1,y1, each in pixels with px")));
        var v = new double[4];
        for (int k = 0; k < 4; k++)
        {
            string p = parts[k].Trim();
            if (p.Length > 0 && !char.IsLetter(p[^1])) return (null, JsonRun.Fail(CliDiagnostics.PictureCoordinateNeedsPx(verb, "--region", p)));
            if (!p.EndsWith("px", StringComparison.OrdinalIgnoreCase) || Number(p[..^2]) is not { } d)
                return (null, JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, "--region", text, "x0,y0,x1,y1, each in pixels with px (10px,10px,400px,300px)")));
            v[k] = d;
        }
        return (new PixelRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3])), null);
    }

    // ── the trace's input ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <see cref="ImageTraceInput"/> these flags spell — the dialog's fields, one for one. A flag absent is the
    /// dialog's default; a preset supplies its map and options, and a flag given beside it wins. Pixel numbers and
    /// lengths per pixel are the FILE's pixels, carried onto a reduced reading exactly as the file's own dpi is.
    /// </summary>
    public (ImageTraceInput? Input, int? Refusal) TraceInput(string verb, ImageSource source, ImageKindResult? kind, Technology tech,
                                                             string? techPath, PixelRect? region)
    {
        double f = source.Raster.ReductionFactor;
        var options = new ImageTraceOptions();
        ImageLayerMap? map = null;
        if (Preset is { } name)
        {
            var presets = ImageTracePresets.Load();
            if (presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } preset)
                return (null, JsonRun.Fail(CliDiagnostics.PicturePresetNotFound(verb, name,
                    presets.Count == 0 ? "(none)" : string.Join(", ", presets.Select(p => p.Name)))));
            options = preset.Options;
            map = new ImageLayerMap([.. preset.Rows.Select(r => new ImageLayerRow(-1, Hex(r.Colour) ?? 0, 0, r.Role, r.Layers))], Edited: true);
        }
        if (LayerMap is { } spelled)
        {
            var (m, refusal) = ParseLayerMap(verb, spelled, tech);
            if (refusal is { } no) return (null, no);
            map = m;
        }
        if (MinFeature is { } mf) options = options with { MinFeaturePx2 = mf };
        if (Simplify is { } sp) options = options with { SimplifyPx = sp };
        if (EdgeStroke is { } es) options = options with { EdgeStrokePx = es };
        if (SnapDeg is { } sd) options = options with { Snap = sd > 0, SnapAngleDeg = sd > 0 ? sd : options.SnapAngleDeg };
        if (Snap45 is { } s45) options = options with { Snap45 = s45 };
        if (Underlay is { } u) options = options with { KeepUnderlay = u };

        if (_corners is not null && region is not null)
            return (null, JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, "--region", "", "nothing beside --corners: its pixels are the photo's, and the trace reads the rectified picture")));
        var scale = Mode switch
        {
            ScaleMode.Stated => ImageScaleStatement.Stated(_metresPerPixel / f),
            ScaleMode.Points => ImageScaleStatement.TwoPoints(new PixelPoint(_points.X0 * f, _points.Y0 * f),
                                                              new PixelPoint(_points.X1 * f, _points.Y1 * f), _points.Distance),
            _ => ImageScaleStatement.Auto,
        };
        var input = new ImageTraceInput
        {
            Source = source, Technology = tech, TechnologyPath = techPath, Scale = scale, LayerMap = map, Options = options,
            Kind = kind is null ? null : kind.AsLayout(),
            Scope = region is { } r ? new PixelRect(r.Left * f, r.Top * f, r.Right * f, r.Bottom * f) : null,
        };
        // The corrections in the picture TRACED: the file's pixels carried onto a reduced reading, or the rectified picture's
        // own with --corners (whose pixels --overlay draws), as the dialog holds them.
        var conductors = ImageTrace.ConductorLayerNames(tech);
        foreach (var c in Corrections.Where(c => c.Kind is ImageCorrectionKind.Copper or ImageCorrectionKind.Fill || c is { Kind: ImageCorrectionKind.Clear, Layer: not null }))
            if (!conductors.Contains(c.Layer ?? "", StringComparer.Ordinal))
                return (null, JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, c.Kind switch
                {
                    ImageCorrectionKind.Copper => "--copper", ImageCorrectionKind.Fill => "--fill", _ => "--clear",
                }, c.Layer ?? "",
                    $"one of {tech.Name}'s conductors: {string.Join(", ", conductors)}")));
        if (OutlineCorners && _corners is null)
            return (null, JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, "--outline", "corners", "a polygon in pixels, or corners beside --corners")));
        double g = _corners is null ? f : 1;
        input = input with
        {
            Corrections = [.. Corrections.Select(c => c with { Points = [.. c.Points.Select(p => new PixelPoint(p.X * g, p.Y * g))] })],
        };
        if (_corners is { } corners)
        {
            var photo = BoardPhoto.Read(source, corners.Scaled(f));
            if (!photo.Ok) return (null, JsonRun.Fail(CliDiagnostics.PictureRefused(verb, photo.Refusal!)));
            input = BoardPhoto.Apply(input, photo);
            if (OutlineCorners) input = ImageTrace.WithPhotoOutline(input);
        }
        return (input, null);
    }

    /// <summary><c>#rrggbb=&lt;layer&gt;[+&lt;layer&gt;]|drill|outline|silk|background|ignore,…</c> as an edited map — the
    /// trace lays it over the picture's colours (each colour takes the nearest row within ΔE 8; one no row names is
    /// ignored), as it lays a preset.</summary>
    private static (ImageLayerMap? Map, int? Refusal) ParseLayerMap(string verb, string text, Technology tech)
    {
        var rows = new List<ImageLayerRow>();
        var roles = new List<(int Index, ImageLayerRole Role)>();
        foreach (string entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq < 0 || Hex(entry[..eq]) is not { } rgb)
                return (null, JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, "--layer-map", entry,
                    "#rrggbb=<layer>|drill|outline|silk|background|ignore, comma-separated")));
            string what = entry[(eq + 1)..].Trim();
            var role = what.ToLowerInvariant() switch
            {
                "drill" => ImageLayerRole.Drill,
                "outline" => ImageLayerRole.BoardOutline,
                "silk" or "silkscreen" => ImageLayerRole.Silkscreen,
                "background" => ImageLayerRole.Background,
                "ignore" => ImageLayerRole.Ignore,
                _ => ImageLayerRole.Layer,
            };
            string[] layers = [];
            if (role == ImageLayerRole.Layer)
            {
                layers = what.Split('+', StringSplitOptions.TrimEntries);
                foreach (string layer in layers)
                    if (!tech.Layers.Any(l => string.Equals(l.Name, layer, StringComparison.Ordinal)))
                        return (null, JsonRun.Fail(CliDiagnostics.PictureLayerUnknown(verb, layer, tech.Name,
                            string.Join(", ", tech.Layers.Select(l => l.Name)))));
            }
            rows.Add(new ImageLayerRow(rows.Count, rgb, 0, role, layers));
            if (role is ImageLayerRole.Drill or ImageLayerRole.BoardOutline or ImageLayerRole.Silkscreen) roles.Add((rows.Count - 1, role));
        }
        // A drill, outline or silkscreen row traces onto the technology's layer for that role, exactly as an Auto row
        // of that role would — the dialog's own rule (WithRole).
        var map = new ImageLayerMap(rows, Edited: true);
        foreach (var (index, role) in roles) map = map.WithRole(tech, index, role);
        return (map, null);
    }

    private static int? Hex(string s)
    {
        s = s.Trim();
        return s.Length == 7 && s[0] == '#' && int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)
            ? rgb : null;
    }

    /// <summary>The map as applied, in the spelling <c>--layer-map</c> takes — printed so a caller can pin it.</summary>
    public static string Spell(ImageLayerMap map) =>
        string.Join(",", map.Rows.Select(r => $"{r.Hex}={(r.Role == ImageLayerRole.Silkscreen ? "silk" : r.Spelling)}"));

    // ── D6, headless ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a traced picture's scale may be used: a stated scale always (its own refusal, if any, is the trace's);
    /// with no flag, never — the dialog's sentence when nothing was inferred, the inference named when one was; with
    /// <c>--scale auto</c>, D6's parts inference, else a refusal listing every candidate.
    /// </summary>
    public int? ScaleGate(string verb, ImageTraceResult trace)
    {
        if (Mode is ScaleMode.Stated or ScaleMode.Points or ScaleMode.Corners) return null;
        var chosen = trace.Scale;
        if (Mode == ScaleMode.Auto)
        {
            if (chosen is not null) return null;
            string list = trace.ScaleCandidates.Count == 0 ? "no evidence at all"
                : string.Join("; ", trace.ScaleCandidates.Select(c =>
                    $"{c.Display} ({KindName(c)}, support {c.Support.ToString(CultureInfo.InvariantCulture)}: {c.Evidence})"));
            return JsonRun.Fail(CliDiagnostics.PictureScaleAutoRefused(verb, ImageScale.MinPartsSupport, list));
        }
        return chosen is not null
            ? JsonRun.Fail(CliDiagnostics.PictureScaleNotAccepted(verb, chosen.Display, chosen.Evidence))
            : JsonRun.Fail(CliDiagnostics.PictureScaleUnstated(verb, ImageTrace.NoScaleRefusal(trace.ScaleCandidates)));
    }

    private static string KindName(ImageScaleCandidate c) => c.Kind.ToString().ToLowerInvariant();

    // ── the technology ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--tech &lt;id|path&gt;</c>: a <c>.ctech</c> file, or a technology id circuitRF knows (never a fallback — an unknown
    /// id is a refusal listing the known ones, <c>new workspace --tech</c>'s rule). Absent: the picture's ancestor
    /// workspace's technology when <paramref name="walkUp"/>, else — and when there is none — the workspace-creation
    /// default, which is what a picture's stackup is taken to be when nothing says otherwise.
    /// </summary>
    /// <returns>The technology and its file — or, for a catalogue technology not on disk, a null file and its id.</returns>
    public static (Technology? Tech, string? Path, string? CatalogId, int? Refusal) Technology(string verb, string? arg, string picturePath,
                                                                                             bool walkUp)
    {
        if (arg is not null)
        {
            if (File.Exists(arg) || arg.EndsWith(".ctech", StringComparison.OrdinalIgnoreCase))
            {
                try { return (TechPersistence.LoadFromFile(arg), System.IO.Path.GetFullPath(arg), null, null); }
                catch (Exception ex) { return (null, null, null, JsonRun.Fail(CliDiagnostics.PictureTechnologyUnreadable(verb, arg, ex.Message))); }
            }
            if (TechnologyCatalog.Find(arg) is { } entry) return (TechnologyCatalog.Load(entry), null, entry.Id, null);
            return (null, null, null, JsonRun.Fail(CliDiagnostics.PictureTechnologyUnknown(verb, arg,
                string.Join(", ", TechnologyCatalog.All.Select(e => e.Id)))));
        }
        if (walkUp)
        {
            var (res, _) = TechnologyResolver.ResolveForDocument(null, picturePath, null, new TechnologyCache());
            if (res.Tech is { } found) return (found, res.ResolvedPath, null, null);
        }
        var fallback = TechnologyCatalog.Find(TechnologyCatalog.DefaultId)!;
        return (TechnologyCatalog.Load(fallback), null, fallback.Id, null);
    }

    // ── the overlay (R-im11-3) ───────────────────────────────────────────────────────────────────────

    /// <summary>Writes <paramref name="overlay"/> over the picture to <c>--overlay</c>, when given — whether or not the
    /// run goes on to be refused, because a refused reading is when it is most useful. A failed write is a warning:
    /// the run's own answer does not depend on it. Of <paramref name="pictures"/> pictures (a plot session), picture 1's
    /// overlay is the path given and picture n's is <c>&lt;stem&gt;-n.png</c> beside it.</summary>
    public void WriteOverlay(string verb, ImageOverlay overlay, ImageSource source, int picture = 1, int pictures = 1)
    {
        if (Overlay is not { } target) return;
        string path = System.IO.Path.GetFullPath(target);
        if (pictures > 1 && picture > 1)
            path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
                $"{System.IO.Path.GetFileNameWithoutExtension(path)}-{picture.ToString(CultureInfo.InvariantCulture)}{System.IO.Path.GetExtension(path)}");
        try
        {
            overlay.WritePng(path, source.Raster);
            Console.Error.WriteLine($"[circuitRF] Wrote {path}");
            JsonRun.AddOutput("overlay", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            JsonRun.Report(CliDiagnostics.PictureOverlayFailed(verb, path, ex.Message));
        }
    }

    // ── the result document (R-im11-4) ───────────────────────────────────────────────────────────────

    public static ImageReadJson ImageJson(ImageSource source, ImageKindResult kind) => new(
        kind.Name, kind.Confidence, kind.Forced, kind.Suggested is { } s ? ImageKind.Name(s) : null, kind.Reason,
        [.. kind.Evidence.Select(e => new ImageKindEvidenceJson(e.Name, e.Value, e.Reads))],
        source.Raster.Width, source.Raster.Height, source.Raster.ReductionFactor);

    public static ImageScaleJson ScaleJson(ImageTraceResult trace) => new(
        trace.Scale?.Display, trace.Scale?.MetresPerPixel, trace.Scale?.Evidence,
        [.. trace.ScaleCandidates.Select(c => new ImageScaleCandidateJson(KindName(c), c.Display, c.MetresPerPixel, c.Support, c.Evidence, c.OfferedOnly))]);

    /// <summary>The corrections as applied (R-dfr29-1-6), or null when none was made.</summary>
    public static IReadOnlyList<ImageCorrectionJson>? CorrectionsJson(ImageTraceResult trace) =>
        trace.Corrections.Count == 0 ? null
        : [.. trace.Corrections.Select(o => new ImageCorrectionJson(o.Correction.Kind.ToString().ToLowerInvariant(), o.Correction.Spelling,
                                                                    o.Applied, o.Refusal))];

    public static IReadOnlyList<ImageLayerJson> LayersJson(ImageTraceResult trace) =>
        trace.LayerMap is not { } map ? [] :
        [.. map.Rows.Select(r => new ImageLayerJson(r.Hex, r.Share, r.Role.ToString().ToLowerInvariant(), r.Layers,
                                                    r.Role == ImageLayerRole.Silkscreen ? "silk" : r.Spelling))];

    /// <summary>IMF-9, R-imf9-7: the reading kept, every candidate read with its plausibility terms, and the counters the
    /// time budget is held by; null when the kind check alone decided.</summary>
    public static ImageReadingJson? ReadingJson(SchematicImageResult result) =>
        result.Plausibility is null && result.Candidates.Count == 0 ? null
        : new ImageReadingJson(result.ReadingName, result.Plausibility is { } p ? Math.Round(p.Score, 3) : null,
            [.. result.Candidates.Select(c => c.Result.Plausibility is { } t
                ? new ImageReadingCandidateJson(c.Name, c.Label, Math.Round(c.Score, 3), Math.Round(t.InkExplained, 3), Math.Round(t.PartsNamed, 3),
                                                Math.Round(t.PinsHonest, 3), Math.Round(t.WordsRead, 3), Math.Round(t.GraphSane, 3))
                : new ImageReadingCandidateJson(c.Name, c.Label, Math.Round(c.Score, 3), 0, 0, 0, 0, 0))],
            result.Cost.Readings, result.Cost.Pixels, result.Cost.Regions, result.Cost.Complex);

    /// <summary>A schematic picture's symbols, words and crossings, in SOURCE-picture pixels — the space
    /// <c>--connect</c>/<c>--disconnect</c> and the parts CSV's pixel rows are in.</summary>
    public static (IReadOnlyList<ImageSymbolJson> Symbols, IReadOnlyList<ImageWordJson> Words, IReadOnlyList<ImageCrossingJson> Crossings)
        SchematicJson(SchematicImageResult result)
    {
        if (result.Read is not { } read) return ([], [], []);
        PixelBoxJson Box(PixelBox b)
        {
            var (l, t) = read.ToSource(b.Left, b.Top);
            var (r, bo) = read.ToSource(b.Right + 1, b.Bottom + 1);
            return new PixelBoxJson((int)Math.Floor(Math.Min(l, r)), (int)Math.Floor(Math.Min(t, bo)),
                                    (int)Math.Ceiling(Math.Max(l, r)) - 1, (int)Math.Ceiling(Math.Max(t, bo)) - 1);
        }
        var refdes = new Dictionary<int, string>();
        foreach (var (board, symbol) in result.Circuit?.SymbolOfRow ?? new Dictionary<string, int>())
            if (result.Parts.Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, board, StringComparison.OrdinalIgnoreCase)) is { } row)
                refdes[symbol] = row.Refdes;
        var syms = result.Symbols?.Symbols ?? [];
        var symbols = syms.Select((s, i) => new ImageSymbolJson(i, s.KindLabel, s.Template?.Name, s.Template is null ? null : s.Orientation.ToString(),
                                                               s.Disposition.ToString(), refdes.GetValueOrDefault(i), Box(s.Region.Box))).ToList();
        var words = result.Words.Select(w => new ImageWordJson(w.Id, w.Reading?.Text, w.Class.ToString(), Box(w.Box))).ToList();
        var crossings = read.Wires.Crossings.Select(c =>
        {
            var (x, y) = read.ToSource(c.X, c.Y);
            return new ImageCrossingJson(c.Id, Math.Round(x, 1), Math.Round(y, 1), c.Reading.ToString(), c.Connected, c.Overridden);
        }).ToList();
        return (symbols, words, crossings);
    }
}
