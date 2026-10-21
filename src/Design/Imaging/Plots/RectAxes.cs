// A rectangular plot's axes, calibrated — brief-img-14-sparameter-plots-to-touchstone.md R-im14-3.
//
// THE LABELS ARE PAIRED WITH THEIR TICKS. A tick label stands centred on its tick (under the x axis, left of the y axis),
// so its centre is a first estimate of the tick's pixel; the nearest grid line or tick mark within half the label's
// size is where it really is, to a fraction of a pixel.
//
// THE FIT IS ROBUST. A linear and a logarithmic mapping are each fitted by consensus: every pair of labels proposes a
// mapping, the one most labels agree with (within InlierPx) wins, and the inliers are refitted by least squares. One
// misread label — an 8 read as a 3 — therefore cannot bend the axis; it is reported as rejected. The better of the two
// mappings (more inliers, then the smaller residual) is the axis, and its residual is reported in pixels.
//
// FEWER THAN TWO LABELS means the axis waits for the user: two clicks on it and two typed values (Manual). Nothing is
// assumed about an axis that could not be read.
//
// A RIGHT-HAND Y AXIS (brief-pdg-1-two-y-axes.md R-pdg1-1) is the left one's mirror image: number words right of the
// frame, the innermost column, paired with the right frame line's own ticks and fitted by the same consensus. A plot whose
// left labels do not read and whose right ones do has its ONLY y axis on the right: that axis is stored as Y with YSide
// Right, so everything downstream that reads Y keeps reading it. Y2 is the second y axis of a plot whose first is left.

using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Design.Imaging.Plots;

public enum AxisScale { Linear, Log }

/// <summary>Which side of a rectangular plot a y axis stands on, and which y axis a trace is read against (R-pdg1-1,
/// R-pdg1-2).</summary>
public enum PlotSide { Left, Right }

/// <summary>A rectangular plot's axes as the user names them: x, its (first) y axis, and the second y axis on the right of
/// a plot whose first is on the left (R-pdg1-3). On a plot whose only y axis is on the right, that axis is Y.</summary>
public enum PlotAxis { X, Y, Y2 }

/// <summary>One tick label as read: where its tick is, what it says, and whether the fit kept it.</summary>
public sealed record AxisLabel(double Pixel, double Value, string Text, bool Used, PixelBox Box);

/// <summary>An axis's mapping from pixels to values (R-im14-3).</summary>
/// <param name="A">The value (or log10 value) at pixel 0.</param>
/// <param name="B">Per pixel.</param>
/// <param name="ResidualPx">The RMS distance of the kept labels from where the mapping puts them, pixels.</param>
/// <param name="Manual">Calibrated by two clicks and two typed values, not read.</param>
public sealed record AxisCalibration(AxisScale Scale, double A, double B, double ResidualPx, int Used, int Rejected, bool Manual)
{
    public double ValueAt(double px) => Scale == AxisScale.Log ? Math.Pow(10, A + B * px) : A + B * px;

    public double PixelAt(double v) => Scale == AxisScale.Log ? (Math.Log10(v) - A) / B : (v - A) / B;

    /// <summary>How much the value changes across one pixel at <paramref name="px"/> — the axis's resolution there.</summary>
    public double PerPixel(double px) => Math.Abs(ValueAt(px + 0.5) - ValueAt(px - 0.5));

    /// <summary>Two points and their values; null when they coincide or a log value is not positive.</summary>
    public static AxisCalibration? FromTwoPoints(double p0, double v0, double p1, double v1, AxisScale scale)
    {
        if (Math.Abs(p1 - p0) < 1) return null;
        if (scale == AxisScale.Log)
        {
            if (!(v0 > 0) || !(v1 > 0)) return null;
            (v0, v1) = (Math.Log10(v0), Math.Log10(v1));
        }
        if (v0 == v1) return null;
        double b = (v1 - v0) / (p1 - p0);
        return new AxisCalibration(scale, v0 - b * p0, b, 0, 2, 0, Manual: true);
    }
}

