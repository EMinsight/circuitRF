// ================================================================
//  TraceLabelTests.cs  —  brief-dfr27-3
//
//  1. Three labelled traces round-trip through the .cdd byte for byte, and an unlabelled one writes no key.
//  2. A label is the whole name in the legend, the axis title, the marker box and a table header; an
//     unlabelled trace beside it keeps its generated name and its source prefix.
// ================================================================

using System.Linq;
using System.Text.Json;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class TraceLabelTests
{
    private static readonly SNP Network = new([1e9, 2e9, 3e9], 6);

    private sealed class OneNetwork : IPlotDataSources
    {
        public string?  ResolveAbs(string? sourceRef)  => sourceRef;
        public bool     Contains(string absPath)       => true;
        public SNP?     NetworkFor(string absPath)     => Network;
        public DataSet? DataFor(string absPath)        => null;
        public string?  AliasFor(string absPath)       => null;
        public string?  DisplayNameFor(string absPath) => absPath;
        public bool     HasMultipleSources             => false;
        public DataSet? SelectedData                   => null;
    }

    private static Plot ThreeTraces(PlotType type, params string?[] labels)
    {
        var plot = new Plot(type, FreqUnit.GHz);
        for (int k = 0; k < 3; k++)
        {
            var t = new Trace(Network, MatrixType.S, 2 * k + 1, 2 * k, DependentVarFormat.Db)
            {
                SourceRef = "bench.s6p", SourcePath = "bench.s6p", Label = labels[k],
            };
            t.BuildPath(type, FreqUnit.GHz);
            plot.Traces.Add(t);
        }
        return plot;
    }

    private static string Json(Plot plot) => JsonSerializer.Serialize(
        plot.Traces.Select(t => DataDisplayViewModel.BuildTraceConfig(t, ".")).ToList(), DataDisplayJson.Options);

    [Fact]
    public void ThreeLabelledTraces_RoundTripByteForByte_AndNoLabelWritesNoKey()
    {
        var plot  = ThreeTraces(PlotType.Rect, "As built", "Test point cut", "Fix, C=2.8 pF");
        string saved = Json(plot);

        var pc = new PlotContainerConfig { PlotType = PlotType.Rect, FreqUnit = FreqUnit.GHz };
        pc.Traces.AddRange(JsonSerializer.Deserialize<List<TraceConfig>>(saved, DataDisplayJson.Options)!);
        var loaded = PlotConfigLoader.LoadPlot(pc, new OneNetwork());

        Assert.Equal(new[] { "As built", "Test point cut", "Fix, C=2.8 pF" }, loaded.Traces.Select(t => t.Label).ToArray());
        Assert.Equal(saved, Json(loaded));

        // Empty is the generated name, and is written exactly as before the field existed.
        Assert.DoesNotContain("\"Label\"", Json(ThreeTraces(PlotType.Rect, null, "", null)));
    }

    [Fact]
    public void ALabel_IsTheWholeName_InTheLegend_TheAxis_TheMarkerBox_AndATableHeader()
    {
        var plot = ThreeTraces(PlotType.Rect, "As built", "Test point cut", null);
        plot.Legend = LegendPlacement.Inside;

        // Source forced on: a label carries none, the unlabelled trace keeps its prefix and its quantity.
        Assert.Equal(new[] { "As built", "Test point cut", "bench·S(6,5) dB20" },
                     TraceLabeler.ComputeMinimalLabels(plot.Traces, alwaysShowSource: true).ToArray());
        Assert.Equal(new[] { "As built", "Test point cut", "S(6,5) dB20" },
                     LegendRenderer.Entries(plot, false, null).Select(e => e.Label).ToArray());

        var m = new Marker(plot.Traces[0], freq: 2e9, isMulti: true, isDelta: false, index: 1);
        var lines = plot.Traces[0].BuildMarkerBoxLines(m, FreqUnit.GHz, showFilePrefix: true, plot.Traces)
                                  .Select(l => l.Text).ToList();
        Assert.StartsWith("As built", lines[2]);                       // marker, freq, then this trace's value
        Assert.StartsWith("Test point cut", lines[3]);                 // the multi-marker rows keep `=value`
        Assert.StartsWith("S(6,5) dB20", lines[4]);

        plot.SetPlotType(PlotType.Table);
        Assert.Equal(new[] { "As built", "Test point cut", "dB(S(6,5))" },
                     TableRenderer.BuildColumns(plot).Where(c => c.Kind == TableColKind.TraceValue).Select(c => c.Header).ToArray());
    }
}
