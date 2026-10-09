// brief-silkscreen-stroke-font.md §5 — a stroke label placed: alignment lands where it says, a rotation turns the
// strokes about the anchor, and the bounds contain every stroke grown by half the pen.

using CircuitRF.Design.Layout.Text;

namespace CircuitRF.Ui.Tests.Layout.Text;

public sealed class StrokeTextTests
{
    private const long Cap = 21_000;

    private static LabelShape Label(LabelHAlign? h = null, LabelVAlign? v = null, double rotation = 0) => new()
    {
        Layer = new LayerKey(21, 0), Text = "TP1", Height = Cap, X = 100_000, Y = 50_000,
        HAlign = h, VAlign = v, RotationDegrees = rotation,
    };

    private static (double MinX, double MinY, double MaxX, double MaxY) Box(StrokeLabelGeometry g)
    {
        var xs = g.Strokes.SelectMany(s => s.Where((_, i) => i % 2 == 0)).ToList();
        var ys = g.Strokes.SelectMany(s => s.Where((_, i) => i % 2 == 1)).ToList();
        return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    [Theory]
    [InlineData(LabelHAlign.Left, LabelVAlign.Baseline)]
    [InlineData(LabelHAlign.Center, LabelVAlign.Middle)]
    [InlineData(LabelHAlign.Right, LabelVAlign.Top)]
    [InlineData(LabelHAlign.Left, LabelVAlign.Bottom)]
    public void AlignmentPlacesTheTextWhereItSays(LabelHAlign h, LabelVAlign v)
    {
        var label = Label(h, v);
        double advance = StrokeFont.Layout(label.Text, label.Style, Cap).Advance;
        var g = StrokeText.For(label);

        // The text's own frame, recovered: x from the advance's start, y from the baseline.
        // Top and Bottom hang the INKED edge on the anchor — half the pen beyond the cap or descender line.
        double startX = label.X - Math.Round(h switch { LabelHAlign.Center => advance / 2, LabelHAlign.Right => advance, _ => 0 });
        double halfPen = g.PenWidth / 2;
        double baseline = label.Y - v switch
        {
            LabelVAlign.Top => Cap + halfPen, LabelVAlign.Middle => Cap / 2.0,
            LabelVAlign.Bottom => -Cap * StrokeFont.DescentOverCap - halfPen, _ => 0,
        };
        var b = Box(g);
        // "TP1" has no descender: it stands on the baseline and reaches the cap line, inside its advance.
        Assert.Equal(baseline, b.MinY, 6);
        Assert.Equal(baseline + Cap, b.MaxY, 6);
        Assert.InRange(b.MinX, startX, startX + advance);
        Assert.InRange(b.MaxX, startX, startX + advance);
    }

    [Fact]
    public void ARotationTurnsEveryStrokeAboutTheAnchor()
    {
        var flat = StrokeText.For(Label());
        var turned = StrokeText.For(Label(rotation: 30));
        double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
        for (int k = 0; k < flat.Strokes.Count; k++)
            for (int i = 0; i + 1 < flat.Strokes[k].Length; i += 2)
            {
                double x = flat.Strokes[k][i] - 100_000, y = flat.Strokes[k][i + 1] - 50_000;
                Assert.Equal(100_000 + x * c - y * s, turned.Strokes[k][i], 6);
                Assert.Equal(50_000 + x * s + y * c, turned.Strokes[k][i + 1], 6);
            }
    }

    [Fact]
    public void BoundsContainEveryStrokeGrownByHalfThePen()
    {
        var label = Label(LabelHAlign.Center, LabelVAlign.Middle, rotation: 30);
        var g = StrokeText.For(label);
        var bb = StrokeText.Bounds(label)!.Value;
        var b = Box(g);
        double r = g.PenWidth / 2;
        Assert.Equal(Cap / StrokeText.PenDivisor, g.PenWidth, 6);
        Assert.True(bb.MinX <= b.MinX - r && bb.MinY <= b.MinY - r && bb.MaxX >= b.MaxX + r && bb.MaxY >= b.MaxY + r);
        Assert.True(bb.MinX > b.MinX - r - 1 && bb.MaxY < b.MaxY + r + 1, "and no looser than rounding to DBU");
    }
}
