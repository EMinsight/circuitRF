// A plot's curves, followed — brief-img-14-sparameter-plots-to-touchstone.md R-im14-5.
//
// SEPARATION BY COLOUR. The plotting area's colours are clustered (IM-1's ColourClusters). The background, the grid
// (a cluster lying mostly on the frame's grid lines, or on a chart's constant-r circles), and the text (a cluster lying
// mostly inside words) are set aside; every other cluster is a trace candidate. Inside a candidate's coverage, words,
// the legend box, excluded rectangles and — when a trace is drawn in the frame's own colour — the frame's lines are
// cleared, so a black trace in a black-framed plot is not read along its axis.
//
// A RECTANGULAR TRACE is a function of x. Each pixel column's coverage is cut into runs; a run's coverage-weighted
// centre is the trace's y there, to a fraction of a pixel. Runs are linked into tracks column by column, each track
// predicting its next y from its recent slope. Two tracks reaching one run are crossing: both take it, their slopes
// frozen, and when the runs part again each takes the run its frozen slope points at — the continuation with the
// smaller change of direction (R-im14-5). A parting where both pairings fit about equally is AMBIGUOUS, reported and
// drawn amber. That one mechanism separates two traces of one colour drawn in different line styles, and the run-length
// pattern along each track says which style it is.
//
// GAPS: a track missing for up to GapShare of the axis — under another trace, a marker, a label, or between a dashed
// line's dashes — is bridged and counted; a longer gap is left as a gap and reported. Nothing is extrapolated.
//
// A SMITH OR POLAR TRACE is a parametric curve: the candidate's coverage is skeletonised and walked end to end, at each
// junction continuing along the branch that turns least.

using CircuitRF.Design.Schematic.Recognition;
using Clipper2Lib;

namespace CircuitRF.Design.Imaging.Plots;

public enum TraceStyle { Solid, Dashed, Dotted }

/// <summary>One curve read off a plot, in pixels (R-im14-5).</summary>
/// <param name="Plot">The frame's index on its picture.</param>
/// <param name="Rgb">Its colour, 0xRRGGBB.</param>
/// <param name="Points">Its centre line: one point per pixel column for a rectangular trace (ascending x), the walk's
/// points in order for a chart's.</param>
/// <param name="Bridged">Gaps bridged (no longer than <see cref="PlotTraces.GapShare"/> of the axis).</param>
/// <param name="Gaps">Gaps left open, as pixel x ranges.</param>
/// <param name="Ambiguous">Where a crossing stayed ambiguous, pixels.</param>
/// <param name="Width">Its line width, pixels.</param>
public sealed record TraceCurve(int Plot, int Rgb, TraceStyle Style, bool Parametric, IReadOnlyList<(double X, double Y)> Points,
                                int Bridged, IReadOnlyList<(double From, double To)> Gaps, IReadOnlyList<(double X, double Y)> Ambiguous,
                                double Width)
{
    /// <summary>The colour as <c>#rrggbb</c> — how <c>--trace</c> and <c>--freq</c> name it.</summary>
    public string Hex => $"#{Rgb:x6}";

    /// <summary>It was followed from a click (R-im14-5).</summary>
    public bool Followed { get; init; }

    public string StyleName => Style.ToString().ToLowerInvariant();
}

/// <summary>What the user told the trace pass about one plot.</summary>
/// <param name="Follow">Clicks on curves the automatic pass missed.</param>
/// <param name="Exclude">Rectangles to read nothing in (a legend, an annotation).</param>
public sealed record TraceHints(IReadOnlyList<(double X, double Y)> Follow, IReadOnlyList<PixelBox> Exclude)
{
    public static TraceHints None { get; } = new([], []);
}

public static class PlotTraces
{
    /// <summary>A gap up to this share of the axis is bridged by interpolation (R-im14-5).</summary>
    public const double GapShare = 0.02;

    /// <summary>A track shorter than this share of the axis is debris (a legend sample, a glyph), not a trace.</summary>
    public const double MinTrackShare = 0.05;

