using System.IO;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-3 R-to3-9: one ghost per bound trace, drawn by the GUI's own export, never saved.</summary>
public sealed class SnapshotGhostTests
{
    [Fact]
    public async Task SnapshotAddsOneGhostPerBoundTrace_ExportDrawsThem_CddUnchanged_ClearRemoves()
    {
        var dir = TuningDisplayFixture.TempDir();
        try
        {
            var path = TuningDisplayFixture.WriteResults(dir, level: 10);
            var vm   = new DataDisplayDocumentViewModel();
            var lib  = vm.Window.DataSourceLibrary;
            lib.ResultsRootProvider = () => dir;
            await lib.LoadFileAsync(path);

            var container = vm.Window.DataDisplay!.Plots[0];
            var plot      = container.PlotVM.Plot;
            plot.SetPlotType(PlotType.Rect);
            foreach (var t in new[] { TuningDisplayFixture.BoundTrace(path), TuningDisplayFixture.BoundTrace(path) })
            {
                plot.Traces.Add(t);
                PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            }

            lib.Publish(path, TuningDisplayFixture.Results(40));
            var cdd = Path.Combine(dir, "Amp.cdd");
            await vm.Window.SaveAllAsync(cdd);
            var before    = File.ReadAllText(cdd);
            var svgBefore = PlotExporter.BuildSvgStringForContainers([container], RenderTheme.Light);
            Assert.True(lib.TakeSnapshot(path));
            Assert.Equal(2, plot.GhostTraces.Count);
            Assert.All(plot.GhostTraces, g => Assert.Equal(40, g.Points[0].Y));

            // A copy/export from the GUI draws the ghosts; the .cdd never carries them.
            Assert.NotEqual(svgBefore, PlotExporter.BuildSvgStringForContainers([container], RenderTheme.Light));
            await vm.Window.SaveAllAsync(cdd);
            Assert.Equal(before, File.ReadAllText(cdd));

            lib.ClearSnapshot(path);
            Assert.Empty(plot.GhostTraces);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
