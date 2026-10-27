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
}
