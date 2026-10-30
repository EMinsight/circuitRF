using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A placed kit part bound to the subcircuit that defines it (brief-gdf-1): its values reach the
/// subcircuit whatever case the symbol spells them in, and a terminal the kit's netlist line writes
/// after the drawn pins is wired rather than the part being skipped.
///
/// <para>Synthetic: a kit name, a part id and a three-line subcircuit.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitPartSubcircuitBindingTests : IDisposable
{
    private const string Kit  = "SampleKit";
    private const string Part = "widget";

    private readonly string _dir = Directory.CreateTempSubdirectory("crf-kitcase-").FullName;

    public KitPartSubcircuitBindingTests() => PdkKitRegistry.ResetAllForTests();

    public void Dispose()
    {
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Registers a two-pin kit part bound to <paramref name="subckt"/>, places it, extracts.</summary>
    private CircuitRF.Core.Design.Instance PlaceAndExtract(
        string subckt, CcellFile ccellExtras, params (string Name, string Value)[] parameters)
    {
        string netlist = Path.Combine(_dir, "kit.spice");
        File.WriteAllText(netlist, subckt);

        var sym = new Symbol(
            primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
            pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
            portCount:  2);
        ccellExtras.NumPorts            = 2;
        ccellExtras.ExternalProvider    = Kit;
        ccellExtras.ExternalType        = Part;
        ccellExtras.ExternalNetlistPath = netlist;
        ccellExtras.ExternalNetlistCell = Part;
        PdkKitRegistry.SetKit(null, Kit, [new PdkKitPart(Part, sym, ccellExtras, IconPath: null)]);

        var model = new SchematicEditModel();
        var comp  = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor(Kit, Part),
        };
        foreach (var (name, value) in parameters)
            comp.Parameters.Add(new EditableParameter { Name = name, Expression = value });
        model.Components.Add(comp);

        var result = SchematicCircuit.Extract(model, "tb");
        return Assert.Single(result.TestBench.Instances, i => i.InstanceName == "X1");
    }

    /// <summary>The kit's netlist dialect is case-insensitive and circuitRF compares names ordinally;
    /// filtered against the definition's spelling, every value a symbol stated in other case was dropped
    /// and the part simulated at the subcircuit's defaults, with nothing said.</summary>
    [Fact]
    public void AValueSpelledInOtherCaseReachesTheSubcircuit_InItsSpelling()
    {
        var x1 = PlaceAndExtract("""
            .subckt widget a b w=1 l=1
            R1 a b {w*l}
            .ends
            """, new CcellFile(), ("W", "5"), ("L", "2"));

        Assert.Equal(Part, x1.Reference);
        Assert.Equal("5", Assert.Single(x1.Overrides, o => o.Name == "w").Expression);
        Assert.Equal("2", Assert.Single(x1.Overrides, o => o.Name == "l").Expression);
    }

    /// <summary>A two-pin symbol whose netlist line writes its body connection after the pins binds to a
    /// three-port subcircuit, the third port on the net the parameter names — it used to be skipped.</summary>
    [Fact]
    public void ABodyTerminalIsWiredToTheNetItsParameterNames()
    {
        var x1 = PlaceAndExtract("""
            .subckt widget a b bn w=1
            R1 a b {w}
            R2 b bn 1e9
            .ends
            """,
            new CcellFile
            {
                ExternalExtraTerminals  = ["body"],
                ExternalFixedParameters = new() { ["body"] = "sub!" },
            });

        Assert.Equal(Part, x1.Reference);
        Assert.Equal(3, x1.NetBindings.Count);
        Assert.Equal("sub!", x1.NetBindings[2]);
    }
}
