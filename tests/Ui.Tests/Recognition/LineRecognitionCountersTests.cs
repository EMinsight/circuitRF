// brief-artsch-5-traces-to-line-elements.md §4 — the trace review is the reader, run once, and its shared-cut
// cache is what makes a whole board affordable. Counters, never a clock.

using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class LineRecognitionCountersTests
{
    [Fact]
    public void AFortyMillimetreLineOfOneWidthSolvesOneCutAndTheReviewRunsOnce()
    {
        var result = Recognize(PlaneAndLine(), TwoLayer());

        Assert.Equal(1, result.ReviewRuns);
        Assert.Equal(1, result.Review!.SolveCount);
        Assert.Single(result.Lines.Elements);
    }
}
