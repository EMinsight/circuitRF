// Where a label goes on a map page so it does not cover what it labels (round-10 field report on the
// impedance report's map: the trace ids and the numbered finding discs sat ON the traces, and on a dense
// board the labels hid the very copper the page reports on).
//
// Greedy, in the order labels are offered: each label tries positions at growing distances round its
// anchor, perpendicular to the trace first, and takes the first one that touches no trace stroke, no
// label already placed and nothing outside the frame. A label that is moved off its anchor is drawn
// with a thin leader back to it. When nothing is free — a board too dense for the page — it takes the
// candidate that covers the least, trace strokes counting far more than other labels: a label over a
// label is untidy, a label over a trace hides the result.
//
// Page units (points) throughout; the obstacles live in a uniform grid so a board of a few thousand
// stations stays a few hundred thousand tests, not millions.

using SkiaSharp;

namespace CircuitRF.Render;

internal sealed class MapLabelPlacer
{
    private readonly SKRect _frame;
    private readonly float _cell;
    private readonly List<(SKPoint A, SKPoint B, float Half)> _segments = [];
    private readonly List<SKRect> _rects = [];
    private readonly Dictionary<(int, int), List<int>> _segGrid = [];
    private readonly Dictionary<(int, int), List<int>> _rectGrid = [];

    /// <summary>A placed label: its box, and the anchor a leader runs to when the box was moved off it.</summary>
    public readonly record struct Placement(SKRect Box, SKPoint Anchor, bool NeedsLeader);

    public MapLabelPlacer(SKRect frame, float cell = 12f)
    {
        _frame = frame;
        _cell = Math.Max(2f, cell);
    }

    /// <summary>A stroke on the page — a trace — that no label may cover.</summary>
    public void AddSegment(SKPoint a, SKPoint b, float halfWidth)
    {
        int i = _segments.Count;
        _segments.Add((a, b, halfWidth));
        var box = new SKRect(Math.Min(a.X, b.X) - halfWidth, Math.Min(a.Y, b.Y) - halfWidth,
                             Math.Max(a.X, b.X) + halfWidth, Math.Max(a.Y, b.Y) + halfWidth);
        foreach (var key in Cells(box)) (_segGrid.TryGetValue(key, out var l) ? l : _segGrid[key] = []).Add(i);
    }

    /// <summary>Something already on the page that a later label may not cover.</summary>
    public void AddRect(SKRect r)
    {
        int i = _rects.Count;
        _rects.Add(r);
        foreach (var key in Cells(r)) (_rectGrid.TryGetValue(key, out var l) ? l : _rectGrid[key] = []).Add(i);
    }

    /// <summary>
    /// Places a <paramref name="width"/> × <paramref name="height"/> box near one of
    /// <paramref name="anchors"/> (tried in order, each with its own direction ALONG the trace, which
    /// may be zero), records it as an obstacle, and returns it. The box never lands on its anchor
    /// point itself: the anchor is on the trace.
    /// </summary>
    public Placement Place(IReadOnlyList<(SKPoint Anchor, SKPoint Along)> anchors, float width, float height, float gap = 1.5f)
    {
        Placement? best = null;
        double bestCost = double.MaxValue;
        foreach (var (anchor, along) in anchors)
        {
            // Perpendicular to the trace first (either side), then diagonals, then along it.
            float len = MathF.Sqrt(along.X * along.X + along.Y * along.Y);
            var d = len > 1e-6f ? new SKPoint(along.X / len, along.Y / len) : new SKPoint(1, 0);
            var n = new SKPoint(-d.Y, d.X);
            ReadOnlySpan<(float Nx, float Dy)> dirs =
                [(1, 0), (-1, 0), (1, 0.7f), (-1, 0.7f), (1, -0.7f), (-1, -0.7f), (0.35f, 1), (0.35f, -1), (-0.35f, 1), (-0.35f, -1)];
            foreach (float radius in (ReadOnlySpan<float>)[gap + 1.5f, gap + 4f, gap + 8f, gap + 13f, gap + 20f])
                foreach (var (a, b) in dirs)
                {
                    float vx = a * n.X + b * d.X, vy = a * n.Y + b * d.Y;
                    float vl = MathF.Sqrt(vx * vx + vy * vy);
                    vx /= vl; vy /= vl;
                    // The box's centre far enough along (vx, vy) that its near side is `radius` off the anchor.
                    float reach = radius + 0.5f * (Math.Abs(vx) * width + Math.Abs(vy) * height);
                    var c = new SKPoint(anchor.X + vx * reach, anchor.Y + vy * reach);
                    var box = new SKRect(c.X - 0.5f * width, c.Y - 0.5f * height, c.X + 0.5f * width, c.Y + 0.5f * height);
                    double cost = Cost(box);
                    if (cost == 0)
                        return Commit(new Placement(box, anchor, radius > gap + 2f));
                    // A farther spot is dearer only slightly: covering nothing is what matters.
                    cost += 0.01 * radius;
                    if (cost < bestCost) { bestCost = cost; best = new Placement(box, anchor, radius > gap + 2f); }
                }
        }
        return Commit(best ?? new Placement(new SKRect(0, 0, width, height), default, false));
    }

