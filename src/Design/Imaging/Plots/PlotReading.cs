// A session of plot pictures, read — brief-img-14-sparameter-plots-to-touchstone.md R-im14-2, R-im14-5…R-im14-8, R-im14-15.
//
// ONE SESSION, MANY PICTURES. A datasheet's S21 and S11 are often separate figures, so every picture handed in is read
// into one session: plots are numbered across the session (plot 1 is the first picture's first plot) and so are
// traces (T1, T2 …), and the flags, the dialog's rows and the report all name them that way.
//
// EVERY READING IS PURE AND IN ORDER (overview D17): the pictures, then each picture's plots in reading order, then
// each plot's traces top to bottom. The dialog, the CLI and the MCP tool call Read and nothing else (R-im14-15).
//
// WHAT EACH TRACE IS comes from its evidence, strongest first (R-im14-6): the legend entry in its colour and style, a
// word in its colour near the curve, the y-axis title, the plot's title. The user's assignment wins over all of them.
//
// WHICH Y AXIS A TRACE IS READ AGAINST (brief-pdg-1-two-y-axes.md R-pdg1-2) matters only on a plot with a y axis on both
// sides, and comes from, strongest first: the user's choice; the legend entry's own axis mark; the colour of an axis's
// title or labels; the trace's quantity agreeing with one axis title and not the other; and, only to break a tie, the
// trace lying inside one axis's labelled span and not the other's. With none of them it is read against the left axis
// and SAID to be ambiguous — never guessed silently. Each trace is valued through its own axis, and the "axis title"
// evidence of what it is reads its own axis's title.
//
// FREQUENCY ALONG A SMITH OR POLAR TRACE (R-im14-8) comes from, strongest first: markers on the trace with frequency
// readouts; a rectangular trace of the same entry in the session (its |S| matched to the chart's |Γ| along both curves,
// in order); a start and stop the user typed, spread in proportion to the angle swept about the chart's centre. Markers
// and a typed range combine. Frequency increases clockwise unless the evidence says otherwise, and the report says
// which rule set the direction. With neither markers nor a matched trace, the trace WAITS for a typed range.
//
// A SESSION READ BY HAND (brief-pdg-4-digitise-by-hand.md R-pdg4-1) — options with Manual set — finds no frame and reads
// no word on any picture: its plots are the boxes the user drew, calibrated by the scales the user typed, and its traces
// are the curves the user placed (ManualCurves). Its words lists are empty, so legend, title and label evidence are absent
// by construction and every assignment and axis is the user's.

using System.Numerics;
using System.Security.Cryptography;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Engine;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>One picture handed to a session.</summary>
/// <param name="Name">Its file name (never its path — D4), or what a pasted picture is called.</param>
public sealed record PlotPictureInput(ImageSource Source, string Name);

/// <summary>An axis calibrated by the user (R-im14-3's fallback): <c>--x-axis</c> / <c>--y-axis</c> / <c>--y2-axis</c>,
/// the dialog's clicks.</summary>
/// <param name="Plot">The session's plot number, 1-based.</param>
public sealed record PlotAxisOverride(int Plot, PlotAxis Axis, AxisClicks Clicks);

/// <summary>A user's assignment of a trace (<c>--trace</c>, the traces table's combos).</summary>
/// <param name="Selector">Which trace: <c>T3</c>, a colour <c>#rrggbb</c>, or a line style (<c>dashed</c>), optionally
/// <c>@n</c> for plot n.</param>
/// <param name="Assignment">What it is; null when only its axis is said.</param>
/// <param name="Axis">The y axis it is read against (<c>axis=right</c>, the traces table's Axis column); null when not said.</param>
public sealed record PlotTraceOverride(string Selector, PlotAssignment? Assignment, PlotSide? Axis = null);

/// <summary>A start and stop typed for a chart's trace (<c>--freq</c>, the frequency row).</summary>
public sealed record PlotFrequencyOverride(string Selector, double StartHz, double StopHz);

/// <summary>Everything the user may tell a reading.</summary>
public sealed record PlotReadingOptions
{
    /// <summary>The plots to read, by session number; null reads all (R-im14-2's exclusion is the complement).</summary>
    public IReadOnlyList<int>? Plots { get; init; }

    public IReadOnlyList<PlotAxisOverride> Axes { get; init; } = [];

    /// <summary>Axes forced logarithmic (or linear): (plot, axis, log); plot 0 is every plot.</summary>
    public IReadOnlyList<(int Plot, PlotAxis Axis, bool Log)> Scales { get; init; } = [];

    public IReadOnlyList<PlotTraceOverride> Traces { get; init; } = [];

    /// <summary>A Smith chart's reference impedance, ohms, in place of the chart's (or the 50 Ω default).</summary>
    public double? SmithZ0 { get; init; }

    public IReadOnlyList<PlotFrequencyOverride> Frequencies { get; init; } = [];

    /// <summary>Clicks on missed curves and excluded rectangles, per session plot number.</summary>
    public IReadOnlyDictionary<int, TraceHints> Hints { get; init; } = new Dictionary<int, TraceHints>();

    /// <summary>The plots drawn by hand (R-pdg4-1). Null reads every picture automatically; set — even empty — the session
    /// is read by hand: no frame is found and no word read on any picture, and its plots are these, numbered in picture
    /// order and then in this order.</summary>
    public IReadOnlyList<ManualPlot>? Manual { get; init; }

    /// <summary>The curves placed by hand on the manual plots (R-pdg4-2), each naming its plot by session number.</summary>
    public IReadOnlyList<ManualCurve> ManualCurves { get; init; } = [];
}

/// <summary>A picture of the session.</summary>
public sealed record PlotPictureRead(int Index, string Name, string Sha256, int Width, int Height, IReadOnlyList<int> Plots);

/// <summary>One plot of the session, read.</summary>
/// <param name="Number">Its session number, 1-based.</param>
/// <param name="Picture">Its picture's index in the session, 0-based.</param>
/// <param name="Excluded">Left out by the user: drawn, not read.</param>
public sealed record PlotRead(int Number, int Picture, PlotFrame Frame, RectCalibration? Rect, SmithCalibration? Smith,
                              PolarCalibration? Polar, bool Excluded, IReadOnlyList<LegendEntry> Legend)
{
    public PlotType Type => Frame.Type;

    /// <summary>The box and scales it was drawn by hand from (R-pdg4-1); null for a plot that was found. Its frame, its
    /// traces' points and their samples' pixels are in the box's own space — <see cref="ToPicture"/> carries them back.</summary>
    public ManualPlot? Manual { get; init; }

    /// <summary>A point of this plot's own pixel space on its picture: through the manual box, else as it is.</summary>
    public (double X, double Y) ToPicture(double x, double y) => Manual is { Axial: false } m ? m.FromBox(x, y) : (x, y);

    /// <summary>A picture point in this plot's own pixel space.</summary>
    public (double X, double Y) FromPicture(double x, double y) => Manual is { Axial: false } m ? m.ToBox(x, y) : (x, y);

    /// <summary>The plot's axes are calibrated (a chart always is).</summary>
    public bool Calibrated => Type != PlotType.Rectangular || Rect is { Calibrated: true };
}

