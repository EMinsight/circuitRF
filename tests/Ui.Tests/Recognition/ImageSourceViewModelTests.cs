// brief-img-5-dialog.md §4 — the picture source: a dropped file that is not a picture is refused and the drop zone
// stays; a picture with no drawing disables Create with its sentence, and the override sets the kind; Make Layout is
// disabled for a schematic picture, and comes back when the picture is read as a layout.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ImageSourceViewModelTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im5-src-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ADroppedNonPicture_IsRefused_AndTheDropZoneStays()
    {
        string text = Path.Combine(_ws, "notes.png");
        File.WriteAllText(text, "not a picture");
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), null, makeSchematic: true);

        Assert.False(vm.LoadFile(text));

        Assert.True(vm.IsEmpty);
        Assert.Equal(ImageSource.FromFile(text).Refusal, vm.Status);
        Assert.False(vm.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task APictureWithNoDrawing_DisablesCreateWithItsSentence_AndTheOverrideSetsTheKind()
    {
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.None), ImageDialog.Board(), makeSchematic: false);
        vm.IsAutoKind = true;
        await vm.Recognition;

        Assert.StartsWith("No drawing found: a photograph-like picture", vm.FailureText);
        Assert.True(vm.FailureOffersKind);
        Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.Equal(vm.FailureText, vm.CreateTip);

        vm.ReadAsLayoutCommand.Execute(null);
        await vm.Recognition;

        Assert.Equal(DrawingKind.Layout, vm.EffectiveKind);
        Assert.False(vm.HasFailure);
    }

    [Fact]
    public async Task MakeLayout_IsDisabledForASchematicPicture_AndComesBackWhenItIsReadAsALayout()
    {
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Schematic), ImageDialog.Board(), makeSchematic: false);
        vm.SchematicOffered = true;   // the reader is withheld from the application, not from its tests
        vm.IsAutoKind = true;
        await vm.Recognition;

        Assert.False(vm.MakeLayoutEnabled);
        Assert.Equal(ImageSourceViewModel.MakeLayoutFromSchematicTip, vm.MakeLayoutTip);
        Assert.True(vm.MakeSchematic);

        vm.KindIndex = 2;
        await vm.Recognition;

        Assert.True(vm.MakeLayoutEnabled);
        Assert.True(vm.MakeLayout);   // what was asked for, not what the misreading forced
    }

    /// <summary>Designer round 29: a board picture makes a layout only — Create Schematic from Image on one comes out as a
    /// layout, its Make Schematic greyed, the schematic's options not shown.</summary>
    [Fact]
    public async Task ABoardPicture_MakesALayoutOnly_EvenFromCreateSchematicFromImage()
    {
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), ImageDialog.Board(), makeSchematic: true);
        await vm.Recognition;

        Assert.Equal(DrawingKind.Layout, vm.EffectiveKind);
        Assert.True(vm.MakeLayout);
        Assert.False(vm.MakeSchematicEnabled);
        Assert.False(vm.OptionsApply);
    }

    /// <summary>Tools ▸ Digitise Image… reads a picture as what it is: a board is traced into a layout, not read as a plot.</summary>
    [Fact]
    public async Task DigitiseImage_ReadsABoardPicture_AsALayout()
    {
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), null, makeSchematic: false);
        vm.StartPlotMode(csv: true, manual: true, classify: true);
        Assert.Equal("Digitise Image", vm.Title);

        Assert.True(vm.LoadBytes(CircuitRF.Ui.Tests.Imaging.Pictures.Png(CircuitRF.Ui.Tests.Imaging.TracePictures.TwoLayerBoard()), "board.png"));
        await vm.Recognition;

        Assert.Equal(DrawingKind.Layout, vm.EffectiveKind);
        Assert.True(vm.MakeLayout);
    }

    [Fact]
    public async Task CreateLayoutFromImage_ReadsEveryPictureAsALayout_WithoutReadingItsKind()
    {
        var runner = new RecordingImageRunner(DrawingKind.Plot);
        var vm = ImageDialog.Open(_ws, runner, ImageDialog.Board(), makeSchematic: false);
        await vm.Recognition;
        vm.Replace(ImageDialog.Board());
        await vm.Recognition;

        Assert.True(vm.IsLayoutKind);
        Assert.Equal(DrawingKind.Layout, vm.EffectiveKind);
        Assert.Equal(0, runner.Classifications);
        Assert.NotEmpty(runner.Traces);

        vm.IsAutoKind = true;   // Auto still reads it
        await vm.Recognition;
        Assert.Equal(1, runner.Classifications);
    }

    /// <summary>brief-dfr29-1 gate 2 — And every one like it: twelve 0.5 mm holes and two 2.6 mm ones; one choice on a 0.5 mm
    /// hole answers the twelve and none of the two, as one undo step.</summary>
    [Fact]
    public async Task OneChoiceOnAHole_AnswersEveryHoleOfItsDiameter_AsOneUndoStep()
    {
        // 25 µm a pixel: the board 17 mm square.
        var picture = Pictures.Draw(680, 680, c =>
        {
            using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
            using var white = Pictures.Fill(SKColors.White);
            c.DrawRect(SKRect.Create(20, 20, 640, 640), copper);
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 2; j++) c.DrawCircle(140 + 80 * i, 300 + 80 * j, 10, white);
            foreach (int x in new[] { 110, 570 }) c.DrawCircle(x, 110, 52, white);
        });
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), TracePictures.Source(picture), makeSchematic: false);
        await vm.Recognition;
        vm.Scale.StartTwoPointsCommand.Execute(null);
        vm.Scale.Click(new PixelPoint(0, 0));
        vm.Scale.Click(new PixelPoint(680, 0));
        vm.Scale.DistanceText = "17 mm";
        vm.Scale.ApplyDistanceCommand.Execute(null);
        await vm.Recognition;

        vm.SelectedItem = vm.Overlay.HitTest(140, 300, 2, vm.Shows);
        Assert.True(vm.HolePickerShown);
        Assert.True(vm.EveryOneLikeIt);   // on by default for holes
        vm.HoleIsNotPlatedCommand.Execute(null);
        await vm.Recognition;

        Assert.Equal(12, vm.Corrections.Count);
        Assert.All(vm.Corrections, c => Assert.Equal(ImageCorrectionKind.Hole, c.Kind));
        Assert.DoesNotContain(vm.Corrections, c => c.At.Y < 200);   // neither mounting hole
        Assert.True(vm.UndoCorrection());
        Assert.Empty(vm.Corrections);
    }

    /// <summary>Any existing technology can be chosen, for its layers (owner, 2026-10-27): one of circuitRF's that the
    /// workspace has no copy of is read from the catalogue, and Create installs it into the workspace and traces onto it.</summary>
    [Fact]
    public async Task ACircuitRFTechnology_IsInstalledIntoTheWorkspace_WhenCreateWritesWithIt()
    {
        var entry = CircuitRF.Design.Layout.TechnologyCatalog.Find(TracePictures.TechId)!;
        var runner = new RecordingImageRunner(DrawingKind.Layout);
        var vm = new ImageSourceViewModel(_ws, [ImageTechnologyChoice.Catalog(entry)], 0, ImageDialog.Board(), makeSchematic: false,
                                          runner, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_ws, "presets"));
        await vm.Recognition;
        Assert.Null(runner.Traces[^1].TechnologyPath);   // read from the catalogue: nothing is installed by looking
        string installed = Path.Combine(_ws, "tech", entry.Id + ".ctech");
        Assert.False(File.Exists(installed));

        vm.Scale.StartTwoPointsCommand.Execute(null);
        vm.Scale.Click(new PixelPoint(0, 0));
        vm.Scale.Click(new PixelPoint(400, 0));
        vm.Scale.DistanceText = "4 mm";
        vm.Scale.ApplyDistanceCommand.Execute(null);
        await vm.Recognition;
        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal(installed, runner.TraceRuns[^1].Input.TechnologyPath);
        Assert.True(File.Exists(installed));
    }

    // ── brief-dfr30-1: corrections as objects, a via's drill, Remove ▾ ─────────────────────────────────────

    /// <summary>The dialog on <paramref name="picture"/>, scaled to <paramref name="widthMm"/> across, read.</summary>
    private async Task<ImageSourceViewModel> Scaled(CircuitRF.Design.Imaging.RasterImage picture, double widthMm)
    {
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), TracePictures.Source(picture), makeSchematic: false);
        await vm.Recognition;
        vm.Scale.StartTwoPointsCommand.Execute(null);
        vm.Scale.Click(new PixelPoint(0, 0));
        vm.Scale.Click(new PixelPoint(picture.Width, 0));
        vm.Scale.DistanceText = $"{widthMm} mm";
        vm.Scale.ApplyDistanceCommand.Execute(null);
        await vm.Recognition;
        return vm;
    }

    /// <summary>A rectangle's or a polygon's bounds, in the picture's pixels (25 µm, so 25,000 DBU), y up.</summary>
    private static (double X1, double Y1, double X2, double Y2) Box(CircuitRF.Design.Layout.LayoutShape shape)
    {
        long[] xy = shape switch
        {
            CircuitRF.Design.Layout.RectShape r => [r.X1, r.Y1, r.X2, r.Y2],
            CircuitRF.Design.Layout.PolygonShape p => p.Xy,
            _ => throw new InvalidOperationException(shape.GetType().Name),
        };
        var xs = xy.Where((_, i) => i % 2 == 0).ToArray();
        var ys = xy.Where((_, i) => i % 2 == 1).ToArray();
        return (Math.Round(xs.Min() / 25_000.0, 1), Math.Round(ys.Min() / 25_000.0, 1), Math.Round(xs.Max() / 25_000.0, 1), Math.Round(ys.Max() / 25_000.0, 1));
    }

    private static ImageTraceResult Traced(ImageSourceViewModel vm) => ImageTrace.Trace(vm.BuildTraceInput(TracePictures.Tech(), null));

    /// <summary>A copper board 17 mm square at 25 µm a pixel, with <paramref name="holes"/> white holes of radius r px.</summary>
    private static CircuitRF.Design.Imaging.RasterImage Board(params (float X, float Y, float R)[] holes) => Pictures.Draw(680, 680, c =>
    {
        using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
        using var white = Pictures.Fill(SKColors.White);
        c.DrawRect(SKRect.Create(20, 20, 640, 640), copper);
        foreach (var (x, y, r) in holes) c.DrawCircle(x, y, r, white);
    });

    /// <summary>The Crop tool (owner, 2026-10-28): armed by its toggle, each box dragged is the part of the picture read, it
    /// stays armed for another, and Escape disarms it.</summary>
    [Fact]
    public async Task TheCropTool_ReadsOnlyTheBoxDragged_AndEscapeDisarmsIt()
    {
        var vm = await Scaled(Board(), 17);
        vm.CropArmed = true;
        vm.CropTo(new PixelPoint(300, 300), new PixelPoint(10, 10));
        await vm.Recognition;
        Assert.Equal(new PixelRect(10, 10, 300, 300), vm.ScopeRect);
        Assert.Equal(new PixelRect(10, 10, 300, 300), Traced(vm).Traced);
        Assert.True(vm.CropArmed);
        vm.CancelTools();
        Assert.False(vm.CropArmed);
    }

    /// <summary>brief-dfr30-3 gate 4 — everything drawn over a layout picture says what it is when pointed at: one line for
    /// each kind of item, an opening drawn as its own outline rather than as a circle.</summary>
    [Fact]
    public async Task EveryItemOverALayoutPicture_HasAHoverLine()
    {
        // A pour with two 0.3 mm holes and a 2.6 mm one, and beside it a pad drawn as an outline, to paint.
        var picture = Pictures.Draw(680, 680, c =>
        {
            using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
            using var white = Pictures.Fill(SKColors.White);
            using var line = Pictures.Stroke(SKColors.Black, 2);
            c.DrawRect(SKRect.Create(20, 20, 440, 640), copper);
            foreach (var (x, y, r) in new[] { (200f, 200f, 6f), (400f, 200f, 6f), (240f, 500f, 52f) }) c.DrawCircle(x, y, r, white);
            c.DrawRect(SKRect.Create(500, 40, 140, 160), line);
        });
        var vm = await Scaled(picture, 17);
        vm.EveryOneLikeIt = false;
        vm.SelectedItem = vm.Overlay.HitTest(400, 200, 2, vm.Shows);
        vm.HoleIsNotPlatedCommand.Execute(null);
        vm.DrawTool = PictureDrawTool.Via;
        vm.PlaceVia(new PixelPoint(300, 400));
        vm.DrawTool = PictureDrawTool.PaintFill;
        vm.DrawLayer = "Top Copper";
        vm.PaintAt(new PixelPoint(570, 120));
        vm.DrawTool = PictureDrawTool.Copper;
        vm.DrawRectangle(new PixelPoint(40, 40), new PixelPoint(80, 80));
        vm.DrawTool = PictureDrawTool.Outline;
        vm.DrawRectangle(new PixelPoint(10, 10), new PixelPoint(670, 670));
        vm.DrawTool = PictureDrawTool.None;
        await vm.Recognition;

        Assert.All(vm.Overlay.Items, i => Assert.False(string.IsNullOrEmpty(vm.HoverLine(i)), i.Id));
        var lines = vm.Overlay.Items.Select(i => vm.HoverLine(i)!).ToList();
        Assert.Contains("Via ⌀12 mil", lines);   // as written, on this inch-unit technology
        Assert.Contains("Opening ⌀2.6 mm — a hole kept open", lines);
        Assert.Contains(lines, l => l.StartsWith("Non-plated hole ⌀", StringComparison.Ordinal));
        Assert.Contains("Painted copper on Top Copper", lines);
        Assert.Contains("Copper drawn on Top Copper", lines);
        Assert.Contains("Board outline", lines);
        Assert.Contains("Hole made non-plated by hand", lines);
        Assert.Contains("Via placed by hand, drilled as the hole under it", lines);
        Assert.Contains(lines, l => l.StartsWith("Copper on ", StringComparison.Ordinal) || l.StartsWith("Painted copper on ", StringComparison.Ordinal));
        var opening = vm.Overlay.Items.Single(i => i.Id.StartsWith(CircuitRF.Design.Imaging.ImageOverlay.HolePrefix, StringComparison.Ordinal)
                                                   && vm.HoverLine(i)!.StartsWith("Opening", StringComparison.Ordinal));
        Assert.True(Assert.Single(opening.Paths).Count >= 8);   // its own outline
    }

    /// <summary>brief-dfr30-1 gate 2 — three hand vias placed with a size read (0.3 mm), another (0.6 mm) and Other… 1.0 mm
    /// are written with those drills: 12, 24 and 39 mil on this inch-unit technology.</summary>
    [Fact]
    public async Task HandVias_AreDrilledAsTheDrillPickerSays()
    {
        var vm = await Scaled(Board((200, 200, 6), (400, 200, 12)), 17);
        Assert.Equal(["From the hole", "12 mil · 1", "24 mil · 1"], vm.ViaDrillChoices.Take(3).Select(c => c.Label));

        vm.DrawTool = PictureDrawTool.Via;
        vm.ViaDrill = vm.ViaDrillChoices[1];
        vm.PlaceVia(new PixelPoint(200, 500));
        vm.ViaDrill = vm.ViaDrillChoices[2];
        vm.PlaceVia(new PixelPoint(300, 500));
        vm.ViaDrill = vm.ViaDrillChoices.Single(c => c.Source == ViaDrillSource.Other);
        vm.OtherDrillText = "1.0 mm";
        vm.PlaceVia(new PixelPoint(400, 500));
        await vm.Recognition;

        var vias = Traced(vm).Shapes.OfType<CircuitRF.Design.Layout.ViaShape>().Where(v => v.Y < -400 * 25_000).OrderBy(v => v.X);
        Assert.Equal([12 * 25_400L, 24 * 25_400L, 39 * 25_400L], vias.Select(v => v.DrillSize));
    }

    /// <summary>brief-dfr30-1 gate 3 — a copper fill drawn, a corner dragged, then deleted from its row: the trace has the
    /// fill, the moved fill, none; each undo restores the step before.</summary>
    [Fact]
    public async Task ACopperFill_IsMovedByItsCorner_AndDeletedFromItsRow_EachStepUndone()
    {
        var vm = await Scaled(Board(), 17);
        vm.DrawTool = PictureDrawTool.Copper;
        vm.DrawLayer = "Bottom Copper";
        vm.DrawRectangle(new PixelPoint(100, 100), new PixelPoint(200, 200));
        vm.DrawTool = PictureDrawTool.None;
        await vm.Recognition;
        double Right() => (Traced(vm).Layers.FirstOrDefault(l => l.Name == "Bottom Copper")?.Shapes ?? []).Select(s => Box(s).X2).DefaultIfEmpty(0).Max();
        Assert.Equal(200, Right(), 0.5);

        vm.SelectedCorrection = vm.CorrectionAt(new PixelPoint(150, 150), 2);
        Assert.Equal(0, vm.SelectedCorrection);
        int corner = vm.VertexAt(0, new PixelPoint(200, 200), 2);
        Assert.True(vm.MoveCorrectionVertex(0, corner, new PixelPoint(300, 200)));
        await vm.Recognition;
        Assert.Equal(300, Right(), 0.5);

        vm.CorrectionRows.Single().DeleteCommand.Execute(null);
        await vm.Recognition;
        Assert.Equal(0, Right());

        Assert.True(vm.UndoCorrection());
        Assert.Equal(300, Right(), 0.5);
        Assert.True(vm.UndoCorrection());
        Assert.Equal(200, Right(), 0.5);
    }

    /// <summary>brief-dfr30-1 gate 4 — Remove ▸ Vias read from the picture, on twelve automatic vias: none is written and the
    /// twelve holes stay openings; one undo brings them back.</summary>
    [Fact]
    public async Task RemoveViasRead_LeavesTheirHolesOpenings_AsOneUndoStep()
    {
        var holes = Enumerable.Range(0, 12).Select(k => (140f + 80 * (k % 6), 300f + 80 * (k / 6), 10f)).ToArray();
        var vm = await Scaled(Board(holes), 17);
        Assert.Equal(12, vm.AutoViaCount);

        vm.RemoveAutoVias();
        await vm.Recognition;
        var after = Traced(vm);
        Assert.Empty(after.Shapes.OfType<CircuitRF.Design.Layout.ViaShape>());
        Assert.Equal(12, after.Holes.Count(h => h.Kind == ImageHoleKind.Opening));
        Assert.Equal(0, vm.AutoViaCount);

        Assert.True(vm.UndoCorrection());
        Assert.Equal(12, Traced(vm).Shapes.OfType<CircuitRF.Design.Layout.ViaShape>().Count());
    }

    /// <summary>brief-dfr30-1 gate 5 — Remove ▸ Copper read on Top, and one fill drawn there: Top copper is exactly the fill.</summary>
    [Fact]
    public async Task RemoveCopperReadOnALayer_LeavesOnlyTheFillDrawnThere()
    {
        var vm = await Scaled(Board(), 17);
        Assert.True(vm.CopperReadCount("Top Copper") > 0);
        vm.RemoveCopperRead("Top Copper");
        vm.DrawTool = PictureDrawTool.Copper;
        vm.DrawLayer = "Top Copper";
        vm.DrawRectangle(new PixelPoint(100, 100), new PixelPoint(200, 300));
        await vm.Recognition;

        var top = Assert.Single(Traced(vm).Layers.Single(l => l.Name == "Top Copper").Shapes);
        Assert.Equal((100, -300, 200, -100), Box(top));
    }

    /// <summary>Owner, 2026-10-28: no schematic is made from a picture until the reader improves. The menu row is greyed, the
    /// dialog's Read as Schematic and Make Schematic too, and a picture Auto reads as a schematic drawing is traced as a
    /// layout.</summary>
    [Fact]
    public async Task SchematicFromImage_IsGreyed_AndAPictureReadAsASchematicIsTracedAsALayout()
    {
        Assert.False(new CircuitRF.Ui.ViewModels.WorkspaceViewModel().CreateSchematicFromImageCommand.CanExecute(null));

        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Schematic), ImageDialog.Board(), makeSchematic: true);
        await vm.Recognition;

        Assert.False(vm.SchematicOffered);
        Assert.Equal(DrawingKind.Layout, vm.EffectiveKind);
        Assert.True(vm.MakeLayout);
        Assert.False(vm.MakeSchematicEnabled);
    }

    /// <summary>Owner, 2026-10-28: on a line drawing the scale is set by Line impedance… on a line painted in first. With no
    /// scale yet, the lines read as copper are removed and the outlined line painted with Paint Fill; a click on it with a 50 Ω
    /// impedance then sets the scale from the painted line's width.</summary>
    [Fact]
    public async Task ALinePaintedBeforeTheScale_SetsTheScaleByItsImpedance()
    {
        var picture = Pictures.Draw(400, 200, c =>
        {
            using var line = Pictures.Stroke(SKColors.Black, 2);
            c.DrawRect(SKRect.Create(40, 90, 320, 20), line);   // a strip drawn as its outline, 20 px wide between centre lines
        });
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), TracePictures.Source(picture), makeSchematic: false);
        await vm.Recognition;
        Assert.Null(vm.Overlay.Frame);
        Assert.True(vm.DrawToolsShown);   // the tools need no scale

        foreach (string layer in vm.ConductorChoices.Where(l => vm.CopperReadCount(l) > 0).ToList()) vm.RemoveCopperRead(layer);
        vm.DrawTool = PictureDrawTool.PaintFill;
        vm.DrawLayer = "Top Copper";
        vm.PaintAt(new PixelPoint(200, 100));
        vm.DrawTool = PictureDrawTool.None;
        await vm.Recognition;

        vm.Scale.StartImpedanceCommand.Execute(null);
        vm.Scale.Click(new PixelPoint(200, 100));
        vm.Scale.Z0Text = "50 Ω";
        vm.Scale.ApplyZ0Command.Execute(null);
        await vm.Recognition;

        var traced = Traced(vm);
        Assert.Equal(ImageScaleKind.Impedance, traced.Scale?.Kind);
        Assert.NotNull(vm.Overlay.Frame);
        // Measured between the drawn line's centre lines, as the painted copper's edge lies.
        double px = double.Parse(System.Text.RegularExpressions.Regex.Match(traced.Scale!.Evidence, @"([0-9.]+) px").Groups[1].Value,
                                 System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(px, 19.5, 20.5);
    }
}
