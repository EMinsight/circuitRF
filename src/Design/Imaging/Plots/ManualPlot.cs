// A plot defined by hand — brief-pdg-4-digitise-by-hand.md R-pdg4-1, R-pdg4-2.
//
// THE USER'S BOX AND THE USER'S SCALE ARE THE PLOT. No frame is found and no word is read: the box's left edge is x-low,
// its right edge x-high, its bottom y-low and its top y-high (the right edge's bottom and top the right-hand scale's), and
// each axis is AxisCalibration.FromTwoPoints on those edges. Text in the picture is ignored by construction — a manual plot
// is read with no words at all, so no legend, title or label can say anything about it.
//
// A ROTATED BOX IS RESAMPLED, NOT TAUGHT TO THE PIPELINE. The box is a quad (four corners, picture pixels); its interior
// is mapped bilinearly onto an axis-aligned rectangle of the box's own width and height centred where the quad is centred
// — "box space". Everything downstream (the tracer, the calibrations, the probe, the CSV, the Touchstone) reads box space
// with plain axis-aligned calibrations, and ToBox / FromBox are the one place a picture pixel and a box pixel are carried
// into each other: a point the user places goes in through ToBox, and everything drawn comes back through FromBox. An
// axis-aligned rectangle's box space IS picture space, exactly, so the common case resamples nothing. A quad is general
// enough for a mild perspective (a photographed page); the dialog only makes rotated rectangles.

