// ================================================================
//  ScalarAndSinglePointTraceTests.cs — a saved Table column of a scalar
//  cube survives reopening, and a one-point trace is framed and drawn.
//
//  Both came from an envelope result: Pout/gain/efficiency are rank-0
//  cubes (an empty saved slice), and a single-carrier ACLR has a one-
//  point carrier axis. The loader dropped the first; autoscale skipped
//  the second's zero-size box and a line through one point drew nothing.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public class ScalarAndSinglePointTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-scalar-" + Guid.NewGuid().ToString("N"));
    private readonly string _npy;

    public ScalarAndSinglePointTraceTests()
    {
        Directory.CreateDirectory(_dir);
        var ds = new DataSet();
        ds.AddToGroup("ENV1", "Gt_dB", DataCube.Scalar(16.83));
        var carrier = new Axis("carrier", new[] { 0.0 }, "", new[] { "SRC1" });
        var metric  = new Axis("metric", new[] { 0.0, 1.0 }, "", new[] { "Pch_dBm", "ACPR1L" });
        ds.AddToGroup("ENV1", "ACLR", new DataCube(new[] { carrier, metric }, new[] { 36.78, -21.76 }));
        _npy = Path.Combine(_dir, "r.npy");
        DataSetExporter.Export(ds, _npy, ExportFormat.Npy);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void AScalarTableColumn_WithItsEmptySlice_LoadsAndResolves()
    {
        var pc = new PlotContainerConfig { PlotType = PlotType.Table };
        pc.Traces.Add(new TraceConfig { SourcePath = _npy, CubeName = "ENV1.Gt_dB" });   // CubeSlice = []

        var plot = PlotConfigLoader.LoadPlot(pc, new OneFile(_npy));

        var t = Assert.Single(plot.Traces);
        Assert.True(t.CubeIsScalar);
        Assert.Null(t.InvalidSpecText);
    }

    [Fact]
    public void AOnePointTrace_IsFramedAroundItsValue()
    {
        var pc = new PlotContainerConfig { PlotType = PlotType.Rect };
        pc.Traces.Add(new TraceConfig
        {
            SourcePath = _npy, CubeName = "ENV1.ACLR",
            CubeSlice =
            {
                new AxisSliceConfig { AxisName = "carrier", Role = AxisRole.KeepAsX },
                new AxisSliceConfig { AxisName = "metric",  Role = AxisRole.PinToIndex, Index = 1, Label = "ACPR1L" },
            },
        });

        var plot = PlotConfigLoader.LoadPlot(pc, new OneFile(_npy));
        plot.Autoscale(force: true);

        var t = Assert.Single(plot.Traces);
        var p = Assert.Single(t.Points);
        Assert.Equal(-21.76, p.Y, 3);
        var w = plot.Axes.Window;
        Assert.InRange(-21.76, w.Y, w.Y + w.Height);
        Assert.InRange((double)p.X, w.X, w.X + w.Width);
        Assert.True(w.Height < 5, $"window height {w.Height} should hug the value");
    }

    private sealed class OneFile(string path) : IPlotDataSources
    {
        private readonly DataSet _data = DataSetImporter.Import(path).DataSet;
        public string? ResolveAbs(string? sourceRef)  => path;
        public bool    Contains(string absPath)       => true;
        public SNP?    NetworkFor(string absPath)     => null;
        public DataSet? DataFor(string absPath)       => _data;
        public string? AliasFor(string absPath)       => null;
        public string? DisplayNameFor(string absPath) => Path.GetFileName(path);
        public bool     HasMultipleSources            => false;
        public DataSet? SelectedData                  => _data;
    }
}
