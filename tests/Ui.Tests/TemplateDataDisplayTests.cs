// ================================================================
//  TemplateDataDisplayTests.cs  —  a schematic template that brings its Data Display
//  (brief-template-two-port-stability)
//
//  One test per claim: the display is found by stem; the copy lands where the schematic's run
//  looks and names that run's results; a clash is refused; the dialog's box is off for a template
//  with no display; and every shipped display fills from its own template's run — which is also
//  the brief's first gate, scripted headlessly (WorkspaceViewModel cannot be constructed here, so
//  the steps are the ones its New Schematic and Simulate take, in order). No pixels are seen.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TemplateDataDisplayTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-tpldd-" + Guid.NewGuid().ToString("N")[..8]);

    public TemplateDataDisplayTests() => Directory.CreateDirectory(_ws);

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    private static ShippedSchematicTemplate Stability
        => ShippedSchematicTemplates.All.Single(t => t.Id == "Two_Port_Stability");

    [Fact]
    public void ADisplay_IsFoundByItsTemplatesStem()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Ui", "resources", "data-display-templates");
        var stems = Directory.GetFiles(dir, "*.cdd").Select(Path.GetFileNameWithoutExtension).ToArray();

        Assert.NotEmpty(stems);
        foreach (var stem in stems)
            Assert.True(ShippedSchematicTemplates.All.Single(t => t.Id == stem).HasDataDisplay, stem);
        Assert.False(ShippedSchematicTemplates.All.Single(t => t.Id == "FET_S-Parameters").HasDataDisplay);
    }

    [Theory]
    [InlineData("Amp", "Amp",  "Amp")]        // New Cell: the cell's primary schematic
    [InlineData("Amp", "stab", "Amp.stab")]   // New Schematic: a second view in the cell
    public void TheCopy_LandsWhereTheRunLooks_AndNamesThatRunsResults(string cell, string view, string key)
    {
        var csch = Path.Combine(_ws, cell, "schematic", view + ".csch");

        var (written, ok) = CellCreate.WriteTemplateDataDisplay(
            _ws, csch, null, ShippedSchematicTemplates.LoadDataDisplayJson(Stability)!);
        Assert.True(ok);

        // Found by the run's own lookup, first — the authored slot, not results/.
        Assert.Equal(key, RunResultsWriter.SchematicKey(csch, "scratch"));
        Assert.Equal(RunResultsWriter.AutoDisplayCandidates(_ws, Path.Combine(_ws, "results"), key)[0], written);

        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(written), DataDisplayJson.Options)!;
        string results = RunResultsWriter.ResolveFileName(null, key);
        Assert.Equal(results, config.SelectedDataSource);
        var traces = config.Tabs.SelectMany(t => t.Plots).SelectMany(p => p.Traces).ToList();
        Assert.NotEmpty(traces);
        Assert.All(traces, t => Assert.Equal(results, t.SourcePath));
    }

    [Fact]
    public void ANameClash_IsRefused_AndTheExistingDisplayIsKept()
    {
        var csch = Path.Combine(_ws, "Amp", "schematic", "Amp.csch");
        var existing = Path.Combine(_ws, "Amp.cdd");
        File.WriteAllText(existing, "mine");

        var (path, written) = CellCreate.WriteTemplateDataDisplay(
            _ws, csch, null, ShippedSchematicTemplates.LoadDataDisplayJson(Stability)!);

        Assert.False(written);
        Assert.Equal(existing, path);   // the caller names this file in its warning
        Assert.Equal("mine", File.ReadAllText(existing));
    }

    [Fact]
    public void TheIncludeBox_IsDisabledAndUnchecked_WithoutADisplay()
    {
        var plain = ShippedSchematicTemplates.All.Single(t => t.Id == "FET_S-Parameters");

        Assert.Equal((false, false), InputNameDialog.IncludeDisplayStateFor(null));
        Assert.Equal((false, false), InputNameDialog.IncludeDisplayStateFor(plain));
        Assert.Equal((true, true),   InputNameDialog.IncludeDisplayStateFor(Stability));
        Assert.False(InputNameDialog.ResolveIncludeDataDisplay(plain, boxChecked: true));
    }

    /// <summary>
    /// Every template that ships a display: create it as New Cell does, open the display before any
    /// run (it must read as waiting, not throw), Simulate as the workspace does, refresh as the
    /// workspace does, and require every trace on every plot to have drawn something. A trace naming
    /// a cube or metric the template's analysis cannot produce fails here by name.
    /// </summary>
    [Fact]
    public async Task EveryShippedDisplay_FillsFromItsOwnTemplatesRun()
    {
        var withDisplay = ShippedSchematicTemplates.All.Where(t => t.HasDataDisplay).ToList();
        Assert.NotEmpty(withDisplay);

        // A two-port circuitRF made itself (the S-Parameters example's FET bench, exported).
        File.Copy(Path.Combine(RepoRoot(), "examples", "S-Parameters", "FetStability", "schematic", "fet_bias.s2p"),
                  Path.Combine(_ws, "two_port.s2p"));

        foreach (var template in withDisplay)
        {
            string cell = "C_" + template.Id;
            CellFolder.CreateCellFolder(_ws, cell);
            var model = ShippedSchematicTemplates.Load(template, CellFolder.SubFolderPath(Path.Combine(_ws, cell), ViewType.Schematic));
            foreach (var p in model.Components.Where(c => c.Symbol == SymbolKind.Snp)
                                              .SelectMany(c => c.Parameters)
                                              .Where(p => p.Name == "File" && p.Expression.Length == 0))
                p.Expression = "two_port.s2p";
            var csch = CellCreate.WriteSchematicView(Path.Combine(_ws, cell), cell, cell, model);
            var (cdd, _) = CellCreate.WriteTemplateDataDisplay(
                _ws, csch, model.ResultsFileName, ShippedSchematicTemplates.LoadDataDisplayJson(template)!);

            var win = new DisplayWindowViewModel();
            win.DataSourceLibrary.ResultsRootProvider = () => Path.Combine(_ws, "results");
            await win.LoadAllAsync(cdd);
            Assert.NotNull(win.AwaitingRunText);

            // Simulate: netlist at the run base, run, file the results under the schematic's key.
            var netlist = Path.Combine(_ws, "netlist.cnl");
            File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
            var run = SchematicRunService.RunNetlist(netlist, _ws);
            Assert.True(run.Status == RunStatus.Success, $"{template.Id}: {run.StatusMessage}");
            var npy = RunResultsWriter.WriteRun(_ws, RunResultsWriter.SchematicKey(csch, cell),
                                                run.GroupedResults, null, model.ResultsFileName).Single();

            // WorkspaceViewModel.RefreshOpenDataDisplaysAsync, step for step.
            var lib = win.DataSourceLibrary;
            win.RefreshAvailableDataSources();
            await lib.ReloadChangedAsync([npy]);
            await lib.SelectDataSourceAsync(lib.SelectedDataSourceRef);

            Assert.Null(win.AwaitingRunText);
            var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
            AssertEveryTraceDrew(template.Id, win, config.Tabs.Sum(t => t.Plots.Count));

            // And the display Simulate opens when it was NOT already open: read fresh, results present.
            var fresh = new DisplayWindowViewModel();
            fresh.DataSourceLibrary.ResultsRootProvider = () => Path.Combine(_ws, "results");
            await fresh.LoadAllAsync(cdd);
            AssertEveryTraceDrew(template.Id, fresh, config.Tabs.Sum(t => t.Plots.Count));
        }
    }

    private static void AssertEveryTraceDrew(string id, DisplayWindowViewModel win, int expectedPlots)
    {
        var plots = win.Tabs.SelectMany(t => t.DataDisplay.Plots).ToList();
        Assert.Equal(expectedPlots, plots.Count);
        foreach (var plot in plots)
        {
            Assert.NotEmpty(plot.PlotVM.Plot.Traces);
            foreach (var t in plot.PlotVM.Plot.Traces)
            {
                string what = $"{id}: '{plot.PlotVM.Plot.CustomTitle}' {t.Derived} S{t.Row + 1}{t.Col + 1}";
                Assert.Null(t.InvalidSpecText);
                Assert.True(t.IsStabilityCircle ? t.StabilityCircleCentres.Count > 0 : t.Points.Count > 0,
                            what + " drew nothing");
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
