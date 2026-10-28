// GSGPAD (brief-review-6 follow-up): the part is registered under its name and code, and its layout cell draws the
// three pads at the pitch with the ground vias the technology's plane takes — a backside via on a die, a plated
// through-hole on a board — and a ground pad long enough for its row of vias. Placed, its pads hold the
// technology's via pad and its pitch clears them.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Mmic;

public sealed class GsgPadTests
{
    private static PCellResult Draw(string tech, int nVia, double pitch = 150e-6) =>
        GsgPadPCell.Generate(new Dictionary<string, PCellValue>
        {
            ["W"] = 72e-6, ["L"] = 300e-6, ["Pitch"] = pitch, ["PadW"] = 80e-6, ["PadL"] = 80e-6, ["NVia"] = (double)nVia,
        }, ShippedTechnologies.Load(tech), PCellLayerSelection.Default);

    [Fact]
    public void TheGsgPad_IsRegistered_AsAPartAndAsALayoutCell()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("GSG", out var kind, out _));
        Assert.Equal(SymbolKind.GsgPad, kind);
        Assert.Contains(ComponentTypeRegistry.DefaultParameters(kind, 2), p => p.Name == "Pitch");
        Assert.True(PCellRegistry.TryGet("GSGPAD", out _));
    }

    [Fact]
    public void OnADie_ThePadsSitAtThePitch_AndTheGroundVias_AreBacksideVias()
    {
        var cell = Draw("mmic-GaAs_2LM_100um", nVia: 1);
        Assert.Null(cell.Diagnostics);
        Assert.Equal((0L, 0L), (cell.Pins[0].X, cell.Pins[0].Y));
        Assert.Equal(40_000 + 300_000, cell.Pins[1].X);   // the pad's far edge plus L, DBU

        var vias = cell.Shapes.OfType<ViaShape>().ToList();
        Assert.Equal(2, vias.Count);
        Assert.All(vias, v => Assert.Equal(new LayerKey(8, 0), v.Layer));   // Backside Via
        Assert.Equal([-150_000L, 150_000L], vias.Select(v => v.Y).Order());
    }

    [Fact]
    public void OnABoard_TheGroundVias_ArePlatedThroughHoles_AndAGroundPadGrowsForItsRow()
    {
        var one = Draw("pcb-2layer_RO4350B_20mil_1oz", nVia: 1, pitch: 1000e-6);
        var three = Draw("pcb-2layer_RO4350B_20mil_1oz", nVia: 3, pitch: 1000e-6);
        Assert.All(three.Shapes.OfType<ViaShape>(), v => Assert.Equal(new LayerKey(7, 0), v.Layer));
        Assert.Equal(6, three.Shapes.OfType<ViaShape>().Count());

        static long GroundPadLength(PCellResult c) =>
            c.Shapes.OfType<RectShape>().Where(r => r.Y1 > 0).Max(r => r.X2 - r.X1);
        Assert.True(GroundPadLength(three) > GroundPadLength(one));
    }

    [Theory]
    [InlineData("mmic-GaAs_2LM_100um", "80", "150")]
    [InlineData("pcb-2layer_RO4350B_20mil_1oz", "610", "1000")]   // the plated through-hole's 0.6096 mm pad
    public void Placed_ThePadsHoldTheTechnologysViaPad_AndThePitchClearsThem(string tech, string pad, string pitch)
    {
        var rows = ComponentTypeRegistry.DefaultParameters(SymbolKind.GsgPad, 2)
            .Select(d => new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension })
            .ToList();
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(rows, ShippedTechnologies.Load(tech), SymbolKind.GsgPad);
        Assert.Equal((pad, "µm"), rows.Single(r => r.Name == "PadW") is var w ? (w.Expression, w.Unit) : default);
        Assert.Equal(pitch, rows.Single(r => r.Name == "Pitch").Expression);
    }
}
