// A photograph of a board's top side — brief-img-13-board-photos.md R-im13-2 … R-im13-5 (D6, D7, D17).
//
// Everything before this reads DRAWN pictures. A phone photo of a real board breaks three of their assumptions — the
// board is seen at an angle, the light is neither white nor even, and the copper is mostly UNDER the solder mask — so a
// photo is not traced as taken. It is first turned into a picture the IM-3 trace already reads:
//
//   photo ─ four corners + the rectangle's size ─ Perspective (DLT) ─ rectified photo, at the photo's own finest sampling
//         ─ WhiteBalance: grey world over the near-grey pixels, then the flat field divided out
//         ─ classes, per pixel: off the board │ mask │ copper under the mask │ exposed copper │ silkscreen │ part body
//           (specular highlights filled from their neighbourhood first)
//         ─ a ColourClusterSet with one cluster per class, in FIXED colours — so a map, a preset or --layer-map written
//           for one board photo applies to every other
//
// The trace then runs unchanged on the rectified photo (it is the picture the dialog shows and the underlay keeps), with
// these clusters in place of its own colour separation, and the scale the rectangle states.
//
// No trained model: the classes come from CIELAB hue, chroma and lightness, the mask hue is the picture's dominant
// chromatic hue, and the copper under it is the mask family's lighter half (Otsu on L). Thin bright strokes are
// silkscreen and wide bright areas are tin — told apart by an opening at a width the known scale converts from
// millimetres. Part bodies cover their pads and are REPORTED as unread parts, never guessed under.

using System.Globalization;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>Four corners picked on a photo, in order around the rectangle (clockwise on the picture, the first to the
/// second along its width), and the rectangle's size with units — which IS the scale (D6 (2) in another form).</summary>
/// <param name="Points">Picture pixels (x right, y down).</param>
public sealed record BoardPhotoCorners(IReadOnlyList<PixelPoint> Points, string Width, string Height)
{
    /// <summary><c>x,y;x,y;x,y;x,y=&lt;w&gt;x&lt;h&gt;</c> — what <c>--corners</c> takes and the provenance records.</summary>
    public string Spelling =>
        string.Join(";", Points.Select(p => $"{Num(p.X)},{Num(p.Y)}")) + $"={Width.Trim()}x{Height.Trim()}";

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The corners in a picture reduced by <paramref name="factor"/> (a file's pixels → a reduced reading's).</summary>
    public BoardPhotoCorners Scaled(double factor) =>
        this with { Points = [.. Points.Select(p => new PixelPoint(p.X * factor, p.Y * factor))] };

    /// <summary>What <see cref="Parse"/> refuses a text of the wrong shape with — the spelling it takes.</summary>
    public const string Shape = "x,y;x,y;x,y;x,y=<width>x<height>: four corners in pixels, clockwise from the top-left, " +
                                "and the rectangle's size with units (120,80;980,95;1010,700;90,690=50mmx30mm)";

    /// <summary>Parses <see cref="Spelling"/>; each number may carry <c>px</c>, each size must carry a unit. A text of
    /// the wrong shape is refused with <see cref="Shape"/>; a size without a unit with its own sentence.</summary>
    public static BoardPhotoCorners? Parse(string text, out string? refusal)
    {
        refusal = null;
        int eq = (text ?? "").IndexOf('=');
        if (eq < 0) { refusal = Shape; return null; }
        var pairs = text![..eq].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length != 4) { refusal = Shape; return null; }
        var points = new List<PixelPoint>(4);
        foreach (string pair in pairs)
        {
            var xy = pair.Split(',', StringSplitOptions.TrimEntries);
            if (xy.Length != 2 || Pixel(xy[0]) is not { } x || Pixel(xy[1]) is not { } y) { refusal = Shape; return null; }
            points.Add(new PixelPoint(x, y));
        }
        var size = text[(eq + 1)..].Split(['x', 'X', '×'], StringSplitOptions.TrimEntries);
        if (size.Length != 2 || size[0].Length == 0 || size[1].Length == 0) { refusal = Shape; return null; }
        var corners = new BoardPhotoCorners(points, size[0], size[1]);
        if (corners.SizeMetres(out refusal) is null) return null;
        return corners;

        static double? Pixel(string s)
        {
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;
        }
    }

    /// <summary>The rectangle's width and height, metres — or null with the refusal: a bare number could be any unit.</summary>
    public (double W, double H)? SizeMetres(out string? refusal)
    {
        refusal = null;
        if (Length(Width, out refusal) is not { } w || Length(Height, out refusal) is not { } h) return null;
        return (w, h);
    }

    private static double? Length(string text, out string? refusal)
    {
        refusal = null;
        string t = (text ?? "").Trim();
        if (t.Length == 0 || !char.IsLetter(t[^1]))
        {
            refusal = $"The size '{t}' has no unit, and it could be micrometres, millimetres or mils. Give it one: " +
                      $"'{t} mm' or '{t} mil'.";
            return null;
        }
        if (!LayoutUnits.TryParse(t, LayoutUnit.Um, 1000, out long nm) || nm <= 0)
        {
            refusal = $"'{t}' is not a length circuitRF reads (a number with nm, um, mm, mil or in).";
            return null;
        }
        return nm * 1e-9;
    }
}

