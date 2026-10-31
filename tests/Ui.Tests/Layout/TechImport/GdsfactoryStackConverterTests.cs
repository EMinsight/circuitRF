// brief-gdf-3 §4 Tier A — a gdsfactory kit's stack and material cards, as gdsfactory_bridge.technology
// states them, converted into the stack description the ONE technology builder reads. Synthetic JSON, no
// Python: every level, layer and material name here is invented.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.TechImport;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.TechImport;

public class GdsfactoryStackConverterTests
{
    private static string Level(string name, int layer, double zmin, double thickness, string? material)
        => "{\"name\":\"" + name + "\",\"layer\":[" + layer + ",0],\"zmin\":" + R(zmin) + ",\"thickness\":" + R(thickness) +
           ",\"material\":" + (material is null ? "null" : "\"" + material + "\"") + ",\"sidewall_angle\":0.0,\"info\":{}}";

    private static string Card(string name, double? sigma, double? epsr)
        => "\"" + name + "\":{\"sigma\":" + (sigma is { } s ? R(s) : "null") + ",\"epsr\":" + (epsr is { } e ? R(e) : "null") + ",\"tand\":null}";

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static GdsfactoryStackDocument Doc(IEnumerable<string> levels, IEnumerable<string> cards)
        => GdsfactoryStackDocument.Parse(
               "{\"module\":\"synth\",\"layers\":{},\"layerViews\":null,\"stack\":[" + string.Join(",", levels) +
               "],\"materials\":{" + string.Join(",", cards) + "},\"problems\":[]}")
           ?? throw new InvalidOperationException("the fixture did not parse");

    /// <summary>
    /// Substrate, a well, an oxide, two metals and their via, a capacitor's dielectric and plate with the
    /// plate's own via, and a cap — every class the conversion knows, in kit order rather than z order.
    /// The capacitor's dielectric and plate are drawn on ONE mask, as a kit draws them, which is what keeps
    /// the dielectric from reading as a via between the metal under it and the plate on it.
    /// </summary>
    private static readonly string[] Levels =
    [
        Level("substrate", 99, -200, 200, "bulk"),
        Level("well",       5,   -1, 1.2, "bulk"),
        Level("ild",       98,    0, 1.0, "oxide"),
        Level("met1",      10,  1.0, 0.5, "metal"),
        Level("via1",      15,  1.5, 0.7, "plug"),
        Level("mim_diel",  30,  1.5, 0.05, "nitride"),
        Level("mim",       30, 1.55, 0.15, "plate"),
        Level("vmim",      32,  1.7, 0.5, "plug"),
        Level("met2",      20,  2.2, 1.0, "metal"),
        Level("cap",       97,  3.2, 0.5, "nitride"),
    ];

    private static readonly string[] Cards =
    [
        Card("bulk", 2.0, 11.9),
        Card("oxide", 0.0, 4.0),
        Card("nitride", 0.0, 7.0),
        Card("metal", 3.0e7, null),
        Card("plug", 1.0e6, null),
        Card("plate", 5.0e5, null),
    ];

    private static (string Name, StackupKind Kind, long Dbu, double Epsr)[] Slabs(Technology t)
        => [.. t.Stackup.Layers.Where(l => l.Kind != StackupKind.Via).Select(l => (l.Name, l.Kind, l.ThicknessDbu, l.Epsr))];

    // ── 1 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachLevelIsClassifiedAsSpecified_AndTheViasSpanTheRightConductors()
    {
        var r = GdsfactoryStackConverter.Convert(Doc(Levels, Cards), null, "synth");
        var t = r.Technology;

        // Top first, every slab at the z the kit states: the cap, the top metal, the gap filled from the oxide
        // card, the plate on its dielectric, the bottom metal, the oxide and the substrate. The well is not in it.
        Assert.Equal(
        [
            ("cap",       StackupKind.Dielectric, 500L,    7.0),
            ("met2",      StackupKind.Conductor,  1000L,   1.0),
            ("fill_1",    StackupKind.Dielectric, 500L,    4.0),
            ("mim",       StackupKind.Conductor,  150L,    1.0),
            ("mim_diel",  StackupKind.Dielectric, 50L,     7.0),
            ("met1",      StackupKind.Conductor,  500L,    1.0),
            ("ild",       StackupKind.Dielectric, 1000L,   4.0),
            ("substrate", StackupKind.Dielectric, 200000L, 11.9),
        ], Slabs(t));

        var vias = t.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).ToDictionary(l => l.Name);
        Assert.Equal(["via1", "vmim"], vias.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("met1", "met2"), (vias["via1"].SpanFromLayer, vias["via1"].SpanToLayer));
        Assert.Equal(("mim", "met2"), (vias["vmim"].SpanFromLayer, vias["vmim"].SpanToLayer));
        Assert.Equal(1.0e6, vias["via1"].SigmaSm);
        Assert.Equal(3.0e7, t.Stackup.Layers.Single(l => l.Name == "met1").SigmaSm);
        Assert.Equal([new LayerKey(15, 0)], vias["via1"].DrawingLayers);

