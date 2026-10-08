using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.Tests.Tuning;
using CircuitRF.Ui.Tuning;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.OptimizerPanel;

/// <summary>brief-tuneopt-10 R-to10-7: the Data Display follows the best point through TO-3's path,
/// coalesced, and a finished run is evaluated ONCE more with every analysis and written.</summary>
public sealed class OptimizerPublishTests
{
    private static DataSet FileData()
    {
        var freq = new Axis("freq", [1e9], "Hz");
        var ds = new DataSet();
        ds.AddToGroup("SP1", "x", new DataCube([freq], [0.0]));
        ds.AddToGroup("HB1", "Pout", new DataCube([freq], [1.0]));
        return ds;
    }

    [Fact]
    public async Task BestPointPublishes_AreCoalesced_AndTheFinishReRunsEveryAnalysisOnce()
    {
        var dir = TuningDisplayFixture.TempDir();
        try
        {
            var lib  = new DataSourceLibraryViewModel { FrameScheduler = _ => { } };   // the first frame never ends
            var sink = new DisplayTuneSink(dir, "Amp", null, () => [lib], null, DataSourceLibraryViewModel.OptimizingChip);
            Directory.CreateDirectory(Path.GetDirectoryName(sink.ResultsPath)!);
            DataSetExporter.Export(FileData(), sink.ResultsPath, ExportFormat.Npy);
            await lib.LoadFileAsync(sink.ResultsPath);
            var stampBefore = File.GetLastWriteTimeUtc(sink.ResultsPath);

            var setup = new TuningSetup
            {
                Variables = [OptimizerPanelFixture.Var("L1.L", "1 nH", "50 nH"), OptimizerPanelFixture.Var("C1.C", "0.1 pF", "10 pF")],
                Goals     = [new OptimizationGoal { Name = "Match", Expression = "mag(SP1.S(1,1))", Analysis = "SP1",
                                                    Type = GoalType.Le, Limit = "0.0001" }],
                Optimizer = new OptimizerSettings { Algorithm = "simplex", MaxIterations = 60 },
            };
            var f = new OptimizerPanelFixture(OptCircuits.LSection("simplex"), setup,
            [
                TuningFixture.Part("L1", SymbolKind.Inductor, 0, ("L", "5", "nH")),
                TuningFixture.Part("C1", SymbolKind.Capacitor, 1000, ("C", "0.5", "pF")),
            ]);
            f.Panel.DisplayFor = _ => sink;

            f.Panel.RunCommand.Execute(null);
            await f.Panel.RunTask!.WaitAsync(TimeSpan.FromSeconds(30));

            long published = f.Panel.BestPublishes + 1;                 // every improvement, and the finish
            Assert.True(f.Panel.BestPublishes >= 3, $"only {f.Panel.BestPublishes} improvements were published");
            // The display drew once, one frame waited and the rest were skipped — and the one that waited is
            // the FINISH, which the commit then shows rather than drops (it used to leave the display on an
            // earlier, worse point while the file held the best one).
            Assert.Equal(2, lib.PublishedRedraws);
            Assert.Equal(published - 2, lib.SkippedFrames);
            Assert.Equal(1, f.Panel.FinalEvaluations);                  // ONE full re-run of the best point
            Assert.Null(lib.PublishedChip);                             // written: the file is that version now
            Assert.True(File.GetLastWriteTimeUtc(sink.ResultsPath) >= stampBefore);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task APartialPublish_KeepsTheOtherAnalyses_AsStale()
    {
        var dir = TuningDisplayFixture.TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Amp.npy");
            DataSetExporter.Export(FileData(), path, ExportFormat.Npy);
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);

            var goalsOnly = new DataSet();
            goalsOnly.AddToGroup("SP1", "x", new DataCube([new Axis("freq", [1e9], "Hz")], [5.0]));
            lib.Publish(path, goalsOnly, DataSourceLibraryViewModel.OptimizingChip, partial: true);

            var shown = lib.Entries.Single().Data!;
            Assert.Equal(5.0, shown["SP1.x"].RealValues[0]);           // the new data
            Assert.Equal(1.0, shown["HB1.Pout"].RealValues[0]);        // the old, kept …
            Assert.Equal(["HB1"], lib.StaleGroupsFor(path));            // … and marked stale

            lib.Publish(path, FileData(), DataSourceLibraryViewModel.OptimizingChip, partial: false);
            Assert.Empty(lib.StaleGroupsFor(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
