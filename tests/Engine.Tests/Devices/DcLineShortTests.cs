using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// A line is a short at DC. Its ω = 0 stamp is the resonance clamp, a purely imaginary admittance, and the nonlinear DC
/// engine keeps the real part of every stamp, so 1 V through a 3 mm line into 50 Ω read 0 V at the load. Both DC engines
/// now stamp a line's declared shorts (DcLineShorts).
/// </summary>
public sealed class DcLineShortTests
{
    private static ElaboratedNetlist Bias(string line)
    {
        var (lib, tb) = new CnlReader().Read($"""
            Vdc:V1 a 0 Vdc=1 V
            {line}
            R:R1 b 0 R=50 Ohm
            R:Rc c 0 R=50 Ohm
            R:Rd d 0 R=50 Ohm
            analysis DC1 type=dc
            """);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static double V(ElaboratedNetlist nl, double[] v, string net) => v[nl.Nodes.GetOrAssign(net) - 1];

    /// <summary>The bias reaches the load whole, through a line of each family; a coupled pair's second strip carries none.</summary>
    [Theory]
    [InlineData("TLIN:T1 a b Z=50 Ohm L=3 mm")]
    [InlineData("MLIN:T1 a b W=1.5 mm L=20 mm H=0.8 mm T=35 um Er=4.4 TanD=0.02 Sigma=5.8e7")]
    [InlineData("CLIN:T1 a b c d Ze=60 Ohm Zo=40 Ohm E=90 deg F=1 GHz")]
    public void ABiasThroughALine_ReachesTheLoad(string line)
    {
        var nl = Bias(line);
        var r = NonlinearDcEngine.Run(nl);
        Assert.True(r.Converged);
        Assert.Equal(1.0, V(nl, r.NodeVoltages, "b"), 9);
        if (line.StartsWith("CLIN")) Assert.Equal(0.0, V(nl, r.NodeVoltages, "d"), 9);
    }

    /// <summary>The linear DC solve agrees, exactly rather than through a 2e7 S reactance.</summary>
    [Fact]
    public void TheLinearDcSolve_AlsoShortsTheLine()
    {
        var nl = Bias("TLIN:T1 a b Z=50 Ohm L=3 mm");
        var r = LinearDcEngine.Run(nl);
        Assert.NotNull(r.Solution);
        Assert.Equal(1.0, V(nl, r.Solution.NodeVoltages, "b"), 12);
    }
}
