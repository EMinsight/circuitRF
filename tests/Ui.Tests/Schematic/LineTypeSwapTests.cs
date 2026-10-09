// brief-artsch-11: Swap Line Type is one function, LineTypeSwap.Swap. One test per claim: a measured gap becomes
// CPWG's G; a swap and its inverse are the identity; a TLIN's Z and εeff are the source model's at F; a TLIN with
// no width to keep gets one synthesised for its Z; a SLIN the stackup cannot carry is the injection's refusal; and
// the extracted netlist differs only in the swapped instance's own line.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Schematic;

public sealed class LineTypeSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-swap-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const double F = 6e9;

    private static EditableComponent Line(SymbolKind kind, params (string Name, string Expr, string Unit)[] values)
    {
        var c = new EditableComponent { InstanceName = "TL1", Symbol = kind };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 2))
            c.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        foreach (var (name, expr, unit) in values)
        {
            var row = c.Parameters.FirstOrDefault(p => p.Name == name);
            if (row is null) c.Parameters.Add(row = new EditableParameter { Name = name });
            row.Expression = expr;
            row.Unit = unit;
        }
        return c;
    }

    private static string Text(EditableComponent c, string name)
    {
        var p = c.Parameters.Single(r => r.Name == name);
        return p.Expression + " " + p.Unit;
    }

    private static double Value(EditableComponent c, string name)
        => double.Parse(c.Parameters.Single(r => r.Name == name).Expression, CultureInfo.InvariantCulture);

    [Fact]
    public void MlinToCpwg_TakesTheMeanMeasuredGap_AndKeepsWAndL()
    {
        var mlin = Line(SymbolKind.Mlin, ("W", "1.4", "mm"), ("L", "10", "mm"));
        mlin.ArtworkMeasured["GapLeft"] = 0.20e-3;
        mlin.ArtworkMeasured["GapRight"] = 0.24e-3;

        var r = LineTypeSwap.Swap(mlin, SymbolKind.Cpwg, new LineTypeSwapContext(null, F));

        var cpwg = Assert.IsType<EditableComponent>(r.Component);
        Assert.Equal(SymbolKind.Cpwg, cpwg.Symbol);
        Assert.Equal("TL1", cpwg.InstanceName);
        Assert.Equal("0.22 mm", Text(cpwg, "G"));
        Assert.Equal("1.4 mm", Text(cpwg, "W"));
        Assert.Equal("10 mm", Text(cpwg, "L"));
        Assert.Equal(0.24e-3, cpwg.ArtworkMeasured["GapRight"]);
    }

    [Fact]
    public void CpwgToMlinToCpwg_RestoresGExactly_AndIsTheIdentity()
    {
        var cpwg = Line(SymbolKind.Cpwg, ("W", "1.4", "mm"), ("G", "0.3", "mm"), ("L", "10", "mm"), ("SignalLayer", "Top", ""));
        var ctx = new LineTypeSwapContext(PlanarLineInjectionTests.FourLayer(), F);

        var mlin = LineTypeSwap.Swap(cpwg, SymbolKind.Mlin, ctx).Component!;
        Assert.DoesNotContain(mlin.Parameters, p => p.Name == "G");
        var back = LineTypeSwap.Swap(mlin, SymbolKind.Cpwg, ctx).Component!;

        Assert.Equal("0.3 mm", Text(back, "G"));
        Assert.Equal(cpwg.Parameters.Select(p => (p.Name, p.Expression, p.Unit, p.ShowOnSchematic)),
                     back.Parameters.Select(p => (p.Name, p.Expression, p.Unit, p.ShowOnSchematic)));
        Assert.Empty(back.SwapRemembered);
    }

    [Fact]
    public void MlinToTlin_ComputesZAndEeff_AsTheMlinModelHasThemAtF()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        var mlin = Line(SymbolKind.Mlin, ("W", "0.4", "mm"), ("L", "10", "mm"), ("SignalLayer", "Top", ""));

        var tlin = LineTypeSwap.Swap(mlin, SymbolKind.Tline, new LineTypeSwapContext(tech, F)).Component!;

        // The line calculator's elaborated MLIN on the same conductor, at the same width and frequency.
        var model = LineCalculator.Calculate(tech, new LineCalcRequest("Top", [0.4e-3], [], null, F)).Rows.Single().Model!;
        Assert.Equal(model.Line.Z0, Value(tlin, "Z"), 1e-6);
        Assert.Equal(model.Line.Eeff, Value(tlin, "Eeff"), 1e-7);
        Assert.Equal("10 mm", Text(tlin, "L"));
        Assert.Equal("6 GHz", Text(tlin, "F"));
    }

    [Fact]
    public void TlinWithNoRememberedW_ToMlin_SynthesisesAWidthWhoseZ0AtFIsTheTlinZ()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        var tlin = Line(SymbolKind.Tline, ("Z", "50", "Ω"), ("F", "6", "GHz"));
        tlin.Parameters.RemoveAll(p => p.Name == "E");
        tlin.Parameters.Add(new EditableParameter { Name = "L", Expression = "10", Unit = "mm", Dimension = UnitDimension.Length });
        tlin.Parameters.Add(new EditableParameter { Name = "Eeff", Expression = "3.2" });

        var r = LineTypeSwap.Swap(tlin, SymbolKind.Mlin, new LineTypeSwapContext(tech, F));

        Assert.True(r.WidthSynthesised);
        var mlin = r.Component!;
        Assert.Equal("10 mm", Text(mlin, "L"));
        // What a run of that MLIN stamps: the extraction's own substrate, elaborated.
        var parameters = new List<ParameterAssignment> { new("W", Value(mlin, "W").ToString("R", CultureInfo.InvariantCulture), "mm"), new("L", "1e-3") };
        parameters.AddRange(MicrostripSubstrateInjection.BuildOverrides(tech, out _));
        var tb = new TestBench("t");
        tb.Instances.Add(new Instance("TL1", "MLIN", ["a", "b"], parameters));
        using var netlist = new Elaborator(new Library("t")).Elaborate(tb);
        var line = (IPlanarLineModel)netlist.Components.Single().Model;
        Assert.Equal(50.0, line.LineParameters(F).Z0, 1e-4);
    }

    [Fact]
    public void MlinToSlin_OnATopLayer_IsTheInjectionsRefusal_AndNothingChanges()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        var mlin = Line(SymbolKind.Mlin, ("W", "0.4", "mm"), ("L", "10", "mm"), ("SignalLayer", "Top", ""));
        var before = mlin.Parameters.Select(p => (p.Name, p.Expression, p.Unit)).ToList();

        var r = LineTypeSwap.Swap(mlin, SymbolKind.Slin, new LineTypeSwapContext(tech, F));

        Assert.False(r.Ok);
        Assert.Contains(PlanarLineSubstrateInjection.Build(tech, SymbolKind.Slin, "Top", null).Refusal!, r.Refusal);
        Assert.Equal(SymbolKind.Mlin, mlin.Symbol);
        Assert.Equal(before, mlin.Parameters.Select(p => (p.Name, p.Expression, p.Unit)));
    }

    [Theory]
    [InlineData(SymbolKind.Cpwg)]
    [InlineData(SymbolKind.Slin)]
    [InlineData(SymbolKind.Tline)]
    public void Swap_KeepsNameAndNets_TheNetlistDiffersOnlyInThatInstancesLine(SymbolKind to)
    {
        Directory.CreateDirectory(_root);
        string cnl = Path.Combine(_root, "line.cnl");
        File.WriteAllText(cnl, """
            Port:P1 in 0 Num=1 Z=50 Ohm
            MLIN:TL1 in out W=1.4 mm L=10 mm
            Port:P2 out 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=6 npts=11 Unit=GHz
            """);
        var (lib, tb) = CnlReader.ReadFile(cnl);
        var model = NetlistSchematic.Build(lib, tb, _root).Schematic!;
        int at = model.Components.FindIndex(c => c.InstanceName == "TL1");
        model.Components[at].ArtworkMeasured["GapLeft"] = 0.3e-3;
        string[] before = Lines(model);

        var swapped = LineTypeSwap.Swap(model.Components[at], to, new LineTypeSwapContext(null, LineTypeSwap.BenchTopFrequencyHz(model))).Component!;
        model.Components[at] = swapped;
        string[] after = Lines(model);

        Assert.Equal(before.Length, after.Length);
        var differing = before.Zip(after).Where(p => p.First != p.Second).ToList();
        var (old, now) = Assert.Single(differing);
        string[] o = old.Split(' '), n = now.Split(' ');
        Assert.Equal("MLIN:TL1", o[0]);
        Assert.Equal($"{ComponentTypeRegistry.DisplayName(to).ToUpperInvariant()}:TL1", n[0]);
        Assert.Equal(o[1..3], n[1..3]);   // the same two nets
    }

    private static string[] Lines(SchematicEditModel model)
        => CnlWriter.Write(NetExtractor.Extract(model).TestBench).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
