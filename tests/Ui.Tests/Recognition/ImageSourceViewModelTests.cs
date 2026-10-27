// brief-img-5-dialog.md §4 — the picture source: a dropped file that is not a picture is refused and the drop zone
// stays; a picture with no drawing disables Create with its sentence, and the override sets the kind; Make Layout is
// disabled for a schematic picture, and comes back when the picture is read as a layout.

using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
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
}
