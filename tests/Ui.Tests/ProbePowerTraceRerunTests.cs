// ================================================================
//  ProbePowerTraceRerunTests.cs  —  brief ENV-MEAS-2 gate 2b
//
//  A trace made on a one-sweep run ([Pin]) stays valid when the design
//  is re-run with an outer Vdd sweep added, and plots one curve per Vdd
//  with Pin still X: for a P measure (Pout_dBm = dBm(ENV1.P("Iout"))),
//  and for the cube it reads, ENV1 › Pprobe_fund_W › Iout.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ProbePowerTraceRerunTests
{
    private static readonly Axis Pin = new("Pin", [0.0, 3.0, 6.0], "dBm");
    private static readonly Axis Vdd = new("Vdd", [1.0, 1.2], "V");
    private static readonly Axis Branch = new("branch", [0.0, 1.0], "", ["Iin", "Iout"]);

    private static DataCube Ramp(params Axis[] axes) =>
        new(axes, [.. Enumerable.Range(0, axes.Aggregate(1, (n, a) => n * a.Length)).Select(i => 1.0 + i)]);

    /// <summary>What a run writes: the measure in the measurements group, the probe power in the analysis's group.</summary>
    private static void Write(string path, bool outerVdd)
    {
        Axis[] sweep = outerVdd ? [Vdd, Pin] : [Pin];
        var ds = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Pout_dBm", Ramp(sweep));
        ds.AddToGroup("ENV1", "Pprobe_fund_W", Ramp([.. sweep, Branch]));
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
    }

    private static Trace CubeTrace(Plot plot, string path, string cube, AxisSlice[] slice)
    {
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = path,
            CubeName   = cube,
            Slice      = slice,
        };
        trace.Expression = trace.BuildPickerExpression();
        plot.Traces.Add(trace);
        return trace;
    }

    [Fact]
    public async Task APowerTrace_SurvivesAnOuterSweep_AsAFamily()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crf_envm2_{Guid.NewGuid():N}.npy");
        try
        {
            Write(path, outerVdd: false);
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var plot  = new Plot(PlotType.Rect, FreqUnit.GHz);
            var meas  = CubeTrace(plot, path, "Pout_dBm", [new AxisSlice("Pin", AxisRole.KeepAsX, 0)]);
            var probe = CubeTrace(plot, path, "ENV1.Pprobe_fund_W",
                                  [new AxisSlice("Pin", AxisRole.KeepAsX, 0), new AxisSlice("branch", AxisRole.PinToIndex, 1, Label: "Iout")]);
            _ = new PlotInspectorViewModel(plot, () => { }, lib);
            foreach (var t in plot.Traces) PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            Assert.Equal(3, meas.Points.Count);
            Assert.Equal(3, probe.Points.Count);

            Write(path, outerVdd: true);
            await lib.ReloadChangedAsync([path]);

            foreach (var t in new[] { meas, probe })
            {
                Assert.Contains(t.Slice!, s => s.AxisName == "Pin" && s.Role == AxisRole.KeepAsX);
                Assert.Contains(t.Slice!, s => s.AxisName == "Vdd" && s.Role == AxisRole.FamilyIterate);
                Assert.Equal(2, t.FamilyCurves.Count);
                Assert.Null(t.ExpressionError);
            }
            Assert.Contains(probe.Slice!, s => s.AxisName == "branch" && s.Role == AxisRole.PinToIndex && s.Index == 1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