/// <summary>One sample of a trace, in its plot's own terms.</summary>
/// <param name="X">A rectangular trace's x value (hertz on a frequency axis); a chart trace's frequency, or NaN until
/// it has one.</param>
/// <param name="Y">A rectangular trace's y value, in the axis's units.</param>
/// <param name="Value">A chart trace's complex value (Γ on a Smith chart).</param>
/// <param name="Exact">From a marker's readout rather than the picture's pixels.</param>
public readonly record struct PlotSample(double X, double Y, Complex Value, double Px, double Py, bool Exact = false);

/// <summary>How a chart trace's frequency was set (R-im14-8).</summary>
/// <param name="Source"><c>markers</c>, <c>matched to T3</c>, <c>typed range</c>, or combinations.</param>
/// <param name="Direction">The rule that set which way frequency runs: <c>clockwise</c> (Foster), <c>markers</c>,
/// <c>matched trace</c>.</param>
public sealed record TraceFrequency(string Source, string Direction, double StartHz, double StopHz);

/// <summary>One trace of the session.</summary>
/// <param name="Number">Its session number: T1, T2 …</param>
/// <param name="Plot">Its plot's session number.</param>
/// <param name="Resolution">How much one pixel is in its quantity (dB, degrees, |S| or |Γ|).</param>
/// <param name="Frequency">A chart trace's frequency evidence; null while it has none (it then waits).</param>
/// <param name="Axis">The side of the y axis it is read against (R-pdg1-2): its plot's only y axis's side, or on a plot
/// with two, what its evidence says.</param>
/// <param name="AxisEvidence">What said so on a plot with two y axes: <c>user</c>, <c>legend</c>, <c>colour</c>,
/// <c>quantity</c>, <c>range</c>, or <see cref="NoAxisEvidence"/>; empty on a plot with one.</param>
public sealed record PlotTraceRead(int Number, int Plot, TraceCurve Curve, PlotAssignment Assignment, IReadOnlyList<PlotSample> Samples,
                                   double Resolution, string ResolutionUnit, TraceFrequency? Frequency,
                                   PlotSide Axis = PlotSide.Left, string AxisEvidence = "")
{
    /// <summary>The axis evidence of a trace nothing placed: it is read against the left axis, and says so.</summary>
    public const string NoAxisEvidence = "none";

    public string Name => $"T{Number}";

    /// <summary>The numbers of the pieces TraceJoin joined into this trace (brief-pdg-2-curve-fit.md R-pdg2-3); empty for a
    /// trace not joined. A fit asked of any piece is the joined trace's.</summary>
    public IReadOnlyList<int> Pieces { get; init; } = [];

    /// <summary>On a plot with two y axes, nothing said which one it is read against (R-pdg1-2 (6)).</summary>
    public bool AxisAmbiguous => AxisEvidence == NoAxisEvidence;

    /// <summary>The name a curve placed by hand was given (R-pdg4-2); null for a trace that was read.</summary>
    public string? ManualName { get; init; }

    /// <summary>Placed by hand: its samples are its sampled grid, and unassigned it is plain data rather than waiting.</summary>
    public bool Manual => ManualName is not null;

    public bool Parametric => Curve.Parametric;

    /// <summary>The evidence that named it, or why it is unassigned.</summary>
    public string Evidence => Assignment.Assigned ? Assignment.Evidence : "no legend, label or axis title names it";
}

/// <summary>One finding of a plot reading.</summary>
public sealed record PlotFinding(string Class, string Sentence);

/// <summary>Everything a session read.</summary>
public sealed record PlotSession(IReadOnlyList<PlotPictureRead> Pictures, IReadOnlyList<PlotRead> Plots, IReadOnlyList<PlotTraceRead> Traces,
                                 IReadOnlyList<PlotMarker> Markers, IReadOnlyList<PlotFinding> Findings,
                                 IReadOnlyDictionary<int, IReadOnlyList<PlotWord>> Words)
{
    /// <summary>The report, one line a finding.</summary>
    public IEnumerable<string> Lines()
    {
        foreach (var p in Pictures)
            yield return $"picture {p.Index + 1}: {p.Name} ({p.Width} × {p.Height} px), plots {string.Join(", ", p.Plots)}";
        foreach (var f in Findings) yield return f.Sentence;
    }
}

public static class PlotReading
{
    /// <summary>The frames of one picture (R-im14-2) — what the dialog numbers before a full read.</summary>
    public static IReadOnlyList<PlotFrame> FindPlots(ImageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return PlotFrames.Find(PlotInk.Of(source.Raster));
    }

