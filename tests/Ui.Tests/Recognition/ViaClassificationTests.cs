// brief-artsch-3-board-graph.md R-as3-4 — which vias matter. One test per claim.

using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ViaClassificationTests
{
    /// <summary>A shunt pad on its own island with six vias: four VIAGND, the nearest, and two counted.</summary>
    [Fact]
    public void AGroundPadOnItsOwnIslandKeepsFourViagndAndCountsTheRest()
    {
        var result = Recognize(Shunt(padOnPour: false), TwoLayer());

        var vias = result.Board!.Vias;
        Assert.All(vias, v => Assert.Equal(ViaClass.PadGround, v.Class));
        Assert.Equal(4, vias.Count(v => v.Element == ViaElement.ViaGnd));
        Assert.Equal(2, vias.Count(v => v.Element == ViaElement.None));
        Assert.Equal(2, result.Report.Count(RecognitionFindingClass.GroundViasCapped));
        Assert.Equal(IslandKind.PadGround, result.Board.IslandAt(Um(9_750), Um(6_500))!.Kind);

        // The nearest four to the pad's centre are kept: both middle vias among them.
        Assert.All(vias.Where(v => v.X == Um(9_750)), v => Assert.Equal(ViaElement.ViaGnd, v.Element));
    }

    /// <summary>The same pad inside a pour is ground copper: its vias are stitching and nothing is a VIAGND.</summary>
    [Fact]
    public void TheSamePadOnAPourIsPlainGround()
    {
        var result = Recognize(Shunt(padOnPour: true), TwoLayer());

        Assert.All(result.Board!.Vias, v => Assert.Equal((ViaClass.Stitching, ViaElement.None), (v.Class, v.Element)));
        Assert.Null(result.Board.IslandAt(Um(9_750), Um(6_500)));
        Assert.DoesNotContain(result.Board.Islands, i => i.Kind == IslandKind.PadGround);
    }

    /// <summary>A via joining a line on Top to its continuation on Bottom, through an antipad in the
    /// inner plane, is a VIA and the two lines are one island.</summary>
    [Fact]
    public void ASignalViaBetweenLayersIsAVia()
    {
        var view = new LayoutView();
        view.Shapes.Add(new PolygonShape
        {
            Layer = Plane,
            Xy = [0, 0, Um(40_000), 0, Um(40_000), Um(10_000), 0, Um(10_000)],
            Holes = [[Um(19_400), Um(4_400), Um(20_600), Um(4_400), Um(20_600), Um(5_600), Um(19_400), Um(5_600)]],
        });
        view.Shapes.Add(Line(Top, 0, 20_300, 5_000));
        view.Shapes.Add(Line(Bottom, 19_700, 40_000, 5_000));
        view.Shapes.Add(ViaAt(20_000, 5_000));

        var result = Recognize(view, FourLayer());

        var via = Assert.Single(result.Board!.Vias);
        Assert.Equal((ViaClass.SignalTransition, ViaElement.Via), (via.Class, via.Element));
        var island = Assert.Single(result.Board.Islands);
        Assert.Equal([Top, Bottom], island.Layers);
    }

    /// <summary>vias=ground: no VIAGND at all — the kept ground vias are plain grounds.</summary>
    [Fact]
    public void ViasGroundLeavesNoViagnd()
    {
        var result = Recognize(Shunt(padOnPour: false), TwoLayer(), new RecognitionOptions { Vias = ViaPolicy.Ground });

        Assert.DoesNotContain(result.Board!.Vias, v => v.Element == ViaElement.ViaGnd);
        Assert.Equal(4, result.Board.Vias.Count(v => v.Element == ViaElement.Gnd));
    }
}
