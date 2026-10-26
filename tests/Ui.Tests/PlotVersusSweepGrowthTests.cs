// ================================================================
//  PlotVersusSweepGrowthTests.cs — a "Gain vs Pout" card kept open while
//  re-runs ADD sweep axes, the shape of the shipped PowerAmplifier example
//  with its RFfreq and VDD sweeps switched on one at a time.
//
//  Each step passed on its own; the sequence did not. The re-run reseed
//  handed "Gt_dB vs Pout_dBm" whole to the cube parser, which cannot read
//  "vs", so the new RFfreq axis was pinned at its first value and the spec
//  was rewritten with brackets. The next sweep then left a bare X side on a
//  three-axis cube, which resolved as "more than one X axis".
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PlotVersusSweepGrowthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vsgrow-" + Guid.NewGuid().ToString("N")[..8]);

    public PlotVersusSweepGrowthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly Axis Pin    = new("Pin",    [0.0, 10.0, 20.0, 30.0], "");
    private static readonly Axis RFfreq = new("RFfreq", [1e9, 2e9, 3e9], "Hz");
    private static readonly Axis Vdd    = new("VDD",    [40.0, 45.0, 50.0], "");

    /// <summary>The measurements an HB run writes, over the given sweeps (outermost first).</summary>
    private string WriteRun(params Axis[] axes)
    {
        int n = axes.Aggregate(1, (k, a) => k * a.Length);
        var ds = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Gt_dB",    new DataCube(axes, Enumerable.Range(0, n).Select(i => 12.0 + i % 4).ToArray()));
        ds.AddToGroup(DataSet.MeasurementsGroup, "Pout_dBm", new DataCube(axes, Enumerable.Range(0, n).Select(i => 10.0 * (i % 4) + i / 4).ToArray()));
        string path = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private static async Task<(DataSourceLibraryViewModel, TraceRowViewModel)> CardOn(string path, DataSourceLibraryViewModel? lib = null)
    {
        if (lib is null)
        {
            lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);
        }
        const string gt = "measurements.Gt_dB";
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = path, CubeName = gt };
        trace.Slice = TraceRowViewModel.BuildDefaultSlice(lib.Entries.First().Data![gt]);
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        var row = inspector.Traces[0];
        row.VersusEnabled = true;
        row.SelectedXSignal = row.XSignals.First(s => s.CubeName!.EndsWith("Pout_dBm"));
        return (lib, row);
    }

    private static string Roles(Trace t) => string.Join(",", t.Slice!.Select(s => $"{s.AxisName}:{s.Role}"));

    [Fact]
    public async Task ReRunsThatAddSweeps_KeepTheCardPlotting_TheFirstNewSweepBecomingTheFamily()
    {
        string path = WriteRun(Pin);
        var (lib, row) = await CardOn(path);
        Assert.Null(row.Trace.ExpressionError);

        WriteRun(RFfreq, Pin);
        await lib.ReloadChangedAsync([path]);
        Assert.Null(row.Trace.ExpressionError);
        Assert.Equal("RFfreq:FamilyIterate,Pin:KeepAsX", Roles(row.Trace));
        Assert.Equal(3, row.Trace.FamilyCurves.Count);

        WriteRun(Vdd, RFfreq, Pin);
        await lib.ReloadChangedAsync([path]);
        Assert.Null(row.Trace.ExpressionError);
        Assert.Equal("VDD:PinToIndex,RFfreq:FamilyIterate,Pin:KeepAsX", Roles(row.Trace));
        Assert.Equal(3, row.Trace.FamilyCurves.Count);
        Assert.Equal("measurements.Gt_dB vs Pout_dBm", row.SpecShorthand);
    }

    [Fact]
    public async Task AFreshCardOnThreeSweeps_OpensOnTheInnermostSweepWithAFamily_AndTakesAVersusX()
    {
        var (_, row) = await CardOn(WriteRun(Vdd, RFfreq, Pin));

        Assert.Null(row.Trace.ExpressionError);
        Assert.Equal("VDD:PinToIndex,RFfreq:FamilyIterate,Pin:KeepAsX", Roles(row.Trace));
        Assert.Equal(3, row.Trace.FamilyCurves.Count);
    }
}
