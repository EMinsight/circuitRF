namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Alt + double-click on a wire: the run of wiring it belongs to, up to the component pins (owner
/// request, 2026-10-01). Plain double-click still opens the net-label editor.
///
/// <para><b>What a run is.</b> Every wire segment is cut at its junctions (<see
/// cref="SchematicEditModel.JunctionsOnSegment"/>: another wire's vertex, a pin, a dot, a label's foot)
/// into pieces. From the clicked piece the run spreads to every piece sharing an end point — through
/// corners, T-junctions and wire-to-wire joins, in every direction — until the wiring ends at the
/// component pins. A pin never blocks it: wires meeting at a pin are one run (owner, PowerAmplifier.csch:
/// the three wires on C3's top pin), and a pin tapping a wire mid-span does not cut it (bad_drag.csch).
/// What stops the run is a PART: its two pins are different points, so the wiring on its far side is
/// another run. A crossing with no vertex is not a join, as everywhere else in the editor. Net labels connect by NAME, not by copper, so the run never jumps from one
/// label to another of the same name.</para>
///
/// <para><b>What is selected.</b> Segment selections, so Delete and the selection overlay treat the
/// run exactly as they treat segments clicked one at a time: a segment wholly in the run is selected
/// whole; one the run covers only part of carries that stretch as its span. A segment reached from
/// from both sides (only possible round a loop) takes the stretch spanning both.</para>
/// </summary>
public static class WireRunSelector
{
    /// <summary>One selected segment: its wire, its index, and the stretch selected (null: all of it).</summary>
    public readonly record struct RunSegment(
        string WireId, int SegmentIndex, ((double X, double Y) A, (double X, double Y) B)? Span);

    private readonly record struct Piece(EditableWire Wire, int Segment, (double X, double Y) A, (double X, double Y) B);

    private const double Tol = SchematicEditModel.ConnectTolerance;

    private static (long, long) Key((double X, double Y) p)
        => ((long)Math.Round(p.X / Tol), (long)Math.Round(p.Y / Tol));

    /// <summary>
    /// The run through the segment of <paramref name="wire"/> nearest (<paramref name="px"/>,
    /// <paramref name="py"/>). Empty when the wire has no segment.
    /// </summary>
    public static IReadOnlyList<RunSegment> RunAt(SchematicEditModel model, EditableWire wire, double px, double py)
    {
        if (wire.Points.Count < 2) return [];

        // Every segment of every wire, cut at its junctions.
        var pieces = new List<Piece>();
        foreach (var w in model.Wires)
            for (int i = 0; i < w.Points.Count - 1; i++)
            {
                var stops = new List<(double X, double Y)> { w.Points[i] };
                stops.AddRange(model.JunctionsOnSegment(w, i));
                stops.Add(w.Points[i + 1]);
                for (int k = 0; k < stops.Count - 1; k++)
                    if (Key(stops[k]) != Key(stops[k + 1]))
                        pieces.Add(new Piece(w, i, stops[k], stops[k + 1]));
            }

        var byEnd = new Dictionary<(long, long), List<int>>();
        for (int n = 0; n < pieces.Count; n++)
            foreach (var end in new[] { pieces[n].A, pieces[n].B })
            {
                if (!byEnd.TryGetValue(Key(end), out var list)) byEnd[Key(end)] = list = [];
                list.Add(n);
            }

        // The clicked piece: the nearest piece of the clicked wire.
        int start = -1;
        double best = double.MaxValue;
        for (int n = 0; n < pieces.Count; n++)
        {
            if (!ReferenceEquals(pieces[n].Wire, wire)) continue;
            double d = DistanceSq(px, py, pieces[n].A, pieces[n].B);
            if (d < best) { best = d; start = n; }
        }
        if (start < 0) return [];

        var inRun = new HashSet<int> { start };
        var queue = new Queue<int>([start]);
        while (queue.Count > 0)
        {
            var p = pieces[queue.Dequeue()];
            foreach (var end in new[] { p.A, p.B })
            {
                // A pin never blocks the run: wires meeting at a pin are joined there like any other
                // wires (owner, PowerAmplifier.csch: three wires on C3's top pin are one run), and a pin
                // tapping a wire mid-span does not cut it (bad_drag.csch). The run ends where the
                // wiring ends — at the pins with no further wire beyond them.
                foreach (int next in byEnd[Key(end)])
                    if (inRun.Add(next)) queue.Enqueue(next);
            }
        }

        var result = new List<RunSegment>();
        foreach (var group in inRun.Select(n => pieces[n]).GroupBy(p => (p.Wire, p.Segment)))
        {
            var (w, i) = group.Key;
            var a = w.Points[i];
            var b = w.Points[i + 1];
            double T((double X, double Y) q)
                => Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y)
                    ? (q.X - a.X) / (b.X - a.X)
                    : (q.Y - a.Y) / (b.Y - a.Y);
            var ends = group.SelectMany(p => new[] { p.A, p.B }).ToList();
            var lo = ends.MinBy(T);
            var hi = ends.MaxBy(T);
            bool whole = Key(lo) == Key(a) && Key(hi) == Key(b);
            result.Add(new RunSegment(w.Id, i, whole ? null : (lo, hi)));
        }
        return result;
    }

    private static double DistanceSq(double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        double t = len < 1e-12 ? 0 : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / len, 0, 1);
        double cx = a.X + t * dx - px, cy = a.Y + t * dy - py;
        return cx * cx + cy * cy;
    }
}
