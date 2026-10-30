using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>
/// The trace card's "Unwrap°" transform (GitHub discussion 27). A complex cube whose phase falls 40° a step
/// wraps at −180°; on the card it must draw as the straight line, spell itself as the expression language's own
/// <c>unwrap(…)</c>, and still count as complex data for the transform list and the plot-type remap.
/// </summary>
public sealed class PhaseUnwrappedTransformTests
{
    [Fact]
    public async Task AComplexCubeTrace_DrawsTheUnwrappedLine_SpelledAsUnwrap_AndStillComplex()
    {
        var ds = new DataSet();
        ds.Add("G", new DataCube([new Axis("freq", [.. Enumerable.Range(0, 13).Select(k => k * 1e9)], "Hz")],
                                 [.. Enumerable.Range(0, 13).Select(k => Complex.FromPolarCoordinates(1, -40.0 * k * Math.PI / 180))]));
        string path = Path.Combine(Path.GetTempPath(), $"crf_unwrap_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);
            var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
            {
                SourcePath = path, CubeName = "G", Slice = [new AxisSlice("freq", AxisRole.KeepAsX, 0)],
                Transform = CubeTransform.PhaseUnwrapped,
            };
            t.Expression = t.BuildPickerExpression();
            Assert.Equal("unwrap(G)", t.Expression);
            Assert.True(CubeTraceSpecParser.TryParse(t.Expression, ds, out _, out _, out var parsed, out _));
            Assert.Equal(CubeTransform.PhaseUnwrapped, parsed);

            PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            Assert.Equal(Enumerable.Range(0, 13).Select(k => -40.0 * k), t.Points.Select(p => Math.Round(p.Y, 3)));
            Assert.True(t.CubeDataIsComplex);
            Assert.False(t.TransformIsInert);

            t.RemapForPlotType(PlotType.Rect, PlotType.Smith);
            Assert.Equal(CubeTransform.None, t.Transform);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TheEntrySitsAfterPhase_EnabledForAComplexCubeOnRect_AndKeyedOutForATouchstoneTrace()
    {
        var cube = TraceRowViewModel.BuildTransformItems(isCubeBound: true, PlotType.Rect, isComplexData: true);
        int phase = cube.ToList().FindIndex(i => i.Transform == CubeTransform.Phase);
        Assert.Equal(CubeTransform.PhaseUnwrapped, cube[phase + 1].Transform);
        Assert.True(cube[phase + 1].Enabled);
        Assert.Equal(Enum.GetValues<CubeTransform>().Length, cube.Count);

        var network = TraceRowViewModel.BuildTransformItems(isCubeBound: false, PlotType.Rect, isComplexData: true);
        Assert.False(network.Single(i => i.Transform == CubeTransform.PhaseUnwrapped).Enabled);
        Assert.False(PlotInspectorViewModel.AllTransformsForNetwork.Single(i => i.Transform == CubeTransform.PhaseUnwrapped).Enabled);
    }
}
