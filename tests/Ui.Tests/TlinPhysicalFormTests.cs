// brief-artsch-2: TLIN's physical form — L with Eeff instead of E at F.
using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TlinPhysicalFormTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Bench(string tlinParameters) =>
        "Port:P1 in 0 Num=1 Z=50 Ohm\nPort:P2 out 0 Num=2 Z=50 Ohm\n" +
        $"TLIN:TL1 in out Z=50 Ohm {tlinParameters}\n" +
        "analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz\n";

    [Fact]
    public void LengthOfAQuarterWave_IsAQuarterWave()
    {
        const double f = 2e9, eeff = 4.0;
        double lMm = MicrostripLoss.SpeedOfLight / (4 * f * Math.Sqrt(eeff)) * 1e3;
        var (_, tb) = new CnlReader().Read(Bench($"L={lMm:R} mm Eeff={eeff}"));

        var ds = SParameterEngine.Run(new Elaborator().Elaborate(tb), [f]);
        var s21 = ds.S(2, 1).ComplexValues[0];

        Assert.Equal(-90.0, s21.Phase * 180.0 / Math.PI, 9);
        Assert.Equal(1.0, s21.Magnitude, 12);
    }

    [Theory]
    [InlineData("E=90 deg L=10 mm", new[] { "E", "L" })]
    [InlineData("L=10 mm Eeff=0.5", new[] { "Eeff" })]
    [InlineData("L=10 mm Ac=2", new[] { "Ac", "F" })]
    public void AMixedOrImpossibleLine_IsRefused_NamingTheLineAndTheKeys(string parameters, string[] keys)
    {
        var (_, tb) = new CnlReader().Read(Bench(parameters));

        var ex = Assert.Throws<ParameterRefusalException>(() => new Elaborator().Elaborate(tb));

        Assert.StartsWith("'TL1':", ex.Message);
        foreach (string key in keys) Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void NetlistToSchematicToNetlist_KeepsThePhysicalForm()
    {
        Directory.CreateDirectory(_root);
        var (lib, tb) = new CnlReader().Read(Bench("L=25 mm Eeff=3 Ac=2 Ad=1 F=2 GHz"));

        var built = NetlistSchematic.Build(lib, tb, _root);
        Assert.True(built.Schematic is not null, string.Join(" | ", built.Refusals));
        string csch = Path.Combine(_root, "tl.csch");
        SchematicPersistence.SaveToFile(csch, built.Schematic!, "tl");
        var (backLib, back) = SchematicCircuit.FromSchematic(csch);

        var line = back.Instances.Single(i => i.InstanceName == "TL1");
        var names = line.Overrides.Select(o => o.Name).ToHashSet();
        Assert.Superset(new System.Collections.Generic.HashSet<string> { "Z", "L", "Eeff", "Ac", "Ad", "F" }, names);
        Assert.DoesNotContain("E", names);
        var model = new Elaborator(backLib).Elaborate(back).Components.Single(c => c.InstancePath == "TL1").Model;
        Assert.True(model is TLineModel { IsPhysical: true, Eeff: 3.0 });
    }
}
