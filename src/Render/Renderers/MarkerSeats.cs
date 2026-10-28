// Markers too close to draw apart, grouped into seats — the one clustering both the Impedance report's maps
// (brief-impedance-9) and the layout canvas's Review markers (brief-review-6 R-rev6-5) use, so a dense board
// reads the same way on paper and on screen.

using SkiaSharp;

namespace CircuitRF.Render;

internal static class MarkerSeats
{
    /// <summary>
    /// Seats: taken in <paramref name="order"/> (the most important first), a marker anchored within
    /// <paramref name="reach"/> of a seat's lead joins that seat, otherwise it takes a seat of its own and leads it.
    /// Bounded by the seat, so a dense board is many badges of neighbours rather than one chained across the
    /// page. Each seat lists its lead and its members; the seats come back in the order of their first member.
    /// </summary>
    public static List<(int Lead, List<int> Members)> Group(IReadOnlyList<SKPoint> anchors, IEnumerable<int> order, float reach)
    {
        var seats = new List<(int Lead, List<int> Members)>();
        var grid = new Dictionary<(int, int), List<int>>();
        (int, int) Cell(SKPoint p) => ((int)MathF.Floor(p.X / reach), (int)MathF.Floor(p.Y / reach));
        foreach (int i in order)
        {
            var p = anchors[i];
            var (cx, cy) = Cell(p);
            int seat = -1;
            for (int gx = cx - 1; gx <= cx + 1 && seat < 0; gx++)
                for (int gy = cy - 1; gy <= cy + 1 && seat < 0; gy++)
                    if (grid.TryGetValue((gx, gy), out var near))
                        foreach (int k in near)
                            if (SKPoint.Distance(anchors[seats[k].Lead], p) <= reach) { seat = k; break; }
            if (seat >= 0) { seats[seat].Members.Add(i); continue; }
            seats.Add((i, [i]));
            (grid.TryGetValue((cx, cy), out var l) ? l : grid[(cx, cy)] = []).Add(seats.Count - 1);
        }
        return [.. seats.OrderBy(q => q.Members.Min())];
    }
}
