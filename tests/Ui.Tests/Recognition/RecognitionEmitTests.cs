// brief-artsch-6-emit-and-target-cell.md §4 — the circuit (R-as6-2): a synthetic board of two ports, a series C
// with a value from the bill of materials, a shunt L of unknown value and a bend becomes a .cnl with those
// instances, the variable L_A1_L and its tune entry, the technology statement and SP1 — which check passes with
// no errors and which simulates.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Cli;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class RecognitionEmitTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as6-emit-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void TheBoardBecomesANetlistThatChecksCleanAndSimulates()
    {
        var input = EmitBoards.Saved(_root);
        var result = ArtworkRecognition.Recognize(input);
        Assert.True(result.Ok, result.Refusal);

        var circuit = RecognitionEmit.Build(result, input);
        string cnl = Path.Combine(_root, "board.cnl");
        string text = circuit.CnlText(_root);
        File.WriteAllText(cnl, text);
        output.WriteLine(text);

        var (_, tb) = new CnlReader().Read(text);
        Assert.Equal("tech/board.ctech", tb.Technology);
        Instance Only(string type) => Assert.Single(tb.Instances, i => i.Reference == type);

        // Ports by AS-3's names, on nets named after them; the parts by designator.
        Assert.Equal(["P1", "P2"], tb.Instances.Where(i => i.Reference == "Port").Select(i => i.InstanceName));
        Assert.Equal(["p1", "0"], tb.Instances.Single(i => i.InstanceName == "P1").NetBindings);
        var c1 = Only("C");
        Assert.Equal(("C1", "10", "pF"), (c1.InstanceName, c1.Overrides.Single().Expression, c1.Overrides.Single().Unit));
        var l = Only("L");
        Assert.Equal(("L_A1", "L_A1_L"), (l.InstanceName, l.Overrides.Single().Expression));
        Assert.Equal("0", l.NetBindings[1]);

        // The lines, numbered from port 1, carrying their layer and no substrate.
        Assert.Equal("B1", Only("MBEND").InstanceName);
        var lines = tb.Instances.Where(i => i.Reference == "MLIN").ToList();
        Assert.Equal(["TL1", "TL2", "TL3"], lines.Select(i => i.InstanceName).Take(3));
        Assert.All(lines, i => Assert.DoesNotContain(i.Overrides, o => o.Name is "H" or "Er"));
        Assert.All(lines, i => Assert.Contains(i.Overrides, o => o.Name == "SignalLayer" && o.Expression == "Top"));

        // The unknown value: a transparent shunt start, and a knob.
        var v = Assert.Single(tb.GlobalVariables);
        Assert.Equal(("L_A1_L", "1", "uH"), (v.Name, v.Expression, v.Unit));
        var tune = Assert.Single(tb.Tuning!.Variables);
        Assert.Equal(("L_A1_L", true, false, "100 nH", "10 uH"), (tune.Key, tune.Tune, tune.Opt, tune.Min, tune.Max));
        Assert.Equal(RecognitionEmit.AnalysisName, Assert.IsType<SParameterAnalysis>(Assert.Single(tb.Analyses)).Name);

        Assert.Equal(0, Cli("check", cnl, "--json"));
        string s2p = Path.Combine(_root, "board.s2p");
        Assert.Equal(0, Cli("sparam", cnl, "-o", s2p));
        Assert.True(File.Exists(s2p));
    }

    private static int Cli(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }
}