/// <summary>Two clicks and two typed values for one axis (R-im14-3's fallback).</summary>
/// <param name="Value0">In the axis's own units — for a frequency axis, hertz.</param>
/// <param name="Scale">The scale the values are mapped on; null for the axis's own — the forced one, else the one its
/// labels fit, else linear. The dialog's typed values were mapped linearly whatever the axis was, so correcting one label
/// of a log axis calibrated it as linear (designer round 24).</param>
public sealed record AxisClicks(double Pixel0, double Value0, double Pixel1, double Value1, AxisScale? Scale = null);

/// <summary>A rectangular plot's calibration: both axes, their labels and titles.</summary>
/// <param name="XUnit">The x axis's frequency unit as an SI multiplier (1e9 for GHz); every x value is in hertz.</param>
/// <param name="XUnitStated">The unit was read (an axis title, a label's suffix) or typed — not assumed.</param>
/// <param name="XIsFrequency">The x axis is frequency: its title says so, or nothing says otherwise.</param>
public sealed record RectCalibration(
    AxisCalibration? X, AxisCalibration? Y, IReadOnlyList<AxisLabel> XLabels, IReadOnlyList<AxisLabel> YLabels,
    string? XTitle, string? YTitle, string? Title, double XUnit, bool XUnitStated, bool XIsFrequency)
{
    /// <summary>The second y axis, on the right of a plot whose first is on the left (R-pdg1-1); null when none was read
    /// or typed.</summary>
    public AxisCalibration? Y2 { get; init; }

    public IReadOnlyList<AxisLabel> Y2Labels { get; init; } = [];

    public string? Y2Title { get; init; }

    /// <summary>The side <see cref="Y"/> stands on: Right on a plot whose only y axis is on the right.</summary>
    public PlotSide YSide { get; init; } = PlotSide.Left;

    /// <summary>The ink of each y axis's title, and of its labels where they are coloured — the colour a datasheet gives
    /// the axis of a trace drawn in it (R-pdg1-2 (3)); null where nothing was read.</summary>
    public int? YTitleRgb { get; init; }
    public int? Y2TitleRgb { get; init; }
    public int? YLabelRgb { get; init; }
    public int? Y2LabelRgb { get; init; }

    /// <summary>x and at least one y axis are calibrated.</summary>
    public bool Calibrated => X is not null && (Y is not null || Y2 is not null);

    /// <summary>The plot has a y axis on both sides: one read or typed on the right as well as the left — its traces each
    /// say which they are read against.</summary>
    public bool TwoY => YSide == PlotSide.Left && (Y2 is not null || Y2Labels.Count > 0);

    /// <summary>The y axis on <paramref name="side"/>, or null.</summary>
    public AxisCalibration? AxisOn(PlotSide side) => side == YSide ? Y : side == PlotSide.Right ? Y2 : null;

    /// <summary>The title of the y axis on <paramref name="side"/>, or null.</summary>
    public string? TitleOn(PlotSide side) => side == YSide ? YTitle : side == PlotSide.Right ? Y2Title : null;

    /// <summary>The labels of the y axis on <paramref name="side"/>.</summary>
    public IReadOnlyList<AxisLabel> LabelsOn(PlotSide side) => side == YSide ? YLabels : side == PlotSide.Right ? Y2Labels : [];

    /// <summary>The side a trace is read against when nothing says otherwise: the first y axis's, or the right's when only
    /// the right is calibrated.</summary>
    public PlotSide DefaultSide => Y is null && Y2 is not null ? PlotSide.Right : YSide;

    /// <summary>What the user calls the y axis on <paramref name="side"/>: Y for the first, Y2 for a second on the right.</summary>
    public PlotAxis AxisNamed(PlotSide side) => side == YSide ? PlotAxis.Y : PlotAxis.Y2;
}

public static class RectAxes
{
    /// <summary>An axis as flags and sentences spell it: <c>x</c>, <c>y</c>, <c>y2</c>.</summary>
    public static string Name(PlotAxis axis) => axis switch { PlotAxis.X => "x", PlotAxis.Y => "y", _ => "y2" };