    /// <summary>Reads every picture's plots into one session. Pure and deterministic.</summary>
    public static PlotSession Read(IReadOnlyList<PlotPictureInput> pictures, PlotReadingOptions? options = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        var o = options ?? new PlotReadingOptions();
        var token = control?.Token ?? CancellationToken.None;
        var findings = new List<PlotFinding>();
        var pics = new List<PlotPictureRead>();
        var plots = new List<PlotRead>();
        var curves = new List<(int Plot, TraceCurve Curve)>();
        var markers = new List<PlotMarker>();
        var wordsOf = new Dictionary<int, IReadOnlyList<PlotWord>>();
        var inks = new Dictionary<int, PlotInk>();

        for (int pi = 0; pi < pictures.Count; pi++)
        {
            control?.BeginStage($"Reading picture {pi + 1}");
            token.ThrowIfCancellationRequested();
            var input = pictures[pi];
            if (o.Manual is { } manual)
            {
                // Read by hand: no frame is found and no word read (R-pdg4-1).
                wordsOf[pi] = [];
                var mine = new List<int>();
                foreach (var m in manual.Where(m => m.Picture == pi))
                {
                    int number = plots.Count + 1;
                    mine.Add(number);
                    bool excluded = o.Plots is { } chosen && !chosen.Contains(number);
                    plots.Add(new PlotRead(number, pi, m.Frame(mine.Count - 1), m.Calibrate(), null, null, excluded, []) { Manual = m });
                    DescribeManual(findings, plots[^1]);
                }
                if (mine.Count == 0)
                    findings.Add(new("picture", $"picture {pi + 1}: no plot area drawn — drag a box across the plot's axes"));
                pics.Add(new PlotPictureRead(pi, input.Name, Sha256(input.Source), input.Source.Raster.Width, input.Source.Raster.Height, mine));
                continue;
            }
            var ink = PlotInk.Of(input.Source.Raster);
            inks[pi] = ink;
            var frames = PlotFrames.Find(ink);
            var numbers = new List<int>();
            var lines = frames.SelectMany(f => f.Lines).ToList();
            var words = PlotText.Read(ink, lines, b => NumberPlace(frames, b));
            wordsOf[pi] = words;
            if (frames.Count == 0)
                findings.Add(new("picture", $"picture {pi + 1}: no plot was found — no rectangular axes with a grid or ticks, and no Smith or polar chart"));

            foreach (var frame in frames)
            {
                // A box with ticks but not one tick label read on either axis is not a plot — a marker's readout box,
                // a table — and is not numbered.
                if (frame.Type == PlotType.Rectangular
                    && RectAxes.Calibrate(frame, words) is { XLabels.Count: 0, YLabels.Count: 0, Y2Labels.Count: 0 }
                    && !o.Axes.Any(a => a.Plot == plots.Count + 1)) continue;
                int number = plots.Count + 1;
                numbers.Add(number);
                bool excluded = o.Plots is { } chosen && !chosen.Contains(number);
                RectCalibration? rect = null;
                SmithCalibration? smith = null;
                PolarCalibration? polar = null;
                switch (frame.Type)
                {
                    case PlotType.Rectangular:
                    {
                        AxisClicks? Clicks(PlotAxis axis) => o.Axes.LastOrDefault(a => a.Plot == number && a.Axis == axis)?.Clicks;
                        rect = RectAxes.Calibrate(frame, words, Clicks(PlotAxis.X), Clicks(PlotAxis.Y), Scale(o, number, PlotAxis.X),
                                                  Scale(o, number, PlotAxis.Y), y2: Clicks(PlotAxis.Y2), y2Log: Scale(o, number, PlotAxis.Y2));
                        break;
                    }
                    case PlotType.Smith:
                        smith = SmithFrame.Calibrate(frame, words, o.SmithZ0);
                        break;
                    default:
                        polar = PolarFrame.Calibrate(frame, words);
                        break;
                }
                var legend = PlotMarkers.Legend(ink, frame, words);
                plots.Add(new PlotRead(number, pi, frame, rect, smith, polar, excluded, legend));
                if (excluded) continue;
                o.Hints.TryGetValue(number, out var hints);
                foreach (var c in PlotTraces.Find(ink, frame, words, PlotMarkers.Boxes(legend), hints)) curves.Add((number, c));
                foreach (var m in PlotMarkers.Find(ink, frame, words)) markers.Add(m with { Plot = number });
                Describe(findings, plots[^1]);
            }
            pics.Add(new PlotPictureRead(pi, input.Name, Sha256(input.Source), input.Source.Raster.Width, input.Source.Raster.Height, numbers));
        }

        // ── traces: numbered, assigned, valued ──
        control?.BeginStage("Reading the traces");
        var traces = new List<PlotTraceRead>();
        var plotMarkers = new List<PlotMarker>();
        foreach (var (plotNumber, curve) in curves)
        {
            var plot = plots[plotNumber - 1];
            int number = traces.Count + 1;
            var words = wordsOf[plot.Picture];
            var t = new PlotTraceRead(number, plotNumber, curve, PlotAssignment.None, [], 0, "", null);
            // Which y axis first: the "axis title" evidence of what it is reads that axis's title (R-pdg1-2).
            var (side, axisEvidence) = plot.Rect is { } rc ? SideOf(t, plot, rc, words, o) : (PlotSide.Left, "");
            var assignment = Assign(plot, curve, words, plot.Rect?.TitleOn(side));
            traces.Add(t with { Assignment = assignment, Axis = side, AxisEvidence = axisEvidence });
        }
        // Curves placed by hand (R-pdg4-2): read through their plot's own box and scales, sampled on its grid.
        var manualLines = new Dictionary<int, string>();
        foreach (var c in o.ManualCurves)
        {
            if (c.Plot < 1 || c.Plot > plots.Count || plots[c.Plot - 1] is not { Manual: { } mp, Rect: { } rc } plot || plot.Excluded) continue;
            var (curve, samples, dropped, refusal) = ManualCurves.Read(c, mp, rc, plot.Frame.Index);
            int number = traces.Count + 1;
            var ya = rc.AxisOn(c.Axis);
            double mid = samples.Count > 0 ? samples.Average(s => s.Py) : (mp.Top + mp.Bottom) / 2;
            string unit = c.Assignment.Assigned ? QuantityUnit(c.Assignment.Quantity) : c.Axis == PlotSide.Right ? mp.Y2Unit : mp.YUnit;
            traces.Add(new PlotTraceRead(number, c.Plot, curve, c.Assignment, samples, ya?.PerPixel(mid) ?? 0, unit, null, c.Axis,
                                         rc.TwoY ? "user" : "") { ManualName = c.Name });
            string range = samples.Count == 0 ? "" : $" from {AxisText(samples[0].X, rc)} to {AxisText(samples[^1].X, rc)}";
            manualLines[number] = refusal is not null
                ? $"T{number} ({c.Name}, plot {c.Plot}): not read — {refusal}"
                : $"T{number} ({c.Name}, plot {c.Plot}): {(c.Assignment.Assigned ? c.Assignment.ToString() : "data")}; {c.Fixpoints.Count} fixpoint(s), "
                  + $"{samples.Count} sample(s) ({mp.Sampling.Method.ToString().ToLowerInvariant()}){range}"
                  + (dropped > 0 ? $"; {dropped} grid point(s) beyond the fixpoints dropped" : "")
                  + (rc.TwoY ? $"; read on the {Side(c.Axis)} scale" : "");
        }

        // Markers onto their traces, by the nearest point.
        foreach (var m0 in markers)
        {
            int plotNumber = m0.Plot;
            var m = m0;
            // A readout placed by its own value: Γ on a Smith chart, the complex value on a polar one.
            if (!m.Placed && m.Complex is { } cv)
            {
                var pr = plots[plotNumber - 1];
                if (pr.Smith is { } sc) { var (px, py) = sc.PixelOf(cv); m = m with { X = px, Y = py }; }
                else if (pr.Polar is { } pc) { var (px, py) = pc.PixelOf(cv); m = m with { X = px, Y = py }; }
            }
            if (!m.Placed) { plotMarkers.Add(m); continue; }
            var best = traces.Where(t => t.Plot == plotNumber)
                             .Select(t => (t, d: t.Curve.Points.Min(p => Math.Sqrt((p.X - m.X) * (p.X - m.X) + (p.Y - m.Y) * (p.Y - m.Y)))))
                             .OrderBy(x => x.d).FirstOrDefault();
            plotMarkers.Add(best.t is not null && best.d <= Math.Max(8, 3 * best.t.Curve.Width) ? m with { Trace = best.t.Number, Plot = plotNumber }
                                                                                               : m with { Plot = plotNumber });
        }

        // The user's assignments win.
        for (int i = 0; i < traces.Count; i++)
        {
            var t = traces[i];
            if (t.Manual) continue;   // a curve placed by hand carries its own
            foreach (var ov in o.Traces)
                if (ov.Assignment is { } a && Selects(ov.Selector, t))
                    // A chart's trace is complex by its chart, whatever quantity the assignment named.
                    t = t with { Assignment = t.Parametric && a.Quantity != PlotQuantityKind.Ignored
                                     ? a with { Quantity = PlotQuantityKind.Complex } : a };
            traces[i] = t;
        }

        // Values — a manual curve's are its sampled grid already.
        for (int i = 0; i < traces.Count; i++)
            if (!traces[i].Manual) traces[i] = Values(traces[i], plots[traces[i].Plot - 1], plotMarkers);

        // Frequency along chart traces (R-im14-8).
        for (int i = 0; i < traces.Count; i++)
            if (traces[i].Parametric) traces[i] = ChartFrequency(traces[i], traces, plotMarkers, o, findings);

        foreach (var t in traces)
        {
            if (manualLines.TryGetValue(t.Number, out var said)) { findings.Add(new("trace", said)); continue; }
            string style = t.Curve.Style == TraceStyle.Solid ? "" : $" {t.Curve.StyleName}";
            string gaps = t.Curve.Gaps.Count > 0 ? $"; {t.Curve.Gaps.Count} gap(s) left open" : "";
            string bridged = t.Curve.Bridged > 0 ? $"; {t.Curve.Bridged} short gap(s) bridged" : "";
            string amb = t.Curve.Ambiguous.Count > 0 ? $"; {t.Curve.Ambiguous.Count} ambiguous crossing(s)" : "";
            string axis = t.AxisEvidence.Length == 0 ? ""
                : $"; read against the {Side(t.Axis)} y axis" + (t.AxisAmbiguous ? " (assumed)" : $" ({AxisEvidenceText(t.AxisEvidence)})");
            findings.Add(new("trace", $"{t.Name} (plot {t.Plot}, {t.Curve.Hex}{style}): {t.Assignment}"
                                      + (t.Assignment.Assigned ? $" from the {t.Assignment.Evidence}" : " — " + t.Evidence)
                                      + $"; {t.Samples.Count} points, ±{Fmt(t.Resolution / 2)}{(t.ResolutionUnit.Length > 0 ? " " + t.ResolutionUnit : "")} per pixel{gaps}{bridged}{amb}{axis}"));
            if (t.AxisAmbiguous)
                findings.Add(new("axis", $"{t.Name} (plot {t.Plot}): nothing says which y axis it is read against — the {Side(t.Axis)} assumed; "
                                         + $"its Axis in the traces table, or --trace {t.Name}={(t.Assignment.Assigned ? t.Assignment.ToString() : "S21:db")},axis=right"));
        }
        foreach (var m in plotMarkers)
            findings.Add(new("marker", $"marker {m.Name} (plot {m.Plot})" + (m.Trace > 0 ? $" on T{m.Trace}" : " on no trace")
                                       + (m.FrequencyHz is { } f ? $" at {Fmt(f / 1e9)} GHz" : ", no frequency read")));
        return new PlotSession(pics, plots, traces, plotMarkers, findings, wordsOf);
    }

