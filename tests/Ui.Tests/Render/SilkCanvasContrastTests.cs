using CircuitRF.Design.Theming;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Render;

// A silk layer's STORED colour is the ink's (near-white in the shipped technologies, dark in a Gerber
// import), and no stored colour reads on both canvases. The renderer draws silk moved away from the
// background instead — LayerCanvasContrast. Stored colours are never changed.
public class SilkCanvasContrastTests
{
    private static readonly LayerKey SilkKey = new(5, 0);

    private static LayerDef Silk(Rgba color, InterchangeMapping? ix = null, string? purpose = "drawing") => new()
    {
        Key = SilkKey, Name = "Silk Top", Color = color, FillOpacity = 1.0, Visible = true,
        Purpose = purpose, Interchange = ix ?? new InterchangeMapping(null, null, null, null, null, "F.SilkS"),
    };

    private static double Lum(Rgba c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
    private static double Lum(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;

    [Theory]
    [InlineData(242, true)]   // shipped Silk Top on the light canvas: unreadable, darkened
    [InlineData(46, false)]   // imported Silk Top on the light canvas: already reads, untouched
    public void LightCanvas_OnlyAnUnreadableSilkIsMoved_AndItEndsUpReadable(byte grey, bool moved)
    {
        var bg = LayoutRenderTheme.Light.Background;
        var def = Silk(new Rgba(grey, grey, grey));
        var drawn = LayerCanvasContrast.Drawn(def, bg);

        Assert.Equal(moved, !ReferenceEquals(drawn, def));
        Assert.True(Math.Abs(Lum(drawn.Color) - Lum(bg)) >= LayerCanvasContrast.MinimumLuminanceDifference);
        Assert.Equal(new Rgba(grey, grey, grey), def.Color); // the stored colour is never written
    }

    [Fact]
    public void DarkCanvas_ADarkSilkIsLightened()
    {
        var bg = LayoutRenderTheme.Dark.Background;
        var drawn = LayerCanvasContrast.Drawn(Silk(new Rgba(46, 46, 46)), bg);
        Assert.True(Lum(drawn.Color) - Lum(bg) >= LayerCanvasContrast.MinimumLuminanceDifference);
    }

    [Fact]
    public void SilkIsIdentifiedByStructuredFields_NeverByTheDisplayName()
    {
        var bg = LayoutRenderTheme.Light.Background;
        var pale = new Rgba(242, 242, 242);
        var byGerberFunction = Silk(pale, new InterchangeMapping(null, null, null, null, "Legend,Top"));
        var byPurpose = Silk(pale, new InterchangeMapping(null, null, null, null, null), "silkscreen");
        var nameOnly = Silk(pale, new InterchangeMapping(null, null, null, null, null));
        var copper = Silk(pale, new InterchangeMapping(null, null, null, null, null, "F.Cu"));

        Assert.NotSame(byGerberFunction, LayerCanvasContrast.Drawn(byGerberFunction, bg));
        Assert.NotSame(byPurpose, LayerCanvasContrast.Drawn(byPurpose, bg));
        Assert.Same(nameOnly, LayerCanvasContrast.Drawn(nameOnly, bg));
        Assert.Same(copper, LayerCanvasContrast.Drawn(copper, bg)); // a pale copper is not silk
    }

    [Fact]
    public void Renderer_PaintsSilkInTheDrawnColour()
    {
        var tech = new Technology
        {
            Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [Silk(new Rgba(242, 242, 242))],
        };
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = SilkKey, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        var vp = LayoutViewport.ZoomToFit(LayoutGeometry.BboxOf(view.Shapes[0]), 200, 200, 0.2);

        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false });
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        // Centre of the box, fully filled at opacity 1: the stored #F2F2F2 would read ~242 here.
        Assert.True(Lum(bmp.GetPixel(100, 100)) < 128);
    }
}
