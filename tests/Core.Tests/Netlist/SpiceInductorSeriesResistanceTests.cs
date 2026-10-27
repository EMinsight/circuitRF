using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Core.Tests.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// R-vl1-3 — one dialect writes an inductor's series resistance as <c>Rser</c>, and circuitRF's
/// inductor reads <c>R</c>. Carried through verbatim, the value was an override nothing read, so the
/// part simulated lossless. Synthetic fixture.
/// </summary>
public sealed class SpiceInductorSeriesResistanceTests
{
    [Fact]
    public void Rser_IsTheInductorsSeriesResistance_AndAnOverrideItDoesNotTakeIsANote()
    {
        var r = SpiceNetlistReader.Read("""
            .subckt part p1 p2
            L1 p1 p2 10n Rser=5 Rpar=1k
            .ends part
            """);

        Assert.Contains(r.Notes, n => n.Message.Contains("Rpar", StringComparison.Ordinal));

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "part", ["a", "0"], []));
        using var nl = new Elaborator(r.Library).Elaborate(tb);
        var l = Assert.Single(nl.Components, c => c.Model is CircuitRF.Core.Devices.InductorModel);

        var mna = new CapturingMnaContext();
        l.Stamp(mna, 2 * Math.PI * 1e9);

        // The branch constraint is V_a − V_b − Z·i = 0, so its diagonal is −Z.
        var z = -mna.BranchConstraints.Single().Value;
        Assert.Equal(5.0, z.Real, 9);
    }
}
