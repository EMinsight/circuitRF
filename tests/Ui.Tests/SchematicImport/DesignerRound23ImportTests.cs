// Designer round 23 — schematic import: a native part longer than the source's symbol left a wire ending inside its
// body; a connector refused as a Port was dropped from the import altogether; test points refused a run for want of a
// model; a part's footprint property carried its case size and nothing read it. And the Gerber layer list that names
// each file's copper side.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Import;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class DesignerRound23ImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-dfr23-" + Guid.NewGuid().ToString("N")[..8]);

    public DesignerRound23ImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A two-pin symbol with its pins at <paramref name="top"/> and <paramref name="bottom"/> mils, Y up.</summary>
    private static ForeignSymbol TwoPin(string key, double top, double bottom) => new(key, key, "lib",
    [
        new ForeignSymbolUnit(1,
            [new ForeignPin("1", "~", ForeignPinType.Passive, 0, top, ForeignPinDirection.Down),
             new ForeignPin("2", "~", ForeignPinType.Passive, 0, bottom, ForeignPinDirection.Up)],
            [new KitSymbolRectangle(-40, -50, 40, 50, false)]),
    ]);

    private static ForeignSymbol Pins(string key, int count) => new(key, key, "lib",
    [
        new ForeignSymbolUnit(1,
            [.. Enumerable.Range(0, count).Select(k =>
                new ForeignPin($"{k + 1}", "~", ForeignPinType.Passive, 0, -100 * k, ForeignPinDirection.Right))],
            [new KitSymbolRectangle(0, -100 * count, 100, 100, false)]),
    ]);

    private static readonly ForeignSymbol Ground = new("GND", "GND", "power",
        [new ForeignSymbolUnit(1, [new ForeignPin("1", "GND", ForeignPinType.PowerIn, 0, 0, ForeignPinDirection.Down, Hidden: true)],
                               [new KitSymbolLine(-50, 0, 50, 0)])]);

    private static ForeignWire W(params (double X, double Y)[] points) => new(points);

    private ImportBuildResult Build(ForeignSchematic source, SchematicImportOptions? options = null) =>
        SchematicImportBuild.Build(source, "Top", Path.Combine(_dir, "Top", "schematic"), Path.Combine(_dir, "parts"), options);

    /// <summary>A capacitor drawn 300 mils pin to pin (900 and 1200), wired from a label above and to ground below, with
    /// another net's wire crossing at <paramref name="crossAt"/> (none when null). circuitRF's capacitor is 400 mils pin to
    /// pin, so it is fitted at 1000 (pins 800, 1200) or at 1100 (pins 900, 1300).</summary>
    private static ForeignSchematic ShortCapacitor(double? crossAt = null) => new("dsn", "short-cap.dsn",
    [
        new ForeignSheet("Page1", 3000, 3000)
        {
            Instances = [new("C1", 1, "C", 1000, 1000, Value: "1nF"), new("#G1", 1, "GND", 1000, 1600)],
            Wires =
            [
                W((1000, 500), (1000, 900)),
                W((1000, 1200), (1000, 1600)),
                .. crossAt is { } y ? new[] { W((700, y), (1300, y)) } : [],
            ],
            Labels =
            [
                new(ForeignLabelKind.Global, "IN", 1000, 500),
                .. crossAt is { } y2 ? new ForeignLabel[] { new(ForeignLabelKind.Global, "X", 700, y2) } : [],
            ],
        },
    ], [TwoPin("C", 100, -200), Ground], "Page1");

    private static void AssertMatched(ForeignSchematic source, ImportBuildResult built)
    {
        var check = ImportConnectivity.Compare(source, built);
        Assert.True(check.Matched, string.Join("\n", check.Geometric.Differences.Concat(built.Notes)));
    }

    private static (double X, double Y)[] Ends(ImportBuildResult built) =>
        [.. built.Top!.Model.Wires.SelectMany(w => new[] { w.Points[0], w.Points[^1] })];

    [Fact]
    public void ALongerNativePart_EndsTheSourceWiresOnItsPins_NotInsideItsBody()
    {
        var source = ShortCapacitor();
        var built = Build(source);

        Assert.True(built.Ok, string.Join("\n", built.Refusals));
        var c1 = built.Top!.Model.Components.Single(c => c.InstanceName == "C1");
        double lo = c1.Y - 200, hi = c1.Y + 200;   // the native capacitor's pins
        Assert.DoesNotContain(Ends(built), p => p.X == 1000 && p.Y > lo && p.Y < hi);
        Assert.Contains(Ends(built), p => p == (1000, lo));
        Assert.Contains(Ends(built), p => p == (1000, hi));
        AssertMatched(source, built);
    }

    [Fact]
    public void ANativePart_IsNotFittedWithAPinOnAnotherNetsWire_WhenTheOtherSnapAvoidsIt()
    {
        // The pins ask for an origin of 1050; snapped to 1000 the top pin lands on X's wire at 800, snapped to 1100 it
        // does not.
        var source = ShortCapacitor(crossAt: 800);
        var built = Build(source);

        Assert.True(built.Ok, string.Join("\n", built.Refusals));
        Assert.Equal(1100, built.Top!.Model.Components.Single(c => c.InstanceName == "C1").Y);
        AssertMatched(source, built);
        Assert.DoesNotContain(built.Notes, n => n.Contains("could not be wired", StringComparison.Ordinal));
    }

    /// <summary>A four-pin header with every pin on its own labelled net, a test point on the first, and a resistor.</summary>
    private static ForeignSchematic HeaderAndTestPoint() => new("dsn", "header.dsn",
    [
        new ForeignSheet("Page1", 3000, 3000)
        {
            Instances =
            [
                new("J1", 1, "HDR4", 500, 500, Value: "Header 4"),
                new("TP1", 1, "TP", 1200, 500, Value: "TP"),
                new("R1", 1, "R", 1500, 700, Value: "10k"),
            ],
            Wires = [W((500, 500), (1200, 500)), W((1200, 500), (1500, 500)), W((1500, 900), (1500, 1100))],
            Labels =
            [
                new(ForeignLabelKind.Global, "B", 500, 600), new(ForeignLabelKind.Global, "C", 500, 700),
                new(ForeignLabelKind.Global, "D", 500, 800), new(ForeignLabelKind.Power, "GND", 1500, 1100),
            ],
        },
    ], [Pins("HDR4", 4), Pins("TP", 1), TwoPin("R", 200, -200)], "Page1");

    [Fact]
    public void AConnectorThatCannotBeAPort_IsImportedAsItsPart_AndIsNotOffered()
    {
        // Written, because the check reads a symbol cell's pins from its folder.
        var source = HeaderAndTestPoint();
        var result = SchematicImport.Import(source, _dir, "Top", new SchematicImportOptions { Ports = ["J1"] });

        Assert.True(result.Ok, result.Refusal);
        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));
        var model = SchematicPersistence.LoadFromFile(result.SchematicPath!).model;
        Assert.Contains(model.Components, c => c.InstanceName == "J1");
        Assert.Contains(result.CreatedCells, c => c.EndsWith("HDR4", StringComparison.Ordinal));
        Assert.DoesNotContain(SchematicImport.PortCandidates(source), c => c.Key == "J1");
    }

    [Fact]
    public void ATestPoint_IsDrawnButLeftOutOfARun()
    {
        var result = SchematicImport.Import(HeaderAndTestPoint(), _dir, "Top");

        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));
        var model = SchematicPersistence.LoadFromFile(result.SchematicPath!).model;
        Assert.Equal(DisableState.Open, model.Components.Single(c => c.InstanceName == "TP1").Disable);
        Assert.Equal(DisableState.None, model.Components.Single(c => c.InstanceName == "J1").Disable);
    }

    [Theory]
    [InlineData("XYZ_RESC1005X038L", "smt:0402@L")]          // an IPC-7351 name's size is METRIC: 1005 is the 0402
    [InlineData("CAPC1608X080N", "smt:0603@N")]
    [InlineData("XYZ_INDC1005X060L_POL", "smt:0402@L")]
    [InlineData("R_0603_1608Metric", "smt:0603@N")]
    [InlineData("XYZ_CON_TERMINAL_2P", null)]
    public void AFootprintName_GivesItsChipCase(string name, string? expected)
    {
        Assert.Equal(expected, SchematicImportMap.FootprintOfName(name)?.ToString());
        Assert.Equal(expected, SchematicImportMap.FootprintOfProperties(new Dictionary<string, string> { ["PCB Footprint"] = name })?.ToString());
    }

    [Fact]
    public void AGerberLayerList_NamesEachCopperFileAndItsPlaceInTheStack()
    {
        const string text = """
            {Layers
                {Layer  1
                    {File        BOARD.TOP}
                    {Type        Top}
                }
                {Layer  2
                    {File        BOARD.GND}
                    {Type        Inner}
                }
                {Layer  3
                    {File        BOARD.BOT}
                    {Type        Bottom}
                }
                {Layer  4
                    {File        BOARD.SMB}
                    {Type        Other}
                }
            }
            """;

        Assert.Equal(GerberFileKind.JobFile, GerberFileClassifier.ClassifyContent("BOARD.LST", text).Kind);
        var job = GerberJobFile.Read(text)!;
        Assert.Equal(3, job.LayerNumber);
        Assert.Equal(["Copper,L1,Top", "Copper,L2,Inr", "Copper,L3,Bot", null], job.Files.Select(f => f.FileFunction));
        Assert.Equal(["BOARD.TOP", "BOARD.GND", "BOARD.BOT", "BOARD.SMB"], job.Files.Select(f => f.Path));
    }
}
