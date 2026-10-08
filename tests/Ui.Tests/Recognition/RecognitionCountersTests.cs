// brief-artsch-3-board-graph.md R-as3-2 — the board's partition is read once, not once per via.
// A counter, never a clock.

using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class RecognitionCountersTests
{
    [Fact]
    public void TheTwoHundredViaBoardReadsItsPartitionOnce()
    {
        using var cache = ConnectivityCache.BeginIsolated();
        using var counting = ConnectivityCounters.Begin();

        var result = Recognize(Stitched(), TwoLayer());

        Assert.Equal(200, result.Report.Count(RecognitionFindingClass.StitchingViasDropped));
        Assert.Equal(1, counting.Counters.Extractions);
    }
}
