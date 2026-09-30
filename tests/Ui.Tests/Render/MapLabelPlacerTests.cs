// The impedance report's map labels sit beside the traces, not on them (round-10 field report: the
// trace ids and finding discs hid the copper the page reports on). One claim: labels crowded onto one
// point of a trace all land off the trace and off each other.

using CircuitRF.Render;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Render;

public class MapLabelPlacerTests
{
    [Fact]
    public void LabelsCrowdedOnATrace_LandOffTheTraceAndOffEachOther()
    {
        var placer = new MapLabelPlacer(new SKRect(0, 0, 200, 200));
        var a = new SKPoint(20, 100);
        var b = new SKPoint(180, 100);
        placer.AddSegment(a, b, 2f);

        var boxes = Enumerable.Range(0, 6)
            .Select(_ => placer.Place([(new SKPoint(100, 100), new SKPoint(1, 0))], 12, 6).Box)
            .ToList();

        foreach (var box in boxes)
            Assert.False(MapLabelPlacer.SegmentHitsRect((a, b, 2f), box), $"a label at {box} covers the trace");
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                Assert.False(boxes[i].IntersectsWith(boxes[j]), $"labels {i} and {j} overlap");
    }
}
