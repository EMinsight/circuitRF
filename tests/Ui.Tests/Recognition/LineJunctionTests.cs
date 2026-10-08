// brief-artsch-5-traces-to-line-elements.md §4 — a T is an MTEE with its branch identified, a + an MCROSS, and
// every arm's length runs from the crossing arm's edge (R-as5-4, R-as5-5).

using System.Linq;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.LineBoards;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class LineJunctionTests
{
    [Fact]
    public void ATIsAnMteeWithItsBranchIdentified()
    {
        var result = Recognize(Junction(cross: false), TwoLayer());

        var tee = Assert.Single(result.Lines.OfType(LineElementType.MTEE));
        // The branch (pin 3) is the arm running down to the bottom edge; the through arms lose half its width.
        var branch = ArmOn(result, tee.Nodes[2]);
        Assert.Equal(10_000 - W / 2, branch.Micro("L"), 1.0);
        Assert.Contains(branch.Nodes, n => NodeOf(result, n) is { Kind: LineNodeKind.Port, Y: 0 });
        Assert.Equal(15_000 - W / 2, ArmOn(result, tee.Nodes[0]).Micro("L"), 1.0);
        Assert.Equal(15_000 - W / 2, ArmOn(result, tee.Nodes[1]).Micro("L"), 1.0);
    }

    [Fact]
    public void APlusIsAnMcrossWhoseArmsStartAtTheCrossingArmsEdge()
    {
        var result = Recognize(Junction(cross: true), TwoLayer());

        var cross = Assert.Single(result.Lines.OfType(LineElementType.MCROSS));
        var lengths = cross.Nodes.Select(n => ArmOn(result, n).Micro("L")).Order().ToList();
        Assert.Equal(10_000 - W / 2, lengths[0], 1.0);
        Assert.Equal(10_000 - W / 2, lengths[1], 1.0);
        Assert.Equal(15_000 - W / 2, lengths[2], 1.0);
        Assert.Equal(15_000 - W / 2, lengths[3], 1.0);
    }

    private static LineElement ArmOn(RecognitionResult result, string node) =>
        Assert.Single(Lines(result), e => e.Nodes.Contains(node));
}
