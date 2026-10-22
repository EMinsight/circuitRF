// brief-artsch-3-board-graph.md R-as3-6 and R-as3-7 — where the ports are. One test per claim.

using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PortDiscoveryTests
{
    /// <summary>With an EM setup, a port label is the setup's port, and it wins over a layout pin and
    /// the board-edge port at the same place.</summary>
    [Fact]
    public void AnEmSetupPortWinsOverALabelAtTheSamePlace()
    {
        var view = PlaneAndLine();
        view.Shapes.Add(new LabelShape { Layer = Top, X = 0, Y = Um(5_000), Text = "1", IsPort = true });
        view.Pins.Add(new LayoutPin { Name = "IN", X = 0, Y = Um(5_000), Layer = Top });

        var ports = Recognize(view, TwoLayer(), setup: new EmSetup()).Board!.Ports;

        Assert.Equal(2, ports.Count);
        var left = Assert.Single(ports, p => p.X < Um(1_000));
        Assert.Equal((1, "P1", PortSource.EmSetup), (left.Number, left.Name, left.Source));
        Assert.Equal(PortSource.BoardEdge, ports.Single(p => p.Number == 2).Source);

        // Without the setup the same label is a port label, still ahead of the pin.
        Assert.Equal(PortSource.PortLabel, Recognize(view, TwoLayer()).Board!.Ports.Single(p => p.X < Um(1_000)).Source);
    }

    /// <summary>A line reaching the board edge is a port at each end.</summary>
    [Fact]
    public void ALineToTheBoardEdgeIsAPort()
    {
        var ports = Recognize(PlaneAndLine(), TwoLayer()).Board!.Ports;

        Assert.Equal(2, ports.Count);
        Assert.All(ports, p => Assert.Equal(PortSource.BoardEdge, p.Source));
        Assert.Equal([0, Um(40_000)], ports.Select(p => p.X).OrderBy(x => x));
        Assert.Equal(["P1", "P2"], ports.Select(p => p.Name));
    }

    /// <summary>A strip a via ties to the plane reaches the edge: in the ground net the artwork names it is ground copper
    /// and no port; unnamed it may be an RF line with a shorted stub, and keeps its edge ports (AS-12).</summary>
    [Fact]
    public void AnEdgeOnGroundTiedCopperIsNoPort()
    {
        LayoutView Board(string? net)
        {
            var view = PlaneAndLine();
            foreach (var s in view.Shapes) s.Net = net;
            view.Shapes.Add(ViaAt(20_000, 5_000));
            return view;
        }

        Assert.DoesNotContain(Recognize(Board("GND"), TwoLayer()).Board!.Ports, p => p.Source == PortSource.BoardEdge);
        Assert.Equal(2, Recognize(Board(null), TwoLayer()).Board!.Ports.Count(p => p.Source == PortSource.BoardEdge));
    }

    /// <summary>A scope rectangle cutting a line makes X1 and X2 at the cuts, and says it cut the plane.</summary>
    [Fact]
    public void AScopeRectangleCuttingALineMakesX1AndX2()
    {
        var result = Recognize(PlaneAndLine(), TwoLayer(),
                               scope: RecognitionScope.Rectangle(Um(10_000), Um(-1_000), Um(30_000), Um(11_000)));

        Assert.True(result.Ok, result.Refusal);
        var ports = result.Board!.Ports;
        Assert.Equal(["X1", "X2"], ports.Select(p => p.Name));
        Assert.All(ports, p => Assert.Equal(PortSource.ScopeCut, p.Source));
        Assert.InRange(ports[0].X, Um(9_990), Um(10_010));
        Assert.InRange(ports[1].X, Um(29_990), Um(30_010));
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.ScopeCutGround));
    }

    /// <summary>Designer report, round 25: a selection made on Top reads an inner-layer line crossing under it only
    /// where a via joins the two — and here none does, so it is left out and said so. A rectangle, which has no layers,
    /// still reads every layer and cuts the inner line into ports of its own.</summary>
    [Fact]
    public void ASelectionOnTopLeavesOutAnInnerLineNotJoinedToIt()
    {
        var tech = TwoLayer();
        tech.Layers = [.. tech.Layers, new LayerDef { Key = Inner, Name = "Inner", Purpose = "conductor" }];
        var core = tech.Stackup.Layers[2];
        tech.Stackup.Layers =
        [
            tech.Stackup.Layers[0], tech.Stackup.Layers[1],
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Prepreg", ThicknessDbu = Um(250), Epsr = 4.3 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Inner", ThicknessDbu = Um(18), SigmaSm = 5.8e7, DrawingLayers = [Inner] },
            core, tech.Stackup.Layers[3],
        ];
        var view = PlaneAndLine();
        view.Shapes.Add(Rect(Inner, 19_800, 0, 20_200, 10_000));
        long[] box = [Um(10_000), Um(3_000), Um(30_000), Um(3_000), Um(30_000), Um(7_000), Um(10_000), Um(7_000)];

        var onTop = Recognize(view, tech, scope: RecognitionScope.Polygons([box], [Top]));
        Assert.True(onTop.Ok, onTop.Refusal);
        Assert.Equal(["X1", "X2"], onTop.Board!.Ports.Select(p => p.Name));
        Assert.All(onTop.Board.Ports, p => Assert.Equal(Top, p.Layer));
        Assert.Contains(onTop.Report.Findings, f => f.Class == RecognitionFindingClass.ScopeOtherLayersLeftOut
                                                    && f.Sentence.Contains("'Inner'", StringComparison.Ordinal));

        var everyLayer = Recognize(view, tech, scope: RecognitionScope.Polygons([box]));
        Assert.Contains(everyLayer.Board!.Ports, p => p.Layer == Inner);

        // A selection on no copper layer seeds nothing, so it reads every layer rather than leaving all copper out.
        var onMask = Recognize(view, tech, scope: RecognitionScope.Polygons([box], [Mask]));
        Assert.True(onMask.Ok, onMask.Refusal);
        Assert.Contains(onMask.Board!.Ports, p => p.Layer == Inner);
        Assert.DoesNotContain(onMask.Report.Findings, f => f.Class == RecognitionFindingClass.ScopeOtherLayersLeftOut);
    }

    /// <summary>Designer report, round 25: a selection's outline is the copper's own edge, and the board's copper
    /// differs from it by a fraction of a micron. That sliver is not a line leaving the selection; the line that
    /// does leave it, at the right, is the one port.</summary>
    [Fact]
    public void CopperASubMicronPastTheOutline_IsNoCut_ALineLeavingItIs()
    {
        var view = PlaneAndLine(10_000, 40_000);
        long[] outline = [Um(10_000), Um(4_525) + 300, Um(30_000), Um(4_525) + 300,
                          Um(30_000), Um(5_475) - 300, Um(10_000), Um(5_475) - 300];

        var result = Recognize(view, TwoLayer(), scope: RecognitionScope.Polygons([outline]));

        Assert.True(result.Ok, result.Refusal);
        var cut = Assert.Single(result.Board!.Ports, p => p.Source == PortSource.ScopeCut);
        Assert.InRange(cut.X, Um(29_990), Um(30_010));
    }

    /// <summary>A board with no port is the refusal sentence.</summary>
    [Fact]
    public void ABoardWithNoPortIsTheRefusal()
    {
        var result = Recognize(PlaneAndLine(10_000, 30_000), TwoLayer());

        Assert.False(result.Ok);
        Assert.Equal(PortDiscovery.NoPortRefusal, result.Refusal);
    }
}
