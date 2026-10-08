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

    /// <summary>A board with no port is the refusal sentence.</summary>
    [Fact]
    public void ABoardWithNoPortIsTheRefusal()
    {
        var result = Recognize(PlaneAndLine(10_000, 30_000), TwoLayer());

        Assert.False(result.Ok);
        Assert.Equal(PortDiscovery.NoPortRefusal, result.Refusal);
    }
}
