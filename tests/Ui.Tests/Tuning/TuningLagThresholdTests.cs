using System;
using CircuitRF.Ui.Tuning;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>
/// The panel says it is behind only once the lag has lasted <see cref="TuningPanelViewModel.LagShownAfter"/>:
/// on a circuit that solves in milliseconds every request was a lag until it landed, and a drag flashed
/// the status dot amber several times a second (owner report, 2026-10-25).
/// </summary>
public class TuningLagThresholdTests
{
    [Fact]
    public void ALagShorterThanTheThreshold_IsNotShown()
    {
        var t0 = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var shown = TuningPanelViewModel.LagShownAfter;

        Assert.False(TuningPanelViewModel.ShowsLag(null, t0));
        Assert.False(TuningPanelViewModel.ShowsLag(t0, t0 + shown - TimeSpan.FromMilliseconds(1)));
        Assert.True (TuningPanelViewModel.ShowsLag(t0, t0 + shown));
    }
}