    /// <summary>A label agrees with a mapping when it lies within this many pixels of where the mapping puts it.</summary>
    public const double InlierPx = 2.5;

    /// <summary>A frequency axis with no unit read is taken to be in GHz — a stated default (R-im14-3), one flag or
    /// click from being changed.</summary>
    public const double DefaultFrequencyUnit = 1e9;

    /// <summary>Frequency labels with no unit read that reach this value are taken as hertz, not GHz — stated the same
    /// way.</summary>
    public const double HertzLabels = 1e5;

    /// <summary>Calibrates <paramref name="frame"/> from <paramref name="words"/>, with <paramref name="x"/>,
    /// <paramref name="y"/> or <paramref name="y2"/> in place of an axis's reading where given;
    /// <paramref name="xLog"/>/<paramref name="yLog"/>/<paramref name="y2Log"/> force a scale. Pure.</summary>
    public static RectCalibration Calibrate(PlotFrame frame, IReadOnlyList<PlotWord> words, AxisClicks? x = null, AxisClicks? y = null,
                                            bool? xLog = null, bool? yLog = null, double? xUnit = null, AxisClicks? y2 = null, bool? y2Log = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        double width = frame.Right - frame.Left, height = frame.Bottom - frame.Top;
        var numbers = words.Where(w => w.Number is not null && !w.Vertical).ToList();

        // x labels: under the x axis, within the plot's span; y labels: left of the y axis, within its span.
        var xWords = numbers.Where(w => w.Box.Top > frame.Bottom && w.Box.Top - frame.Bottom < Math.Max(4 * w.Height, 0.12 * height)
                                        && w.Box.CentreX > frame.Left - 0.05 * width && w.Box.CentreX < frame.Right + 0.05 * width).ToList();
        // Only the first row under the axis: a second row is the title or another plot's.
        if (xWords.Count > 0)
        {
            int row = xWords.Min(w => w.Box.Top);
            xWords = [.. xWords.Where(w => w.Box.Top <= row + w.Height)];
        }
        var yWords = numbers.Where(w => w.Box.Right < frame.Left && frame.Left - w.Box.Right < Math.Max(3 * w.Box.Width, 0.15 * width)
                                        && w.Box.CentreY > frame.Top - 0.05 * height && w.Box.CentreY < frame.Bottom + 0.05 * height).ToList();
        if (yWords.Count > 0)
        {
            int col = yWords.Max(w => w.Box.Right);
            yWords = [.. yWords.Where(w => w.Box.Right >= col - 2 * w.Height)];
        }
        // y2 labels: the mirror image — right of the right frame line, the innermost column (R-pdg1-1).
        var y2Words = numbers.Where(w => w.Box.Left > frame.Right && w.Box.Left - frame.Right < Math.Max(3 * w.Box.Width, 0.15 * width)
                                         && w.Box.CentreY > frame.Top - 0.05 * height && w.Box.CentreY < frame.Bottom + 0.05 * height).ToList();
        if (y2Words.Count > 0)
        {
            int col = y2Words.Min(w => w.Box.Left);
            y2Words = [.. y2Words.Where(w => w.Box.Left <= col + 2 * w.Height)];
        }

        // Titles: under the x labels; left of the y labels (turned); above the plot.
        double xLabelBottom = xWords.Count > 0 ? xWords.Max(w => w.Box.Bottom) : frame.Bottom;
        string? xTitle = words.Where(w => w.Read && w.Number is null && !w.Vertical && w.Box.Top > xLabelBottom
                                          && w.Box.Top - xLabelBottom < 0.25 * height
                                          && w.Box.CentreX > frame.Left && w.Box.CentreX < frame.Right)
                              .OrderBy(w => w.Box.Top).Select(w => w.Text).FirstOrDefault();
        double yLabelLeft = yWords.Count > 0 ? yWords.Min(w => w.Box.Left) : frame.Left;
        string? yTitle = words.Where(w => w.Read && w.Vertical && w.Box.Right < yLabelLeft + 2 && yLabelLeft - w.Box.Right < 0.3 * width
                                          && w.Box.CentreY > frame.Top && w.Box.CentreY < frame.Bottom)
                              .OrderByDescending(w => w.Box.Right).Select(w => w.Text).FirstOrDefault();
        double y2LabelRight = y2Words.Count > 0 ? y2Words.Max(w => w.Box.Right) : frame.Right;
        string? y2Title = words.Where(w => w.Read && w.Vertical && w.Box.Left > y2LabelRight - 2 && w.Box.Left - y2LabelRight < 0.3 * width
                                           && w.Box.CentreY > frame.Top && w.Box.CentreY < frame.Bottom)
                               .OrderBy(w => w.Box.Left).Select(w => w.Text).FirstOrDefault();
        // A title's colour says which trace its axis is, read or not (R-pdg1-2 (3)).
        var yTitleInk = words.Where(w => w.Vertical && w.Box.Right < yLabelLeft + 2 && yLabelLeft - w.Box.Right < 0.3 * width
                                         && w.Box.CentreY > frame.Top && w.Box.CentreY < frame.Bottom)
                             .OrderByDescending(w => w.Box.Right).FirstOrDefault();
        var y2TitleInk = words.Where(w => w.Vertical && w.Box.Left > y2LabelRight - 2 && w.Box.Left - y2LabelRight < 0.3 * width
                                          && w.Box.CentreY > frame.Top && w.Box.CentreY < frame.Bottom)
                              .OrderBy(w => w.Box.Left).FirstOrDefault();
        string? title = words.Where(w => w.Read && w.Number is null && !w.Vertical && w.Box.Bottom < frame.Top
                                         && frame.Top - w.Box.Bottom < 0.25 * height && w.Box.CentreX > frame.Left && w.Box.CentreX < frame.Right)
                             .OrderByDescending(w => w.Box.Bottom).Select(w => w.Text).FirstOrDefault();

        var xLabels = Pair(xWords, frame.XTicks, horizontal: true);
        var yLabels = Pair(yWords, frame.YTicks, horizontal: false);
        var (xCal, xUsed) = x is { } cx ? (FromClicks(cx, xLog, xLabels), xLabels)
                                        : Fit(xLabels, xLog);
        var y2Labels = Pair(y2Words, frame.Y2Ticks, horizontal: false);
        // The only y axis on the right: not one number read left of the frame, the right labels fitting. Its clicks are
        // then the y axis's (y2's when only those were typed), since it IS the y axis.
        bool rightOnly = yLabels.Count == 0 && !(y is not null && y2 is not null) && Fit(y2Labels, y2Log ?? yLog).Fit is not null;
        if (rightOnly)
        {
            (y, y2, yLog, yWords, yLabels) = (y ?? y2, null, y2Log ?? yLog, y2Words, y2Labels);
            y2Words = [];
            y2Labels = [];
        }
        var (yCal, yUsed) = y is { } cy ? (FromClicks(cy, yLog, yLabels), yLabels)
                                        : Fit(yLabels, yLog);
        var (y2Cal, y2Used) = y2 is { } c2 ? (FromClicks(c2, y2Log ?? yLog, y2Labels), y2Labels)
                                           : Fit(y2Labels, y2Log ?? yLog);

        // The frequency unit: typed, else a label's own suffix, else the title's, else assumed GHz.
        bool isFrequency = xTitle is null || PlotQuantity.IsFrequencyTitle(xTitle) || xWords.Any(w => w.UnitScale is not null);
        double unit = 1;
        bool stated = true;
        if (x is not null) unit = 1;                                  // clicks are typed in hertz
        else if (xUnit is { } u) unit = u;
        else if (xWords.Any(w => w.UnitScale is not null)) unit = 1;  // the labels carry their multipliers
        else if (xTitle is not null && PlotQuantity.FrequencyUnit(xTitle) is { } tu) unit = tu;
        // Labels running past 1e5 are hertz already (1e8, 1e9, 1e10): no RF plot is labelled to 100 000 GHz.
        else if (isFrequency && xLabels.Count > 0 && xLabels.Max(l => Math.Abs(l.Value)) >= HertzLabels) { unit = 1; stated = false; }
        else if (isFrequency) { unit = DefaultFrequencyUnit; stated = false; }
        if (xCal is not null && unit != 1)
            xCal = xCal.Scale == AxisScale.Log
                ? xCal with { A = xCal.A + Math.Log10(unit), B = xCal.B }
                : xCal with { A = xCal.A * unit, B = xCal.B * unit };
        if (rightOnly)
            return new RectCalibration(xCal, yCal, xUsed, yUsed, xTitle, y2Title, title, unit, stated, isFrequency)
            {
                YSide = PlotSide.Right, YTitleRgb = Ink(y2TitleInk), YLabelRgb = LabelInk(yWords),
            };
        return new RectCalibration(xCal, yCal, xUsed, yUsed, xTitle, yTitle, title, unit, stated, isFrequency)
        {
            YTitleRgb = Ink(yTitleInk), YLabelRgb = LabelInk(yWords),
            Y2 = y2Cal, Y2Labels = y2Used, Y2Title = y2Title, Y2TitleRgb = Ink(y2TitleInk), Y2LabelRgb = LabelInk(y2Words),
        };
    }

