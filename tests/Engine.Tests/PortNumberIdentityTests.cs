// ================================================================
//  PortNumberIdentityTests.cs — brief DFR27-2.
//
//  A Term's Num is its identity. Terms numbered 1, 2, 9, 10 give an S cube whose port axes are
//  [1, 2, 9, 10], not 1…4 — compacting them made S(4,3) silently mean Terms 10 and 9, so every
//  trace, goal and measurement keyed by number moved to a different pair after an edit.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests;

public class PortNumberIdentityTests
{
    // Terms 9 and 10 sit either side of a 100 Ω series resistor, so S(10,9) = 100/(100+100) = 0.5;
    // Term 1 is matched and Term 2 is open, so neither can be mistaken for that pair.
    private static string Bench(int a, int b, int c, int d) => $"""
        Term:T1  c 0 Num={a} Z=50 Ohm
        R:R1     c 0 R=50 Ohm
        Term:T2  d 0 Num={b} Z=50 Ohm
        Term:T9  x 0 Num={c} Z=50 Ohm
        Term:T10 y 0 Num={d} Z=50 Ohm
        R:R2     x y R=100 Ohm
        analysis SP1 type=sparam start=1 GHz stop=1 GHz npts=1
        """;

    private static (ElaboratedNetlist Nl, RfCore.Data.DataSet Ds) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (nl, SParameterEngine.Run(nl, [1e9]));
    }

    [Fact]
    public void GappedTerms_TheAxesCarryTheirNumbers_AndLookupIsByNumber()
    {
        var (_, ds) = Run(Bench(1, 2, 9, 10));

        Assert.Equal([1.0, 2, 9, 10], ds["S"].Axes[1].Values);
        Assert.Equal([1.0, 2, 9, 10], ds["S"].Axes[2].Values);
        Assert.Equal([1.0, 2, 9, 10], ds["Z0"].Axes[0].Values);

        Assert.Equal(0.5, ds.S(10, 9).ComplexValues[0].Real, 12);

        var ex = Assert.Throws<ArgumentException>(() => ds.S(4, 3));
        Assert.Contains("Port 4", ex.Message);
        Assert.Contains("1, 2, 9, 10", ex.Message);
    }

    [Fact]
    public void ContiguousTerms_KeepOneToN()
    {
        var (_, ds) = Run(Bench(1, 2, 3, 4));
        Assert.Equal([1.0, 2, 3, 4], ds["S"].Axes[1].Values);
        Assert.Equal(0.5, ds.S(4, 3).ComplexValues[0].Real, 12);
    }

    [Fact]
    public void Gaps_AreOneWarning_NamingTheMissingRun()
    {
        var (nl, _) = Run(Bench(1, 2, 9, 10));
        var gap = Assert.Single(nl.Warnings, w => w.Contains("missing"));
        Assert.Contains("1, 2, 9, 10", gap);
        Assert.Contains("3–8", gap);
        Assert.Contains("Touchstone", gap);
    }
}
