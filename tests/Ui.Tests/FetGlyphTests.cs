// The MESFET/HEMT glyph shared by the five FET laws. It was a MOS-style gate bar standing off the channel with a junction
// arrow on its lead — an insulated gate carrying a junction mark (reported against CurticeCubic, 2026-09-28). A Schottky
// gate is a junction ON the channel, so the glyph is the JFET's drawing: the gate lead and its arrow land on one unbroken
// channel bar, pointing in for n-channel and out for p-channel. The pins are unchanged, so placed parts stay wired.

using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Ui.Tests;

public class FetGlyphTests
{
    [Theory]
    [InlineData(SymbolKind.FetCurtice, SymbolKind.JfetN)]
    [InlineData(SymbolKind.FetCurticeCubic, SymbolKind.JfetN)]
    [InlineData(SymbolKind.FetStatz, SymbolKind.JfetN)]
    [InlineData(SymbolKind.FetMaterka, SymbolKind.JfetN)]
    [InlineData(SymbolKind.FetAngelov, SymbolKind.JfetN)]
    [InlineData(SymbolKind.PFetCurtice, SymbolKind.JfetP)]
    [InlineData(SymbolKind.PFetStatz, SymbolKind.JfetP)]
    [InlineData(SymbolKind.PFetMaterka, SymbolKind.JfetP)]
    public void AFetLaw_IsDrawnWithItsGateOnTheChannel_AsTheJfetOfItsChannelIs(SymbolKind fet, SymbolKind jfet)
    {
        var drawn = BuiltInSymbols.Primitives(fet);
        Assert.Equal(Shape(BuiltInSymbols.Primitives(jfet)), Shape(drawn));

        // No second vertical bar beside the channel: the gate does not stand off it.
        Assert.Single(drawn.Primitives.OfType<LinePrimitive>(), l => l.X1 == l.X2 && l.X1 != 0);
        // The pins are where they were: gate left, drain top, source bottom.
        Assert.Equal([(-200.0, 0.0), (0.0, -200.0), (0.0, 200.0)],
                     drawn.Pins.Select(p => (p.LocalX, p.LocalY)).Order().ToArray());
    }

    private static string[] Shape(Symbol s) =>
    [
        .. s.Primitives.OfType<LinePrimitive>().Select(l => $"L {l.X1} {l.Y1} {l.X2} {l.Y2}"),
        .. s.Primitives.OfType<PolygonPrimitive>().Select(p => "P " + string.Join(" ", p.Points.Select(q => $"{q[0]},{q[1]}"))),
    ];
}
