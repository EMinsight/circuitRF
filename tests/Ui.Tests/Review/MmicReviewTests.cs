// Reviewing a die (brief-review-6, the PCB / MMIC switch): a MMIC review runs only the checks of a die, and the
// target is saved on the layout only when it is not a board.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Review;
using static CircuitRF.Ui.Tests.Review.ReviewRunTests;

namespace CircuitRF.Ui.Tests.Review;

public sealed class MmicReviewTests
{
    private static LayoutReviewResult Review(LayoutView view, Technology tech, ReviewSettings settings) =>
        LayoutReview.Run(new TraceImpedanceAnalysis.LayoutSource("die.clay", view, tech, null, [.. view.Shapes], null), settings);

    [Fact]
    public void AMmicReview_RunsOnlyTheChecksOfADie()
    {
        var mmic = Review(new LayoutView(), Tech(), new ReviewSettings { Target = ReviewTarget.Mmic });
        Assert.Equal(["ground-pin-via", "coupled-lines", "probe-pads", "impedance"], mmic.Checks.Select(c => c.Id));
        Assert.Equal(ReviewTarget.Mmic, mmic.Target);

        var pcb = Review(new LayoutView(), Tech(), new ReviewSettings());
        Assert.Equal(LayoutReview.ChecksFor(ReviewTarget.Pcb).Select(c => c.Id), pcb.Checks.Select(c => c.Id));
        Assert.DoesNotContain(pcb.Checks, c => c.Id is "ground-pin-via" or "probe-pads");
    }

    [Fact]
    public void TheTarget_IsSavedOnlyWhenItIsNotABoard()
    {
        Assert.Null(new ReviewSettings { Target = ReviewTarget.Pcb }.ForSave(1000));
        Assert.Equal(ReviewTarget.Mmic, new ReviewSettings { Target = ReviewTarget.Mmic }.ForSave(1000)!.Target);
    }
}
