// ================================================================
//  EnvelopeExampleDisplaysTests.cs  —  brief ENV-MEAS-3 gate 9
//
//  Every envelope example, run as Simulate runs it (the schematic's
//  netlist, the results filed under the schematic's key) at a short Stop,
//  then its display opened: every trace on every plot resolves. The
//  migrated traces pin an IProbe or a labeled net by label, so they
//  survive a re-run that adds a Pin sweep, which NR20_ACPR checks.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class EnvelopeExampleDisplaysTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-envdd-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    /// <summary>Stops short enough for the routine gate. The pulsed bench's pulse is narrowed to 10 µs so one whole pulse
    /// fits: its traces need a measured pulse to resolve.</summary>
    [Theory]
    [InlineData("Envelope", "NR20_ACPR", "20e-6")]
    [InlineData("Envelope", "NR20_ACPR_vs_Pin", "10e-6")]
    [InlineData("Envelope", "NR20_2C_400MHz_single", "2e-6")]
    [InlineData("Pulsed Radar", "PulsedPA_pCW", "30e-6")]
    public async Task EveryTraceResolves(string workspace, string cell, string stop)
    {
        CopyDirectory(Path.Combine(PulsedRadarExampleAuthoring.Workspace, "..", workspace), _ws);
        string csch = Path.Combine(_ws, cell, "schematic", cell + ".csch");
        string text = Regex.Replace(File.ReadAllText(csch), "\"EnvStop\": \"[^\"]*\"", $"\"EnvStop\": \"{stop}\"");
        File.WriteAllText(csch, text.Replace("PW=100us", "PW=10us", StringComparison.Ordinal));
        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);

        var win = await Open(cell);
        string npy = Simulate(model, csch, cell);
        await Refresh(win, npy);
        AssertEveryTraceResolves(cell, win);

        if (cell != "NR20_ACPR") return;
        // a re-run with a Pin sweep added: every trace pinned to Iout is still the same probe, now one curve per Pin
        model.Analyses.Add(new ParametricSweepAnalysis("SW_Pin", "Pin", [10.0, 14.0], "ENV1"));
        await Refresh(win, Simulate(model, csch, cell));
        AssertEveryTraceResolves(cell + " under a Pin sweep", win);
    }

    private async Task<DisplayWindowViewModel> Open(string cell)
    {
        var win = new DisplayWindowViewModel();
        win.DataSourceLibrary.ResultsRootProvider = () => Path.Combine(_ws, "results");
        await win.LoadAllAsync(Path.Combine(_ws, cell + ".cdd"));
        return win;
    }

    /// <summary>Simulate: netlist at the run base, run, file the results under the schematic's key.</summary>
    private string Simulate(SchematicEditModel model, string csch, string cell)
    {
        var netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        return RunResultsWriter.WriteRun(_ws, RunResultsWriter.SchematicKey(csch, cell), run.GroupedResults, null, model.ResultsFileName).Single();
    }

    /// <summary>WorkspaceViewModel.RefreshOpenDataDisplaysAsync, step for step.</summary>
    private static async Task Refresh(DisplayWindowViewModel win, string npy)
    {
        var lib = win.DataSourceLibrary;
        win.RefreshAvailableDataSources();
        await lib.ReloadChangedAsync([npy]);
        await lib.SelectDataSourceAsync(lib.SelectedDataSourceRef);
    }

    private void AssertEveryTraceResolves(string what, DisplayWindowViewModel win)
    {
        Assert.Null(win.AwaitingRunText);
        var plots = win.Tabs.SelectMany(t => t.DataDisplay.Plots).ToList();
        Assert.NotEmpty(plots);
        foreach (var plot in plots)
            foreach (var t in plot.PlotVM.Plot.Traces)
            {
                string trace = $"{what}: '{plot.PlotVM.Plot.CustomTitle}' {t.CubeName}";
                output.WriteLine($"{trace}: {t.Points.Count} points, {t.FamilyCurves.Count} curves");
                Assert.True(t.InvalidSpecText is null && t.ExpressionError is null, $"{trace}: {t.ExpressionError ?? t.InvalidSpecText}");
                // a table reads its cells from the cube rather than from the points a graph draws
                if (plot.PlotVM.Plot.PlotType != PlotType.Table)
                    Assert.True(t.Points.Count > 0 || t.FamilyCurves.Count > 0, trace + " drew nothing");
            }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
