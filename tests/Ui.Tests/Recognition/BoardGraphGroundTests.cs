// brief-artsch-3-board-graph.md R-as3-3 — what is ground. One test per claim.

using System.Linq;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class BoardGraphGroundTests
{
    /// <summary>A top pour stitched by 200 vias to a bottom plane, a line through a gap in it: ground is
    /// the pour and the plane, every via is stitching, and the line is one signal island.</summary>
    [Fact]
    public void StitchedPourAndPlaneAreGroundAndTheLineIsOneSignalIsland()
    {
        var result = Recognize(Stitched(), TwoLayer());

        Assert.True(result.Ok, result.Refusal);
        var board = result.Board!;
        Assert.Equal(GroundSource.ReferenceConductor, board.Ground.Source);
        Assert.Equal(200, board.Vias.Count);
        Assert.All(board.Vias, v => Assert.Equal((ViaClass.Stitching, ViaElement.None), (v.Class, v.Element)));
        Assert.Equal(200, result.Report.Count(RecognitionFindingClass.StitchingViasDropped));

        var island = Assert.Single(board.Islands);
        Assert.Equal(IslandKind.Signal, island.Kind);
        Assert.Equal([Top], island.Layers);
        Assert.Same(island, board.IslandAt(Um(20_000), Um(15_000)));
        Assert.Null(board.IslandAt(Um(20_000), Um(5_000)));   // the pour is ground, not an island
    }

    /// <summary>A net the artwork names GND wins over the larger plane, which is then a separate pour.</summary>
    [Fact]
    public void AStatedGroundNetWinsOverArea()
    {
        var view = PlaneAndLine();
        view.Shapes.Add(Rect(Top, 30_000, 6_000, 38_000, 10_000, net: "GND"));

        var result = Recognize(view, TwoLayer());

        Assert.Equal(GroundSource.StatedNet, result.Board!.Ground.Source);
        Assert.Equal("GND", result.Board.Ground.NetName);
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.SeparatePour));
        Assert.Contains(result.Board.Islands, i => i.IsSeparatePour && i.Kind == IslandKind.Signal);
    }

    /// <summary>A ground point that lands on no copper is a refusal naming the point.</summary>
    [Fact]
    public void AGroundPointOnEmptyBoardIsARefusal()
    {
        var result = Recognize(PlaneAndLine(), TwoLayer(),
                               new RecognitionOptions { GroundAt = (Um(-5_000), Um(-5_000)) });

        Assert.Null(result.Board);
        Assert.Contains("lands on no copper", result.Refusal);
        Assert.Contains("-5", result.Refusal);
    }
}