    /// <summary>A word's ink when it is coloured — not black, white or grey — else null: only a colour says which trace an
    /// axis belongs to.</summary>
    private static int? Ink(PlotWord? w) => w is not null && Chromatic(w.Rgb) ? w.Rgb : null;

    /// <summary>The labels' ink when most of them are one colour, else null.</summary>
    private static int? LabelInk(List<PlotWord> labels)
    {
        var coloured = labels.Where(w => Chromatic(w.Rgb)).ToList();
        if (coloured.Count == 0 || 2 * coloured.Count <= labels.Count) return null;
        return coloured.GroupBy(w => w.Rgb & 0xF0F0F0).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().First().Rgb;
    }

    /// <summary>A colour, as opposed to black, white or a grey: its channels differ (the test PlotReading's label evidence
    /// applies to a word in a trace's colour).</summary>
    public static bool Chromatic(int rgb) =>
        PlotInk.Distance(rgb, 0) > 60 && PlotInk.Distance(rgb, 0xFFFFFF) > 60
        && Math.Abs(((rgb >> 16) & 0xFF) - ((rgb >> 8) & 0xFF)) + Math.Abs(((rgb >> 8) & 0xFF) - (rgb & 0xFF)) > 40;

    /// <summary>Each label at its tick: the nearest tick within half the label's extent along the axis, else the
    /// label's own centre.</summary>
    private static List<AxisLabel> Pair(List<PlotWord> words, IReadOnlyList<double> ticks, bool horizontal)
    {
        var result = new List<AxisLabel>();
        foreach (var w in words.OrderBy(w => horizontal ? w.Box.CentreX : w.Box.CentreY))
        {
            double centre = horizontal ? w.Box.CentreX - 0.5 : w.Box.CentreY - 0.5;
            double half = 0.5 * (horizontal ? w.Box.Width : w.Box.Height) + 1;
            double at = centre, best = double.MaxValue;
            foreach (double t in ticks)
                if (Math.Abs(t - centre) <= half && Math.Abs(t - centre) < best) { best = Math.Abs(t - centre); at = t; }
            result.Add(new AxisLabel(at, w.Number!.Value, w.Text, false, w.Box));
        }
        return result;
    }

