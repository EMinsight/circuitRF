// Painting copper into a picture — the Fill tool, and convert's --fill.
//
// A datasheet often draws a board as a line drawing: each pour and pad is an outline, with white inside it, and the trace
// reads almost no copper from it (ImageTrace.LineArtNote). A Fill correction says "the area enclosed here is copper on
// this conductor". It is a paint bucket. Starting at the point clicked, it floods every pixel of the clicked colour
// connected to it. It then takes the near half of the drawn line around that area: a line pixel belongs to the area when
// the area is no farther from it than the colour beyond the line. The copper's edge, and each hole's, therefore lands on
// the line's centre line, where the drawing put it, just as a dark-rimmed hole is drilled on its rim's centre line. A
// round hole left inside the area (a drawn via) is then read as any round hole in copper is: a via, the copper whole
// under it.
//
// A Fill is a correction, not an edit to the picture. It is kept in the picture's pixels, undone and listed like every
// other correction, and it works the same on a picture pasted from the clipboard, which has no file to edit.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Layout.Recognition.Image;

public static partial class ImageTrace
{
    /// <summary>The widest drawn line a Fill takes half of, pixels each side.</summary>
    internal const int FillEdgePx = 4;

    public const string FillBackgroundRefusal =
        "That is the picture's background, reaching all four of its edges: click inside a closed outline.";

    /// <summary>The copper a Fill at <paramref name="seed"/> paints, as regions with their holes — empty when the point is
    /// off the picture. <paramref name="background"/> is set when the flood reaches all four of the picture's edges.</summary>
    internal static List<ContourRegion> FillRegions(ColourClusterSet clusters, PixelPoint seed, ContourOptions contour, double minArea,
                                                    out bool background)
    {
        background = false;
        int w = clusters.Width, h = clusters.Height;
        int sx = (int)Math.Floor(seed.X), sy = (int)Math.Floor(seed.Y);
        if (sx < 0 || sy < 0 || sx >= w || sy >= h) return [];
        int colour = clusters.Label(sx, sy);
        var label = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) label[y * w + x] = clusters.Label(x, y);

        // The clicked colour, 4-connected to the point.
        var inside = new bool[w * h];
        var queue = new Queue<int>();
        inside[sy * w + sx] = true;
        queue.Enqueue(sy * w + sx);
        bool left = false, right = false, top = false, bottom = false;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % w, y = i / w;
            left |= x == 0; right |= x == w - 1; top |= y == 0; bottom |= y == h - 1;
            foreach (int j in Neighbours(x, y, w, h))
                if (!inside[j] && label[j] == colour) { inside[j] = true; queue.Enqueue(j); }
        }
        if (left && right && top && bottom) { background = true; return []; }

        // The line round it, up to its centre: distance into the other colours from the area, and from the clicked colour
        // beyond it — a line pixel is the area's when the area is no farther.
        var fromInside = Distances(label, colour, w, h, i => inside[i]);
        var fromBeyond = Distances(label, colour, w, h, i => !inside[i] && label[i] == colour);
        // A pixel as far from both is the line's middle: half of it each side, so the contour crosses it half way.
        var v = new float[w * h];
        for (int i = 0; i < v.Length; i++)
            v[i] = inside[i] ? 1f
                 : fromInside[i] > FillEdgePx ? 0f
                 : fromInside[i] < fromBeyond[i] ? 1f
                 : fromInside[i] == fromBeyond[i] ? 0.5f : 0f;
        return Despeckle(Contours.Trace(new CoverageMap(w, h, v), contour), minArea, out _);
    }

    /// <summary>Steps from the pixels <paramref name="from"/> picks into those of any other colour than
    /// <paramref name="colour"/>, up to <see cref="FillEdgePx"/>; farther is <see cref="int.MaxValue"/>.</summary>
    private static int[] Distances(int[] label, int colour, int w, int h, Func<int, bool> from)
    {
        var d = new int[w * h];
        Array.Fill(d, int.MaxValue);
        var queue = new Queue<int>();
        for (int i = 0; i < d.Length; i++)
            if (from(i)) { d[i] = 0; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (d[i] >= FillEdgePx) continue;
            foreach (int j in Neighbours(i % w, i / w, w, h))
                if (d[j] == int.MaxValue && label[j] != colour) { d[j] = d[i] + 1; queue.Enqueue(j); }
        }
        return d;
    }

    /// <summary>Whether <paramref name="p"/> is in <paramref name="region"/>'s copper: inside its outline, in none of its holes.</summary>
    internal static bool Covers(ContourRegion region, PointD p) =>
        Clipper.PointInPolygon(p, region.Outer) != PointInPolygonResult.IsOutside
        && region.Holes.All(h => Clipper.PointInPolygon(p, h) != PointInPolygonResult.IsInside);

    /// <summary>The width of the painted strip through <paramref name="p"/>, pixels, from its outline itself: the runs of
    /// copper along the row and the column through the point, <c>W = h·v / √(h² + v²)</c> as <c>ImageScale.LocalWidth</c>
    /// takes them from coverage. Counted in whole pixels a painted line lost the half pixel at each edge — 11 px for a line
    /// drawn 12 px between its centre lines.</summary>
    internal static double PaintedWidth(IReadOnlyList<ContourRegion> regions, PointD p)
    {
        var paths = regions.SelectMany(r => r.Paths).ToList();
        double Run(bool across)
        {
            var hits = new List<double>();
            foreach (var path in paths)
                for (int i = 0; i < path.Count; i++)
                {
                    var (a, b) = (path[i], path[(i + 1) % path.Count]);
                    var (ca, cb, at) = across ? (a.y, b.y, p.y) : (a.x, b.x, p.x);
                    if ((ca > at) == (cb > at)) continue;
                    double t = (at - ca) / (cb - ca);
                    hits.Add(across ? a.x + t * (b.x - a.x) : a.y + t * (b.y - a.y));
                }
            hits.Sort();
            double here = across ? p.x : p.y;
            for (int k = 0; k + 1 < hits.Count; k += 2)
                if (hits[k] <= here && here <= hits[k + 1]) return hits[k + 1] - hits[k];
            return 0;
        }
        double h = Run(across: true), v = Run(across: false);
        return h > 0 && v > 0 ? h * v / Math.Sqrt(h * h + v * v) : 0;
    }

    private static IEnumerable<int> Neighbours(int x, int y, int w, int h)
    {
        if (x > 0) yield return y * w + x - 1;
        if (x < w - 1) yield return y * w + x + 1;
        if (y > 0) yield return (y - 1) * w + x;
        if (y < h - 1) yield return (y + 1) * w + x;
    }
}
