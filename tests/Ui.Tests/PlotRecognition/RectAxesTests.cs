// brief-img-14-sparameter-plots-to-touchstone.md §4 — a rectangular plot's axis fit (R-im14-3): a misread tick label
// is outvoted, and one readable label is not an axis.

using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class RectAxesTests
{
    private static AxisLabel Label(double pixel, double value) => new(pixel, value, value.ToString(), false, new PixelBox(0, 0, 0, 0));

    [Fact]
    public void OneMisreadTickLabel_DoesNotMoveTheFit()
    {
        // 0, -5 … -30 dB every 60 px; the -20 read as -2.
        AxisLabel[] good = [Label(100, 0), Label(160, -5), Label(220, -10), Label(280, -15), Label(340, -20), Label(400, -25), Label(460, -30)];
        var misread = (AxisLabel[])good.Clone();
        misread[4] = Label(340, -2);

        var (clean, _) = RectAxes.Fit(good);
        var (fit, labels) = RectAxes.Fit(misread);
        Assert.NotNull(fit);
        Assert.Equal(AxisScale.Linear, fit!.Scale);
        Assert.Equal(1, fit.Rejected);
        Assert.False(labels[4].Used);
        Assert.Equal(clean!.ValueAt(250), fit.ValueAt(250), 6);
        Assert.Equal(0, fit.ResidualPx, 6);
    }

    [Fact]
    public void OneReadableLabel_WaitsForTwoClicks()
    {
        var (fit, _) = RectAxes.Fit([Label(100, 0)]);
        Assert.Null(fit);

        // Two clicks and two typed values calibrate it; a log axis is one toggle.
        var clicked = AxisCalibration.FromTwoPoints(100, 1e9, 500, 5e9, AxisScale.Linear)!;
        Assert.Equal(3e9, clicked.ValueAt(300), 3);
        Assert.True(clicked.Manual);
        var log = AxisCalibration.FromTwoPoints(100, 1e8, 500, 1e10, AxisScale.Log)!;
        Assert.Equal(1e9, log.ValueAt(300), 3);
    }

    /// <summary>
    /// A log y axis read from its labels, one label corrected in the dialog: the typed pair keeps the axis's own scale.
    /// The dialog's typed values used to be mapped linearly whatever the axis was (designer round 24).
    /// </summary>
    [Fact]
    public void TypedValuesOnALogAxis_StayLogarithmic()
    {
        var frame = new PlotFrame { Left = 100, Top = 50, Right = 500, Bottom = 350, YTicks = [50, 150, 250, 350] };
        PlotWord Word(int id, int y, double v) => new(id, new PixelBox(60, y - 6, 90, y + 6), false, v.ToString(), v, 0, 0);
        PlotWord[] words = [Word(1, 50, 10), Word(2, 150, 1), Word(3, 250, 0.1), Word(4, 350, 0.01)];
        Assert.Equal(AxisScale.Log, RectAxes.Calibrate(frame, words).Y!.Scale);

        var typed = RectAxes.Calibrate(frame, words, y: new AxisClicks(250, 0.1, 50, 10));
        Assert.Equal(AxisScale.Log, typed.Y!.Scale);
        Assert.Equal(1, typed.Y.ValueAt(150), 9);

        // Stated by name, the pair's scale wins; a pair that cannot be logarithmic is linear.
        Assert.Equal(AxisScale.Linear, RectAxes.Calibrate(frame, words, y: new AxisClicks(250, 0.1, 50, 10, AxisScale.Linear)).Y!.Scale);
        Assert.Equal(AxisScale.Linear, RectAxes.Calibrate(frame, words, y: new AxisClicks(250, 0, 50, 10)).Y!.Scale);
    }
}