/// <summary>What each pixel of a board photo was read as — one cluster each, in a fixed colour.</summary>
public enum BoardPhotoClass { OffBoard, Mask, CopperUnderMask, ExposedCopper, Silkscreen, PartBody }

/// <summary>A board photo put square, its light corrected and its pixels classed — what the trace reads instead of the
/// photo as taken.</summary>
public sealed record BoardPhotoReading
{
    public string? Refusal { get; init; }
    public bool Ok => Refusal is null;

    public BoardPhotoCorners? Corners { get; init; }

    /// <summary>The photo's own file name and SHA-256 — the provenance's, since the traced picture is the rectified one.</summary>
    public string PhotoName { get; init; } = "";
    public string PhotoSha256 { get; init; } = "";

    /// <summary>What IM-2 read the photo as taken as — the provenance's kind when the caller states none.</summary>
    public ImageKindResult? PhotoKind { get; init; }

    /// <summary>The rectified, light-corrected photo as a picture (PNG) — what the dialog shows, the trace reads and the
    /// underlay keeps.</summary>
    public ImageSource? Rectified { get; init; }

    /// <summary>One cluster per class present; <see cref="ClassOf"/> names each.</summary>
    public ColourClusterSet? Clusters { get; init; }
    public IReadOnlyList<BoardPhotoClass> ClassOf { get; init; } = [];

    /// <summary>Photo pixels → rectified pixels.</summary>
    public Homography PhotoToRectified { get; init; }

    public double MetresPerPixel { get; init; }

    /// <summary>Where the rectangle's first corner lies in the rectified picture, pixels.</summary>
    public PixelPoint Origin { get; init; }

    /// <summary>How far each corner of the rectangle lies from the corner the rectified picture shows there, metres; NaN
    /// where no corner shows (R-im13-5).</summary>
    public IReadOnlyList<double> CornerResidualM { get; init; } = [];

    public int HighlightsFilled { get; init; }
    public int UnreadParts { get; init; }

    /// <summary>The scale this reading states (<see cref="ImageScaleKind.Corners"/>).</summary>
    public ImageScaleCandidate? Scale { get; init; }

    /// <summary>A point on the rectangle (metres from its first corner, along its width and height) in rectified pixels.</summary>
    public PixelPoint RectangleToPixel(double xm, double ym) => new(Origin.X + xm / MetresPerPixel, Origin.Y + ym / MetresPerPixel);

    /// <summary>The rectangle the corners name, in rectified pixels, or null — what Board corners…'s <i>Use as the board
    /// outline</i> and <c>--outline corners</c> write as the outline (brief-dfr29-1 R-dfr29-1-3).</summary>
    public IReadOnlyList<PixelPoint>? Rectangle() => Corners?.SizeMetres(out _) is { } size
        ? [RectangleToPixel(0, 0), RectangleToPixel(size.W, 0), RectangleToPixel(size.W, size.H), RectangleToPixel(0, size.H)]
        : null;

    /// <summary>The cluster index of a class, or −1 when no pixel is of it.</summary>
    public int ClusterOf(BoardPhotoClass c)
    {
        for (int i = 0; i < ClassOf.Count; i++) if (ClassOf[i] == c) return i;
        return -1;
    }

    /// <summary>R-im13-4's default map: both copper classes to the top copper, the mask and what is off the board to the
    /// background, the silkscreen to the silkscreen, part bodies ignored.</summary>
    public ImageLayerMap DefaultMap(Technology technology)
    {
        ArgumentNullException.ThrowIfNull(technology);
        var clusters = Clusters!;
        var copper = ImageLayerMap.CopperLayers(technology);
        string? top = copper.Count > 0 ? ImageLayerMap.NameOf(technology, copper[0]) : null;
        var rows = new List<ImageLayerRow>();
        int silk = -1;
        foreach (var c in clusters.Clusters)
        {
            var cls = ClassOf[c.Index];
            var (role, layers) = cls switch
            {
                BoardPhotoClass.CopperUnderMask or BoardPhotoClass.ExposedCopper when top is not null => (ImageLayerRole.Layer, new[] { top }),
                BoardPhotoClass.CopperUnderMask or BoardPhotoClass.ExposedCopper => (ImageLayerRole.Ignore, Array.Empty<string>()),
                BoardPhotoClass.PartBody => (ImageLayerRole.Ignore, Array.Empty<string>()),
                _ => (ImageLayerRole.Background, Array.Empty<string>()),
            };
            if (cls == BoardPhotoClass.Silkscreen) silk = c.Index;
            rows.Add(new ImageLayerRow(c.Index, c.Rgb, c.Share, role, layers));
        }
        var map = new ImageLayerMap(rows);
        if (silk >= 0) map = map.WithRole(technology, silk, ImageLayerRole.Silkscreen) with { Edited = false };
        return map;
    }
}

public static class BoardPhoto
{
    // ── the classes' colours: fixed, so a map written for one board photo applies to every other ─────────────

