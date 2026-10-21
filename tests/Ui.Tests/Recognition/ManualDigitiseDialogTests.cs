// brief-pdg-4-digitise-by-hand.md §5 — the dialog's Manual steps, as the view model holds them: each step enabled when the
// one before it is done; a fixpoint added, moved and removed (as the canvas's right-click and its Delete each remove one)
// with Undo putting every change back; and Make: CSV hiding the Touchstone's controls (R-pdg4-6).

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.PlotRecognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class ManualDigitiseDialogTests(PlotPictureSet pictures) : IClassFixture<PlotPictureSet>, IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-pdg4-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    /// <summary>Digitise Plot… on a blank picture: read by hand, nothing for a frame finder to suggest a box from.</summary>
    private async Task<(ImageSourceViewModel Vm, ManualPlotViewModel Manual)> Blank()
    {
        var vm = new ImageSourceViewModel(_ws, [], 0, null, true, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_ws, "presets"));
        var raster = new RasterImage(600, 400, Enumerable.Repeat((byte)255, 600 * 400 * 4).ToArray());
        vm.ReadPlot(ImageSource.FromBytes(raster.EncodePng(), "blank.png").Source!, csv: true);
        await vm.Recognition;
        Assert.True(vm.Plots.Manual);
        return (vm, vm.Plots.ShownManual!);
    }

    private static void Scales(ManualPlotViewModel m)
    {
        m.XLowText = "1 GHz";
        m.XHighText = "5 GHz";
        m.YLowText = "-40 dB";
        m.YHighText = "0 dB";
    }

    [Fact]
    public async Task EachStep_IsEnabled_WhenTheOneBeforeItIsDone()
    {
        var (vm, m) = await Blank();
        Assert.Equal((false, false, false, false), (m.HasBox, m.StepScalesEnabled, m.StepCurvesEnabled, m.StepPointsEnabled));
        Assert.Equal(ManualTool.Box, vm.Plots.ManualTool);

        m.DrawBox(100, 50, 500, 350);
        Assert.Equal((true, false), (m.StepScalesEnabled, m.StepCurvesEnabled));

        Scales(m);
        Assert.Equal((true, false), (m.StepCurvesEnabled, m.StepPointsEnabled));

        m.AddCurveCommand.Execute(null);
        Assert.Equal((true, false), (m.StepPointsEnabled, m.StepSampleEnabled));
        Assert.Equal(ManualTool.Points, vm.Plots.ManualTool);

        m.AddPoint(100, 200);
        Assert.False(m.StepSampleEnabled);
        m.AddPoint(500, 125);
        Assert.Equal((true, true), (m.StepSampleEnabled, m.StepCreateEnabled));

        // What the steps say is what is read: one curve, sampled on the default grid, and Create can write it.
        await vm.Recognition;
        var t = vm.Plots.Session!.Traces.Single();
        Assert.True(t.Manual);
        Assert.Equal(101, t.Samples.Count);
        Assert.Null(vm.CreateBlocked);
    }

    [Fact]
    public async Task AFixpoint_IsAdded_Moved_AndRemovedByRightClickOrDelete_AndUndoPutsEachBack()
    {
        var (vm, m) = await Blank();
        m.DrawBox(100, 50, 500, 350);
        Scales(m);
        m.AddCurveCommand.Execute(null);
        var curve = m.SelectedCurve!;
        m.AddPoint(120, 300);
        m.AddPoint(300, 200);
        m.AddPoint(480, 100);
        Assert.Equal(3, curve.Fixpoints.Count);

        // A drag is one step, however far it went.
        m.BeginEdit();
        m.DragPoint(curve, 1, 310, 190);
        m.DragPoint(curve, 1, 320, 180);
        m.EndEdit();
        Assert.Equal((320.0, 180.0), curve.Fixpoints[1]);
        vm.Plots.UndoCommand.Execute(null);
        Assert.Equal((300.0, 200.0), curve.Fixpoints[1]);

        // The right-click: the fixpoint under the pointer removed.
        var under = m.PointAt(481, 101, 6)!.Value;
        m.RemovePoint(under.Curve, under.Index);
        Assert.Equal(2, curve.Fixpoints.Count);
        vm.Plots.UndoCommand.Execute(null);
        Assert.Equal(3, curve.Fixpoints.Count);

        // Delete: the selected fixpoint removed.
        m.SelectedPoint = (curve, 0);
        Assert.True(m.RemoveSelectedPoint());
        Assert.Equal([(300.0, 200.0), (480.0, 100.0)], curve.Fixpoints);
        vm.Plots.UndoCommand.Execute(null);
        Assert.Equal([(120.0, 300.0), (300.0, 200.0), (480.0, 100.0)], curve.Fixpoints);

        // A curve removed with its points comes back with them.
        curve.RemoveCommand.Execute(null);
        Assert.Empty(m.Curves);
        vm.Plots.UndoCommand.Execute(null);
        Assert.Equal([(120.0, 300.0), (300.0, 200.0), (480.0, 100.0)], Assert.Single(m.Curves).Fixpoints);

        // Back to before the first point.
        while (vm.Plots.UndoCommand.CanExecute(null)) vm.Plots.UndoCommand.Execute(null);
        Assert.False(m.HasBox);
    }

    [Fact]
    public async Task MakeCsv_HidesTheTouchstonesControls()
    {
        var vm = new ImageSourceViewModel(_ws, [], 0, null, true, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(_ws, "presets"));
        vm.ReadPlot(ImageSource.FromFile(pictures.Picture("s21db")).Source!, csv: false);
        await vm.Recognition;
        Assert.False(vm.Plots.Manual);   // a Touchstone entry reads automatically
        vm.Plots.Traces[0].EntryIndex = 2 + 1 * 4 + 0;   // S21
        await vm.Recognition;
        Assert.True(vm.Plots.HasMatrix);
        Assert.Contains("Touchstone", vm.Plots.Traces[0].FitTip, StringComparison.Ordinal);

        vm.IsMakeCsv = true;
        Assert.True(vm.Plots.MakeCsv);
        Assert.False(vm.Plots.HasMatrix);
        Assert.False(vm.Plots.RefusalShown);
        Assert.DoesNotContain("Touchstone", vm.Plots.Traces[0].FitTip, StringComparison.Ordinal);
    }
}