    /// <summary>An axis calibrated by typed values, on <see cref="AxisClicks.Scale"/> or else the axis's own scale:
    /// <paramref name="log"/> when forced, else the one <paramref name="labels"/> fit, else linear. A pair that cannot be
    /// logarithmic (a value at or below zero) is linear unless log was asked for by name.</summary>
    private static AxisCalibration? FromClicks(AxisClicks c, bool? log, IReadOnlyList<AxisLabel> labels)
    {
        var scale = c.Scale ?? (log is { } l ? (l ? AxisScale.Log : AxisScale.Linear) : Fit(labels).Fit?.Scale ?? AxisScale.Linear);
        var cal = AxisCalibration.FromTwoPoints(c.Pixel0, c.Value0, c.Pixel1, c.Value1, scale);
        return cal is null && scale == AxisScale.Log && c.Scale is null && log is null
            ? AxisCalibration.FromTwoPoints(c.Pixel0, c.Value0, c.Pixel1, c.Value1, AxisScale.Linear)
            : cal;
    }

    /// <summary>The better of a linear and a logarithmic consensus fit (or the forced one), and the labels marked
    /// used or rejected; null with fewer than two labels.</summary>
    public static (AxisCalibration? Fit, List<AxisLabel> Labels) Fit(IReadOnlyList<AxisLabel> labels, bool? log = null)
    {
        var list = labels.ToList();
        if (list.Count < 2) return (null, list);
        (AxisCalibration Fit, bool[] Inlier)? best = null;
        foreach (var scale in new[] { AxisScale.Linear, AxisScale.Log })
        {
            if (log is { } l && l != (scale == AxisScale.Log)) continue;
            if (scale == AxisScale.Log && list.Count(a => a.Value > 0) < 2) continue;
            if (Consensus(list, scale) is not { } c) continue;
            int n = c.Inlier.Count(b => b), bn = best?.Inlier.Count(b => b) ?? -1;
            if (best is null || n > bn || (n == bn && c.Fit.ResidualPx < best.Value.Fit.ResidualPx)) best = c;
        }
        if (best is not { } b) return (null, list);
        return (b.Fit, [.. list.Select((a, i) => a with { Used = b.Inlier[i] })]);
    }