    public static int Rgb(BoardPhotoClass c) => c switch
    {
        BoardPhotoClass.OffBoard => 0x9e9e9e,
        BoardPhotoClass.Mask => 0x1f6b3a,
        BoardPhotoClass.CopperUnderMask => 0x74c476,
        BoardPhotoClass.ExposedCopper => 0xc87533,
        BoardPhotoClass.Silkscreen => 0xf5f5f5,
        _ => 0x202020,
    };

    // ── thresholds, each with its reason in docs/design/image-to-circuit.md §14 ─────────────────────────────

    /// <summary>The rectified picture extends past the rectangle by at most its own size on each side.</summary>
    public const double MaxMarginShare = 1.0;

    /// <summary>…and holds at most this many times the photo's pixels (and never more than 50 MP).</summary>
    public const double MaxPixelGrowth = 4.0;

    /// <summary>A rectangle side shorter than this many rectified pixels is refused: the corners are too close.</summary>
    public const double MinSidePx = 16;

    /// <summary>The mask is chromatic: its hue is read from pixels at least this saturated (CIELAB chroma).</summary>
    public const double MaskMinChroma = 15;

    /// <summary>A pixel within this many degrees of the mask's hue, and at least <see cref="MaskFamilyMinChroma"/>, is
    /// the mask or the copper under it.</summary>
    public const double MaskHueHalfWidth = 30, MaskFamilyMinChroma = 8;

    /// <summary>Copper and gold hues (CIELAB, degrees) — never the mask's; a copper-hued pixel this saturated is exposed
    /// copper.</summary>
    public const double CopperHueMin = 35, CopperHueMax = 105, CopperMinChroma = 15;

    /// <summary>The mask's hue must hold at least this share of the photo, or the board has no mask to read under.</summary>
    public const double MinMaskShare = 0.05;

    /// <summary>The mask family's two halves must differ by this much lightness to be mask and copper under it.</summary>
    public const double UnderMaskMinDeltaL = 8;

    /// <summary>Near-grey: below this chroma a pixel is tin, silkscreen, a part body or the bench.</summary>
    public const double GreyChroma = 12;

    /// <summary>A near-grey pixel darker than this is a part body (black epoxy); brighter than <see cref="BrightMinL"/>,
    /// silkscreen or tin.</summary>
    public const double BodyMaxL = 30, BrightMinL = 70;

    /// <summary>A bright area at least this wide is tin (a pad); narrower, a silkscreen stroke (0.1–0.25 mm).</summary>
    public const double PadMinWidthM = 0.35e-3;

    /// <summary>The board is the mask closed over gaps this wide, holes filled — with the rectangle.</summary>
    public const double BoardCloseM = 1.5e-3;

    /// <summary>Mask-hued specks narrower than this many pixels are noise, not the board, when the board is found.</summary>
    public const double MaskSpeckPx = 1.5;

    /// <summary>Copper narrower than this is read as the mask (at least a 1 px opening): 0.1 mm is the narrowest line a
    /// board is etched with, and a photo that resolves less cannot read one.</summary>
    public const double MinCopperWidthM = 0.1e-3;

    /// <summary>Breaks in a silkscreen stroke up to this many pixels are closed.</summary>
    public const double SilkGapPx = 1.5;

    /// <summary>A part body is reported when at least this wide on both sides.</summary>
    public const double BodyMinM = 0.8e-3;

    /// <summary>A highlight is clipped (every channel ≥ <see cref="WhiteBalance.Clipped"/>) and at least this many pixels
    /// from its edge somewhere — a blob, not a silkscreen stroke.</summary>
    public const double HighlightMinHalfWidthPx = 2.0;

    /// <summary>A highlight's bloom — the anti-aliased, half-clipped ring around it — is this many pixels wide.</summary>
    public const double HighlightBloomPx = 2.5;

    /// <summary>The corner search window, as a share of the rectangle's shorter side (at least 6 px).</summary>
    public const double CornerWindowShare = 0.03;

    public const string NoMaskNote = "no mask hue was found, so no copper under a mask was read";