    /// <summary>A manual plot's finding: its box and the scales typed, or why they cannot calibrate it.</summary>
    private static void DescribeManual(List<PlotFinding> findings, PlotRead p)
    {
        var m = p.Manual!;
        string Scale(ManualAxis a, bool x) =>
            $"{(x ? AxisValueText(a.Low, m.XIsFrequency) : Fmt(a.Low))} … {(x ? AxisValueText(a.High, m.XIsFrequency) : Fmt(a.High))} {a.Scale.ToString().ToLowerInvariant()}";
        string angle = Math.Abs(m.Box.AngleDeg) >= 0.05 ? $", turned {Fmt(Math.Round(m.Box.AngleDeg, 2))}°" : "";
        string y2 = m.Y2 is { } r ? $"; right y {Scale(r, false)}" : "";
        findings.Add(new("plot", $"plot {p.Number}: rectangular plot drawn by hand ({Fmt(Math.Round(m.Box.Width, 1))} × {Fmt(Math.Round(m.Box.Height, 1))} px{angle}); "
                                 + $"x {Scale(m.X, true)}; y {Scale(m.Y, false)}{y2}"));
        if (m.Refusal is { } why) findings.Add(new("axis", $"plot {p.Number}: {why}"));
    }

    /// <summary>An x value as a sentence says it: a frequency with its unit, else the number.</summary>
    private static string AxisValueText(double v, bool frequency) => frequency ? AssemblyGrid.Hz(Significant(v)) : Fmt(v);

