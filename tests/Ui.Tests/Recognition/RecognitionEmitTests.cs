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
        var c1Value = c1.Overrides.Single(o => o.Name == "C");
        Assert.Equal(("C1", "10", "pF"), (c1.InstanceName, c1Value.Expression, c1Value.Unit));
        var l = Only("L");
        Assert.Equal(("L_A1", "L_A1_L"), (l.InstanceName, l.Overrides.Single(o => o.Name == "L").Expression));
        // Each part carries the land pattern its case was read as (designer report, round 15: it was read and dropped).
        Assert.All([c1, l], p => Assert.Equal("smt:0603@N", p.Overrides.Single(o => o.Name == "Footprint").Expression));
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

    [Fact]
    public void Digits_SetsTheSignificantFiguresEveryNumberIsWrittenWith()
    {
        var input = EmitBoards.Saved(_root);
        var result = ArtworkRecognition.Recognize(input);
        Assert.True(result.Ok, result.Refusal);
        var all = RecognitionEmit.Build(result, input, new() { Digits = RecognitionEmitOptions.AllDigits }).TestBench;
        var two = RecognitionEmit.Build(result, input, new() { Digits = 2 }).TestBench;

        // Every number the 2-digit circuit carries is the full one rounded, and at least one was longer.
        int shortened = 0;
        foreach (var (a, b) in all.Instances.Zip(two.Instances))
            foreach (var (oa, ob) in a.Overrides.Zip(b.Overrides))
            {
                if (!double.TryParse(oa.Expression, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out double full)) continue;
                Assert.Equal(RecognitionEmitOptions.Spell(full, 2), ob.Expression);
                if (ob.Expression != oa.Expression) shortened++;
            }
        Assert.True(shortened > 0, "the board carries no value longer than two figures");

        // Plain notation at any size: the netlist reads a bare number.
        Assert.Equal("1200000", RecognitionEmitOptions.Spell(1_234_567, 2));
        Assert.Equal("0.0000123", RecognitionEmitOptions.Spell(0.0000123456, 3));
    }

    /// <summary>A trace reaching no port and no part changes no S-parameter and drew as a loose element: it is left
    /// out and reported (designer report, round 15).</summary>
    [Fact]
    public void ALineReachingNoPortAndNoPart_IsLeftOutAndReported()
    {
        // D1, a two-pad D the circuit leaves out (as the designer's QFN was), and a trace from its pad to nowhere.
        var input = EmitBoards.Saved(_root);
        var d1 = RecognitionBoards.Land("0603", CircuitRF.Design.Layout.Footprints.DensityLevel.Nominal, 18_000, 9_000);
        var (_, right) = RecognitionBoards.Ends(d1);
        input.View.Shapes.AddRange(d1);
        input.View.Shapes.Add(RecognitionBoards.Line(RecognitionBoards.Top, right, 28_000, 9_000));
        var placement = input.Placement! with
        {
            Rows = [.. input.Placement!.Rows,
                    new CircuitRF.Design.Layout.Interchange.PlacementRow("D1", RecognitionBoards.Um(18_000), RecognitionBoards.Um(9_000), 0, false, "0603", 1)],
        };
        input = input with { Shapes = input.View.Shapes, Placement = placement };
        var (result, circuit) = ArtworkRecognition.Circuit(input);
        Assert.True(result.Ok, result.Refusal);

        Assert.NotEmpty(circuit!.StrayLines);
        Assert.Equal(circuit.StrayLines.Count, result.Report.Count(RecognitionFindingClass.StrayLines));
        var (_, plain) = ArtworkRecognition.Circuit(EmitBoards.Saved(Path.Combine(_root, "plain")));
        Assert.Equal(plain!.TestBench.Instances.Count(i => i.Reference == "MLIN"),
                     circuit.TestBench.Instances.Count(i => i.Reference == "MLIN"));
    }

    private static int Cli(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }
}