    /// <summary>Reads <paramref name="photo"/> on <paramref name="corners"/> (in its raster's pixels).</summary>
    public static BoardPhotoReading Read(ImageSource photo, BoardPhotoCorners corners, int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(corners);
        BoardPhotoReading Refused(string why) => new() { Refusal = why, Corners = corners };
        if (corners.Points.Count != 4) return Refused("Four corners are needed, in order around the rectangle.");
        if (corners.SizeMetres(out string? sizeRefusal) is not { } size) return Refused(sizeRefusal!);
        var (wm, hm) = size;

        var src = corners.Points.Select(p => (p.X, p.Y)).ToList();
        if (!Perspective.IsConvexQuad(src))
            return Refused("The four corners do not make a four-sided shape: take them in order around the rectangle, none crossing.");
        if (Shoelace(src) < 0)
            return Refused("The corners go anticlockwise: give them clockwise from the top-left, so the first to the second is the width.");

        // Photo → the rectangle in metres.
        var rect = new List<(double, double)> { (0, 0), (wm, 0), (wm, hm), (0, hm) };
        if (Perspective.Solve(src, rect) is not { } toMetres) return Refused("The four corners do not define a rectangle: three lie on a line.");

        // Sampling: the photo's finest along any side, so nothing on the near side is lost.
        double s = Math.Max(Math.Max(Dist(src[0], src[1]) / wm, Dist(src[3], src[2]) / wm),
                            Math.Max(Dist(src[0], src[3]) / hm, Dist(src[1], src[2]) / hm));   // pixels per metre

        // Extent: the photo as mapped, within the rectangle grown by its own size each way.
        var raster = photo.Raster;
        double bx0 = -MaxMarginShare * wm, by0 = -MaxMarginShare * hm, bx1 = (1 + MaxMarginShare) * wm, by1 = (1 + MaxMarginShare) * hm;
        double mx0 = double.MaxValue, my0 = double.MaxValue, mx1 = double.MinValue, my1 = double.MinValue;
        bool finite = true;
        double sign = Math.Sign(W(toMetres, src[0].X, src[0].Y));
        foreach (var (x, y) in new[] { (0.0, 0.0), (raster.Width, 0.0), (raster.Width, raster.Height), (0.0, raster.Height) })
        {
            var (u, v) = toMetres.Map(x, y);
            if (Math.Sign(W(toMetres, x, y)) != sign || !double.IsFinite(u) || !double.IsFinite(v)) { finite = false; break; }
            mx0 = Math.Min(mx0, u); my0 = Math.Min(my0, v); mx1 = Math.Max(mx1, u); my1 = Math.Max(my1, v);
        }
        if (finite)
        {
            bx0 = Math.Max(bx0, Math.Min(0, mx0)); by0 = Math.Max(by0, Math.Min(0, my0));
            bx1 = Math.Min(bx1, Math.Max(wm, mx1)); by1 = Math.Min(by1, Math.Max(hm, my1));
        }
        double limit = Math.Min(RasterImage.MaxPixels, MaxPixelGrowth * raster.Width * raster.Height);
        double area = (bx1 - bx0) * (by1 - by0) * s * s;
        if (area > limit) s *= Math.Sqrt(limit / area);
        if (wm * s < MinSidePx || hm * s < MinSidePx)
            return Refused("The corners are too close together to read anything between them: pick the board's own corners.");

        int ow = Math.Max(1, (int)Math.Ceiling((bx1 - bx0) * s)), oh = Math.Max(1, (int)Math.Ceiling((by1 - by0) * s));
        var outputToMetres = new Homography(1 / s, 0, bx0, 0, 1 / s, by0, 0, 0, 1);
        var metresToPhoto = toMetres.Inverse()!.Value;
        var outputToPhoto = metresToPhoto.After(outputToMetres);
        var photoToOutput = outputToPhoto.Inverse()!.Value;

        var raw = Perspective.Warp(raster, outputToPhoto, ow, oh, out var inside, maxThreads);
        var lit = WhiteBalance.Correct(raw, inside, maxThreads);
        var origin = new PixelPoint(-bx0 * s, -by0 * s);
        double mpp = 1 / s;

        var classes = Classify(raw, lit, inside, origin, wm * s, hm * s, s, maxThreads, out int highlights, out int bodies);
        var (set, classOf) = Clusters(classes, ow, oh);

        byte[] png = lit.EncodePng();
        string stem = Path.GetFileNameWithoutExtension(photo.OriginalName);
        var rectified = ImageSource.FromBytes(png, (stem.Length == 0 ? "photo" : stem) + ".rectified.png").Source!;

        var residuals = new double[4];
        double window = Math.Max(6, CornerWindowShare * Math.Min(wm, hm) * s);
        var rc = new[] { (0.0, 0.0), (wm, 0.0), (wm, hm), (0.0, hm) };
        for (int k = 0; k < 4; k++)
        {
            double px = origin.X + rc[k].Item1 * s, py = origin.Y + rc[k].Item2 * s;
            residuals[k] = CornerAt(lit, inside, px, py, window) is { } found
                ? Math.Sqrt((found.X - px) * (found.X - px) + (found.Y - py) * (found.Y - py)) * mpp
                : double.NaN;
        }

        string evidence = $"four corners on a {ImageScale.FormatLength(wm)} × {ImageScale.FormatLength(hm)} rectangle";
        return new BoardPhotoReading
        {
            Corners = corners, PhotoName = photo.OriginalName, PhotoSha256 = photo.Sha256, PhotoKind = ImageKind.Classify(raster),
            Rectified = rectified, Clusters = set, ClassOf = classOf, PhotoToRectified = photoToOutput,
            MetresPerPixel = mpp, Origin = origin, CornerResidualM = residuals,
            HighlightsFilled = highlights, UnreadParts = bodies,
            Scale = new ImageScaleCandidate(ImageScaleKind.Corners, mpp, 1, evidence),
        };
    }

