using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Tuning;

/// <summary>brief-tuneopt-3 R-to3-5/R-to3-8: a published DataSet stands in for the file under its own path.</summary>
public sealed class PublishedSourceTests
{
    [Fact]
    public async Task BoundTraceShowsThePublishedCube_AndRevertRestoresTheFile()
    {
        var dir = TuningDisplayFixture.TempDir();
        try
        {
            var path = TuningDisplayFixture.WriteResults(dir, level: 10);
            var lib  = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            var trace = TuningDisplayFixture.BoundTrace(path);
            var plot  = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            _ = new PlotInspectorViewModel(plot, () => { }, lib);
            PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Rect, FreqUnit.GHz);
            Assert.Equal(10, trace.Points[0].Y);

            Assert.True(lib.Publish(path, TuningDisplayFixture.Results(40)));
            Assert.Equal(40, trace.Points[0].Y);
            Assert.Equal(DataSourceLibraryViewModel.TuningChip, lib.PublishedChip);

            lib.Unpublish(path);
            Assert.Equal(10, trace.Points[0].Y);
            Assert.Null(lib.PublishedChip);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