    private Placement Commit(Placement p)
    {
        AddRect(p.Box);
        return p;
    }

    /// <summary>0 when <paramref name="box"/> covers nothing; otherwise trace strokes weigh 100 each,
    /// placed labels 1 each, and leaving the frame 1000.</summary>
    internal double Cost(SKRect box)
    {
        if (box.Left < _frame.Left || box.Top < _frame.Top || box.Right > _frame.Right || box.Bottom > _frame.Bottom)
            return 1000;
        double cost = 0;
        var seenSeg = new HashSet<int>();
        var seenRect = new HashSet<int>();
        foreach (var key in Cells(box))
        {
            if (_segGrid.TryGetValue(key, out var segs))
                foreach (int i in segs)
                    if (seenSeg.Add(i) && SegmentHitsRect(_segments[i], box)) cost += 100;
            if (_rectGrid.TryGetValue(key, out var rects))
                foreach (int i in rects)
                    if (seenRect.Add(i) && _rects[i].IntersectsWith(box)) cost += 1;
        }
        return cost;
    }

    private IEnumerable<(int, int)> Cells(SKRect r)
    {
        int x0 = (int)MathF.Floor(r.Left / _cell), x1 = (int)MathF.Floor(r.Right / _cell);
        int y0 = (int)MathF.Floor(r.Top / _cell), y1 = (int)MathF.Floor(r.Bottom / _cell);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                yield return (x, y);
    }

    /// <summary>Whether a stroke of half-width h from a to b touches the box.</summary>
    internal static bool SegmentHitsRect((SKPoint A, SKPoint B, float Half) s, SKRect r) =>
        DistanceToRect(s, r) <= s.Half;

    private static float DistanceToRect((SKPoint A, SKPoint B, float Half) s, SKRect r)
    {
        if (r.Contains(s.A) || r.Contains(s.B) || SegmentCrossesRect(s.A, s.B, r)) return 0;
        float best = Math.Min(PointRect(s.A, r), PointRect(s.B, r));
        foreach (var corner in (ReadOnlySpan<SKPoint>)[new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)])
            best = Math.Min(best, PointSegment(corner, s.A, s.B));
        return best;
    }

    private static bool SegmentCrossesRect(SKPoint a, SKPoint b, SKRect r) =>
        Crosses(a, b, new(r.Left, r.Top), new(r.Right, r.Top)) || Crosses(a, b, new(r.Right, r.Top), new(r.Right, r.Bottom))
        || Crosses(a, b, new(r.Right, r.Bottom), new(r.Left, r.Bottom)) || Crosses(a, b, new(r.Left, r.Bottom), new(r.Left, r.Top));

    private static bool Crosses(SKPoint p1, SKPoint p2, SKPoint q1, SKPoint q2)
    {
        static float Cross(SKPoint o, SKPoint a, SKPoint b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        float d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
    }

    private static float PointRect(SKPoint p, SKRect r)
    {
        float dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        float dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float PointSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y;
        float l2 = vx * vx + vy * vy;
        float t = l2 <= 1e-12f ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / l2, 0, 1);
        float cx = a.X + t * vx - p.X, cy = a.Y + t * vy - p.Y;
        return MathF.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>The point of <paramref name="box"/> nearest <paramref name="p"/> — where a leader ends.</summary>
    public static SKPoint Nearest(SKRect box, SKPoint p) =>
        new(Math.Clamp(p.X, box.Left, box.Right), Math.Clamp(p.Y, box.Top, box.Bottom));
}
