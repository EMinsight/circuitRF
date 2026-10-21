// A plot picture's overlay — brief-img-14-sparameter-plots-to-touchstone.md R-im14-12, R-im14-14.
//
// Built only from the session's reading of one picture: each plot's frame (its axes box, or its chart's circle) labelled
// with its number; every tick label the reader read, green where the axis fit kept it and amber where it was rejected or
// unread — a right-hand y axis's as well as the left's; each trace drawn over its curve in its own colour and labelled with
// its assignment (amber while unassigned), a trace read against the right-hand y axis tagged → R (brief-pdg-1 R-pdg1-3);
// bridged gaps dashed; ambiguous crossings amber; markers at the points they mark; and a fitted trace's fit
// (brief-pdg-2-curve-fit.md R-pdg2-4) thin and dashed over it in a colour that contrasts with the trace's, mapped back
// through the axes, so a fit that missed is seen at a glance — the trace's own colour is never covered. The dialog's
// canvas and the CLI's --overlay picture draw these same items.
//
// A PLOT DRAWN BY HAND (brief-pdg-4-digitise-by-hand.md R-pdg4-1) is read in its box's own space; every point drawn of it
// is carried back onto the picture through PlotRead.ToPicture, so a turned box is drawn turned and its curves lie on the
// picture's curves.

using System.Globalization;
using CircuitRF.Design.Imaging.Plots;

namespace CircuitRF.Design.Imaging;

public sealed partial class ImageOverlay
{
    /// <summary>The id prefix of a bridged gap's dashed segment.</summary>
    public const string GapPrefix = "gap:";

    /// <summary>The id prefix of a trace — <c>trace:T3</c> — so the traces table and the canvas name each other.</summary>
    public const string TracePrefix = "trace:";

    /// <summary>The id prefix of a trace's fitted curve — <c>fit:T3</c>. Drawn, never selected.</summary>
    public const string FitPrefix = "fit:";

    /// <summary>The fitted curve's colour beside a trace's: magenta, or teal beside a trace near magenta.</summary>
    public static int FitColour(int traceRgb) => PlotInk.Distance(traceRgb, 0xD81B9A) < 140 ? 0x00A3A3 : 0xD81B9A;