    private static (AxisCalibration Fit, bool[] Inlier)? Consensus(List<AxisLabel> labels, AxisScale scale)
    {
        double V(AxisLabel a) => scale == AxisScale.Log ? (a.Value > 0 ? Math.Log10(a.Value) : double.NaN) : a.Value;
        int n = labels.Count;
        bool[]? bestIn = null;
        int bestCount = 0;
        double bestRes = double.MaxValue;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double vi = V(labels[i]), vj = V(labels[j]);
                if (double.IsNaN(vi) || double.IsNaN(vj) || vi == vj || Math.Abs(labels[j].Pixel - labels[i].Pixel) < 4) continue;
                double b = (vj - vi) / (labels[j].Pixel - labels[i].Pixel), a = vi - b * labels[i].Pixel;
                var inl = new bool[n];
                int count = 0;
                double res = 0;
                for (int k = 0; k < n; k++)
                {
                    double vk = V(labels[k]);
                    if (double.IsNaN(vk)) continue;
                    double d = Math.Abs((vk - a) / b - labels[k].Pixel);
                    if (d <= InlierPx) { inl[k] = true; count++; res += d * d; }
                }
                if (count > bestCount || (count == bestCount && res < bestRes)) (bestIn, bestCount, bestRes) = (inl, count, res);
            }
        if (bestIn is null || bestCount < 2) return null;
        // Two labels agreeing prove nothing when more were read: a consensus is at least half of them.
        if (n >= 3 && bestCount < Math.Max(2, (n + 1) / 2)) return null;

        // Least squares on the inliers: value = a + b·pixel.
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int m = 0;
        for (int k = 0; k < n; k++)
        {
            if (!bestIn[k]) continue;
            double p = labels[k].Pixel, v = V(labels[k]);
            sx += p; sy += v; sxx += p * p; sxy += p * v; m++;
        }
        double den = m * sxx - sx * sx;
        if (Math.Abs(den) < 1e-12) return null;
        double bb = (m * sxy - sx * sy) / den, aa = (sy - bb * sx) / m;
        double rr = 0;
        for (int k = 0; k < n; k++)
            if (bestIn[k]) rr += Math.Pow((V(labels[k]) - aa) / bb - labels[k].Pixel, 2);
        return (new AxisCalibration(scale, aa, bb, Math.Round(Math.Sqrt(rr / m), 3), m, n - m, false), bestIn);
    }
}
