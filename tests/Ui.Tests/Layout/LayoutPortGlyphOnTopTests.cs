using System;
using System.Collections.Generic;
using CircuitRF.Render;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// <b>A port's glyph renders above every piece of geometry in the frame, in one colour, with its
/// name above its own marker.</b> Owner instruction, 2026-09-09, in three parts: the name was being
/// bisected by a line from the marker and must sit on top; a horizontal line was appearing in a
/// colour that is not the port's tint; and a port renders higher than any geometry, because seeing
/// it is what the glyph is for.
///
/// <para>All three were one defect plus one wrong fix. <c>DrawLayer</c> batches a layer's fills and
/// outlines into one path each and paints them after every shape in the layer has been visited, so a
/// port drawn inline in that loop went under its own layer's artwork — and an edge port's
/// reference-plane bar lies exactly along the conductor outline that covered it, which is the
/// untinted horizontal line. The background-coloured halo that had been added underneath the marker
/// to compensate was the second stray colour.</para>
///
/// <para>A pixel probe cannot tell a glyph from the artwork beneath it, so the oracle throughout is
/// the same frame rendered WITHOUT the port: whatever differs is the glyph, and nothing else is.</para>
/// </summary>
public class LayoutPortGlyphOnTopTests
{
    private const int Dbu = 1000;

    private static readonly LayerKey Lower = new(1, 0);
    private static readonly LayerKey Upper = new(2, 0);

    private static readonly Rgba LowerRgb = new(200, 120, 40);
    private static readonly Rgba UpperRgb = new(60, 130, 200);

    private static Technology Tech() => new()
    {
        Layers =
        [
            new LayerDef { Key = Lower, Name = "M1", Color = LowerRgb, ZOrder = 0 },
            new LayerDef { Key = Upper, Name = "M2", Color = UpperRgb, ZOrder = 5 },
        ],
    };

    /// <summary>A trace on the LOWER layer; the port names its left end.</summary>
    private static PolygonShape Trace() => new()
    {
        Layer = Lower, Xy = [0, 3_000, 20_000, 3_000, 20_000, 7_000, 0, 7_000],
    };

    /// <summary>Metal on the HIGHER layer, laid straight over the port.</summary>
    private static PolygonShape Cover() => new()
    {
        Layer = Upper, Xy = [-3_000, 1_000, 6_000, 1_000, 6_000, 9_000, -3_000, 9_000],
    };

    private static LabelShape Port() => new()
    {
        Layer = Lower, X = 0, Y = 5_000, Text = "P1", Height = 1_200,
        IsPort = true, PortDirection = LayoutRotation.R0, PortLayer = Lower,
    };

    private static readonly LayoutViewport Vp =
        LayoutViewport.ZoomToFit(new Bbox(-5_000, -1_000, 24_000, 11_000), 900, 500, 0.05);