    /// <summary>To nine significant digits: a value read through a calibration a hair under 1 GHz is said as 1 GHz.</summary>
    private static double Significant(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return v;
        double step = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(v))) - 8);
        return Math.Round(v / step) * step;
    }

    private static string AxisText(double v, RectCalibration r) => AxisValueText(v, r.XIsFrequency);

    /// <summary>A quantity's unit, as a read trace's resolution is said in.</summary>
    private static string QuantityUnit(PlotQuantityKind q) => q switch
    {
        PlotQuantityKind.Db or PlotQuantityKind.ReturnLoss or PlotQuantityKind.InsertionLoss => "dB",
        PlotQuantityKind.PhaseDeg => "°",
        PlotQuantityKind.PhaseRad => "rad",
        _ => "",
    };

    private static bool? Scale(PlotReadingOptions o, int plot, PlotAxis axis)
    {
        foreach (var (p, a, log) in o.Scales.Reverse())
            if ((p == 0 || p == plot) && a == axis) return log;
        return null;
    }

    /// <summary>Where a tick label stands: under a rectangular plot's x axis, either side of it (a y axis may stand on the
    /// right, R-pdg1-1), or on a chart.</summary>
    private static bool NumberPlace(IReadOnlyList<PlotFrame> frames, PixelBox b)
    {
        foreach (var f in frames)
        {
            if (f.IsChart)
            {
                if (Math.Abs(b.CentreX - f.Cx) <= 1.3 * f.Radius && Math.Abs(b.CentreY - f.Cy) <= 1.3 * f.Radius) return true;
                continue;
            }
            double w = f.Right - f.Left, h = f.Bottom - f.Top;
            bool below = b.Top > f.Bottom && b.Top - f.Bottom < 0.12 * h && b.CentreX > f.Left - 0.05 * w && b.CentreX < f.Right + 0.05 * w;
            bool left = b.Right < f.Left && f.Left - b.Right < 0.15 * w && b.CentreY > f.Top - 0.05 * h && b.CentreY < f.Bottom + 0.05 * h;
            bool right = b.Left > f.Right && b.Left - f.Right < 0.15 * w && b.CentreY > f.Top - 0.05 * h && b.CentreY < f.Bottom + 0.05 * h;
            if (below || left || right) return true;
        }
        return false;
    }

    private static void Describe(List<PlotFinding> findings, PlotRead p)
    {
        string head = $"plot {p.Number}: {PlotKind.Name(p.Type)}";
        switch (p.Type)
        {
            case PlotType.Rectangular when p.Rect is { } r:
            {
                string Axis(string name, AxisCalibration? a, IReadOnlyList<AxisLabel> labels, string flag) => a is null
                    ? $"{name} axis uncalibrated ({labels.Count} label(s) read) — two clicks and two values, or {flag}"
                    : $"{name} axis {a.Scale.ToString().ToLowerInvariant()}{(a.Manual ? " (typed)" : $" from {a.Used} label(s), residual {Fmt(a.ResidualPx)} px")}"
                      + (a.Rejected > 0 ? $", {a.Rejected} misread label(s) rejected" : "");
                string y = Axis(r.YSide == PlotSide.Right ? "y (right)" : "y", r.Y, r.YLabels, "--y-axis");
                string y2 = r.TwoY ? $"; {Axis("y2 (right)", r.Y2, r.Y2Labels, "--y2-axis")}" : "";
                findings.Add(new("plot", $"{head}; {Axis("x", r.X, r.XLabels, "--x-axis")}; {y}{y2}"));
                if (r.XIsFrequency && !r.XUnitStated)
                    findings.Add(new("assumption", $"plot {p.Number}: no frequency unit was read on the x axis; "
                                                   + (r.XUnit == 1 ? "the labels taken as hertz" : "GHz assumed") + " — --x-axis with units changes it"));
                break;
            }
            case PlotType.Smith when p.Smith is { } s:
                findings.Add(new("plot", $"{head}{(s.Admittance ? " (admittance chart)" : s.Overlaid ? " (Z-Y chart, read as Z)" : "")}; "
                                         + $"circle r = {Fmt(s.Radius)} px, residual {Fmt(s.ResidualPx)} px; constant-r circles checked: "
                                         + string.Join(", ", s.CirclesChecked.Select(Fmt))));
                if (!s.Z0Stated)
                    findings.Add(new("assumption", $"plot {p.Number}: the chart states no Z0; 50 Ω assumed — --smith-z0 changes it"));
                break;
            case PlotType.Polar when p.Polar is { } pl:
                findings.Add(new("plot", $"{head}; {(pl.Db ? "dB" : "linear")} radius {Fmt(pl.InnerValue)} … {Fmt(pl.OuterValue)}"
                                         + (pl.Stated ? " from its ring labels" : " (no ring labels read: |S| = 1 at the outer ring assumed)")
                                         + (pl.AnglesStated ? "" : "; 0° at the right, counter-clockwise, assumed")));
                break;
        }
    }

    // ── assignment (R-im14-6) ──────────────────────────────────────────────────────────────────────────────

    /// <param name="axisTitle">The title of the y axis the trace is read against; null leaves that evidence out.</param>
    private static PlotAssignment Assign(PlotRead plot, TraceCurve curve, IReadOnlyList<PlotWord> words, string? axisTitle) =>
        PlotQuantity.Assign(Sources(plot, curve, words, axisTitle), plot.Type);

    /// <summary>The words that say what a trace is, strongest first: (evidence, text).</summary>
    private static List<(string Evidence, string Text)> Sources(PlotRead plot, TraceCurve curve, IReadOnlyList<PlotWord> words, string? axisTitle)
    {
        var sources = new List<(string, string)>();
        // 1. the legend entry in its colour and style (style decides between two of one colour)
        if (LegendEntryOf(plot, curve) is { } e) sources.Add(("legend", e.Text));

        // 2. a word in its colour near the plot — written along the trace, or beside the chart as circuitRF draws it
        var f = plot.Frame;
        double size = f.IsChart ? 2 * f.Radius : Math.Max(f.Right - f.Left, f.Bottom - f.Top);
        bool coloured = RectAxes.Chromatic(curve.Rgb);
        var legendWords = plot.Legend.Select(e => e.Word).ToHashSet();
        foreach (var w in words.Where(w => w.Read && w.Number is null && !legendWords.Contains(w.Box) && PlotMarkers.Name(w.Text) is null
                                           && w.Box.CentreX > f.Left - 0.35 * size && w.Box.CentreX < f.Right + 0.35 * size
                                           && w.Box.CentreY > f.Top - 0.35 * size && w.Box.CentreY < f.Bottom + 0.35 * size)
                              .OrderBy(w => DistanceTo(curve, w.Box)))
        {
            bool sameColour = coloured && PlotInk.Distance(w.Rgb, curve.Rgb) < 72;
            bool along = !sameColour && plot.Frame.Inside(w.Box.CentreX, w.Box.CentreY) && DistanceTo(curve, w.Box) < 2.5 * w.Height
                         && PlotQuantity.Entry(w.Text) is not null;
            if (sameColour || along) { sources.Add(("label", w.Text)); break; }
        }
        // 3. the title of its own y axis
        if (axisTitle is { } yt) sources.Add(("axis title", yt));
        // 4. the plot's title — a chart's is the word just above it, the one naming an entry when several stand there
        if (plot.Rect?.Title is { } title) sources.Add(("plot title", title));
        else if (f.IsChart && ChartTitle(f, words) is { } ct) sources.Add(("plot title", ct));
        return sources;
    }

    // ── which y axis (R-pdg1-2) ────────────────────────────────────────────────────────────────────────

    /// <summary>The side of the y axis <paramref name="t"/> is read against, and what said so — empty evidence on a plot
    /// with one y axis.</summary>
    private static (PlotSide Side, string Evidence) SideOf(PlotTraceRead t, PlotRead plot, RectCalibration r, IReadOnlyList<PlotWord> words,
                                                           PlotReadingOptions o)
    {
        // 1. the user's
        if (o.Traces.LastOrDefault(ov => ov.Axis is not null && Selects(ov.Selector, t))?.Axis is { } said) return (said, "user");
        if (!r.TwoY) return (r.DefaultSide, "");

        // 2. the legend entry's own axis mark
        if (LegendEntryOf(plot, t.Curve) is { } entry && LegendSide(entry.Text) is { } marked) return (marked, "legend");

        // 3. the colour of an axis's title or labels
        static bool Near(int? ink, int rgb) => ink is { } i && PlotInk.Distance(i, rgb) <= PlotTraces.MergeColour;
        bool left = Near(r.YTitleRgb, t.Curve.Rgb) || Near(r.YLabelRgb, t.Curve.Rgb);
        bool right = Near(r.Y2TitleRgb, t.Curve.Rgb) || Near(r.Y2LabelRgb, t.Curve.Rgb);
        if (left != right) return (right ? PlotSide.Right : PlotSide.Left, "colour");

        // 4. its quantity agreeing with one axis title and not the other — the user's, else what the words other than the
        //    axis titles say
        var quantity = o.Traces.LastOrDefault(ov => ov.Assignment is not null && Selects(ov.Selector, t))?.Assignment?.Quantity
                       ?? Sources(plot, t.Curve, words, axisTitle: null).Select(w => PlotQuantity.Kind(w.Text)).FirstOrDefault(k => k is not null);
        if (Family(quantity) is { } family)
        {
            bool l = r.YTitle is { } lt && Family(PlotQuantity.Kind(lt)) == family;
            bool rr = r.Y2Title is { } rt && Family(PlotQuantity.Kind(rt)) == family;
            if (l != rr) return (rr ? PlotSide.Right : PlotSide.Left, "quantity");
        }

        // 5. inside one axis's labelled span and not the other's — weak, both mappings being linear over the same pixels
        if (t.Curve.Points.Count > 0)
        {
            double top = t.Curve.Points.Min(p => p.Y), bottom = t.Curve.Points.Max(p => p.Y);
            static bool Within(IReadOnlyList<AxisLabel> labels, double lo, double hi)
            {
                var used = labels.Where(a => a.Used).ToList();
                return used.Count >= 2 && lo >= used.Min(a => a.Pixel) - RectAxes.InlierPx && hi <= used.Max(a => a.Pixel) + RectAxes.InlierPx;
            }
            bool inLeft = Within(r.YLabels, top, bottom), inRight = Within(r.Y2Labels, top, bottom);
            if (inLeft != inRight) return (inRight ? PlotSide.Right : PlotSide.Left, "range");
        }

        // 6. nothing: the left, said to be assumed
        return (PlotSide.Left, PlotTraceRead.NoAxisEvidence);
    }

    /// <summary>The legend entry in the curve's colour and style, as the assignment's legend evidence takes it.</summary>
    private static LegendEntry? LegendEntryOf(PlotRead plot, TraceCurve curve)
    {
        var legend = plot.Legend.Where(e => PlotInk.Distance(e.Rgb, curve.Rgb) < 72).ToList();
        if (legend.Count > 1) legend = [.. legend.Where(e => e.Style == curve.Style).DefaultIfEmpty(legend[0])];
        return legend.FirstOrDefault();
    }

    private static readonly System.Text.RegularExpressions.Regex RightMark =
        new(@"\((?:R|right)\)|\bright\b|\bY2\b|→|->|\bRHS\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex LeftMark =
        new(@"\((?:L|left)\)|\bleft\b|\bY1\b|←|<-|\bLHS\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The side a legend entry's own words point at — <c>(R)</c>, <c>right</c>, <c>Y2</c>, an arrow — or null.</summary>
    internal static PlotSide? LegendSide(string text)
    {
        bool r = RightMark.IsMatch(text), l = LeftMark.IsMatch(text);
        return r == l ? null : r ? PlotSide.Right : PlotSide.Left;
    }

    /// <summary>Quantities one axis title can carry together: dB and the losses, the two phase units.</summary>
    private static int? Family(PlotQuantityKind? k) => k switch
    {
        PlotQuantityKind.Db or PlotQuantityKind.ReturnLoss or PlotQuantityKind.InsertionLoss => 1,
        PlotQuantityKind.PhaseDeg or PlotQuantityKind.PhaseRad => 2,
        PlotQuantityKind.Magnitude => 3,
        PlotQuantityKind.Vswr => 4,
        PlotQuantityKind.Real => 5,
        PlotQuantityKind.Imag => 6,
        _ => null,
    };

    /// <summary><c>left</c> / <c>right</c>.</summary>
    public static string Side(PlotSide side) => side == PlotSide.Right ? "right" : "left";

    /// <summary>An axis's evidence as a sentence says it.</summary>
    public static string AxisEvidenceText(string evidence) => evidence switch
    {
        "user" => "chosen",
        "legend" => "its legend entry",
        "colour" => "the axis's colour",
        "quantity" => "the axis title's quantity",
        "range" => "the axis's labelled span",
        PlotTraceRead.NoAxisEvidence => "assumed",
        _ => evidence,
    };

    private static string? ChartTitle(PlotFrame f, IReadOnlyList<PlotWord> words)
    {
        double top = f.Cy - f.Radius;
        var above = words.Where(w => w.Read && !w.Vertical && w.Number is null && w.Box.Bottom <= top + 1
                                     && top - w.Box.Bottom < 0.5 * f.Radius && Math.Abs(w.Box.CentreX - f.Cx) < f.Radius)
                         .OrderByDescending(w => w.Box.Bottom).ToList();
        return (above.FirstOrDefault(w => PlotQuantity.Entry(w.Text) is not null) ?? above.FirstOrDefault())?.Text;
    }

    private static double DistanceTo(TraceCurve c, PixelBox b)
    {
        double best = double.MaxValue;
        foreach (var (x, y) in c.Points) best = Math.Min(best, b.Distance(x, y));
        return best;
    }

    /// <summary>Whether <paramref name="selector"/> names trace <paramref name="t"/>: <c>T3</c>, <c>#rrggbb</c> (within
    /// a JPEG's tolerance), a line style, each optionally <c>@plot</c>.</summary>
    public static bool Selects(string selector, PlotTraceRead t)
    {
        string s = selector.Trim();
        int at = s.LastIndexOf('@');
        if (at > 0)
        {
            if (!int.TryParse(s[(at + 1)..], out int plot) || plot != t.Plot) return false;
            s = s[..at];
        }
        if (s.Length > 1 && (s[0] == 'T' || s[0] == 't') && int.TryParse(s[1..], out int n)) return n == t.Number;
        if (s.StartsWith('#') && s.Length == 7 && int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return PlotInk.Distance(rgb, t.Curve.Rgb) < 48;
        return string.Equals(s, t.Curve.StyleName, StringComparison.OrdinalIgnoreCase);
    }

    // ── values ─────────────────────────────────────────────────────────────────────────────────────────────

    private static PlotTraceRead Values(PlotTraceRead t, PlotRead plot, IReadOnlyList<PlotMarker> markers)
    {
        var samples = new List<PlotSample>(t.Curve.Points.Count);
        double resolution = 0;
        string unit = "";
        switch (plot.Type)
        {
            case PlotType.Rectangular when plot.Rect is { X: { } xa } rc && rc.AxisOn(t.Axis) is { } ya:
            {
                foreach (var (px, py) in t.Curve.Points) samples.Add(new PlotSample(xa.ValueAt(px), ya.ValueAt(py), default, px, py));
                double midY = t.Curve.Points.Count > 0 ? t.Curve.Points.Average(p => p.Y) : 0;
                resolution = ya.PerPixel(midY);
                unit = t.Assignment.Quantity switch
                {
                    PlotQuantityKind.Db or PlotQuantityKind.ReturnLoss or PlotQuantityKind.InsertionLoss => "dB",
                    PlotQuantityKind.PhaseDeg => "°",
                    PlotQuantityKind.PhaseRad => "rad",
                    _ => "",
                };
                // A marker's readout is an exact point: it replaces the pixel reading within one pixel of it.
                foreach (var m in markers.Where(m => m.Trace == t.Number && m.Value is not null))
                {
                    int k = Nearest(samples, m.X, m.Y);
                    if (k < 0) continue;
                    for (int j = 0; j < samples.Count; j++)
                        if (Math.Abs(samples[j].Px - samples[k].Px) <= 1)
                            samples[j] = samples[j] with { Y = m.Value!.Value, X = m.FrequencyHz ?? samples[j].X, Exact = j == k };
                }
                break;
            }
            case PlotType.Smith when plot.Smith is { } s:
                foreach (var (px, py) in t.Curve.Points) samples.Add(new PlotSample(double.NaN, 0, s.GammaAt(px, py), px, py));
                resolution = s.PerPixel;
                unit = "|Γ|";
                break;
            case PlotType.Polar when plot.Polar is { } pc:
                foreach (var (px, py) in t.Curve.Points) samples.Add(new PlotSample(double.NaN, 0, pc.ValueAt(px, py), px, py));
                resolution = pc.PerPixel;
                unit = "|S|";
                break;
        }
        // On a chart, a readout's complex value is the exact point at its marker.
        if (plot.Type != PlotType.Rectangular)
            foreach (var m in markers.Where(m => m.Trace == t.Number && m.Complex is not null && m.Placed))
            {
                int k = Nearest(samples, m.X, m.Y);
                if (k >= 0) samples[k] = samples[k] with { Value = m.Complex!.Value, Exact = true };
            }
        // A phase trace that runs past ±180° (or ±π) was drawn unwrapped.
        if (t.Assignment.IsPhase && samples.Count > 0)
        {
            double lim = t.Assignment.Quantity == PlotQuantityKind.PhaseRad ? Math.PI : 180;
            if (samples.Any(s => Math.Abs(s.Y) > lim * 1.02)) t = t with { Assignment = t.Assignment with { Unwrapped = true } };
        }
        return t with { Samples = samples, Resolution = resolution, ResolutionUnit = unit };
    }

    private static int Nearest(List<PlotSample> s, double x, double y)
    {
        int best = -1;
        double bd = double.MaxValue;
        for (int i = 0; i < s.Count; i++)
        {
            double d = (s[i].Px - x) * (s[i].Px - x) + (s[i].Py - y) * (s[i].Py - y);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // ── frequency along a chart trace (R-im14-8) ───────────────────────────────────────────────────────────

    /// <summary>The cumulative angle swept about the chart's centre along the samples, radians — the measure frequency
    /// is spread in proportion to.</summary>
    internal static double[] Swept(IReadOnlyList<PlotSample> s)
    {
        var a = new double[s.Count];
        for (int i = 1; i < s.Count; i++)
        {
            double d = Math.Abs(Math.IEEERemainder(s[i].Value.Phase - s[i - 1].Value.Phase, 2 * Math.PI));
            a[i] = a[i - 1] + d;
        }
        return a;
    }

    private static PlotTraceRead ChartFrequency(PlotTraceRead t, List<PlotTraceRead> all, List<PlotMarker> markers, PlotReadingOptions o,
                                                List<PlotFinding> findings)
    {
        var s = t.Samples.ToList();
        if (s.Count < 2) return t;
        var typed = o.Frequencies.LastOrDefault(f => Selects(f.Selector, t));

        // Direction by Foster: frequency runs clockwise — the signed sweep of arg Γ is negative.
        double signed = 0;
        for (int i = 1; i < s.Count; i++) signed += Math.IEEERemainder(s[i].Value.Phase - s[i - 1].Value.Phase, 2 * Math.PI);
        string direction = "clockwise";
        bool reverse = signed > 0;

        // 1. markers with frequencies
        var known = new List<(int Index, double F)>();
        foreach (var m in markers.Where(m => m.Trace == t.Number && m.FrequencyHz is not null))
        {
            int k = Nearest(s, m.X, m.Y);
            if (k >= 0) known.Add((k, m.FrequencyHz!.Value));
        }
        string source = "";
        var matched = Matched(t, all);
        if (known.Count >= 2)
        {
            known = [.. known.OrderBy(k => k.Index)];
            reverse = known[^1].F < known[0].F;
            direction = "markers";
            source = "markers";
        }
        else if (matched is { } match)
        {
            // 2. a rectangular trace of the same entry: frequency by matching |Γ| along both curves, in order.
            known = match.Known;
            reverse = match.Reverse;
            direction = "matched trace";
            source = $"matched to {match.Trace}";
            matched = null;
        }
        else if (known.Count == 1 && typed is null) known.Clear();

        // From here every anchor is in FORWARD order — the order frequency increases in — so one rule combines them.
        if (reverse) s.Reverse();
        int n = s.Count;
        List<(int Index, double F)> Forward(IEnumerable<(int Index, double F)> anchors) =>
            [.. anchors.Select(a => (reverse ? n - 1 - a.Index : a.Index, a.F)).OrderBy(a => a.Item1)];
        known = Forward(known);

        // Markers set the direction; a matched rectangular trace that agrees adds its anchors between and beyond them,
        // wherever frequency still increases through every anchor already held.
        if (direction == "markers" && matched is { } mt && mt.Reverse == reverse)
        {
            int held = known.Count;
            foreach (var a in Forward(mt.Known))
            {
                if (known.Any(k => Math.Abs(k.Index - a.Index) < 3)) continue;
                double below = known.Where(k => k.Index < a.Index).Select(k => k.F).DefaultIfEmpty(double.MinValue).Max();
                double above = known.Where(k => k.Index > a.Index).Select(k => k.F).DefaultIfEmpty(double.MaxValue).Min();
                if (a.F > below && a.F < above) known.Add(a);
            }
            if (known.Count > held) { known = [.. known.OrderBy(k => k.Index)]; source = $"markers + matched to {mt.Trace}"; }
        }
        var swept = Swept(s);

        // 3. a typed range: its ends at the trace's ends.
        if (typed is not null)
        {
            if (known.All(k => k.Index != 0)) known.Insert(0, (0, typed.StartHz));
            if (known.All(k => k.Index != s.Count - 1)) known.Add((s.Count - 1, typed.StopHz));
            source = source.Length == 0 ? "typed range" : source + " + typed range";
        }
        known = [.. known.OrderBy(k => k.Index).GroupBy(k => k.Index).Select(g => g.First())];
        if (known.Count < 2)
        {
            findings.Add(new("frequency", $"{t.Name} (plot {t.Plot}) has no frequency: no markers with readouts and no rectangular trace of "
                                          + $"{t.Assignment.Entry} to match — type a start and stop (--freq {t.Name}=<start>..<stop>)"));
            return t with { Samples = s };
        }

        // Between two known frequencies, by swept angle; outside the outermost, cut (nothing is extrapolated).
        var result = new List<PlotSample>();
        for (int i = known[0].Index; i <= known[^1].Index; i++)
        {
            int k = 0;
            while (k + 2 < known.Count && known[k + 1].Index < i) k++;
            var (ia, fa) = known[k];
            var (ib, fb) = known[k + 1];
            double span = swept[ib] - swept[ia];
            double u = span > 1e-12 ? (swept[i] - swept[ia]) / span : (ib == ia ? 0 : (double)(i - ia) / (ib - ia));
            result.Add(s[i] with { X = fa + u * (fb - fa) });
        }
        // Frequency must increase along the trace; a marker misread out of order would not.
        for (int i = 1; i < result.Count; i++)
            if (!(result[i].X >= result[i - 1].X)) result[i] = result[i] with { X = result[i - 1].X };
        return t with { Samples = result, Frequency = new TraceFrequency(source, direction, result[0].X, result[^1].X) };
    }

    private sealed record Match(string Trace, List<(int Index, double F)> Known, bool Reverse);

    /// <summary>
    /// A rectangular magnitude trace of the same entry, matched to the chart trace by |Γ| (R-im14-8 (2)): both sequences
    /// are matched in order (dynamic time warping, monotone), in both directions along the chart trace, and the better
    /// direction wins. Matched points where |Γ| changes fast enough along the rectangular trace to pin a frequency
    /// become anchors; the swept angle interpolates between them.
    /// </summary>
    private static Match? Matched(PlotTraceRead t, List<PlotTraceRead> all)
    {
        var rect = all.FirstOrDefault(r => !r.Parametric && r.Assignment.IsMagnitude && r.Assignment.I == t.Assignment.I
                                           && r.Assignment.J == t.Assignment.J && r.Samples.Count >= 8);
        if (rect is null || !t.Assignment.Assigned) return null;
        var rs = rect.Samples.OrderBy(p => p.X).ToList();
        var rf = rs.Select(p => p.X).ToArray();
        var rm = rs.Select(p => PlotQuantity.Magnitude(rect.Assignment.Quantity, p.Y)).ToArray();
        var cm = t.Samples.Select(p => p.Value.Magnitude).ToArray();
        if (rm.Max() - rm.Min() < 0.02) return null;   // |S| hardly varies: nothing to match on

        (double Cost, int[] Map) Dtw(double[] c)
        {
            int n = c.Length, m = rm.Length;
            var d = new double[n + 1, m + 1];
            for (int i = 0; i <= n; i++) for (int j = 0; j <= m; j++) d[i, j] = double.PositiveInfinity;
            d[0, 0] = 0;
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                    d[i, j] = Math.Abs(c[i - 1] - rm[j - 1]) + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));
            var map = new int[n];
            int ii = n, jj = m;
            while (ii > 0 && jj > 0)
            {
                map[ii - 1] = jj - 1;
                double a = d[ii - 1, jj - 1], b = d[ii - 1, jj], e = d[ii, jj - 1];
                if (a <= b && a <= e) { ii--; jj--; }
                else if (b <= e) ii--;
                else jj--;
            }
            return (d[n, m] / (n + m), map);
        }
        // Thin the chart samples so the matrix stays small.
        int step = Math.Max(1, cm.Length / 400);
        var idx = Enumerable.Range(0, cm.Length).Where(i => i % step == 0).ToArray();
        var fwd = Dtw([.. idx.Select(i => cm[i])]);
        var bwd = Dtw([.. idx.Reverse().Select(i => cm[i])]);
        bool reverse = bwd.Cost < fwd.Cost;
        var (cost, map) = reverse ? bwd : fwd;
        var ordered = reverse ? idx.Reverse().ToArray() : idx;

        // Anchors where |S| moves at least a tenth of its range over the neighbouring tenth of the band.
        var known = new List<(int Index, double F)>();
        double range = rm.Max() - rm.Min();
        int win = Math.Max(1, rm.Length / 20);
        for (int k = 0; k < ordered.Length; k++)
        {
            int j = map[k];
            int a = Math.Max(0, j - win), b = Math.Min(rm.Length - 1, j + win);
            if (Math.Abs(rm[b] - rm[a]) < 0.1 * range) continue;
            int forward = reverse ? cm.Length - 1 - ordered[k] : ordered[k];
            if (known.Count > 0 && (forward <= known[^1].Index || rf[j] <= known[^1].F)) continue;
            known.Add((forward, rf[j]));
        }
        if (known.Count < 2 || cost > 0.05) return null;
        // Handed back in the trace's own sample order, like a marker's anchor; the caller turns both forward once.
        return new Match(rect.Name, [.. known.Select(k => (reverse ? cm.Length - 1 - k.Index : k.Index, k.F))], reverse);
    }

    private static string Sha256(ImageSource source)
    {
        if (!string.IsNullOrEmpty(source.Sha256)) return source.Sha256;
        return Convert.ToHexString(SHA256.HashData(source.Raster.EncodePng())).ToLowerInvariant();
    }

    internal static string Fmt(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