    /// <summary>The r (and x) values a Smith chart's grid is commonly drawn at.</summary>
    private static readonly double[] SmithGridValues = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1, 1.2, 1.4, 1.6, 1.8, 2, 3, 4, 5, 10, 20, 50];

    /// <summary>A curve this much of whose length lies along a longer one is that one read twice.</summary>
    public const double MergeShare = 0.8;

    /// <summary>Colours this close (per channel) can be one line read twice: a JPEG line's core and its body.</summary>
    internal const int MergeColour = 64;

    /// <summary>Coverage above this is the trace.</summary>
    public const float MinCoverage = 0.3f;

    /// <summary>The traces of <paramref name="frame"/> on <paramref name="ink"/>. Pure.</summary>
    public static IReadOnlyList<TraceCurve> Find(PlotInk ink, PlotFrame frame, IReadOnlyList<PlotWord> words, IReadOnlyList<PixelBox> legend,
                                                 TraceHints? hints = null)
    {
        ArgumentNullException.ThrowIfNull(ink);
        ArgumentNullException.ThrowIfNull(frame);
        hints ??= TraceHints.None;
        var box = frame.Box;
        int x0 = Math.Max(0, box.Left - 2), y0 = Math.Max(0, box.Top - 2);
        int x1 = Math.Min(ink.Width - 1, box.Right + 2), y1 = Math.Min(ink.Height - 1, box.Bottom + 2);
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        var crop = Crop(ink.Raster, x0, y0, cw, ch);
        var clusters = ColourClusters.Find(crop, 8);
        int background = Nearest(clusters, ink.Background);

        // Where nothing is read: words, the legend, the user's rectangles.
        var cleared = new bool[cw * ch];
        void Clear(PixelBox b, int pad, int? rgb = null)
        {
            for (int y = Math.Max(y0, b.Top - pad); y <= Math.Min(y1, b.Bottom + pad); y++)
                for (int x = Math.Max(x0, b.Left - pad); x <= Math.Min(x1, b.Right + pad); x++)
                    if (rgb is not { } c || PlotInk.Between(ink.Raster.Rgb(x, y), c, ink.Background))
                        cleared[(y - y0) * cw + (x - x0)] = true;
        }
        // A word clears only its own ink: a marker's label standing on a curve must not cut the curve. Inside the plotting
        // area only a word that READ clears anything: the dashes of a steep dashed curve are word-sized pieces too, and an
        // unread "word" there is far more often a curve than text.
        foreach (var w in words)
            if ((w.Box.Width < 0.5 * (box.Width) || w.Box.Height < 0.5 * box.Height)
                && (w.Read || !frame.Inside(w.Box.CentreX, w.Box.CentreY)))
                Clear(w.Box, 1, w.Rgb);
        foreach (var b in legend) Clear(b, 2);
        foreach (var b in hints.Exclude) Clear(b, 0);

        // Each cluster's pixels inside the plotting area: how many, how many on the frame's lines, how many in words.
        bool Inside(int x, int y) => frame.Inside(x + 0.5, y + 0.5, frame.Pen + 0.5);
        var onLine = LineMask(frame, x0, y0, cw, ch);
        int k = clusters.Clusters.Count;
        var count = new int[k];
        var lineCount = new int[k];
        var textCount = new int[k];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                if (!Inside(x + x0, y + y0)) continue;
                int c = clusters.Label(x, y);
                count[c]++;
                if (onLine[y * cw + x]) lineCount[c]++;
                if (cleared[y * cw + x]) textCount[c]++;
            }
        double axis = frame.IsChart ? 2 * frame.Radius : frame.Right - frame.Left;
        int minPixels = (int)Math.Max(12, MinTrackShare * axis);

        // A cluster that is a blend of another's colour with the background is that cluster's anti-aliased fringe.
        int FringeOf(int c) => Enumerable.Range(0, k).FirstOrDefault(o => o != c && o != background && count[o] > count[c]
            && PlotInk.Distance(clusters.Clusters[o].Rgb, clusters.Clusters[c].Rgb) >= 24
            && PlotInk.Between(clusters.Clusters[c].Rgb, clusters.Clusters[o].Rgb, ink.Background)
            && PlotInk.Distance(clusters.Clusters[c].Rgb, ink.Background) < PlotInk.Distance(clusters.Clusters[o].Rgb, ink.Background), -1);
        bool Fringe(int c) => FringeOf(c) >= 0;

        // A click names the cluster under it — and a click on a curve's anti-aliased edge names the curve, since a fringe
        // is never traced itself (brief-pdg-4-digitise-by-hand.md R-pdg4-2).
        var followed = new HashSet<int>();
        foreach (var (fx, fy) in hints.Follow)
        {
            int c = NearestInk(clusters, background, (int)fx - x0, (int)fy - y0);
            if (c >= 0) followed.Add(FringeOf(c) is var parent and >= 0 ? parent : c);
        }

        bool Candidate(int c)
        {
            if (c == background || count[c] == 0 || Fringe(c)) return false;
            // A chart's grid is uncoloured: a coloured trace may lie exactly on a grid circle (a series R of 20 Ω on
            // a 50 Ω chart IS the r = 0.4 circle) and is still the trace.
            bool grid = frame.IsChart ? !PlotText.Chromatic(clusters.Clusters[c].Rgb) && lineCount[c] >= 0.4 * count[c]
                                      : lineCount[c] >= 0.5 * count[c];
            bool text = textCount[c] >= 0.5 * count[c];
            bool faint = PlotInk.Distance(clusters.Clusters[c].Rgb, ink.Background) <= PlotInk.InkLevels;
            return !grid && !text && !faint && count[c] - textCount[c] >= minPixels;
        }
        var candidateOf = Enumerable.Range(0, k).Select(Candidate).ToArray();

        var curves = new List<TraceCurve>();
        for (int c = 0; c < k; c++)
        {
            if (c == background || count[c] == 0 || Fringe(c)) continue;
            bool candidate = candidateOf[c];
            if (!candidate && !followed.Contains(c)) continue;

            var cov = clusters.Coverage(c).Values;
            int rgb = clusters.Clusters[c].Rgb;
            // A like-coloured cluster lying inside this one — a JPEG line's darker core — is the same line: traced
            // apart, the core is a hole that cuts each column's run in two, and the halves are followed as parallel
            // tracks a pen apart. Traced together they are one run, centred where the line is.
            for (int o = 0; o < k; o++)
                if (o != c && o != background && !Fringe(o) && candidateOf[o] && PlotInk.Distance(clusters.Clusters[o].Rgb, rgb) <= MergeColour)
                {
                    var more = clusters.Coverage(o).Values;
                    for (int i = 0; i < cov.Length; i++) cov[i] = Math.Min(1, cov[i] + more[i]);
                }
            // A trace in a frame line's own colour loses THOSE lines — a black curve on a black frame's axes, never on
            // its light grid, which it would be cut along wherever it runs level with a grid line; any other trace keeps
            // every pixel it covers.
            bool[]? own = frame.Lines.Any(l => PlotInk.Distance(l.Rgb, rgb) < 48)
                ? LineMask(frame, x0, y0, cw, ch, l => PlotInk.Distance(l.Rgb, rgb) < 48) : null;
            for (int i = 0; i < cov.Length; i++)
            {
                int x = i % cw + x0, y = i / cw + y0;
                if (cleared[i] || !Inside(x, y) || (own is not null && own[i])) cov[i] = 0;
            }
            var found = frame.IsChart
                ? Parametric(cov, cw, ch, x0, y0, frame, rgb)
                : Functions(cov, cw, ch, x0, y0, frame, rgb);
            foreach (var curve in found)
            {
                bool clicked = followed.Contains(c) && hints.Follow.Any(p => curve.Points.Any(q => Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y) < 6));
                if (!candidate && !clicked) continue;
                curves.Add(curve with { Followed = !candidate });
            }
        }
        curves = MergeAlongside(curves, frame);
        return [.. curves.OrderBy(c => c.Points.Count == 0 ? 0 : c.Points.Average(p => p.Y)).ThenBy(c => c.Rgb)];
    }

    private static RasterImage Crop(RasterImage r, int x0, int y0, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(r.Rgba, ((y + y0) * r.Width + x0) * 4, rgba, y * w * 4, w * 4);
        return new RasterImage(w, h, rgba);
    }

    private static int Nearest(ColourClusterSet set, int rgb)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < set.Clusters.Count; i++)
        {
            int d = PlotInk.Distance(set.Clusters[i].Rgb, rgb);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static int NearestInk(ColourClusterSet set, int background, int x, int y)
    {
        for (int r = 0; r <= 4; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if ((uint)xx >= (uint)set.Width || (uint)yy >= (uint)set.Height) continue;
                    int c = set.Label(xx, yy);
                    if (c != background) return c;
                }
        return -1;
    }

    /// <summary>The frame's own lines (those <paramref name="keep"/> keeps), and for a chart the circles it was checked
    /// against, as a mask of the crop.</summary>
    private static bool[] LineMask(PlotFrame frame, int x0, int y0, int w, int h, Func<PlotLine, bool>? keep = null)
    {
        var m = new bool[w * h];
        foreach (var l in frame.Lines.Where(keep ?? (_ => true)))
        {
            int c0 = (int)Math.Floor(l.Position - l.Thickness / 2.0 - 1), c1 = (int)Math.Ceiling(l.Position + l.Thickness / 2.0 + 1);
            for (int a = l.From; a <= l.To; a++)
                for (int c = c0; c <= c1; c++)
                {
                    int x = (l.Horizontal ? a : c) - x0, y = (l.Horizontal ? c : a) - y0;
                    if ((uint)x < (uint)w && (uint)y < (uint)h) m[y * w + x] = true;
                }
        }
        if (frame.IsChart)
        {
            // Every grid a chart is commonly drawn with: a Smith chart's constant-r circles and constant-x arcs at the
            // usual values (mirrored for an admittance chart), a polar chart's rings and its radials every 15°. A
            // cluster lying mostly on these is the grid, whatever subset of them this chart draws.
            double win = Math.Max(2, 0.008 * frame.Radius) + 1, R = frame.Radius, sign = frame.Admittance ? -1 : 1;
            var circles = new List<(double Cx, double Cy, double R)>();
            if (frame.Type == PlotType.Smith)
                foreach (double v in SmithGridValues)
                {
                    circles.Add((frame.Cx + sign * R * v / (1 + v), frame.Cy, R / (1 + v)));
                    circles.Add((frame.Cx + sign * R, frame.Cy - R / v, R / v));
                    circles.Add((frame.Cx + sign * R, frame.Cy + R / v, R / v));
                }
            else
                for (int k = 1; k <= 10; k++) circles.Add((frame.Cx, frame.Cy, R * k / 10.0));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double px = x + x0 + 0.5, py = y + y0 + 0.5;
                    double dx0 = px - frame.Cx, dy0 = py - frame.Cy;
                    if (dx0 * dx0 + dy0 * dy0 > R * R) continue;
                    foreach (var (cx, cy, r) in circles)
                        if (Math.Abs(Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - r) <= win) { m[y * w + x] = true; break; }
                    if (frame.Type == PlotType.Polar && !m[y * w + x])
                    {
                        double ang = Math.Atan2(-dy0, dx0) * 180 / Math.PI, rr = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
                        double off = Math.Abs(Math.IEEERemainder(ang, 15)) * Math.PI / 180 * rr;
                        if (off <= win) m[y * w + x] = true;
                    }
                }
        }
        return m;
    }

    // ── rectangular: functions of x ─────────────────────────────────────────────────────────────────────────

    private sealed class Track
    {
        public readonly List<(double X, double Y)> Points = [];
        public double Slope;
        public Track? Partner;       // in a crossing: the track whose run it shares
        public double LastX => Points[^1].X;
        public double LastY => Points[^1].Y;
        public double Predict(double x) => LastY + Slope * (x - LastX);
    }

    private readonly record struct Run(double Centre, int Top, int Bottom);

    private static List<TraceCurve> Functions(float[] cov, int w, int h, int x0, int y0, PlotFrame frame, int rgb)
    {
        int left = (int)Math.Ceiling(frame.Left + frame.Pen) - x0, right = (int)Math.Floor(frame.Right - frame.Pen) - x0;
        double axis = frame.Right - frame.Left;
        int maxGap = Math.Max(3, (int)Math.Round(GapShare * axis));

        var columns = new List<Run>[w];
        var lengths = new List<int>();
        for (int x = Math.Max(0, left); x <= Math.Min(w - 1, right); x++)
        {
            var runs = new List<Run>();
            for (int y = 0; y < h;)
            {
                if (cov[y * w + x] < MinCoverage) { y++; continue; }
                int s = y;
                double sw = 0, sy = 0;
                // Include the faint anti-aliased rows either side in the centre.
                for (int yy = Math.Max(0, s - 1); yy < s; yy++) { sw += cov[yy * w + x]; sy += cov[yy * w + x] * (yy + 0.5); }
                while (y < h && cov[y * w + x] >= MinCoverage) { sw += cov[y * w + x]; sy += cov[y * w + x] * (y + 0.5); y++; }
                if (y < h) { sw += cov[y * w + x]; sy += cov[y * w + x] * (y + 0.5); }
                runs.Add(new Run(sy / sw + y0, s + y0, y - 1 + y0));
            }
            columns[x] = runs;
            if (runs.Count == 1) lengths.Add(runs[0].Bottom - runs[0].Top + 1);
        }
        lengths.Sort();
        double pen = lengths.Count > 0 ? Math.Max(1, lengths[lengths.Count / 2]) : 2;

        var tracks = new List<Track>();
        var closed = new List<Track>();
        var ambiguous = new List<(double, double)>();
        for (int x = Math.Max(0, left); x <= Math.Min(w - 1, right); x++)
        {
            var runs = columns[x];
            if (runs is null) continue;
            double px = x + x0 + 0.5;
            foreach (var t in tracks.Where(t => px - t.LastX > maxGap + 0.5).ToList()) { tracks.Remove(t); closed.Add(t); }

            // Costs: the predicted y's distance from each run.
            var pairs = new List<(int T, int R, double Cost)>();
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                var t = tracks[ti];
                double dx = px - t.LastX, pred = t.Predict(px);
                double tol = 2 * pen + 2 + Math.Abs(t.Slope) * dx * 0.5 + (dx > 1 ? 2 : 0);
                for (int ri = 0; ri < runs.Count; ri++)
                {
                    var r = runs[ri];
                    double d = pred < r.Top ? r.Top - pred : pred > r.Bottom + 1 ? pred - r.Bottom - 1 : 0;
                    if (d <= tol) pairs.Add((ti, ri, d + 0.01 * Math.Abs(pred - r.Centre)));
                }
            }
            var trackRun = new int[tracks.Count];
            Array.Fill(trackRun, -1);
            var runTrack = new int[runs.Count];
            Array.Fill(runTrack, -1);

            // Tracks that were sharing a run and now find two: the pairing whose frozen slopes fit best (R-im14-5).
            foreach (var t in tracks.Where(t => t.Partner is not null).ToList())
            {
                int ia = tracks.IndexOf(t), ib = tracks.IndexOf(t.Partner!);
                if (ib < 0 || trackRun[ia] >= 0 || trackRun[ib] >= 0) continue;
                var near = pairs.Where(p => p.T == ia || p.T == ib).Select(p => p.R).Distinct().ToList();
                if (near.Count < 2) continue;
                var ta = tracks[ia];
                var tb = tracks[ib];
                double best = double.MaxValue, second = double.MaxValue;
                (int Ra, int Rb) choice = (-1, -1);
                foreach (int ra in near)
                    foreach (int rb in near)
                    {
                        if (ra == rb) continue;
                        double cost = Math.Abs(ta.Predict(px) - runs[ra].Centre) + Math.Abs(tb.Predict(px) - runs[rb].Centre);
                        if (cost < best) { second = best; best = cost; choice = (ra, rb); }
                        else if (cost < second) second = cost;
                    }
                if (choice.Ra < 0) continue;
                if (second < double.MaxValue && best > 0.75 * second && second - best < 2 * pen) ambiguous.Add((px, runs[choice.Ra].Centre));
                trackRun[ia] = choice.Ra; runTrack[choice.Ra] = ia;
                trackRun[ib] = choice.Rb; runTrack[choice.Rb] = ib;
            }

            foreach (var (ti, ri, _) in pairs.OrderBy(p => p.Cost).ThenBy(p => p.T))
            {
                if (trackRun[ti] >= 0 || runTrack[ri] >= 0) continue;
                trackRun[ti] = ri;
                runTrack[ri] = ti;
            }
            // A track whose run another took: it shares it (a crossing) — but only when it predicts INSIDE that run. A
            // dashed track in one of its gaps predicts beside the other curve, not in it, and taking the other's run there
            // drags it onto the other curve: the two swap at the crossing.
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                if (trackRun[ti] >= 0) continue;
                var reach = pairs.Where(p => p.T == ti).OrderBy(p => p.Cost).ToList();
                if (reach.Count == 0 || reach[0].Cost > 1) continue;
                trackRun[ti] = reach[0].R;
                int other = runTrack[reach[0].R];
                if (other < 0) continue;
                tracks[ti].Partner = tracks[other];
                tracks[other].Partner = tracks[ti];
            }

            for (int ti = 0; ti < tracks.Count; ti++)
            {
                int ri = trackRun[ti];
                if (ri < 0) continue;
                var t = tracks[ti];
                bool shared = Enumerable.Range(0, tracks.Count).Count(u => trackRun[u] == ri) > 1;
                double y = runs[ri].Centre;
                // A run much taller than the pen is a steep stretch or a crossing; where the track predicts inside it,
                // its prediction is a better y than the run's centre.
                if (shared)
                {
                    // Not more than a pen from the run: a prediction drifting off a shrinking merged run (the other curve
                    // in a dash gap) is not dragged along with it — the column is a gap for this track instead.
                    double p = t.Predict(px);
                    if (p < runs[ri].Top - pen || p > runs[ri].Bottom + 1 + pen) continue;
                    y = Math.Clamp(p, runs[ri].Top, runs[ri].Bottom + 1);
                }
                t.Points.Add((px, y));
                if (!shared)
                {
                    t.Partner = null;
                    t.Slope = Slope(t.Points);
                }
            }
            // A run no track took starts a track of its own. The tracks this column began with are the ones trackRun
            // covers: the new ones are appended as this loop goes.
            int held = trackRun.Length;
            for (int ri = 0; ri < runs.Count; ri++)
            {
                if (runTrack[ri] >= 0 || Enumerable.Range(0, held).Any(u => trackRun[u] == ri)) continue;
                var t = new Track();
                t.Points.Add((px, runs[ri].Centre));
                tracks.Add(t);
            }
        }
        closed.AddRange(tracks);

        // Join fragments: one ends, a later one starts where the first one's slope points.
        var kept = closed.Where(t => t.Points.Count >= 3).OrderBy(t => t.Points[0].X).ToList();
        bool joined = true;
        while (joined)
        {
            joined = false;
            for (int a = 0; a < kept.Count && !joined; a++)
                for (int b = 0; b < kept.Count && !joined; b++)
                {
                    if (a == b) continue;
                    var ta = kept[a];
                    var tb = kept[b];
                    double gap = tb.Points[0].X - ta.LastX;
                    if (gap <= 0 || gap > 3 * maxGap) continue;
                    double pred = ta.Predict(tb.Points[0].X);
                    if (Math.Abs(pred - tb.Points[0].Y) > 3 * pen + 3 + Math.Abs(ta.Slope) * gap * 0.3) continue;
                    if (kept.Any(o => o != ta && o != tb && o.Points[0].X > ta.LastX && o.Points[0].X < tb.Points[0].X
                                      && Math.Abs(ta.Predict(o.Points[0].X) - o.Points[0].Y) < Math.Abs(pred - tb.Points[0].Y))) continue;
                    ta.Points.AddRange(tb.Points);
                    ta.Slope = Slope(ta.Points);
                    kept.RemoveAt(b);
                    joined = true;
                }
        }

        // Fragments of one colour that follow each other along x with nothing else of that colour beside them are one
        // trace, whatever the jump between them: a phase trace wrapping from −180° to +180° is drawn as a vertical line
        // the tracker cannot follow.
        joined = true;
        while (joined)
        {
            joined = false;
            kept = [.. kept.Where(t => t.LastX - t.Points[0].X >= MinTrackShare * axis).OrderBy(t => t.Points[0].X)];
            for (int a = 0; a + 1 < kept.Count && !joined; a++)
            {
                var ta = kept[a];
                var tb = kept[a + 1];
                double gap = tb.Points[0].X - ta.LastX;
                if (gap <= 0 || gap > 2 * maxGap) continue;
                double lo = ta.Points[0].X, hi = tb.LastX;
                if (kept.Where((t, k) => k != a && k != a + 1)
                        .Any(t => t.LastX >= lo && t.Points[0].X <= hi && t.LastX - t.Points[0].X >= MinTrackShare * axis)) continue;
                ta.Points.AddRange(tb.Points);
                ta.Slope = Slope(ta.Points);
                kept.RemoveAt(a + 1);
                joined = true;
            }
        }

        var result = new List<TraceCurve>();
        double minLen = MinTrackShare * axis;
        foreach (var t in kept)
        {
            if (t.LastX - t.Points[0].X < minLen) continue;
            var pts = t.Points.OrderBy(p => p.X).ToList();
            var amb = ambiguous.Where(a => pts.Any(p => Math.Abs(p.X - a.Item1) < 1 && Math.Abs(p.Y - a.Item2) < 3 * pen)).ToList();
            result.Add(Finish(frame.Index, rgb, pts, amb, Math.Round(pen, 2), maxGap));
        }
        return result;
    }

    /// <summary>A rectangular curve from its points (ascending x): its style, the gaps bridged and the gaps left open.</summary>
    private static TraceCurve Finish(int plot, int rgb, List<(double X, double Y)> pts, IReadOnlyList<(double X, double Y)> amb,
                                     double pen, int maxGap)
    {
        int bridged = 0;
        var gaps = new List<(double, double)>();
        var onRuns = new List<int>();
        var offRuns = new List<int>();
        int on = 1;
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X;
            if (dx <= 1.5) { on++; continue; }
            onRuns.Add(on);
            on = 1;
            offRuns.Add((int)Math.Round(dx - 1));
            if (dx - 1 <= maxGap) bridged++;
            else gaps.Add((pts[i - 1].X, pts[i].X));
        }
        onRuns.Add(on);
        var style = Style(onRuns, offRuns, pen);
        // A dashed line's own gaps are its style, not gaps: counted as bridged only when the line is solid.
        if (style != TraceStyle.Solid) bridged = offRuns.Count(o => o > Math.Max(3, 3 * pen) && o <= maxGap);
        return new TraceCurve(plot, rgb, style, false, pts, bridged, gaps, amb, pen);
    }

    /// <summary>Share of <paramref name="pts"/> lying within <paramref name="tol"/> of <paramref name="other"/>: at the
    /// same x for a rectangular curve (interpolated, never beyond its ends), the nearest point for a chart's.</summary>
    private static double Alongside(TraceCurve curve, TraceCurve other, double tol)
    {
        var a = curve.Points;
        var b = other.Points;
        if (a.Count == 0 || b.Count < 2) return 0;
        int near = 0;
        if (!curve.Parametric)
        {
            int j = 0;
            foreach (var (x, y) in a)
            {
                if (x < b[0].X || x > b[^1].X) continue;
                while (j + 1 < b.Count && b[j + 1].X < x) j++;
                var (p, q) = (b[j], b[Math.Min(j + 1, b.Count - 1)]);
                // Inside one of the other's gaps there is nothing to compare with.
                if (q.X - p.X > 1.5 && x > p.X + 0.5 && x < q.X - 0.5) continue;
                double yy = q.X - p.X < 1e-9 ? p.Y : p.Y + (q.Y - p.Y) * (x - p.X) / (q.X - p.X);
                if (Math.Abs(yy - y) <= tol) near++;
            }
        }
        else
            foreach (var (x, y) in a)
                if (b.Any(p => Math.Abs(p.X - x) <= tol && Math.Abs(p.Y - y) <= tol && (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= tol * tol))
                    near++;
        return (double)near / a.Count;
    }

    /// <summary>A curve lying within about a pen of a longer one, of a like colour, for <see cref="MergeShare"/> of its
    /// length is that curve read twice — a blurred or JPEG-compressed line splits into parallel tracks and a darker core
    /// cluster — and is merged into it: its points fill the longer one's gaps, and the style is judged again.</summary>
    private static List<TraceCurve> MergeAlongside(List<TraceCurve> curves, PlotFrame frame)
    {
        int maxGap = Math.Max(3, (int)Math.Round(GapShare * (frame.Right - frame.Left)));
        var list = curves.OrderByDescending(c => c.Points.Count).ToList();
        var merged = new HashSet<TraceCurve>(ReferenceEqualityComparer.Instance);
        for (int i = list.Count - 1; i > 0; i--)
        {
            var c = list[i];
            for (int j = 0; j < i; j++)
            {
                var l = list[j];
                if (l.Parametric != c.Parametric || PlotInk.Distance(l.Rgb, c.Rgb) > MergeColour) continue;
                if (Alongside(c, l, Math.Max(l.Width, c.Width) + 1) < MergeShare) continue;
                if (!l.Parametric)
                {
                    var byX = l.Points.ToDictionary(p => p.X, p => p.Y);
                    foreach (var (x, y) in c.Points) byX.TryAdd(x, y);
                    var pts = byX.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)).ToList();
                    list[j] = Finish(l.Plot, l.Rgb, pts, [.. l.Ambiguous.Concat(c.Ambiguous).Distinct()], l.Width, maxGap) with { Followed = l.Followed };
                    merged.Add(list[j]);
                }
                list.RemoveAt(i);
                break;
            }
        }
        // The tracks a merged curve "crossed" were mostly its own duplicates: a crossing stays ambiguous only where some
        // other curve still passes.
        for (int j = 0; j < list.Count; j++)
        {
            var l = list[j];
            if (!merged.Contains(l)) continue;
            var others = list.Where(o => o != l).ToList();
            list[j] = l with { Ambiguous = [.. l.Ambiguous.Where(a => others.Any(o => o.Points.Any(p => Math.Abs(p.X - a.X) < 1 && Math.Abs(p.Y - a.Y) < 3 * l.Width + 2)))] };
        }
        return list;
    }

    private static double Slope(List<(double X, double Y)> pts)
    {
        int n = Math.Min(6, pts.Count);
        if (n < 2) return 0;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = pts.Count - n; i < pts.Count; i++)
        {
            sx += pts[i].X; sy += pts[i].Y; sxx += pts[i].X * pts[i].X; sxy += pts[i].X * pts[i].Y;
        }
        double den = n * sxx - sx * sx;
        return Math.Abs(den) < 1e-9 ? 0 : (n * sxy - sx * sy) / den;
    }

    /// <summary>Solid, dashed or dotted, from the on/off run lengths along the track.</summary>
    private static TraceStyle Style(List<int> on, List<int> off, double pen)
    {
        var gaps = off.Where(o => o >= 2).ToList();
        if (gaps.Count < 3) return TraceStyle.Solid;
        int total = on.Sum() + off.Sum();
        if (gaps.Sum() < 0.08 * total) return TraceStyle.Solid;
        var dashes = on.Skip(1).Take(Math.Max(1, on.Count - 2)).OrderBy(v => v).ToList();
        double median = dashes[dashes.Count / 2];
        return median <= Math.Max(3, 2.5 * pen) ? TraceStyle.Dotted : TraceStyle.Dashed;
    }

    // ── charts: parametric curves ───────────────────────────────────────────────────────────────────────────

    private static List<TraceCurve> Parametric(float[] cov, int w, int h, int x0, int y0, PlotFrame frame, int rgb)
    {
        var mask = new BinaryImage(w, h);
        int count = 0;
        for (int i = 0; i < cov.Length; i++) if (cov[i] >= 0.4f) { mask.Pixels[i] = 1; count++; }
        if (count < 12) return [];
        var g = SkeletonGraph.Build(mask, new SkeletonGraphOptions { MaxThreads = 1 });
        var used = new bool[g.Edges.Count];
        var result = new List<TraceCurve>();
        var walks = new List<List<(double X, double Y)>>();
        double minLen = 0.1 * frame.Radius;
        while (true)
        {
            List<(double X, double Y)>? best = null;
            bool[]? bestUsed = null;
            var starts = g.Nodes.Where(n => n.Degree == 1).Select(n => n.Id)
                .Concat(g.Nodes.Where(n => n.Degree != 1).Select(n => n.Id)).ToList();
            foreach (int start in starts)
            {
                if (!g.Edges.Any(e => !used[e.Id] && (e.From == start || e.To == start))) continue;
                var u = (bool[])used.Clone();
                var walk = Walk(g, start, u);
                if (best is null || Length(walk) > Length(best)) { best = walk; bestUsed = u; }
            }
            if (best is null || Length(best) < 2 * g.StrokeWidth + 2) break;
            used = bestUsed!;
            walks.Add(best);
        }

        // Walks whose ends meet — the skeleton cut at a self-crossing, a marker's glyph, a label — are one curve: join
        // the nearest pair of ends within a few pens; farther apart (a glyph over the curve), up to a tenth of the
        // chart's radius, only where both ends head along the gap between them.
        double near = Math.Max(6, 4 * g.StrokeWidth), far = Math.Max(near, 0.1 * frame.Radius);
        bool joined = true;
        while (joined && walks.Count > 1)
        {
            joined = false;
            double bestD = double.MaxValue;
            (int A, int B, bool RevA, bool RevB)? pick = null;
            for (int a = 0; a < walks.Count; a++)
                for (int b = 0; b < walks.Count; b++)
                {
                    if (a == b) continue;
                    foreach (bool ra in new[] { false, true })
                        foreach (bool rb in new[] { false, true })
                        {
                            var wa = ra ? Enumerable.Reverse(walks[a]).ToList() : walks[a];
                            var wb = rb ? Enumerable.Reverse(walks[b]).ToList() : walks[b];
                            var pa = wa[^1];
                            var pb = wb[0];
                            double dx = pb.X - pa.X, dy = pb.Y - pa.Y, d = Math.Sqrt(dx * dx + dy * dy);
                            if (d > far || d >= bestD) continue;
                            if (d > near && !(Heading(wa, end: true, dx, dy) && Heading(wb, end: false, dx, dy))) continue;
                            bestD = d;
                            pick = (a, b, ra, rb);
                        }
                }
            if (pick is not { } p) break;
            var first = p.RevA ? Enumerable.Reverse(walks[p.A]).ToList() : walks[p.A];
            var second = p.RevB ? Enumerable.Reverse(walks[p.B]).ToList() : walks[p.B];
            var merged = first.Concat(second).ToList();
            int hi = Math.Max(p.A, p.B), lo = Math.Min(p.A, p.B);
            walks.RemoveAt(hi);
            walks.RemoveAt(lo);
            walks.Add(merged);
            joined = true;
        }
        foreach (var walk in walks.Where(wk => Length(wk) >= minLen).OrderByDescending(Length))
        {
            var pts = Resample(walk).Select(q => (q.X + x0, q.Y + y0)).ToList();
            result.Add(new TraceCurve(frame.Index, rgb, TraceStyle.Solid, true, pts, 0, [], [], Math.Round(g.StrokeWidth, 2)));
        }
        return result;
    }

    /// <summary>From node <paramref name="start"/> along unused edges, at each junction the one turning least.</summary>
    private static List<(double X, double Y)> Walk(SkeletonGraph g, int start, bool[] used)
    {
        var pts = new List<(double X, double Y)>();
        int node = start;
        (double X, double Y)? dir = null;
        while (true)
        {
            SkeletonEdge? next = null;
            bool forward = true;
            double bestTurn = double.MaxValue;
            foreach (var e in g.Edges)
            {
                if (used[e.Id] || (e.From != node && e.To != node)) continue;
                bool fw = e.From == node;
                var poly = e.Polyline;
                if (poly.Count < 2) continue;
                var a = fw ? poly[0] : poly[^1];
                var b = fw ? poly[Math.Min(poly.Count - 1, 2)] : poly[Math.Max(0, poly.Count - 3)];
                double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                double turn = dir is { } d ? Math.Acos(Math.Clamp((d.X * dx + d.Y * dy) / len, -1, 1)) : 0;
                if (turn < bestTurn) { bestTurn = turn; next = e; forward = fw; }
            }
            if (next is null || (dir is not null && bestTurn > 0.6 * Math.PI)) break;
            used[next.Id] = true;
            var p = next.Polyline;
            var seq = forward ? p : [.. p.AsEnumerable().Reverse()];
            foreach (var v in seq)
                if (pts.Count == 0 || Math.Abs(pts[^1].X - v.x) + Math.Abs(pts[^1].Y - v.y) > 1e-9) pts.Add((v.x, v.y));
            if (seq.Count >= 2)
            {
                var a = seq[^Math.Min(seq.Count, 3)];
                var b = seq[^1];
                double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
                dir = len > 1e-9 ? (dx / len, dy / len) : dir;
            }
            node = forward ? next.To : next.From;
        }
        return pts;
    }

    /// <summary>Whether a walk's end (or start) heads along (dx, dy) — within 45°, its tangent taken over its last (or
    /// first) eight pixels.</summary>
    private static bool Heading(List<(double X, double Y)> w, bool end, double dx, double dy)
    {
        int k = Math.Min(w.Count - 1, 8);
        if (k < 1) return false;
        var (a, b) = end ? (w[^(k + 1)], w[^1]) : (w[0], w[k]);
        double tx = b.X - a.X, ty = b.Y - a.Y, tl = Math.Sqrt(tx * tx + ty * ty), dl = Math.Sqrt(dx * dx + dy * dy);
        return tl > 1e-9 && dl > 1e-9 && (tx * dx + ty * dy) / (tl * dl) > Math.Cos(Math.PI / 4);
    }

    private static double Length(List<(double X, double Y)> p)
    {
        double s = 0;
        for (int i = 1; i < p.Count; i++) s += Math.Sqrt(Math.Pow(p[i].X - p[i - 1].X, 2) + Math.Pow(p[i].Y - p[i - 1].Y, 2));
        return s;
    }

    /// <summary>The polyline at one point per pixel of length.</summary>
    private static List<(double X, double Y)> Resample(List<(double X, double Y)> p)
    {
        var r = new List<(double X, double Y)> { p[0] };
        double carry = 0;
        for (int i = 1; i < p.Count; i++)
        {
            double dx = p[i].X - p[i - 1].X, dy = p[i].Y - p[i - 1].Y, len = Math.Sqrt(dx * dx + dy * dy);
            double s = 1 - carry;
            while (s <= len)
            {
                r.Add((p[i - 1].X + dx * s / len, p[i - 1].Y + dy * s / len));
                s += 1;
            }
            carry = len - (s - 1);
        }
        if (Math.Abs(r[^1].X - p[^1].X) + Math.Abs(r[^1].Y - p[^1].Y) > 0.25) r.Add(p[^1]);
        return r;
    }
}