    /// <summary>The trace's input reading <paramref name="reading"/>'s rectified photo instead of the photo as taken: the
    /// picture, its classes, and its scale. A scope drawn on the photo means nothing on the rectified picture and is
    /// dropped; a map the user edited is kept (the classes' colours are fixed, so it rebinds exactly).</summary>
    public static ImageTraceInput Apply(ImageTraceInput input, BoardPhotoReading reading)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(reading);
        if (!reading.Ok) throw new ArgumentException("A refused reading cannot be traced.", nameof(reading));
        return input with { Source = reading.Rectified!, Photo = reading, Scope = null };
    }

    /// <summary>R-im13-5's findings, added to a trace's report.</summary>
    internal static void Report(BoardPhotoReading r, RecognitionReport report)
    {
        string Res(double m) => double.IsNaN(m) ? "—" : Math.Round(m * 1e6).ToString("0", CultureInfo.InvariantCulture);
        report.Add(RecognitionFindingClass.ImagePhotoRectified, 1,
            $"Rectified on {r.Scale!.Evidence}; residual at the corners {string.Join(", ", r.CornerResidualM.Select(Res))} µm.");
        if (r.ClusterOf(BoardPhotoClass.Mask) < 0 && r.ClusterOf(BoardPhotoClass.CopperUnderMask) < 0)
            report.Add(RecognitionFindingClass.ImagePhotoRectified, 0, $"On this board {NoMaskNote}.");
        report.Add(RecognitionFindingClass.ImagePhotoHighlights, r.HighlightsFilled,
            $"{ImageTrace.Plural(r.HighlightsFilled, "highlight was", "highlights were")} filled from {(r.HighlightsFilled == 1 ? "its" : "their")} neighbourhood.");
        report.Add(RecognitionFindingClass.ImagePhotoUnreadParts, r.UnreadParts,
            $"{ImageTrace.Plural(r.UnreadParts, "part body covers", "part bodies cover")} {(r.UnreadParts == 1 ? "its" : "their")} pads and " +
            $"{(r.UnreadParts == 1 ? "was" : "were")} not read.");
        report.Add(RecognitionFindingClass.ImagePhotoWidths, 1,
            "Photographed widths are not good enough for impedance: line widths are read, and a Z0 from them is estimated.");
    }

    // ── geometry ──────────────────────────────────────────────────────────────────────────────────

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static double W(Homography h, double x, double y) => h.H6 * x + h.H7 * y + h.H8;

    /// <summary>Twice the signed area, y down: positive when the points go clockwise on the picture.</summary>
    private static double Shoelace(IReadOnlyList<(double X, double Y)> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var (x0, y0) = p[i];
            var (x1, y1) = p[(i + 1) % p.Count];
            a += x0 * y1 - x1 * y0;
        }
        return a;
    }

    /// <summary>
    /// The corner the picture shows near (x, y), sub-pixel (Förstner): the point every gradient in the window is
    /// perpendicular to the line to, iterated to a fixed point. Null when the window holds no corner — one edge
    /// direction only, or none — or the iteration leaves the window.
    /// </summary>
    internal static (double X, double Y)? CornerAt(RasterImage img, BinaryImage inside, double x, double y, double window)
    {
        int w = img.Width, h = img.Height;
        var p = img.Rgba;
        double Lum(int xx, int yy)
        {
            xx = Math.Clamp(xx, 0, w - 1);
            yy = Math.Clamp(yy, 0, h - 1);
            int o = (yy * w + xx) * 4;
            return 0.2126 * p[o] + 0.7152 * p[o + 1] + 0.0722 * p[o + 2];
        }
        double qx = x, qy = y;
        int r = (int)Math.Ceiling(window);
        for (int it = 0; it < 20; it++)
        {
            double a = 0, b = 0, c = 0, bx = 0, by = 0;
            int cx = (int)Math.Floor(qx), cy = (int)Math.Floor(qy);
            for (int yy = cy - r; yy <= cy + r; yy++)
                for (int xx = cx - r; xx <= cx + r; xx++)
                {
                    if ((uint)xx >= (uint)w || (uint)yy >= (uint)h || inside.Pixels[yy * w + xx] == 0) continue;
                    double px = xx + 0.5, py = yy + 0.5;
                    if ((px - qx) * (px - qx) + (py - qy) * (py - qy) > window * window) continue;
                    double gx = (Lum(xx + 1, yy) - Lum(xx - 1, yy)) / 2, gy = (Lum(xx, yy + 1) - Lum(xx, yy - 1)) / 2;
                    double gxx = gx * gx, gxy = gx * gy, gyy = gy * gy;
                    a += gxx; b += gxy; c += gyy;
                    bx += gxx * px + gxy * py;
                    by += gxy * px + gyy * py;
                }
            double det = a * c - b * b, tr = a + c;
            // Both edge directions present: the smaller eigenvalue a fair share of the larger.
            if (!(tr > 0) || det <= 0) return null;
            double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
            if ((tr / 2 - disc) < 0.05 * (tr / 2 + disc)) return null;
            double nx = (c * bx - b * by) / det, ny = (a * by - b * bx) / det;
            if ((nx - x) * (nx - x) + (ny - y) * (ny - y) > window * window) return null;
            bool done = Math.Abs(nx - qx) < 0.01 && Math.Abs(ny - qy) < 0.01;
            qx = nx;
            qy = ny;
            if (done) break;
        }
        return (qx, qy);
    }

    // ── classes (R-im13-4) ─────────────────────────────────────────────────────────────────────────

    private static byte[] Classify(RasterImage raw, RasterImage lit, BinaryImage inside, PixelPoint origin, double rectW, double rectH,
                                   double pxPerMetre, int maxThreads, out int highlights, out int bodies)
    {
        int w = lit.Width, h = lit.Height, n = w * h;
        var cls = new byte[n];   // OffBoard = 0
        var L = new float[n];
        var C = new float[n];
        var Hue = new float[n];
        var lp = lit.Rgba;
        Bands.For(h, maxThreads, (y0, y1) =>
        {
            for (int i = y0 * w; i < y1 * w; i++)
            {
                if (inside.Pixels[i] == 0) continue;
                int o = i * 4;
                var lab = CieLab.FromRgb(lp[o], lp[o + 1], lp[o + 2]);
                L[i] = (float)lab.L;
                C[i] = (float)Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
                double hue = Math.Atan2(lab.B, lab.A) * 180 / Math.PI;
                Hue[i] = (float)(hue < 0 ? hue + 360 : hue);
            }
        });

        // Highlights: clipped blobs in the photo as taken.
        var clipped = new BinaryImage(w, h);
        var rp = raw.Rgba;
        for (int i = 0; i < n; i++)
            if (inside.Pixels[i] != 0 && rp[i * 4] >= WhiteBalance.Clipped && rp[i * 4 + 1] >= WhiteBalance.Clipped && rp[i * 4 + 2] >= WhiteBalance.Clipped)
                clipped.Pixels[i] = 1;
        var dt = DistanceTransform.Compute(clipped, maxThreads);
        var blobs = Components.Label(clipped);
        var deep = new bool[blobs.Components.Count + 1];
        for (int i = 0; i < n; i++)
            if (blobs.Labels[i] > 0 && dt[i] >= HighlightMinHalfWidthPx) deep[blobs.Labels[i]] = true;
        var highlight = new BinaryImage(w, h);
        for (int i = 0; i < n; i++) if (blobs.Labels[i] > 0 && deep[blobs.Labels[i]]) highlight.Pixels[i] = 1;
        highlights = deep.Count(d => d);
        // The bloom around a highlight is not the surface's colour either.
        highlight = Morphology.Dilate(highlight, HighlightBloomPx, maxThreads);
        for (int i = 0; i < n; i++) if (inside.Pixels[i] == 0) highlight.Pixels[i] = 0;

        // The mask's hue: the peak of the chromatic, non-copper hues.
        var hist = new double[36];
        for (int i = 0; i < n; i++)
        {
            if (inside.Pixels[i] == 0 || highlight.Pixels[i] != 0 || C[i] < MaskMinChroma || IsCopperHue(Hue[i])) continue;
            hist[Math.Min(35, (int)(Hue[i] / 10))]++;
        }
        int peak = -1;
        double best = 0;
        for (int b = 0; b < 36; b++)
        {
            double v = hist[(b + 35) % 36] + hist[b] + hist[(b + 1) % 36];
            if (v > best) { best = v; peak = b; }
        }
        long insideCount = inside.Count();
        double maskHue = double.NaN;
        if (peak >= 0)
        {
            // The weighted mean hue over the peak's three bins, measured from the peak bin's centre.
            double centre = peak * 10 + 5, sum = 0, weight = 0;
            for (int d = -1; d <= 1; d++)
            {
                int b = (peak + d + 36) % 36;
                sum += hist[b] * (d * 10);
                weight += hist[b];
            }
            maskHue = (centre + sum / Math.Max(1, weight) + 360) % 360;
        }

        var family = new BinaryImage(w, h);
        long familyCount = 0;
        if (!double.IsNaN(maskHue))
            for (int i = 0; i < n; i++)
                if (inside.Pixels[i] != 0 && highlight.Pixels[i] == 0 && C[i] >= MaskFamilyMinChroma && HueDistance(Hue[i], maskHue) <= MaskHueHalfWidth)
                {
                    family.Pixels[i] = 1;
                    familyCount++;
                }
        bool hasMask = insideCount > 0 && familyCount >= MinMaskShare * insideCount;
        if (!hasMask) { family = new BinaryImage(w, h); familyCount = 0; }

        // The mask family's lighter half is copper under the mask (Otsu on L), when the halves are far enough apart.
        double splitL = double.MaxValue;
        if (hasMask)
        {
            var lh = new long[101];
            for (int i = 0; i < n; i++) if (family.Pixels[i] != 0) lh[Math.Clamp((int)Math.Round(L[i]), 0, 100)]++;
            int t = Otsu(lh, out double darkMean, out double lightMean, out double lightShare);
            if (lightMean - darkMean >= UnderMaskMinDeltaL && lightShare >= 0.005) splitL = t + 0.5;
        }

        // The board: the mask closed over small gaps, holes filled, with the rectangle.
        var board = new BinaryImage(w, h);
        if (hasMask)
        {
            // Opened first: a stray mask-hued pixel on the bench, closed with the board, would pull the bench in with it.
            var closed = Morphology.Close(Morphology.Open(family, MaskSpeckPx, maxThreads), Math.Max(1, BoardCloseM * pxPerMetre), maxThreads);
            var gaps = Components.Label(closed, foreground: false);
            for (int i = 0; i < n; i++)
            {
                int l = gaps.Labels[i];
                if (closed.Pixels[i] != 0 || (l > 0 && !gaps.Components[l - 1].TouchesBorder)) board.Pixels[i] = 1;
            }
        }
        int rx0 = (int)Math.Floor(origin.X), ry0 = (int)Math.Floor(origin.Y);
        int rx1 = (int)Math.Ceiling(origin.X + rectW), ry1 = (int)Math.Ceiling(origin.Y + rectH);
        for (int y = Math.Max(0, ry0); y < Math.Min(h, ry1); y++)
            for (int x = Math.Max(0, rx0); x < Math.Min(w, rx1); x++)
                board.Pixels[y * w + x] = 1;

        // Per pixel; bright near-grey pixels wait for the width test.
        var bright = new BinaryImage(w, h);
        for (int i = 0; i < n; i++)
        {
            if (inside.Pixels[i] == 0 || board.Pixels[i] == 0 || highlight.Pixels[i] != 0) continue;
            if (family.Pixels[i] != 0)
            {
                cls[i] = (byte)(L[i] >= splitL ? BoardPhotoClass.CopperUnderMask : BoardPhotoClass.Mask);
                continue;
            }
            if (C[i] >= CopperMinChroma && IsCopperHue(Hue[i])) { cls[i] = (byte)BoardPhotoClass.ExposedCopper; continue; }
            if (C[i] < GreyChroma)
            {
                if (L[i] < BodyMaxL) cls[i] = (byte)BoardPhotoClass.PartBody;
                else if (L[i] >= BrightMinL) bright.Pixels[i] = 1;
                else cls[i] = (byte)BoardPhotoClass.ExposedCopper;   // tin: mid-grey metal
                continue;
            }
            cls[i] = (byte)BoardPhotoClass.Mask;   // any other colour on the board: read as the board, not as copper
        }
        var wide = Morphology.Open(bright, Math.Max(1, PadMinWidthM * pxPerMetre / 2), maxThreads);
        for (int i = 0; i < n; i++)
            if (bright.Pixels[i] != 0) cls[i] = (byte)(wide.Pixels[i] != 0 ? BoardPhotoClass.ExposedCopper : BoardPhotoClass.Silkscreen);

        // Copper narrower than the narrowest line a board carries is the edge of something else — a mix of the mask and a
        // legend stroke, the board's edge against the bench — and is read as the mask.
        double copperOpen = Math.Max(1, MinCopperWidthM * pxPerMetre / 2);
        foreach (var copper in new[] { BoardPhotoClass.CopperUnderMask, BoardPhotoClass.ExposedCopper })
        {
            var m = new BinaryImage(w, h);
            for (int i = 0; i < n; i++) if (cls[i] == (byte)copper) m.Pixels[i] = 1;
            var kept = Morphology.Open(m, copperOpen, maxThreads);
            for (int i = 0; i < n; i++) if (m.Pixels[i] != 0 && kept.Pixels[i] == 0) cls[i] = (byte)BoardPhotoClass.Mask;
        }

        // A legend stroke is two or three pixels wide, and noise breaks it: closed over a pixel's gap, through the mask only.
        {
            var m = new BinaryImage(w, h);
            for (int i = 0; i < n; i++) if (cls[i] == (byte)BoardPhotoClass.Silkscreen) m.Pixels[i] = 1;
            var joined = Morphology.Close(m, SilkGapPx, maxThreads);
            for (int i = 0; i < n; i++)
                if (joined.Pixels[i] != 0 && cls[i] == (byte)BoardPhotoClass.Mask && highlight.Pixels[i] == 0) cls[i] = (byte)BoardPhotoClass.Silkscreen;
        }

        FillHighlights(cls, highlight, inside, w, h);

        // Part bodies big enough to be a part are reported; their pads are under them and are not read.
        var body = new BinaryImage(w, h);
        for (int i = 0; i < n; i++) if (cls[i] == (byte)BoardPhotoClass.PartBody) body.Pixels[i] = 1;
        double minSide = BodyMinM * pxPerMetre;
        bodies = Components.Label(body).Components.Count(c => c.Right - c.Left + 1 >= minSide && c.Bottom - c.Top + 1 >= minSide);
        return cls;
    }

    private static bool IsCopperHue(double hue) => hue >= CopperHueMin && hue <= CopperHueMax;

    private static double HueDistance(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Otsu's threshold over a 0…100 lightness histogram: the dark half is ≤ the result.</summary>
    private static int Otsu(long[] hist, out double darkMean, out double lightMean, out double lightShare)
    {
        long total = 0;
        double sumAll = 0;
        for (int i = 0; i < hist.Length; i++) { total += hist[i]; sumAll += i * (double)hist[i]; }
        int bestT = 0;
        double bestVar = -1, sumDark = 0;
        long dark = 0;
        darkMean = lightMean = lightShare = 0;
        for (int t = 0; t < hist.Length - 1; t++)
        {
            dark += hist[t];
            sumDark += t * (double)hist[t];
            long light = total - dark;
            if (dark == 0 || light == 0) continue;
            double md = sumDark / dark, ml = (sumAll - sumDark) / light;
            double between = (double)dark * light * (md - ml) * (md - ml);
            if (between > bestVar)
            {
                bestVar = between;
                bestT = t;
                darkMean = md;
                lightMean = ml;
                lightShare = (double)light / total;
            }
        }
        return bestT;
    }

    /// <summary>
    /// Fills each highlight pixel from the surface around the highlight. A class that CONTINUES across it — the rim class
    /// at both ends of a ray pair through the pixel (across, down, or either diagonal) — wins, copper first: a line under
    /// a glint runs on through it rather than being pinched where the glint's middle is equidistant from the line and the
    /// mask beside it. A pixel no pair agrees on takes its nearest rim class, by a breadth-first fill seeded in raster
    /// order, so the fill is the same every time.
    /// </summary>
    private static void FillHighlights(byte[] cls, BinaryImage highlight, BinaryImage inside, int w, int h)
    {
        var filled = new int[cls.Length];
        Array.Fill(filled, -1);
        (int Dx, int Dy)[] pairs = [(1, 0), (0, 1), (1, 1), (1, -1)];
        int? Rim(int x, int y, int dx, int dy)
        {
            for (int step = 1; ; step++)
            {
                int xx = x + step * dx, yy = y + step * dy;
                if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) return null;
                int j = yy * w + xx;
                if (inside.Pixels[j] == 0) return null;
                if (highlight.Pixels[j] == 0) return cls[j];
            }
        }
        for (int i = 0; i < cls.Length; i++)
        {
            if (highlight.Pixels[i] == 0) continue;
            int x = i % w, y = i / w, agreed = -1;
            foreach (var (dx, dy) in pairs)
            {
                if (Rim(x, y, dx, dy) is not { } a || Rim(x, y, -dx, -dy) is not { } b || a != b) continue;
                if (IsCopper(a)) { agreed = a; break; }
                if (agreed < 0) agreed = a;
            }
            filled[i] = agreed;
        }

        var queue = new Queue<int>();
        var done = new bool[cls.Length];
        for (int i = 0; i < cls.Length; i++)
        {
            if (highlight.Pixels[i] != 0)
            {
                if (filled[i] < 0) continue;
                cls[i] = (byte)filled[i];
            }
            else if (inside.Pixels[i] == 0) continue;
            done[i] = true;
            queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            void Visit(int j)
            {
                if (done[j] || highlight.Pixels[j] == 0) return;
                done[j] = true;
                cls[j] = cls[i];
                queue.Enqueue(j);
            }
            if (x > 0) Visit(i - 1);
            if (x < w - 1) Visit(i + 1);
            if (y > 0) Visit(i - w);
            if (y < h - 1) Visit(i + w);
        }

        static bool IsCopper(int c) => c is (int)BoardPhotoClass.ExposedCopper or (int)BoardPhotoClass.CopperUnderMask;
    }

    /// <summary>
    /// The classes as a cluster set: one cluster per class present, largest share first. Each pixel is a mix of its own
    /// class and the commonest other class among its eight neighbours, weighted by their count — a box filter of the
    /// hard labels, which puts a class's 0.5 coverage on the boundary between pixels and smooths a staircase into a line.
    /// </summary>
    private static (ColourClusterSet Set, IReadOnlyList<BoardPhotoClass> ClassOf) Clusters(byte[] cls, int w, int h)
    {
        const int K = 6;
        var count = new long[K];
        var border = new long[K];
        long borderTotal = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = cls[y * w + x];
                count[c]++;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) { border[c]++; borderTotal++; }
            }
        var present = Enumerable.Range(0, K).Where(c => count[c] > 0).OrderByDescending(c => count[c]).ThenBy(c => c).ToArray();
        var index = new byte[K];
        for (int r = 0; r < present.Length; r++) index[present[r]] = (byte)r;

        var a = new byte[w * h];
        var b = new byte[w * h];
        var t = new byte[w * h];
        Span<int> n9 = stackalloc int[K];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                n9.Clear();
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        n9[cls[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)]]++;
                int i = y * w + x, own = cls[i], other = -1;
                for (int c = 0; c < K; c++)
                    if (c != own && n9[c] > 0 && (other < 0 || n9[c] > n9[other])) other = c;
                a[i] = index[own];
                b[i] = other < 0 ? index[own] : index[other];
                t[i] = other < 0 ? (byte)0 : (byte)Math.Round(255.0 * n9[other] / 9);
            }

        double total = (double)w * h;
        var clusters = new List<ColourCluster>(present.Length);
        for (int r = 0; r < present.Length; r++)
        {
            int c = present[r], rgb = Rgb((BoardPhotoClass)c);
            clusters.Add(new ColourCluster(r, CieLab.FromRgb(rgb), rgb, count[c] / total, borderTotal == 0 ? 0 : border[c] / (double)borderTotal));
        }
        return (new ColourClusterSet(w, h, clusters, a, b, t), [.. present.Select(c => (BoardPhotoClass)c)]);
    }
}
