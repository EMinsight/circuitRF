// brief-dfr29-1-picture-to-layout-by-hand.md §2 — corrections made by hand to a traced picture are applied by the trace:
// a hole said to be an opening or not plated, copper and an outline drawn on the picture, the board photo's rectangle as
// the outline, and a stated stackup written as the cell's own technology with the workspace's untouched.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageTraceCorrectionsTests : IDisposable
{
    /// <summary>10 µm a pixel: 10,000 DBU, so half a pixel is 5,000 DBU. The picture's top-left is the origin, y up.</summary>
    private const double Dbu = 10_000, Half = 5_000;

    private readonly string _root = Directory.CreateTempSubdirectory("crf-dfr29-1-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>A pour with three round holes 0.3 mm across, at x = 100, 200 and 300 px, y = 150 px.</summary>
    private static ImageSource ThreeHoles() => TracePictures.Source(Pictures.Draw(400, 300, c =>
    {
        using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
        using var hole = Pictures.Fill(SKColors.White);
        c.DrawRect(SKRect.Create(20, 20, 360, 260), copper);
        foreach (int x in new[] { 100, 200, 300 }) c.DrawCircle(x, 150, 15, hole);
    }));

    private static ImageTraceResult Trace(params ImageCorrection[] fixes) => ImageTrace.Trace(new ImageTraceInput
    {
        Source = ThreeHoles(), Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(10e-6), Corrections = fixes,
    });

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - Half, expected + Half);

    /// <summary>Gate 1: one automatic via made an opening, one a non-plated hole on a technology without a layer for one
    /// (D2: the copy gains it), one left a via.</summary>
    [Fact]
    public void AHoleSaidToBeAnOpeningOrNotPlated_IsWrittenSo_AndTheCopyGainsTheNonPlatedLayer()
    {
        var r = Trace(ImageCorrection.NotVia(100, 150), ImageCorrection.Hole(201, 149));
        Assert.True(r.Ok, r.Refusal);
        Assert.All(r.Corrections, o => Assert.True(o.Applied, o.Refusal));

        var via = Assert.Single(r.Shapes.OfType<ViaShape>());
        Near(300 * Dbu, via.X);

        var copy = Assert.IsType<Technology>(r.TechnologyCopy);
        var entry = copy.Stackup.Layers.Single(l => l.Name == ImageTrace.NonPlatedHoleLayerName);
        Assert.Equal((StackupKind.Via, false), (entry.Kind, entry.Plated));
        var hole = Assert.Single(r.Shapes.OfType<CircleShape>(), s => s.Layer == entry.DrawingLayers[0]);
        Near(200 * Dbu, hole.Cx);
        Near(-150 * Dbu, hole.Cy);
        Assert.Equal(12 * 25_400 / 2, hole.R);   // 0.3 mm stated as a designer would: 12 mil
        Assert.DoesNotContain(TracePictures.Tech().Stackup.Layers, l => l.Name == ImageTrace.NonPlatedHoleLayerName);

        // The pour keeps its opening where the hole was left open, and where the non-plated hole is drilled; the via's
        // copper is whole under it.
        var pour = Assert.Single(r.Layers.Single(l => l.Name == "Top Copper").Shapes.OfType<PolygonShape>());
        Assert.Equal([100 * Dbu, 200 * Dbu], pour.Holes!.Select(h => Math.Round(h.Where((_, i) => i % 2 == 0).Average() / Dbu) * Dbu).Order());
        Assert.Contains("2 corrections: 1 hole not plated, 1 hole kept as an opening.", r.Report.Lines());
    }

    /// <summary>A correction that lands on nothing is refused in place with its sentence, never dropped.</summary>
    [Fact]
    public void ACorrectionOnNothing_IsRefusedWithItsSentence()
    {
        var r = Trace(ImageCorrection.Hole(60, 60), ImageCorrection.Via(5, 5));
        Assert.Equal(["There is no round hole at 60px,60px.", "There is no copper at 5px,5px for a via to land on."],
                     r.Corrections.Select(o => o.Refusal));
        Assert.Null(r.TechnologyCopy);   // nothing was placed on the layer it would have added
    }

    /// <summary>Gate 3: a Copper rectangle and an Outline rectangle land within half a pixel of where they were drawn, on
    /// the chosen layers.</summary>
    [Fact]
    public void ACopperAndAnOutlineRectangle_LandWhereTheyWereDrawn()
    {
        var r = Trace(ImageCorrection.Copper(ImageCorrection.Rectangle(40.25, 60.5, 140.75, 110.25), "Bottom Copper"),
                      ImageCorrection.Outline(ImageCorrection.Rectangle(5.5, 4.75, 394.5, 295.25)));
        Assert.All(r.Corrections, o => Assert.True(o.Applied, o.Refusal));

        var fill = Assert.IsType<RectShape>(Assert.Single(r.Layers.Single(l => l.Name == "Bottom Copper").Shapes));
        Near(40.25 * Dbu, fill.X1); Near(140.75 * Dbu, fill.X2); Near(-110.25 * Dbu, fill.Y1); Near(-60.5 * Dbu, fill.Y2);

        var edge = Assert.IsType<PathShape>(Assert.Single(r.Layers.Single(l => l.Role == ImageLayerRole.BoardOutline).Shapes));
        Assert.Equal("Outline", TracePictures.Tech().Layers.Single(l => l.Key == edge.Layer).Name);
        Assert.Equal((edge.Xy[0], edge.Xy[1]), (edge.Xy[^2], edge.Xy[^1]));   // closed, as a board outline is read
        var xs = edge.Xy.Where((_, i) => i % 2 == 0).ToList();
        var ys = edge.Xy.Where((_, i) => i % 2 == 1).ToList();
        Near(5.5 * Dbu, xs.Min()); Near(394.5 * Dbu, xs.Max()); Near(-295.25 * Dbu, ys.Min()); Near(-4.75 * Dbu, ys.Max());
    }

    /// <summary>Gate 4: Board corners with Use as the board outline writes the board's rectangle on the outline layer.</summary>
    [Fact]
    public void BoardCorners_WriteTheBoardsRectangleAsTheOutline()
    {
        var photo = BoardPhoto.Read(BoardPhotoPictures.Source(), BoardPhotoPictures.Corners());
        Assert.True(photo.Ok, photo.Refusal);
        var input = ImageTrace.WithPhotoOutline(BoardPhoto.Apply(new ImageTraceInput
        {
            Source = BoardPhotoPictures.Source(), Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Auto,
        }, photo));
        var r = ImageTrace.Trace(input);
        Assert.True(r.Ok, r.Refusal);

        var edge = Assert.IsType<PathShape>(Assert.Single(r.Layers.Single(l => l.Role == ImageLayerRole.BoardOutline).Shapes));
        double um(long dbu) => dbu / (double)r.DbuPerMicron;
        var xs = edge.Xy.Where((_, i) => i % 2 == 0).ToList();
        var ys = edge.Xy.Where((_, i) => i % 2 == 1).ToList();
        double px = photo.MetresPerPixel * 1e6;
        Assert.InRange(um(xs.Max() - xs.Min()), BoardPhotoPictures.WidthMm * 1e3 - px, BoardPhotoPictures.WidthMm * 1e3 + px);
        Assert.InRange(um(ys.Max() - ys.Min()), BoardPhotoPictures.HeightMm * 1e3 - px, BoardPhotoPictures.HeightMm * 1e3 + px);
    }

    /// <summary>Gate 5, as the owner decided it: the dialog states no stackup, and the one technology it writes — the copy a
    /// non-plated hole needs (D2) — is the new cell's own, referenced by its layout, with the workspace's byte-identical.</summary>
    [Fact]
    public void ANonPlatedHolesCopy_IsTheCellsOwnTechnology_AndTheWorkspacesIsUntouched()
    {
        string techPath = Path.Combine(_root, "board.ctech");
        TechPersistence.SaveToFile(techPath, TracePictures.Tech());
        byte[] before = File.ReadAllBytes(techPath);

        var run = ImageTrace.Run(new ImageTraceInput
        {
            Source = ThreeHoles(), Technology = TechPersistence.LoadFromFile(techPath), TechnologyPath = techPath,
            Scale = ImageScaleStatement.Stated(10e-6), Corrections = [ImageCorrection.Hole(200, 150)],
        }, ImageTraceTarget.NewCell(_root, "npth"));
        Assert.True(run.Ok, run.Refusal);

        Assert.Equal(before, File.ReadAllBytes(techPath));
        string own = Path.Combine(_root, "npth", "npth.ctech");
        Assert.Equal(own, run.TechnologyCopyPath);
        var layout = LayoutPersistence.LoadFromFile(run.LayoutPath!);
        Assert.Equal(own, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(run.LayoutPath!)!, layout.TechRef!)));
        // It loads where it is written — its material libraries named from there — and carries the layer.
        Assert.Contains(TechPersistence.LoadFromFile(own).Stackup.Layers, l => l.Name == ImageTrace.NonPlatedHoleLayerName && l.Plated == false);
    }
}