        Assert.DoesNotContain(t.Stackup.Layers, l => l.Name == "well");
        Assert.Contains(r.Notes, n => n.Text.StartsWith("Device layers") && n.Text.EndsWith(": well."));
        Assert.StartsWith("Technology built: 3 conductors, 2 vias, 1 filled values (see notes)", r.Summary);
    }

    // ── 2 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GapFilling_FillsExactlyTheUncoveredIntervals_EachANote_FromTheBackgroundCard()
    {
        var r = GdsfactoryStackConverter.Convert(Doc(Levels, Cards), null, "synth");

        // One interval nothing covers: between the plate's top and the top metal. The oxide card fills it —
        // the kit's stated dielectrics hold more oxide (1 µm) than nitride (0.55 µm).
        var fills = r.Technology.Stackup.Layers.Where(l => l.Name.StartsWith("fill_")).ToList();
        Assert.Equal(["fill_1"], fills.Select(f => f.Name));
        Assert.Equal(1, r.FilledValues);
        var note = Assert.Single(r.Notes, n => n.Text.Contains("no level states the medium"));
        Assert.Equal(TechnologyNoteSeverity.Note, note.Severity);
        Assert.StartsWith("z = 1.7 to 2.2 µm:", note.Text);
        Assert.Contains("\"oxide\" (εr 4)", note.Text);
        Assert.Equal(0, r.Warnings);
    }

    [Fact]
    public void GapFilling_WithNoDielectricCard_IsEpsr1_WithAWarningPerInterval()
    {
        // The two dielectric cards gone: the oxide, the capacitor's dielectric and the cap state a material
        // with no card, so no rule classifies them and the z they occupy is filled like any other gap.
        var cards = Cards.Where(c => !c.Contains("\"oxide\"") && !c.Contains("\"nitride\""));
        var r = GdsfactoryStackConverter.Convert(Doc(Levels, cards), null, "synth");

        var fills = r.Technology.Stackup.Layers.Where(l => l.Name.StartsWith("fill_")).ToList();
        Assert.Equal([500L, 500L, 50L, 1000L], fills.Select(f => f.ThicknessDbu));
        Assert.All(fills, f => Assert.Equal(1.0, f.Epsr));
        Assert.Equal(4, r.FilledValues);

        var warnings = r.Notes.Where(n => n.Severity == TechnologyNoteSeverity.Warning).Select(n => n.Text).ToList();
        Assert.Equal(4, warnings.Count);
        foreach (string interval in new[] { "3.2 and 3.7", "1.7 and 2.2", "1.5 and 1.55", "0 and 1" })
            Assert.Contains(warnings, w => w.StartsWith($"εr unknown between z = {interval} µm; set before running EM"));
    }

    // ── 3 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALevelWithNoMaterialProperties_IsAConductorByTheViaSpanRule_AndTheNoteSaysSo()
    {
        string[] levels = [.. Levels.Select(l => l.StartsWith("{\"name\":\"met1\",") ? Level("met1", 10, 1.0, 0.5, "mystery") : l)];
        var r = GdsfactoryStackConverter.Convert(Doc(levels, Cards), null, "synth");

        var met1 = r.Technology.Stackup.Layers.Single(l => l.Name == "met1");
        Assert.Equal(StackupKind.Conductor, met1.Kind);
        Assert.Contains(r.Notes, n => n.Text.StartsWith("Conductors decided by the via-span rule") && n.Text.EndsWith(": met1."));
    }

    // ── 4 ──────────────────────────────────────────────────────────────────────

    private const string LayerTable = """
        <?xml version="1.0" encoding="utf-8"?>
        <layer-properties>
          <properties><fill-color>#ff8000</fill-color><name>met1</name><source>10/0</source></properties>
          <properties><fill-color>#808080</fill-color><name>via1</name><source>15/0</source></properties>
          <properties><fill-color>#0080ff</fill-color><name>met2</name><source>20/0</source></properties>
        </layer-properties>
        """;

    /// <summary>
    /// One builder. The same stack written as a stack file — conductors embedded in the dielectric above
    /// them, conductivity as sheet resistance — and as gdsfactory JSON — z and thickness, conductivity on a
    /// card — builds byte-identical <c>.ctech</c> files. The conversion's whole output is the description it
    /// hands the builder; the kit's cards are then added as the technology's materials, which changes no
    /// number, and with them removed the file is still byte-identical.
    /// </summary>
    [Fact]
    public void TheSameStack_AsAStackFileAndAsGdsfactoryJson_BuildsByteIdenticalCtech()
    {
        const double rs1 = 0.08, rs2 = 0.04;
        string stackFile = """
            TECHNOLOGY = synth
            DIELECTRIC cap       { THICKNESS=1.5 ER=7 }
            CONDUCTOR  met2      { THICKNESS=1.0 RPSQ=0.04 }
            DIELECTRIC fill_1    { THICKNESS=1.2 ER=4 }
            CONDUCTOR  met1      { THICKNESS=0.5 RPSQ=0.08 }
            DIELECTRIC ild       { THICKNESS=1.0 ER=4 }
            DIELECTRIC substrate { THICKNESS=200 ER=11.9 }
            VIA        via1      { FROM=met1 TO=met2 }
            """;

        // σ = 1/(Rs·t), evaluated exactly as the builder derives it from a sheet resistance.
        string[] levels =
        [
            Level("substrate", 99, -200, 200, "bulk"),
            Level("ild",       98,    0, 1.0, "oxide"),
            Level("met1",      10,  1.0, 0.5, "metal1"),
            Level("via1",      15,  1.5, 0.7, null),
            Level("met2",      20,  2.2, 1.0, "metal2"),
            Level("cap",       97,  3.2, 0.5, "nitride"),
        ];
        string[] cards =
        [
            Card("bulk", null, 11.9), Card("oxide", 0.0, 4.0), Card("nitride", 0.0, 7.0),
            Card("metal1", 1.0 / (rs1 * 0.5 * 1e-6), null), Card("metal2", 1.0 / (rs2 * 1.0 * 1e-6), null),
        ];

        var fromFile = ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read(stackFile), LayerPropertiesReader.Read(LayerTable), "synth").Technology;
        var converted = GdsfactoryStackConverter.Convert(Doc(levels, cards), LayerPropertiesReader.Read(LayerTable), "synth");
        var fromDescription = ProcessTechnologyBuilder.Build(
            converted.Description, LayerPropertiesReader.Read(LayerTable), "synth").Technology;

        string expected = TechPersistence.Serialize(fromFile);
        Assert.Equal(expected, TechPersistence.Serialize(fromDescription));

        var withoutCards = converted.Technology;
        Assert.NotEmpty(withoutCards.Materials);
        withoutCards.Materials = [];
        foreach (var l in withoutCards.Stackup.Layers) l.Material = null;
        Assert.Equal(expected, TechPersistence.Serialize(withoutCards));
    }
    // ── review fixes ───────────────────────────────────────────────────────────

    /// <summary>A top conductor with nothing on it is exact geometry, not a zero separation; a conductor
    /// with no card is a WARNING that it is 0 S/m, said once rather than again as the builder's note.</summary>
    [Fact]
    public void ATopConductorWithNothingOnIt_IsNotAZeroSeparation_AndAnUnknownSigmaIsOneWarning()
    {
        // The fixture without its cap, so the top metal has nothing on it, and met1 with no card.
        string[] levels = [.. Levels.Where(l => !l.StartsWith("{\"name\":\"cap\","))
                                    .Select(l => l.StartsWith("{\"name\":\"met1\",") ? Level("met1", 10, 1.0, 0.5, "mystery") : l)];
        var r = GdsfactoryStackConverter.Convert(Doc(levels, Cards), null, "synth");

        Assert.DoesNotContain(r.Notes, n => n.Text.Contains("zero separation"));
        Assert.Equal(StackupKind.Conductor, r.Technology.Stackup.Layers.Single(l => l.Name == "met1").Kind);
        var sigma = Assert.Single(r.Notes, n => n.Text.StartsWith("σ unknown for the conductor \"met1\""));
        Assert.Equal(TechnologyNoteSeverity.Warning, sigma.Severity);
        Assert.Contains("\"mystery\" has no card", sigma.Text);
        Assert.DoesNotContain(r.Notes, n => n.Text.StartsWith("Conductor \"met1\" states no sheet resistance"));
    }

    /// <summary>A substrate whose card states it is an insulator has nothing implanted into it: an oxide of
    /// the same material above it is a dielectric of the stack, not a device layer.</summary>
    [Fact]
    public void OnAStatedInsulatorSubstrate_ALevelOfTheSameMaterialAboveIt_IsADielectric()
    {
        string[] levels =
        [
            Level("substrate", 99, -200, 200, "glass"),
            Level("ild",       98,    0, 1.0, "glass"),
            Level("met1",      10,  1.0, 0.5, "metal"),
        ];
        string[] cards = [Card("glass", 0.0, 3.8), Card("metal", 3.0e7, null)];
        var r = GdsfactoryStackConverter.Convert(Doc(levels, cards), null, "synth");

        var ild = Assert.Single(r.Technology.Stackup.Layers, l => l.Name == "ild");
        Assert.Equal((StackupKind.Dielectric, 3.8), (ild.Kind, ild.Epsr));
        Assert.DoesNotContain(r.Notes, n => n.Text.StartsWith("Device layers"));
    }
}
