// brief-artsch-6-emit-and-target-cell.md §4 — NetlistSchematic.Build's artwork hints (R-as6-3): hints that agree
// with the drawing it would make anyway change no byte of it; a shunt part whose artwork lies above the line is
// drawn above it, and the drawing still extracts to the netlist.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class NetlistSchematicHintTests
{
    private const string Netlist =
        "Port:P1 in 0 Num=1 Z=50 Ohm\nR:R1 in out R=10 Ohm\nC:C1 out 0 C=1 pF\nL:L1 out 0 L=1 nH\n" +
        "Port:P2 out 0 Num=2 Z=50 Ohm\nanalysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n";

    private static SchematicEditModel Draw(IReadOnlyDictionary<string, (long X, long Y)>? hints)
    {
        var (lib, tb) = new CnlReader().Read(Netlist);
        var result = NetlistSchematic.Build(lib, tb, null, hints);
        return Assert.IsType<SchematicEditModel>(result.Schematic);
    }

    private static Dictionary<string, (long X, long Y)> Hints(long c1Y) => new()
    {
        ["P1"] = (0, 0), ["R1"] = (5_000, 0), ["P2"] = (10_000, 0), ["C1"] = (6_000, c1Y), ["L1"] = (7_000, -1_000),
    };

    [Fact]
    public void HintsThatAgreeWithTheDrawingChangeNoByteOfIt()
    {
        string plain = SchematicPersistence.Serialize(Draw(null));

        // Both shunt parts on the right of travel (below a left-to-right line), in the order the netlist has them.
        Assert.Equal(plain, SchematicPersistence.Serialize(Draw(Hints(-1_000))));
    }

    [Fact]
    public void AShuntPartWhoseArtworkLiesAboveTheLineIsDrawnAboveIt()
    {
        var model = Draw(Hints(+1_000));

        var c1 = model.Components.Single(c => c.InstanceName == "C1");
        var l1 = model.Components.Single(c => c.InstanceName == "L1");
        Assert.True(c1.Y < 0, $"C1 at y = {c1.Y}");
        Assert.True(l1.Y > 0, $"L1 at y = {l1.Y}");

        var tb = NetExtractor.Extract(model).TestBench;
        Assert.Equal(["out", "0"], tb.Instances.Single(i => i.InstanceName == "C1").NetBindings);
        Assert.Equal(["in", "out"], tb.Instances.Single(i => i.InstanceName == "R1").NetBindings);
    }
}
