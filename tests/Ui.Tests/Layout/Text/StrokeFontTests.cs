// brief-silkscreen-stroke-font.md §5 — the stroke font itself: the whole printable ASCII set is there, a string's
// advance is its glyphs' advances, and a character the font lacks is a counted hollow box (D5).

using System.Text;
using CircuitRF.Design.Layout.Text;

namespace CircuitRF.Ui.Tests.Layout.Text;

public sealed class StrokeFontTests
{
    [Fact]
    public void EveryPrintableAsciiCharacterAndTheFourExtrasHaveAGlyph()
    {
        var missing = Enumerable.Range(0x20, 0x7F - 0x20).Concat(['Ω', 'µ', '°', '±'])
                                .Where(c => !StrokeFont.Glyphs.ContainsKey(c)).Select(c => (char)c).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void AStringAdvancesByTheSumOfItsGlyphs()
    {
        const double cap = 2100;
        const string text = "R12 Ω±5°";
        double expected = text.EnumerateRunes().Sum(r => StrokeFont.Glyphs[r.Value] is var g ? (g.Right - g.Left) * cap / StrokeFont.CapUnits : 0);

        var laid = StrokeFont.Layout(text, LabelFontStyle.Regular, cap);

        Assert.Equal(expected, laid.Advance, 6);
        Assert.Equal(0, laid.Unknown);
        Assert.Equal(cap * StrokeFont.CondensedScale * laid.Advance / cap,
                     StrokeFont.Layout(text, LabelFontStyle.Condensed, cap).Advance, 6);
    }

    [Fact]
    public void ACharacterNotInTheFontIsCountedAndDrawnAsAClosedBox()
    {
        const double cap = 21;
        var laid = StrokeFont.Layout("A中B", LabelFontStyle.Regular, cap);

        Assert.Equal(1, laid.Unknown);
        double aAdvance = StrokeFont.Glyphs['A'].Right - StrokeFont.Glyphs['A'].Left;
        var box = laid.Strokes.Single(st => st.Length == 10 && st[0] == st[8] && st[1] == st[9]);
        Assert.True(box[0] > aAdvance && box[2] < aAdvance + 20, "the box sits inside its own advance, after the A");
        Assert.Equal([0.0, 0.0, cap, cap], [box[1], box[3], box[5], box[7]]);
    }
}
