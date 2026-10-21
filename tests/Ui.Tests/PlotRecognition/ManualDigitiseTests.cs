// brief-pdg-4-digitise-by-hand.md §5 — a plot read by hand, one test per claim. The picture is circuitRF's own plotter's
// (PlotPictureSet's s21lowpass: a known curve on known axes); the frame finder and the automatic calibration are used only
// HERE, as the truth the hand-made reading is checked against — the reading under test finds no frame and reads no word.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class ManualDigitiseTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    /// <summary>The low-pass picture, its frame, and its axes as the automatic reading calibrates them from its labels.</summary>
    private (ImageSource Source, PlotFrame Frame, AxisCalibration X, AxisCalibration Y) Truth()
    {
        string png = pictures.Picture("s21lowpass");
        var source = ImageSource.FromFile(png).Source!;
        var frame = PlotReading.FindPlots(source).Single();
        var r = PlotPictureSet.Read(null, png).Plots[0].Rect!;
        return (source, frame, r.X!, r.Y!);
    }

    /// <summary>The typed scales: the truth's values at the frame's edges, to the round numbers the plotter put there.</summary>
    private static ManualPlot Typed(PlotFrame f, AxisCalibration x, AxisCalibration y, ManualQuad? box = null) =>
        new(0, box ?? ManualQuad.Rectangle(f.Left, f.Top, f.Right, f.Bottom),
            new ManualAxis(Round(x.ValueAt(f.Left), 1e8), Round(x.ValueAt(f.Right), 1e8), AxisScale.Linear),
            new ManualAxis(Round(y.ValueAt(f.Bottom), 0.1), Round(y.ValueAt(f.Top), 0.1), AxisScale.Linear), null, 1e9, true);

    private static double Round(double v, double step) => Math.Round(v / step) * step;

    private static PlotSession ReadByHand(ImageSource source, ManualPlot plot, params ManualCurve[] curves) =>
        PlotReading.Read([new PlotPictureInput(source, "manual.png")], new PlotReadingOptions { Manual = [plot], ManualCurves = curves });

    private static ManualCurve Curve(params (double, double)[] points) => new("Curve 1", 1, points, PlotSide.Left, PlotAssignment.None, 0x1F77B4);

    /// <summary>Where the drawn curve is at picture column <paramref name="px"/>: its known S21 through the truth's axes.</summary>
    private static double CurveAt(double px, AxisCalibration x, AxisCalibration y) =>
        y.PixelAt(20 * Math.Log10(PlotPictureSet.LowPassS21(x.ValueAt(px)).Magnitude));

    [Fact]
    public void TypedScales_ReadAHandPlacedPoint_OnAPictureWithItsLabelsPaintedOut()
    {
        var (source, f, x, y) = Truth();
        var painted = Paint(source, f);
        // The labels are gone: the automatic reading has nothing to calibrate the plot with.
        Assert.DoesNotContain(PlotReading.Read([new PlotPictureInput(painted, "painted.png")]).Plots, p => p.Calibrated);

        double px = f.Left + 0.37 * (f.Right - f.Left), py = CurveAt(px, x, y);
        var s = ReadByHand(painted, Typed(f, x, y), Curve((px, py)));
        Assert.Empty(s.Words[0]);
        var sample = s.Traces.Single().Samples.Single();
        output.WriteLine($"x {sample.X} vs {x.ValueAt(px)}, y {sample.Y} vs {y.ValueAt(py)}");
        Assert.InRange(Math.Abs(sample.X - x.ValueAt(px)), 0, x.PerPixel(px));
        Assert.InRange(Math.Abs(sample.Y - y.ValueAt(py)), 0, y.PerPixel(py));
    }

    [Fact]
    public void ABoxTurned3Degrees_OnATurnedPicture_ReadsWhatTheLevelOneReads()
    {
        var (source, f, x, y) = Truth();
        double cx = (f.Left + f.Right) / 2, cy = (f.Top + f.Bottom) / 2;
        var turned = Turn(source, cx, cy, 3);
        var level = Typed(f, x, y);
        var tilted = Typed(f, x, y, ManualQuad.Rectangle(f.Left, f.Top, f.Right, f.Bottom, 3));

        // A hand-placed point, where the picture turned it.
        double px = f.Left + 0.6 * (f.Right - f.Left), py = CurveAt(px, x, y);
        var a = ReadByHand(source, level, Curve((px, py))).Traces.Single().Samples.Single();
        var b = ReadByHand(turned, tilted, Curve(TurnPoint(px, py, cx, cy, 3))).Traces.Single().Samples.Single();
        Assert.InRange(Math.Abs(a.X - b.X), 0, x.PerPixel(px));
        Assert.InRange(Math.Abs(a.Y - b.Y), 0, y.PerPixel(py));

        // And the resampled picture is followed to the same curve: both read on one grid agree to a pixel's worth.
        double sx = f.Left + 0.4 * (f.Right - f.Left);
        var levelFollow = ManualCurves.Follow(PlotInk.Of(source.Raster), level, (sx, CurveAt(sx, x, y)));
        var tiltedFollow = ManualCurves.Follow(PlotInk.Of(turned.Raster), tilted, TurnPoint(sx, CurveAt(sx, x, y), cx, cy, 3));
        Assert.True(levelFollow.Ok, levelFollow.Refusal);
        Assert.True(tiltedFollow.Ok, tiltedFollow.Refusal);
        var grid = new ManualSampleSpec(1.5e9, 4.5e9, 31);
        var ya = ReadByHand(source, level with { Sampling = grid }, Curve([.. levelFollow.Fixpoints])).Traces.Single().Samples;
        var yb = ReadByHand(turned, tilted with { Sampling = grid }, Curve([.. tiltedFollow.Fixpoints])).Traces.Single().Samples;
        Assert.Equal(ya.Count, yb.Count);
        double worst = ya.Zip(yb).Max(p => Math.Abs(p.First.Y - p.Second.Y) / y.PerPixel(p.First.Py));
        output.WriteLine($"worst |Δy| {worst:0.###} px");
        Assert.InRange(worst, 0, 1);
    }

    [Fact]
    public void LogAxes_APointHalfwayBetweenTwoDecades_ReadsTheirGeometricMean()
    {
        var plot = new ManualPlot(0, ManualQuad.Rectangle(100, 50, 500, 350), new ManualAxis(1e8, 1e10, AxisScale.Log),
                                  new ManualAxis(0.1, 10, AxisScale.Log), null, 1e9, true);
        // Half a decade along each: x between 1e8 and 1e9, y between 0.1 and 1.
        var sample = ReadByHand(Blank(), plot, Curve((200, 275))).Traces.Single().Samples.Single();
        Assert.Equal(Math.Sqrt(1e8 * 1e9), sample.X, 1e-6 * sample.X);
        Assert.Equal(Math.Sqrt(0.1 * 1), sample.Y, 1e-9);
    }

    [Fact]
    public void TwoScales_ACurveOnTheRight_IsReadThroughTheRightScale()
    {
        var plot = new ManualPlot(0, ManualQuad.Rectangle(100, 50, 500, 350), new ManualAxis(1e9, 5e9, AxisScale.Linear),
                                  new ManualAxis(-40, 0, AxisScale.Linear), new ManualAxis(-180, 180, AxisScale.Linear), 1e9, true);
        var t = ReadByHand(Blank(), plot, new ManualCurve("phase", 1, [(300, 125)], PlotSide.Right, PlotAssignment.None, 0xD62728)).Traces.Single();
        Assert.Equal(PlotSide.Right, t.Axis);
        Assert.Equal("user", t.AxisEvidence);
        Assert.Equal(90, t.Samples.Single().Y, 1e-9);   // a quarter of the way down from +180 on −180…180 (−10 dB on the left)
    }

    [Fact]
    public void Follow_FromOneClickOnTheCurve_ReturnsFixpointsOnIt_AndNothingFromBlankPaper()
    {
        var (source, f, x, y) = Truth();
        var plot = Typed(f, x, y);
        var ink = PlotInk.Of(source.Raster);
        double sx = f.Left + 0.4 * (f.Right - f.Left);
        var followed = ManualCurves.Follow(ink, plot, (sx, CurveAt(sx, x, y)));
        Assert.True(followed.Ok, followed.Refusal);
        Assert.InRange(followed.Fixpoints.Count, 5, 80);
        // On the curve: within a pixel of the centre of the curve's own ink in the fixpoint's pixel column.
        double worst = followed.Fixpoints.Max(p => Math.Abs(p.Py - InkCentre(source.Raster, followed.Rgb, p.Px, p.Py)));
        output.WriteLine($"{followed.Fixpoints.Count} fixpoints, worst {worst:0.###} px from the curve's ink");
        Assert.InRange(worst, 0, 1);

        var blank = BlankSpot(ink, f);
        var nothing = ManualCurves.Follow(ink, plot, blank);
        Assert.False(nothing.Ok);
        Assert.Empty(nothing.Fixpoints);
    }

    [Fact]
    public void Sampling_PassesThroughEveryFixpoint_SmoothNeverOvershootsAStep_AndNothingIsExtrapolated()
    {
        (double, double)[] step = [(0, 0), (1, 0), (2, 0), (3, 1), (4, 1), (5, 1)];
        foreach (var method in new[] { ManualSampleMethod.Linear, ManualSampleMethod.Smooth })
        {
            var at = ManualSampling.At(step, [.. step.Select(p => p.Item1)], method, false, false);
            Assert.Equal(step.Select(p => p.Item2), at);
        }
        var smooth = ManualSampling.Sample(step, new ManualSampleSpec(Points: 501, Method: ManualSampleMethod.Smooth), false, false);
        Assert.All(smooth.Y, v => Assert.InRange(v, 0, 1));
        Assert.All(smooth.Y.Zip(smooth.Y.Skip(1)), p => Assert.True(p.Second >= p.First - 1e-12));

        // A grid wider than the fixpoints: the points beyond them are dropped and counted.
        var wide = ManualSampling.Sample(step, new ManualSampleSpec(-1, 6, 8), false, false);
        Assert.Equal(2, wide.Dropped);
        Assert.Equal(6, wide.X.Count);
        Assert.All(wide.X.Select((v, k) => (v, k)), p => Assert.Equal(p.k, p.v, 1e-12));
    }

    [Fact]
    public void Csv_KeepsItsColumns_AndAnSParameterPairOnAFrequencyAxis_AssemblesTheExpectedS2p()
    {
        var plot = new ManualPlot(0, ManualQuad.Rectangle(100, 50, 500, 350), new ManualAxis(1e9, 5e9, AxisScale.Linear),
                                  new ManualAxis(-40, 0, AxisScale.Linear), null, 1e9, true);
        double Py(double db) => 50 + (0 - db) / 40 * 300;
        var s = ReadByHand(Blank(), plot,
            new ManualCurve("S21", 1, [(100, Py(-3)), (500, Py(-3))], PlotSide.Left, PlotQuantity.ParseAssignment("S21:db")!, 0x1F77B4),
            new ManualCurve("S11", 1, [(100, Py(-20)), (300, Py(-20)), (500, Py(-20))], PlotSide.Left, PlotQuantity.ParseAssignment("S11:db")!, 0xD62728),
            new ManualCurve("noise", 1, [(150, Py(-30)), (450, Py(-35))], PlotSide.Left, PlotAssignment.None, 0x2CA02C));

        var csv = PlotCsv.Write(s).Split('\n');
        var header = csv.First(l => !l.StartsWith('#'));
        Assert.Equal(string.Join(",", PlotCsv.Columns), header);
        Assert.Contains(csv, l => l.StartsWith("noise,1,data,", StringComparison.Ordinal));
        Assert.Equal(3 * ManualSampleSpec.DefaultPoints, csv.Count(l => l.Length > 0 && !l.StartsWith('#')) - 1);

        var built = TouchstoneAssembly.Build(s);
        Assert.True(built.Ok, built.Refusal?.Sentence);
        Assert.Equal(2, built.Ports);
        Assert.Equal(ManualSampleSpec.DefaultPoints, built.Snp!.FrequencyCount);
        Assert.Equal(1e9, built.Snp.Frequencies[0], 1e-3);
        Assert.Equal(5e9, built.Snp.Frequencies[^1], 1e-3);
        for (int k = 0; k < built.Snp.FrequencyCount; k++)
        {
            Assert.Equal(Math.Pow(10, -3 / 20.0), built.Snp[k][1, 0].Magnitude, 1e-9);
            Assert.Equal(Math.Pow(10, -20 / 20.0), built.Snp[k][0, 0].Magnitude, 1e-9);
        }
    }

    [Fact]
    public void TheTouchstone_IsOnTheSampledGrid_EvenALogGridOnALinearAxis()
    {
        var plot = new ManualPlot(0, ManualQuad.Rectangle(100, 50, 500, 350), new ManualAxis(1e9, 5e9, AxisScale.Linear),
                                  new ManualAxis(-40, 0, AxisScale.Linear), null, 1e9, true)
        { Sampling = new ManualSampleSpec(Points: 21, Log: true) };
        var s = ReadByHand(Blank(), plot,
            new ManualCurve("S21", 1, [(100, 80), (500, 80)], PlotSide.Left, PlotQuantity.ParseAssignment("S21:db")!, 0x1F77B4),
            new ManualCurve("S11", 1, [(100, 200), (500, 200)], PlotSide.Left, PlotQuantity.ParseAssignment("S11:db")!, 0xD62728));

        var built = TouchstoneAssembly.Build(s);
        Assert.True(built.Ok, built.Refusal?.Sentence);
        var sampled = s.Traces[0].Samples.Select(p => p.X).ToArray();
        Assert.Equal(sampled.Length, built.Snp!.FrequencyCount);
        for (int k = 0; k < sampled.Length; k++) Assert.Equal(sampled[k], built.Snp.Frequencies[k], 1e-3);
    }

    // ── pictures ─────────────────────────────────────────────────────────────────────────────────────────

    private static ImageSource Blank() => FromRaster(new RasterImage(600, 400, Enumerable.Repeat((byte)255, 600 * 400 * 4).ToArray()));

    private static ImageSource FromRaster(RasterImage r) => ImageSource.FromBytes(r.EncodePng(), "manual.png").Source!;

    /// <summary>Everything outside the frame (a pixel's margin) painted in the background: no tick label, no title.</summary>
    private static ImageSource Paint(ImageSource source, PlotFrame f)
    {
        var r = source.Raster;
        var rgba = (byte[])r.Rgba.Clone();
        int bg = r.Rgb(1, 1);
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                if (x >= f.Left - 1 && x <= f.Right + 1 && y >= f.Top - 1 && y <= f.Bottom + 1) continue;
                int i = 4 * (y * r.Width + x);
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg, 255);
            }
        return FromRaster(new RasterImage(r.Width, r.Height, rgba));
    }

    /// <summary>A point turned <paramref name="deg"/> counter-clockwise as seen about (cx, cy) — ManualQuad.Rectangle's sense.</summary>
    private static (double, double) TurnPoint(double x, double y, double cx, double cy, double deg)
    {
        double a = deg * Math.PI / 180, dx = x - cx, dy = y - cy;
        return (cx + dx * Math.Cos(a) + dy * Math.Sin(a), cy - dx * Math.Sin(a) + dy * Math.Cos(a));
    }

    /// <summary>The picture turned <paramref name="deg"/> about (cx, cy), bilinearly, white beyond it.</summary>
    private static ImageSource Turn(ImageSource source, double cx, double cy, double deg)
    {
        var r = source.Raster;
        var rgba = new byte[r.Width * r.Height * 4];
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double sx = cx + dx * c - dy * s - 0.5, sy = cy + dx * s + dy * c - 0.5;
                int o = 4 * (y * r.Width + x);
                for (int k = 0; k < 4; k++) rgba[o + k] = 255;
                if (sx < 0 || sy < 0 || sx > r.Width - 1 || sy > r.Height - 1) continue;
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(r.Width - 1, x0 + 1), y1 = Math.Min(r.Height - 1, y0 + 1);
                double fx = sx - x0, fy = sy - y0;
                for (int k = 0; k < 3; k++)
                {
                    double v = (r.Rgba[4 * (y0 * r.Width + x0) + k] * (1 - fx) + r.Rgba[4 * (y0 * r.Width + x1) + k] * fx) * (1 - fy)
                             + (r.Rgba[4 * (y1 * r.Width + x0) + k] * (1 - fx) + r.Rgba[4 * (y1 * r.Width + x1) + k] * fx) * fy;
                    rgba[o + k] = (byte)Math.Round(v);
                }
            }
        return FromRaster(new RasterImage(r.Width, r.Height, rgba));
    }

    /// <summary>The centre of the ink of colour <paramref name="rgb"/> in the pixel column at <paramref name="px"/>, within
    /// 6 px of <paramref name="near"/> — each pixel weighted by how close to the colour it is against the white paper.</summary>
    private static double InkCentre(RasterImage r, int rgb, double px, double near)
    {
        int x = (int)Math.Floor(px);
        double sw = 0, sy = 0;
        for (int y = (int)near - 6; y <= (int)near + 6; y++)
        {
            int c = r.Rgb(x, y);
            if (PlotInk.Distance(c, rgb) >= 72 && !PlotInk.Between(c, rgb, 0xFFFFFF)) continue;
            double w = Math.Max(0, 1 - PlotInk.Distance(c, rgb) / (double)Math.Max(1, PlotInk.Distance(0xFFFFFF, rgb)));
            sw += w;
            sy += w * (y + 0.5);
        }
        return sw > 0 ? sy / sw : double.NaN;
    }

    /// <summary>A point inside the frame with no ink within 6 px: blank paper.</summary>
    private static (double, double) BlankSpot(PlotInk ink, PlotFrame f)
    {
        for (int y = (int)f.Top + 8; y < f.Bottom - 8; y += 3)
            for (int x = (int)f.Left + 8; x < f.Right - 8; x += 3)
            {
                bool clear = true;
                for (int dy = -6; dy <= 6 && clear; dy++)
                    for (int dx = -6; dx <= 6 && clear; dx++)
                        if (ink.At(x + dx, y + dy)) clear = false;
                if (clear) return (x + 0.5, y + 0.5);
            }
        throw new InvalidOperationException("no blank paper inside the frame");
    }
}
