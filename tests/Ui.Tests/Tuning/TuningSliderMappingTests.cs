using CircuitRF.Core.Design;
using CircuitRF.Ui.Tuning;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-4 R-to4-4 and overview §3: the slider's value↔position mapping, integer snap, keyboard steps.</summary>
public sealed class TuningSliderMappingTests
{
    [Fact]
    public void LogAndLinear_RoundTrip_AndAutoPicksByRange()
    {
        // Log: the geometric middle of [0.9, 3.6] is the slider's middle.
        Assert.Equal(0.5, TuningSliderMapping.ToPosition(1.8, 0.9, 3.6, TuneScale.Log), 12);
        Assert.Equal(1.8, TuningSliderMapping.FromPosition(0.5, 0.9, 3.6, TuneScale.Log), 12);

        // Linear: the arithmetic middle.
        Assert.Equal(0.5, TuningSliderMapping.ToPosition(75, 50, 100, TuneScale.Lin), 12);
        Assert.Equal(75, TuningSliderMapping.FromPosition(0.5, 50, 100, TuneScale.Lin), 12);

        // Auto: a ratio under 10 is linear, a decade or more is log.
        Assert.Equal(0.5, TuningSliderMapping.ToPosition(75, 50, 100, TuneScale.Auto), 12);
        Assert.Equal(0.5, TuningSliderMapping.ToPosition(10, 1, 100, TuneScale.Auto), 12);
    }

    [Fact]
    public void IntegerRows_Snap_AndNeverMoveByLessThanOne()
    {
        Assert.Equal(4, TuningSliderMapping.Snap(3.6, 1, 10, integer: true, step: null));
        Assert.Equal(5, TuningSliderMapping.Nudge(4, +1, TuningNudge.Step, 1, 10, TuneScale.Lin, integer: true, step: 0.25));
    }

    [Fact]
    public void Keys_ArrowIsOneStep_ShiftIsTen_PageIsATenthOfTheRange()
    {
        double Nudge(TuningNudge size, double? step) =>
            TuningSliderMapping.Nudge(500, +1, size, 0, 1000, TuneScale.Lin, integer: false, step);

        Assert.Equal(505,  Nudge(TuningNudge.Step, 5), 9);
        Assert.Equal(550,  Nudge(TuningNudge.TenSteps, 5), 9);
        Assert.Equal(600,  Nudge(TuningNudge.Page, 5), 9);
        Assert.Equal(510,  Nudge(TuningNudge.Step, null), 9);    // no step: a hundredth of the range
    }
}
