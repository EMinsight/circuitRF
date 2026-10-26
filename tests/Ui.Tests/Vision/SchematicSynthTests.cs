// brief-iml-2 §3 — generated pictures of circuitRF's own schematics (tools/SchematicSynth). Fast and data-free: three
// seeds generate in-process; each label passes the structural checks crfml.validate makes (classes, pins, ids, value
// texts), every body box lies inside the picture or is marked cut, every pin is on its symbol's ink in the symbol mask,
// and the drawing extracts — through NetExtractor, the extraction Simulate performs — to the netlist written beside it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.SchematicSynth;
using CircuitRF.Ui.Tests.ImageAcceptance;
using Xunit;

namespace CircuitRF.Ui.Tests.Vision;

public sealed class SchematicSynthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-synth-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Underlays _underlays;

    public SchematicSynthTests() => _underlays = Underlays.Create(Path.Combine(_dir, ".underlay"), seed: 1, count: 2);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PictureFiles Make(int seed)
    {
        var pic = Batch.MakeOne(seed, 0, authored: [], _underlays, out string? why);
        Assert.True(pic is not null, $"seed {seed}: {why}");
        return pic!;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AGeneratedPicture_IsLabelledExactly(int seed)
    {
        var pic = Make(seed);
        var label = pic.Label;

        // The structural checks, the pin-on-symbol-ink check among them (it reads the symbol mask).
        var errors = LabelChecks.Check(label, pic.SymbolMask).Where(f => f.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.NotEmpty(label.Parts);

        foreach (var p in label.Parts)
        {
            bool inside = p.Body[0] >= 0 && p.Body[1] >= 0 && p.Body[2] <= label.Width && p.Body[3] <= label.Height;
            Assert.True(inside || p.Cut, $"{p.Id}: body [{string.Join(", ", p.Body)}] leaves the picture and is not marked cut");
        }

        // The drawing is the circuit: its extraction is the netlist written beside the picture — same instances, values
        // and connections, nets renamed at most — and, independently, the same part–net graph.
        var (_, truth) = new CnlReader().Read(pic.Netlist, "tb");
        var drawn = NetExtractor.Extract(pic.Drawing!.Model).TestBench;
        Assert.True(Netlists.Same(truth, drawn, values: true, out string why), why);
        Assert.True(NetlistShape.SameTopology(truth, drawn));

        // A hidden parameter is not text: every word in the label is one the render model draws.
        var (rm, _) = pic.Drawing.Model.BuildRenderModel();
        var drawnWords = rm.Components.SelectMany(c => c.Labels).Concat(rm.NetLabels.Select(l => l.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var c in rm.Components)
            foreach (var t in (c.InstanceSymbol ?? BuiltInSymbols.Primitives(c.Symbol, c.Ports.Count)).Primitives.OfType<CircuitRF.Design.Symbol.TextPrimitive>())
                drawnWords.Add(t.Content);
        foreach (var t in label.Texts) Assert.Contains(t.String, drawnWords);
    }

    /// <summary>
    /// A label's <c>turn</c> is measured from the class's canonical drawing, and IML-2 takes it from the part's own
    /// rotation — right only where circuitRF's drawing of a kind IS the class's canonical one, turned by
    /// <see cref="ClassList.CanonicalTurn"/>. For every kind circuitRF draws, its pins, turned back by that, must sit on
    /// the sides of the body that classes.json puts the class's pins on. (circuitRF's Pin, its pin on the right where the
    /// terminal class has it on top, was a quarter turn off until CanonicalTurn said so.)
    /// </summary>
    [Fact]
    public void EveryKind_ItsPinsTurnedBack_SitWhereTheClassPutsThem()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ClassListTests.KitDir(), "classes.json")));
        var canonical = doc.RootElement.GetProperty("classes").EnumerateArray()
            .Where(c => c.GetProperty("pins").ValueKind == System.Text.Json.JsonValueKind.Array)
            .ToDictionary(c => c.GetProperty("name").GetString()!,
                          c => c.GetProperty("pins").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!,
                                   p => (U: p.GetProperty("at")[0].GetDouble(), V: p.GetProperty("at")[1].GetDouble())));
        var wrong = new List<string>();
        foreach (var (kind, cls) in ClassList.OfKind)
        {
            if (!canonical.TryGetValue(cls, out var pins)) continue;   // a perimeter class, or no pins
            var symbol = BuiltInSymbols.Primitives(kind, SymbolPortDefs.For(kind).Length);
            var body = GlyphBody.Of(kind, symbol.PortCount);
            int back = (360 - ClassList.CanonicalTurn(kind)) % 360;
            foreach (var p in symbol.Pins)
            {
                double x = p.LocalX - body.CenterX, y = p.LocalY - body.CenterY;
                // A quarter turn clockwise on a y-down page is (x, y) → (−y, x).
                for (int k = 0; k < back / 90; k++) (x, y) = (-y, x);
                var (u, v) = pins[p.Name!];
                if ((u < 0.4 && x > -1) || (u > 0.6 && x < 1) || (v < 0.4 && y > -1) || (v > 0.6 && y < 1))
                    wrong.Add($"{kind} pin {p.Name}: at ({x:0}, {y:0}) from the body centre, canonically ({u}, {v})");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void ASeed_GivesTheSameBytes()
    {
        var a = Make(5);
        var b = Make(5);
        Assert.Equal(a.Image, b.Image);
        Assert.Equal(a.Label.ToJson(), b.Label.ToJson());
        Assert.Equal(a.Masks.Keys, b.Masks.Keys);
        foreach (var k in a.Masks.Keys) Assert.Equal(a.Masks[k], b.Masks[k]);
    }
}