    /// <summary>What was read on picture <paramref name="picture"/> of <paramref name="session"/>, with each trace's fit in
    /// <paramref name="fits"/> (by trace name) drawn over it.</summary>
    public static ImageOverlay BuildPlot(PlotSession session, int picture, IReadOnlyDictionary<string, TraceFitResult>? fits = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var items = new List<ImageOverlayItem>();
        IReadOnlyList<(double X, double Y)> Box(double l, double t, double r, double b) => [(l, t), (r, t), (r, b), (l, b)];

        foreach (var p in session.Plots.Where(p => p.Picture == picture))
        {
            var f = p.Frame;
            string label = $"plot {p.Number.ToString(CultureInfo.InvariantCulture)}{(p.Excluded ? " (excluded)" : "")}";
            if (f.IsChart)
                items.Add(new ImageOverlayItem($"plot:{p.Number}", ImageOverlayClass.Plot, [], true, label, null, p.Excluded, null,
                                               new PixelBounds(f.Cx - f.Radius, f.Cy - f.Radius, f.Cx + f.Radius, f.Cy + f.Radius), (f.Cx, f.Cy, f.Radius)));
            else if (p.Manual is { } m)
            {
                IReadOnlyList<(double X, double Y)> quad = [m.Box.TopLeft, m.Box.TopRight, m.Box.BottomRight, m.Box.BottomLeft];
                items.Add(new ImageOverlayItem($"plot:{p.Number}", ImageOverlayClass.Plot, [quad], true, label, null, p.Excluded || !p.Calibrated, null,
                                               PixelBounds.Of(quad)));
            }
            else
            {
                var box = Box(f.Left, f.Top, f.Right, f.Bottom);
                items.Add(new ImageOverlayItem($"plot:{p.Number}", ImageOverlayClass.Plot, [box], true, label, null, p.Excluded || !p.Calibrated, null,
                                               PixelBounds.Of(box)));
            }
            if (p.Rect is { } r)
                foreach (var (l, k) in r.XLabels.Select((l, k) => (l, k)).Concat(r.YLabels.Select((l, k) => (l, k + 1000)))
                                        .Concat(r.Y2Labels.Select((l, k) => (l, k + 2000))))
                {
                    var b = Box(l.Box.Left - 1, l.Box.Top - 1, l.Box.Right + 2, l.Box.Bottom + 2);
                    items.Add(new ImageOverlayItem($"tick:{p.Number}:{k}", ImageOverlayClass.Text, [b], true, l.Text,
                                                   l.Used ? Colours.Connected : null, !l.Used, null, PixelBounds.Of(b)));
                }
        }

        // Words on the picture that were not read (a tick label the reader could not make out, a title): amber.
        if (session.Words.TryGetValue(picture, out var words))
            foreach (var w in words.Where(w => !w.Read))
            {
                var b = Box(w.Box.Left - 1, w.Box.Top - 1, w.Box.Right + 2, w.Box.Bottom + 2);
                items.Add(new ImageOverlayItem($"text:{w.Id}", ImageOverlayClass.Text, [b], true, null, null, true, null, PixelBounds.Of(b)));
            }

        var plotsHere = session.Plots.Where(p => p.Picture == picture).Select(p => p.Number).ToHashSet();
        foreach (var t in session.Traces.Where(t => plotsHere.Contains(t.Plot)))
        {
            var owner = session.Plots[t.Plot - 1];
            var pts = owner.Manual is { Axial: false } ? [.. t.Curve.Points.Select(q => owner.ToPicture(q.X, q.Y))] : t.Curve.Points;
            if (pts.Count == 0) continue;
            // Split at the gaps left open; draw each bridged gap dashed.
            var runs = new List<List<(double X, double Y)>> { new() };
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0 && !t.Parametric && t.Curve.Points[i].X - t.Curve.Points[i - 1].X > 1.5)
                {
                    bool open = t.Curve.Gaps.Any(g => Math.Abs(g.From - t.Curve.Points[i - 1].X) < 0.5);
                    if (!open)
                        items.Add(new ImageOverlayItem($"{GapPrefix}{t.Name}:{i}", ImageOverlayClass.Trace, [[pts[i - 1], pts[i]]], false, null,
                                                       t.Curve.Rgb, false, null, PixelBounds.Of([pts[i - 1], pts[i]]).Inflate(1)));
                    runs.Add([]);
                }
                runs[^1].Add(pts[i]);
            }
            string what = t.Assignment.Ignored ? "ignored" : t.Assignment.Assigned
                ? $"{t.Assignment.Entry} {Spoken(t.Assignment.Quantity)}" : "unassigned";
            string side = !t.Parametric && session.Plots[t.Plot - 1].Rect is { TwoY: true } && t.Axis == PlotSide.Right ? " → R" : "";
            items.Add(new ImageOverlayItem($"{TracePrefix}{t.Name}", ImageOverlayClass.Trace, [.. runs.Where(r => r.Count > 0)], false,
                                           $"{t.Name} {what}{side}", t.Curve.Rgb, (!t.Assignment.Assigned && !t.Assignment.Ignored) || t.AxisAmbiguous, t.Name,
                                           PixelBounds.Of(pts).Inflate(2)));
            if (fits is not null && fits.TryGetValue(t.Name, out var fit) && fit.Fitted
                && session.Plots[t.Plot - 1] is { Rect: { X: { } xa } rect } plot && rect.AxisOn(t.Axis) is { } ya)
                FitCurve(items, t, fit, xa, ya, plot);
            foreach (var (x, y) in t.Curve.Ambiguous)
                items.Add(new ImageOverlayItem($"ambiguous:{t.Name}:{x.ToString("0", CultureInfo.InvariantCulture)}", ImageOverlayClass.Crossing, [],
                                               true, "ambiguous crossing", Colours.Amber, true, null, new PixelBounds(x - 6, y - 6, x + 6, y + 6), (x, y, 6)));
        }
        foreach (var m in session.Markers.Where(m => plotsHere.Contains(m.Plot) && m.Placed))
        {
            string label = m.Name + (m.FrequencyHz is { } f ? " " + AssemblyGrid.Hz(f) : "");
            items.Add(new ImageOverlayItem($"marker:{m.Plot}:{m.Name}:{m.X.ToString("0", CultureInfo.InvariantCulture)}", ImageOverlayClass.Marker, [], true,
                                           label, null, m.FrequencyHz is null, null, new PixelBounds(m.X - 5, m.Y - 5, m.X + 5, m.Y + 5), (m.X, m.Y, 5)));
        }
        return new ImageOverlay(items, null, [], [], null, pixelAnchors: true);
    }

    /// <summary>The fit of <paramref name="t"/> across its pixel columns, one point a column, broken where a phase fit
    /// changes branch with the trace beneath it.</summary>
    private static void FitCurve(List<ImageOverlayItem> items, PlotTraceRead t, TraceFitResult fit, AxisCalibration xa, AxisCalibration ya, PlotRead plot)
    {
        var frame = plot.Frame;
        var under = t.Samples.Where(s => double.IsFinite(s.Y)).OrderBy(s => s.Px).ToList();
        if (under.Count < 2) return;
        var runs = new List<List<(double X, double Y)>> { new() };
        int k = 0;
        for (double px = Math.Ceiling(under[0].Px); px <= under[^1].Px; px++)
        {
            while (k + 1 < under.Count && Math.Abs(under[k + 1].Px - px) <= Math.Abs(under[k].Px - px)) k++;
            double y = TraceFit.EvaluateNear(fit, xa.ValueAt(px), under[k].Y);
            double py = double.IsFinite(y) ? ya.PixelAt(y) : double.NaN;
            if (!double.IsFinite(py) || (runs[^1].Count > 0 && Math.Abs(py - runs[^1][^1].Y) > (frame.Bottom - frame.Top) / 2))
            {
                if (runs[^1].Count > 0) runs.Add([]);
                if (!double.IsFinite(py)) continue;
            }
            runs[^1].Add((px, py));
        }
        var drawn = runs.Where(r => r.Count > 1).Select(r => plot.Manual is { Axial: false } ? [.. r.Select(q => plot.ToPicture(q.X, q.Y))] : r).ToList();
        if (drawn.Count == 0) return;
        items.Add(new ImageOverlayItem($"{FitPrefix}{t.Name}", ImageOverlayClass.Trace, [.. drawn], false, null, FitColour(t.Curve.Rgb), false, null,
                                       PixelBounds.Of(drawn.SelectMany(r => r))));
    }

    /// <summary>A quantity as the overlay labels it: <c>dB</c>, <c>phase</c>, <c>|S|</c>, <c>Γ</c>.</summary>
    public static string Spoken(PlotQuantityKind k) => k switch
    {
        PlotQuantityKind.Db => "dB",
        PlotQuantityKind.Magnitude => "|S|",
        PlotQuantityKind.PhaseDeg or PlotQuantityKind.PhaseRad => "phase",
        PlotQuantityKind.Real => "Re",
        PlotQuantityKind.Imag => "Im",
        PlotQuantityKind.Complex => "complex",
        PlotQuantityKind.Vswr => "VSWR",
        PlotQuantityKind.ReturnLoss => "RL",
        PlotQuantityKind.InsertionLoss => "IL",
        _ => "?",
    };
}