    private static SKColor[] Render(bool withPort, bool withCover, LayoutRenderTheme theme)
    {
        var v = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um };
        v.Shapes.Add(Trace());
        if (withPort) v.Shapes.Add(Port());
        if (withCover) v.Shapes.Add(Cover());
        return RenderView(v, theme);
    }

    /// <summary>Pixels as <see cref="SKColor"/>, so nothing here depends on the surface's byte
    /// order.</summary>
    private static SKColor[] RenderView(LayoutView v, LayoutRenderTheme theme)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)Vp.Width, (int)Vp.Height));
        LayoutRenderer.Draw(surface.Canvas, v, Tech(), Vp, new LayoutRenderOptions { Theme = theme });
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Pixels;
    }

    private static SKColor At(SKColor[] px, int x, int y) => px[y * (int)Vp.Width + x];

    private static bool Same(SKColor a, SKColor b) =>
        a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;

    /// <summary>Every pixel the port's presence changes.</summary>
    private static List<(int X, int Y)> GlyphPixels(SKColor[] with, SKColor[] without)
    {
        var hits = new List<(int, int)>();
        for (int y = 0; y < (int)Vp.Height; y++)
        for (int x = 0; x < (int)Vp.Width; x++)
        {
            var a = At(with, x, y);
            var b = At(without, x, y);
            if (Math.Abs(a.Red - b.Red) > 8 || Math.Abs(a.Green - b.Green) > 8 || Math.Abs(a.Blue - b.Blue) > 8)
                hits.Add((x, y));
        }
        return hits;
    }

    /// <summary>
    /// The whole glyph survives metal drawn over it on a HIGHER layer — the case that used to remove
    /// it entirely, and the reason "higher z than any geometry" is the requirement rather than
    /// "higher z than its own layer".
    /// </summary>
    [Fact]
    public void ThePortGlyphDrawsAboveGeometryOnAHigherLayer()
    {
        int bare = GlyphPixels(Render(true, withCover: false, LayoutRenderTheme.Light),
                               Render(false, withCover: false, LayoutRenderTheme.Light)).Count;
        int covered = GlyphPixels(Render(true, withCover: true, LayoutRenderTheme.Light),
                                  Render(false, withCover: true, LayoutRenderTheme.Light)).Count;

        Assert.True(bare > 0, "the port drew nothing at all");

        // Not merely non-zero: essentially all of it has to survive. The cover sits over the anchor,
        // the plane bar, both serifs and the whole arrow, so a port drawn under it loses nearly
        // everything.
        Assert.True(covered >= bare * 0.9,
            $"the glyph lost area under metal on a higher layer ({covered} px against {bare} bare)");
    }

    /// <summary>
    /// Every pixel the glyph occupies is its own tinted colour — no halo, no untinted layer colour,
    /// no alpha-thinned leader. Checked in both themes because the tint is defined against the
    /// background and reverses between them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPixelOfTheGlyphIsTheOneTintedColour(bool dark)
    {
        var theme = dark ? LayoutRenderTheme.Dark : LayoutRenderTheme.Light;
        var with = Render(true, withCover: false, theme);
        var without = Render(false, withCover: false, theme);
        var hits = GlyphPixels(with, without);
        Assert.NotEmpty(hits);

        var tint = LayoutRenderer.TintForContrast(
            new SKColor(LowerRgb.R, LowerRgb.G, LowerRgb.B), theme.Background,
            LayoutRenderer.PortMarkerContrastTintAmount);

        // A stroked, antialiased glyph has edge pixels part-way between the tint and whatever is
        // under them, so the claim can only be made of the pixels the glyph actually covers: at least
        // half of what it changed must BE the tint exactly, and none of it may be further from the
        // tint than the thing underneath was — which is what a halo or an untinted line would be.
        int exact = 0;
        foreach (var (x, y) in hits)
        {
            var a = At(with, x, y);
            if (Same(a, tint)) { exact++; continue; }

            var under = At(without, x, y);
            Assert.True(Dist(a, tint) <= Dist(under, tint) + 1e-9,
                $"({x},{y}) moved AWAY from the port's tint: {a} against {under}, tint {tint}");
        }

        // A third, not a half: the glyph is thin stroked work and lettering, so a large share of
        // what it changes is legitimately an antialiased edge. The floor is here to stop the clause
        // above passing vacuously on a glyph that never actually covers anything.
        Assert.True(exact >= hits.Count / 3,
            $"only {exact} of {hits.Count} glyph pixels are the tint exactly");

        static double Dist(SKColor c, SKColor t)
        {
            double dr = c.Red - t.Red, dg = c.Green - t.Green, db = c.Blue - t.Blue;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }
    }

    /// <summary>
    /// The name is never crossed by its own marker.
    ///
    /// <para><b>UPDATED (brief-dfr32-2 R-dfr32-2-4).</b> This compared the name's pixels in a name-only frame with the
    /// full frame, because the name was centred on the anchor — exactly where an edge port's arrow arrives — and only
    /// the knock-out kept the shaft out of the letters. The name is now drawn beside the mark on its outward side, at a
    /// fixed screen size, so the two no longer share pixels by construction; the claim is the placement's geometry
    /// (the name's box and the mark's do not meet) plus the name actually being painted there in the port's tint.</para>
    /// </summary>
    [Fact]
    public void TheNameIsNeverCrossedByItsOwnMarker()
    {
        var port = Port();
        var shapes = new List<LayoutShape> { Trace(), port };
        var lookup = LayoutPortDirection.LookupFor(shapes);
        var name = LayoutRenderer.PlacePortNames([(1, port)], lookup, Vp.Zoom, Tech(), Dbu).Single();
        var hint = LayoutPortDirection.Resolve(lookup, port)!.Value;
        var mark = LayoutPortDirection.MarkerBbox(port, CircuitRF.Design.Layout.Em3d.EdgePortSheet.Bar(hint, port, Tech(), Dbu),
                                                  atAnchor: false, padding: 0);
        Assert.False(name.Box.Intersects(mark), $"name {name.Box} meets its mark {mark}");

        var full = Render(true, withCover: false, LayoutRenderTheme.Light);
        var tint = LayoutRenderer.TintForContrast(
            new SKColor(LowerRgb.R, LowerRgb.G, LowerRgb.B), LayoutRenderTheme.Light.Background,
            LayoutRenderer.PortMarkerContrastTintAmount);
        int x0 = (int)Math.Floor(Vp.WorldToScreenX(name.Box.MinX)), x1 = (int)Math.Ceiling(Vp.WorldToScreenX(name.Box.MaxX));
        int y0 = (int)Math.Floor(Vp.WorldToScreenY(name.Box.MaxY)), y1 = (int)Math.Ceiling(Vp.WorldToScreenY(name.Box.MinY));
        int inked = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (Same(At(full, x, y), tint)) inked++;
        Assert.True(inked > 20, $"only {inked} pixels of the name's box carry the port's tint");
    }
}
