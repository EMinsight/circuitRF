// brief-silkscreen-stroke-font.md R-ssf-10 — the round trip is a gate: designators circuitRF draws in the stroke font,
// exported to a Gerber legend and read back from it, are read by AS-10's silkscreen reader.
//
// The 45° designator is ALLOWED to come back unread: AS-10 reads text in the four cardinal frames only, so it is
// placed (to prove an angled label exports and does not disturb the others) but not asserted.

using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Ui.Tests.Layout.Text;

public sealed class StrokeTextRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stroke-text-roundtrip-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void StrokeDesignatorsAtTheCardinalAnglesReadBackFromTheirGerber()
    {
        var tech = GerberStrokeLabelTests.SilkTech();
        const long mm = 1_000_000;
        LabelShape At(string text, long x, long y, double deg) => new()
        {
            Layer = GerberStrokeLabelTests.Silk, Text = text, X = x * mm, Y = y * mm,
            Height = (long)(FootprintLabel.DefaultHeightMm * mm), RotationDegrees = deg,
        };

        var (gerber, plan) = GerberStrokeLabelTests.Export(_dir, tech,
            At("R1", 0, 0, 0), At("C22", 20, 0, 90), At("U3", 40, 0, 180), At("L14", 60, 0, 270), At("J5", 80, 0, 45));
        Assert.Equal(5, plan.StrokeLabelsWritten);

        var read = GerberReader.Read(new StringReader(gerber), 1000);
        Assert.Null(read.Refusal);
        var shapes = read.Shapes.Select(s => s.Shape).ToList();
        foreach (var s in shapes) s.Layer = GerberStrokeLabelTests.Silk;

        var reading = SilkscreenText.Read(shapes, tech, GlyphTemplates.BuiltIn);

        var designators = reading.Designators.Select(l => l.Refdes).ToHashSet();
        Assert.Superset(new HashSet<string?> { "R1", "C22", "U3", "L14" }, designators);
        Assert.Equal(0, reading.FilledShapes);
    }
}
