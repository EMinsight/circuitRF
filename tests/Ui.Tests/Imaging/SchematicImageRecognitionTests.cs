// brief-img-10-schematic-emit.md §4 — a schematic picture made into a circuit (R-im10-1 … R-im10-5).
//
// The picture is a two-port L-C-L ladder as a datasheet draws it — terminal circles, a series coil, a shunt capacitor to a
// ground, a stub up to a supply bar, a second coil — drawn in memory with its designators and values in DejaVu Sans. It
// must come back as the .cnl it was drawn from: the same kinds, the same connectivity, the same values. Smudged values
// come back as variables with tune entries; with the ports taken away there is no analysis, and the report says why.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SchematicImageRecognitionTests : IDisposable
{
    // DejaVu Sans's cap height is 0.729 em: a 14 px cap height.
    private const float Size = 14f / 0.729f;

    private const string DrawnFrom =
        "Port:P1 in 0 Num=1 Z=50 Ohm\nL:L1 in VCC L=3.3 nH\nC:C1 VCC 0 C=1.5 pF\nL:L2 VCC out L=3.3 nH\n" +
        "Port:P2 out 0 Num=2 Z=50 Ohm\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-schimg-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The ladder. Without values each is a scrawl of glyph size; without ports the two ends are named nets.</summary>
    internal static RasterImage Ladder(bool values = true, bool ports = true) => Pictures.Draw(560, 300, c => DrawLadder(c, values, ports));

    internal static void DrawLadder(SKCanvas c, bool values = true, bool ports = true)
    {
        using var pen = SchematicPictures.Ink();
        void Coil(float x)
        {
            using var humps = new SKPath();
            humps.MoveTo(x, 140);
            for (int k = 0; k < 4; k++) humps.ArcTo(new SKRect(x + 12 * k, 134, x + 12 + 12 * k, 146), 180, 180, false);
            c.DrawPath(humps, pen);
        }
        void Value(string text, float left, float baseline)
        {
            if (values) { SchematicPictures.Text(c, text, left, baseline, Size); return; }
            var rng = new Random((int)left);
            for (int k = 0; k < 3; k++)
            {
                using var path = new SKPath();
                float x = left + 16 * k, y = baseline - 14;
                path.MoveTo(x + (float)rng.NextDouble() * 11, y + (float)rng.NextDouble() * 14);
                for (int i = 0; i < 9; i++) path.LineTo(x + (float)rng.NextDouble() * 11, y + (float)rng.NextDouble() * 14);
                c.DrawPath(path, pen);
            }
        }
        if (ports)
        {
            c.DrawCircle(40, 140, 8, pen);
            c.DrawCircle(508, 140, 8, pen);
            SchematicPictures.Text(c, "P1", 30, 118, Size);
            SchematicPictures.Text(c, "P2", 498, 118, Size);
            c.DrawLine(48, 140, 120, 140, pen);
            c.DrawLine(428, 140, 500, 140, pen);
        }
        else
        {
            SchematicPictures.Text(c, "NA", 30, 147, Size);
            SchematicPictures.Text(c, "NB", 502, 147, Size);
            c.DrawLine(60, 140, 120, 140, pen);
            c.DrawLine(428, 140, 500, 140, pen);
        }
        Coil(120);
        c.DrawLine(168, 140, 380, 140, pen);
        Coil(380);
        SchematicPictures.Text(c, "L1", 132, 118, Size);
        Value("3.3nH", 112, 176);
        SchematicPictures.Text(c, "L2", 392, 118, Size);
        Value("3.3nH", 372, 176);

        c.DrawLine(230, 140, 230, 200, pen);
        c.DrawLine(216, 200, 244, 200, pen);
        c.DrawLine(216, 208, 244, 208, pen);
        c.DrawLine(230, 208, 230, 250, pen);
        SchematicPictures.Ground(c, 230, 250);
        SchematicPictures.Text(c, "C1", 256, 200, Size);
        Value("1.5pF", 256, 228);

        c.DrawLine(310, 140, 310, 70, pen);
        c.DrawLine(296, 70, 324, 70, pen);
        SchematicPictures.Text(c, "VCC", 292, 65, Size);
    }

    private static SchematicImageInput Input(RasterImage img) =>
        new() { Source = ImageSource.FromBytes(Pictures.Png(img), "ladder.png").Source! };

    /// <summary>Each net as the instances on it — ground marked — so pin order and net names do not matter.</summary>
    private static IReadOnlyList<string> Connectivity(TestBench tb) =>
        [.. tb.Instances.SelectMany(i => i.NetBindings.Distinct().Select(n => (Net: n, i.InstanceName)))
                        .GroupBy(t => t.Net)
                        .Select(g => (g.Key == "0" ? "GND:" : "") + string.Join(",", g.Select(t => t.InstanceName).Order(StringComparer.Ordinal)))
                        .Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> Kinds(TestBench tb) =>
        [.. tb.Instances.Select(i => $"{i.InstanceName}:{i.Reference}").Order(StringComparer.Ordinal)];

    private static string? ValueOf(TestBench tb, string name, string param) =>
        tb.Instances.Single(i => i.InstanceName == name).Overrides.Where(o => o.Name == param).Select(o => $"{o.Expression} {o.Unit}".Trim()).SingleOrDefault();

    [Fact]
    public void TheLadder_IsTheNetlistItWasDrawnFrom_AndItsDrawingExtractsToIt()
    {
        var (_, want) = new CnlReader().Read(DrawnFrom);
        var run = SchematicImageRecognition.Run(Input(Ladder()), SchematicImageTarget.NewCell(_dir, "ladder"));

        Assert.True(run.Ok, run.Refusal);
        var tb = run.Reading.Circuit!.TestBench;
        Assert.Equal(Kinds(want), Kinds(tb));
        Assert.Equal(Connectivity(want), Connectivity(tb));
        foreach (var (name, param) in new[] { ("L1", "L"), ("C1", "C"), ("L2", "L") })
            Assert.Equal(ValueOf(want, name, param), ValueOf(tb, name, param));
        Assert.Single(tb.Analyses);

        // As drawn, the drawing is the same circuit, with the picture kept beside it and laid under it.
        var drawn = NetExtractor.Extract(run.Schematic!).TestBench;
        Assert.Equal(Connectivity(want), Connectivity(drawn));
        Assert.DoesNotContain(run.Report.Findings, f => f.Class == RecognitionFindingClass.PicturePlacementFallbacks);
        var underlay = Assert.Single(run.Schematic!.CanvasObjects.OfType<EditableBitmap>());
        Assert.False(underlay.IsLocked);   // an ordinary picture: selected, moved, resized or deleted like any other
        // A label only where the picture writes a net's name — not one per net, as a redrawn netlist has.
        Assert.Equal(["VCC"], run.Schematic!.NetLabels.Select(l => l.Name));
        Assert.True(File.Exists(Path.Combine(_dir, "ladder", underlay.ImagePath.Replace("../", ""))), underlay.ImagePath);
        Assert.Equal("schematic", run.Schematic!.ImageSource!.Kind);
    }

    [Fact]
    public void SmudgedValues_AreThreeVariablesWithThreeTuneEntries()
    {
        var (_, want) = new CnlReader().Read(DrawnFrom);
        var r = SchematicImageRecognition.Circuit(Input(Ladder(values: false)));

        Assert.True(r.Ok, r.Refusal);
        var tb = r.Circuit!.TestBench;
        Assert.Equal(Kinds(want), Kinds(tb));
        Assert.Equal(Connectivity(want), Connectivity(tb));
        Assert.Equal(["C1_C", "L1_L", "L2_L"], tb.GlobalVariables.Select(v => v.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["C1_C", "L1_L", "L2_L"], tb.Tuning!.Variables.Where(v => v.Tune).Select(v => v.Key).Order(StringComparer.Ordinal));
        Assert.Equal("L1_L", ValueOf(tb, "L1", "L"));
    }

    [Fact]
    public void WithThePortsRemoved_ThereIsNoAnalysis_AndTheReportSaysWhy()
    {
        var r = SchematicImageRecognition.Circuit(Input(Ladder(ports: false)));

        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Circuit!.TestBench.Analyses);
        Assert.DoesNotContain(r.Circuit.TestBench.Instances, i => i.Reference == "Port");
        Assert.Equal(SchematicImageEmit.NoPortLine, r.Report.Of(RecognitionFindingClass.PictureNoPort)?.Sentence);
    }
}
