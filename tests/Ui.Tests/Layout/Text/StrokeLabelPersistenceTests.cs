// brief-silkscreen-stroke-font.md §5, R-ssf-3 — the font is additive in the file: a .clay whose labels carry no Font
// key loads as all-stroke and re-saves byte for byte; a Sans label and a stated pen width round-trip.

namespace CircuitRF.Ui.Tests.Layout.Text;

public sealed class StrokeLabelPersistenceTests
{
    [Fact]
    public void AnExistingClayLoadsAsAllStrokeAndReSavesByteForByte()
    {
        // Labels of every kind a file written before the stroke font holds — plain, styled, aligned, angled, a port —
        // with neither new field set, which is exactly what that writer wrote: no Font key, no StrokeWidth key.
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new LabelShape { Layer = new LayerKey(21, 0), Text = "R1", Height = 800_000, X = 10, Y = 20 });
        view.Shapes.Add(new LabelShape { Layer = new LayerKey(21, 0), Text = "C2", Height = 800_000, Style = LabelFontStyle.Bold,
                                         HAlign = LabelHAlign.Center, VAlign = LabelVAlign.Middle, RotationDegrees = 45 });
        view.Shapes.Add(new LabelShape { Layer = new LayerKey(1, 0), Text = "1", Height = 600_000, IsPort = true,
                                         PortDirection = LayoutRotation.R90 });
        string before = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("\"Font\"", before);
        Assert.DoesNotContain("\"StrokeWidth\"", before);

        var loaded = LayoutPersistence.Deserialize(before);

        Assert.All(loaded.Shapes.OfType<LabelShape>(), l => Assert.Equal(LabelFont.Stroke, l.Font));
        Assert.Equal(before, LayoutPersistence.Serialize(loaded));
    }

    [Fact]
    public void ASansLabelAndAStatedPenWidthRoundTrip()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new LabelShape { Layer = new LayerKey(21, 0), Text = "Sans", Height = 1000, Font = LabelFont.Sans });
        view.Shapes.Add(new LabelShape { Layer = new LayerKey(21, 0), Text = "Pen", Height = 1000, StrokeWidth = 250 });

        string json = LayoutPersistence.Serialize(view);
        var back = LayoutPersistence.Deserialize(json).Shapes.OfType<LabelShape>().ToList();

        Assert.Equal(1, json.Split("\"Font\"").Length - 1);
        Assert.Contains("\"Font\": \"Sans\"", json);
        Assert.Equal([LabelFont.Sans, LabelFont.Stroke], back.Select(l => l.Font));
        Assert.Equal([null, 250L], back.Select(l => l.StrokeWidth));
    }
}