using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>A box's four corners, picture pixels, in the order the axes name them.</summary>
public sealed record ManualQuad((double X, double Y) BottomLeft, (double X, double Y) BottomRight, (double X, double Y) TopRight,
                                (double X, double Y) TopLeft)
{
    /// <summary>An axis-aligned rectangle (any two opposite corners) turned <paramref name="angleDeg"/> about its centre —
    /// a positive angle turns it counter-clockwise as seen on the picture.</summary>
    public static ManualQuad Rectangle(double x0, double y0, double x1, double y1, double angleDeg = 0)
    {
        double l = Math.Min(x0, x1), r = Math.Max(x0, x1), t = Math.Min(y0, y1), b = Math.Max(y0, y1);
        double cx = (l + r) / 2, cy = (t + b) / 2;
        double a = angleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        // Screen y runs down: counter-clockwise as seen takes a point right of the centre upwards.
        (double X, double Y) Turn(double x, double y)
        {
            double dx = x - cx, dy = y - cy;
            return (cx + dx * c + dy * s, cy - dx * s + dy * c);
        }
        return new ManualQuad(Turn(l, b), Turn(r, b), Turn(r, t), Turn(l, t));
    }

    public (double X, double Y) Centre =>
        ((BottomLeft.X + BottomRight.X + TopRight.X + TopLeft.X) / 4, (BottomLeft.Y + BottomRight.Y + TopRight.Y + TopLeft.Y) / 4);

    /// <summary>The mean length of the bottom and top edges, pixels.</summary>
    public double Width => (Dist(BottomLeft, BottomRight) + Dist(TopLeft, TopRight)) / 2;

    /// <summary>The mean length of the left and right edges, pixels.</summary>
    public double Height => (Dist(BottomLeft, TopLeft) + Dist(BottomRight, TopRight)) / 2;

    /// <summary>How far the bottom edge is turned from level, counter-clockwise as seen, degrees.</summary>
    public double AngleDeg => -Math.Atan2(BottomRight.Y - BottomLeft.Y, BottomRight.X - BottomLeft.X) * 180 / Math.PI;

    /// <summary>The corners in drawing order, closing nowhere: bottom-left, bottom-right, top-right, top-left.</summary>
    public IReadOnlyList<(double X, double Y)> Corners => [BottomLeft, BottomRight, TopRight, TopLeft];

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>One typed scale: the values at the box's two edges, in the axis's own units (hertz on a frequency x).</summary>
public sealed record ManualAxis(double Low, double High, AxisScale Scale);

/// <summary>How a manual curve is sampled between its fixpoints (R-pdg4-3).</summary>
public enum ManualSampleMethod
{
    /// <summary>Straight between fixpoints — in log x on a log x axis, in log y on a log y axis.</summary>
    Linear,
    /// <summary>A monotone piecewise cubic (Fritsch–Carlson, PCHIP) in the same coordinates: it never overshoots between
    /// two points the user placed.</summary>
    Smooth,
}

/// <summary>The x grid a manual curve is sampled on (R-pdg4-3): <see cref="AssemblyGrid"/>'s start, stop, points and log,
/// or a step in place of the points. A start or stop not given is the curve's first or last fixpoint; a log not given
/// follows the x axis.</summary>
public sealed record ManualSampleSpec(double? Start = null, double? Stop = null, int Points = ManualSampleSpec.DefaultPoints, double? Step = null,
                                      bool? Log = null, ManualSampleMethod Method = ManualSampleMethod.Linear)
{
    public const int DefaultPoints = 101;

    public static ManualSampleSpec Default { get; } = new();
}

/// <summary>A plot defined by hand (R-pdg4-1).</summary>
/// <param name="Picture">The session picture it is on, 0-based.</param>
/// <param name="Box">Its four corners, picture pixels.</param>
/// <param name="Y2">The right-hand scale; null for a plot with one y scale.</param>
/// <param name="XUnit">The x axis's display unit as an SI multiplier (1e9 for GHz); the typed values are already in hertz.</param>
public sealed record ManualPlot(int Picture, ManualQuad Box, ManualAxis X, ManualAxis Y, ManualAxis? Y2, double XUnit, bool XIsFrequency)
{
    /// <summary>How its curves are sampled.</summary>
    public ManualSampleSpec Sampling { get; init; } = ManualSampleSpec.Default;

    /// <summary>The y scale's unit label (<c>dB</c>, <c>°</c>, free text) — a Data curve's CSV <c>y_unit</c>.</summary>
    public string YUnit { get; init; } = "";

    /// <summary>The right-hand scale's unit label.</summary>
    public string Y2Unit { get; init; } = "";

    // ── box space ──

    public double Left => Box.Centre.X - Box.Width / 2;
    public double Right => Box.Centre.X + Box.Width / 2;
    public double Top => Box.Centre.Y - Box.Height / 2;
    public double Bottom => Box.Centre.Y + Box.Height / 2;

    /// <summary>The box is an axis-aligned rectangle: box space is picture space and nothing is resampled.</summary>
    public bool Axial
    {
        get
        {
            const double eps = 1e-9;
            var (bl, br, tr, tl) = (Box.BottomLeft, Box.BottomRight, Box.TopRight, Box.TopLeft);
            return Math.Abs(bl.Y - br.Y) < eps && Math.Abs(tl.Y - tr.Y) < eps && Math.Abs(bl.X - tl.X) < eps && Math.Abs(br.X - tr.X) < eps
                   && br.X > bl.X && bl.Y > tl.Y;
        }
    }

    /// <summary>A box-space point on the picture: the bilinear map of the quad (extended outside it the same way).</summary>
    public (double X, double Y) FromBox(double bx, double by)
    {
        if (Axial) return (bx, by);
        double w = Box.Width, h = Box.Height;
        double u = (bx - Left) / w, v = (Bottom - by) / h;
        return Bilinear(u, v);
    }

    /// <summary>A picture point in box space: <see cref="FromBox"/> inverted (Newton's method; exact in one step for a
    /// rotated rectangle).</summary>
    public (double X, double Y) ToBox(double px, double py)
    {
        if (Axial) return (px, py);
        double u = 0.5, v = 0.5;
        for (int k = 0; k < 30; k++)
        {
            var (x, y) = Bilinear(u, v);
            double ex = x - px, ey = y - py;
            if (ex * ex + ey * ey < 1e-20) break;
            // ∂P/∂u and ∂P/∂v of the bilinear patch.
            var (bl, br, tr, tl) = (Box.BottomLeft, Box.BottomRight, Box.TopRight, Box.TopLeft);
            double xu = (1 - v) * (br.X - bl.X) + v * (tr.X - tl.X), yu = (1 - v) * (br.Y - bl.Y) + v * (tr.Y - tl.Y);
            double xv = (1 - u) * (tl.X - bl.X) + u * (tr.X - br.X), yv = (1 - u) * (tl.Y - bl.Y) + u * (tr.Y - br.Y);
            double det = xu * yv - xv * yu;
            if (Math.Abs(det) < 1e-12) break;
            u -= (ex * yv - ey * xv) / det;
            v -= (xu * ey - yu * ex) / det;
        }
        return (Left + u * Box.Width, Bottom - v * Box.Height);
    }

    private (double X, double Y) Bilinear(double u, double v)
    {
        var (bl, br, tr, tl) = (Box.BottomLeft, Box.BottomRight, Box.TopRight, Box.TopLeft);
        return ((1 - u) * (1 - v) * bl.X + u * (1 - v) * br.X + u * v * tr.X + (1 - u) * v * tl.X,
                (1 - u) * (1 - v) * bl.Y + u * (1 - v) * br.Y + u * v * tr.Y + (1 - u) * v * tl.Y);
    }

    /// <summary>The synthetic axis-aligned frame the session carries for the box, in box space (R-pdg4-1): no lines, no
    /// ticks — nothing was found.</summary>
    public PlotFrame Frame(int index) => new()
    {
        Index = index, Type = PlotType.Rectangular,
        Box = new PixelBox((int)Math.Floor(Left), (int)Math.Floor(Top), (int)Math.Ceiling(Right), (int)Math.Ceiling(Bottom)),
        Left = Left, Top = Top, Right = Right, Bottom = Bottom, Pen = 1,
    };

    /// <summary>Why the typed scales cannot calibrate the box, or null: a scale whose two values coincide, a log scale with
    /// a value at or below zero, a box too small to tell its edges apart.</summary>
    public string? Refusal
    {
        get
        {
            if (Box.Width < 4 || Box.Height < 4) return "the box is too small: drag it across the plot's axes";
            string? Bad(string name, ManualAxis? a) => a is null ? null
                : !double.IsFinite(a.Low) || !double.IsFinite(a.High) ? $"{name} needs two numbers"
                : a.Low == a.High ? $"{name}'s low and high are the same value"
                : a.Scale == AxisScale.Log && !(a.Low > 0 && a.High > 0) ? $"{name} is logarithmic, so both its values must be above zero"
                : null;
            return Bad("the x scale", X) ?? Bad("the y scale", Y) ?? Bad("the right-hand y scale", Y2);
        }
    }

    /// <summary>The box's axes, calibrated from the typed scales on its edges; an axis that cannot be is null and the plot
    /// waits (<see cref="Refusal"/> says why).</summary>
    public RectCalibration Calibrate()
    {
        AxisCalibration? Axis(ManualAxis? a, double p0, double p1) => a is null ? null : AxisCalibration.FromTwoPoints(p0, a.Low, p1, a.High, a.Scale);
        return new RectCalibration(Axis(X, Left, Right), Axis(Y, Bottom, Top), [], [], null, null, null,
                                   XIsFrequency ? XUnit : 1, true, XIsFrequency)
        {
            Y2 = Axis(Y2, Bottom, Top),
        };
    }

    /// <summary>The picture in box space: every pixel of the result sampled (bilinearly) at its <see cref="FromBox"/>
    /// point, an edge pixel standing in beyond the picture. The picture itself when the box is axis-aligned.</summary>
    public RasterImage Resample(RasterImage picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (Axial) return picture;
        int w = picture.Width, h = picture.Height;
        var src = picture.Rgba;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (sx, sy) = FromBox(x + 0.5, y + 0.5);
                double fx = Math.Clamp(sx - 0.5, 0, w - 1), fy = Math.Clamp(sy - 0.5, 0, h - 1);
                int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
                double ax = fx - x0, ay = fy - y0;
                int o = (y * w + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = src[(y0 * w + x0) * 4 + c] * (1 - ax) + src[(y0 * w + x1) * 4 + c] * ax;
                    double bottom = src[(y1 * w + x0) * 4 + c] * (1 - ax) + src[(y1 * w + x1) * 4 + c] * ax;
                    rgba[o + c] = (byte)Math.Clamp(Math.Round(top * (1 - ay) + bottom * ay), 0, 255);
                }
            }
        return new RasterImage(w, h, rgba) { ReductionFactor = picture.ReductionFactor };
    }
}

/// <summary>A curve defined by hand (R-pdg4-2): its fixpoints ARE the curve.</summary>
/// <param name="Name">What the user calls it — <c>Curve 1</c>, <c>S21</c>; the CSV's trace column.</param>
/// <param name="Plot">The session plot it is on, 1-based.</param>
/// <param name="Fixpoints">Picture pixels, in the order placed.</param>
/// <param name="Axis">The y scale it is read on.</param>
/// <param name="Assignment">What it is: <see cref="PlotAssignment.None"/> for plain data (the dialog's <i>Data</i>), else an
/// entry and quantity the Touchstone can take.</param>
/// <param name="Rgb">Its colour, 0xRRGGBB — the swatch, and what a follow matched.</param>
public sealed record ManualCurve(string Name, int Plot, IReadOnlyList<(double Px, double Py)> Fixpoints, PlotSide Axis,
                                 PlotAssignment Assignment, int Rgb);
