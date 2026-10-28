// brief-review-6 R-rev6-5 — the Layout Review's finding markers over the artwork.
//
// Drawn by DrawFindingMarkers, the DRC and LVS markers' own routine (its hairline crosshair included), in the
// review's colour. What is added here is the CLUSTERING: a board can give a check hundreds of findings, and at
// board zoom most of them are a few pixels across and land on one another, so markers whose centres fall
// within a badge's width of each other are drawn as ONE pill carrying their count — seated by MarkerSeats, the
// clustering the Impedance report's maps use. A marker big enough on screen to be read as a region is drawn
// as one whatever its neighbours, and the selected marker is never folded into a badge: it is the one the
// reviewer just asked to see.

using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    /// <summary>A marker at least this large on screen in both directions is drawn as its region, never seated.</summary>
    private const double ReviewRegionDevicePixels = 24.0;

    /// <summary>Two small markers whose centres are within this many device pixels share a badge.</summary>
    internal const float ReviewClusterReachDevicePixels = 14f;

    private const float ReviewBadgeHeightDevicePixels = 14f;
    private const float ReviewBadgeFontDevicePixels   = 9.5f;
    private const float ReviewLineDevicePixels        = 1.6f;

    internal static void DrawReviewMarkers(
        SKCanvas canvas, IReadOnlyList<ReviewFindingMarker> markers, LayoutRenderTheme theme,
        PathSpace ps, double scaleUm)
    {
        var (singles, badges) = SeatReviewMarkers(markers, ps, scaleUm);

        DrawFindingMarkers(canvas, singles.Select(i => (markers[i].Rings,
            markers[i].Accepted ? theme.DrcWaived : theme.ReviewFinding, markers[i].Accepted, markers[i].Selected)), ps, scaleUm);

        // A two-point ring is a line (a pad to its nearest via); the shared routine draws regions only.
        using var line = new SKPaint { IsStroke = true, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        foreach (int i in singles)
            foreach (var ring in markers[i].Rings)
                if (ring.Length == 4)
                {
                    line.Color = (markers[i].Accepted ? theme.DrcWaived : theme.ReviewFinding).WithAlpha(DrcOutlineAlpha);
                    line.StrokeWidth = DevicePixelsToPathSpace(scaleUm, markers[i].Selected ? DrcSelectedStrokeDevicePixels : ReviewLineDevicePixels);
                    canvas.DrawLine(ps.X(ring[0]), ps.Y(ring[1]), ps.X(ring[2]), ps.Y(ring[3]), line);
                }

        foreach (var (x, y, count, accepted) in badges)
            DrawReviewBadge(canvas, x, y, count, accepted ? theme.DrcWaived : theme.ReviewFinding, scaleUm);
    }

    /// <summary>
    /// Which markers draw on their own and which become badges at this zoom: a badge's centre in path space,
    /// how many findings it stands for, and whether every one of them is accepted. What the canvas draws, and
    /// what a test counts.
    /// </summary>
    internal static (List<int> Singles, List<(float X, float Y, int Count, bool Accepted)> Badges) SeatReviewMarkers(
        IReadOnlyList<ReviewFindingMarker> markers, PathSpace ps, double scaleUm)
    {
        var singles = new List<int>();
        var small = new List<int>();
        var centres = new List<SKPoint>();   // device pixels, for the seating
        var path = new List<SKPoint>();      // path space, where a badge is drawn
        for (int i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var ring in m.Rings)
                for (int k = 0; k + 1 < ring.Length; k += 2)
                {
                    float x = ps.X(ring[k]), y = ps.Y(ring[k + 1]);
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            if (double.IsInfinity(minX)) continue;
            bool large = (maxX - minX) * scaleUm >= ReviewRegionDevicePixels && (maxY - minY) * scaleUm >= ReviewRegionDevicePixels;
            if (m.Selected || large) { singles.Add(i); continue; }
            small.Add(i);
            float cx = (float)((minX + maxX) / 2), cy = (float)((minY + maxY) / 2);
            path.Add(new SKPoint(cx, cy));
            centres.Add(new SKPoint((float)(cx * scaleUm), (float)(cy * scaleUm)));
        }

        // An open finding leads a seat before an accepted one, so a badge sits where there is something to do.
        var order = Enumerable.Range(0, small.Count).OrderBy(j => markers[small[j]].Accepted ? 1 : 0).ThenBy(j => j);
        var badges = new List<(float, float, int, bool)>();
        foreach (var (lead, members) in MarkerSeats.Group(centres, order, ReviewClusterReachDevicePixels))
        {
            if (members.Count == 1) { singles.Add(small[lead]); continue; }
            badges.Add((path[lead].X, path[lead].Y, members.Count, members.All(j => markers[small[j]].Accepted)));
        }
        return (singles, badges);
    }

    /// <summary>A pill carrying the count, drawn in device pixels at a path-space point — the report's badge on
    /// screen: filled in the finding colour with white figures, grey when every finding in it is accepted.</summary>
    private static void DrawReviewBadge(SKCanvas canvas, float x, float y, int count, SKColor colour, double scaleUm)
    {
        canvas.Save();
        canvas.Translate(x, y);
        float s = (float)(1.0 / Math.Max(scaleUm, 1e-12));
        canvas.Scale(s, s);

        using var font = new SKFont(SkiaFonts.PlexSemiBold, ReviewBadgeFontDevicePixels);
        string label = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        float h = ReviewBadgeHeightDevicePixels;
        float w = Math.Max(h, font.MeasureText(label) + 8f);
        var r = new SKRect(-w / 2, -h / 2, w / 2, h / 2);
        using var fill = new SKPaint { IsAntialias = true, Color = colour.WithAlpha(DrcOutlineAlpha) };
        using var ring = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 1f, Color = SKColors.White };
        using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawRoundRect(r, h / 2, h / 2, fill);
        canvas.DrawRoundRect(r, h / 2, h / 2, ring);
        canvas.DrawText(label, 0, 0.35f * ReviewBadgeFontDevicePixels, SKTextAlign.Center, font, ink);
        canvas.Restore();
    }
}
