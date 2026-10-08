// brief-artsch-5-traces-to-line-elements.md §4 — D13's line types and R-as5-2's coplanar reading: the user's
// choice and gap factor decide CPWG against MLIN, the measured gaps are recorded whatever is chosen, a stripline
// is an SLIN and a stripline with coplanar ground a TLIN at the solved Z and εeff.

using System.Linq;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.LineBoards;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class LineTypeChoiceTests
{
    [Fact]
    public void SideGroundAtOneHeightIsCpwgUnderAutoAndMlinWithTheGapUnderMicrostrip()
    {
        var auto = Assert.Single(Lines(Recognize(Coplanar(500), TwoLayer())));
        Assert.Equal(LineElementType.CPWG, auto.Type);
        Assert.Equal(500, auto.Micro("G"), 1.0);

        var microstrip = Assert.Single(Lines(Recognize(Coplanar(500), TwoLayer(),
            new RecognitionOptions { Coplanar = CoplanarReading.Microstrip })));
        Assert.Equal(LineElementType.MLIN, microstrip.Type);
        Assert.Equal(500e-6, microstrip.GapLeft!.Value, 1e-6);
        Assert.Equal(500e-6, microstrip.GapRight!.Value, 1e-6);
    }

    [Fact]
    public void SideGroundAtFiveHeightsIsMlinUnderAutoUntilTheFactorIsSix()
    {
        var auto = Assert.Single(Lines(Recognize(Coplanar(2_500), TwoLayer())));
        Assert.Equal(LineElementType.MLIN, auto.Type);
        Assert.Equal(2_500e-6, auto.GapLeft!.Value, 1e-6);

        var wide = Assert.Single(Lines(Recognize(Coplanar(2_500), TwoLayer(), new RecognitionOptions { CoplanarGapFactor = 6 })));
        Assert.Equal(LineElementType.CPWG, wide.Type);
    }

    [Fact]
    public void AnInnerLineBetweenTwoPlanesIsSlin()
    {
        var line = Assert.Single(Lines(Recognize(InnerLine(), FourLayerStripline())));
        Assert.Equal(LineElementType.SLIN, line.Type);
    }

    [Fact]
    public void AStriplineWithCoplanarGroundIsTlinAtTheStationsMean()
    {
        var result = Recognize(InnerLine(sideGapUm: 300), FourLayerStripline());

        var line = Assert.Single(Lines(result));
        Assert.Equal(LineElementType.TLIN, line.Type);
        Assert.Equal("stripline with coplanar ground", line.Fallback);
        var solved = result.Review!.AllTraces.Single().Stations.Where(s => s.Z0 is not null).ToList();
        double len = solved.Sum(s => s.Length);
        Assert.Equal(solved.Sum(s => s.Z0!.Value * s.Length) / len, line.Parameters["Z"], 1e-9);
        Assert.Equal(solved.Sum(s => s.Eeff!.Value * s.Length) / len, line.Parameters["Eeff"], 1e-9);
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.TlinFallbacks));
    }
}
