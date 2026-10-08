using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Optimization;
using CircuitRF.Ui.Views.Tuning;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Tuning and Optimization chapters' figures (<c>brief-tuneopt-12-docs-and-example.md</c> R-to12-2).
/// </summary>
/// <remarks>
/// <b>Both panels are shown on the example a reader can open</b> — <c>examples/Optimization/</c>'s
/// <c>StabilityAndGain</c> cell, read from disk by the ordinary <c>SchematicPersistence</c> reader —
/// <see cref="DocSmithFixtures"/>' rule, for its reason: the chapter quotes the values that example
/// reaches, and a change that moves either one moves the picture too.
///
/// <para><b>The Optimizer figure is a finished run, run here.</b> The panel's background starter is
/// replaced by one that runs inline, so the capture waits for nothing and the run is the one the
/// panel would make: the example's own algorithm and seed, which fix every evaluation it makes. The
/// status line after a finish reads iterations and evaluations only — no elapsed time — so the figure
/// draws nothing that measures the machine.</para>
/// </remarks>
public static class DocTuningFixtures
{
    private const string Example = "Optimization";
    private const string Cell    = "StabilityAndGain";

    /// <summary>The Tuning panel on the amplifier: two resistors and the magnitude and phase of the
    /// complex source impedance, each with its slider at the schematic's value.</summary>
    public static FigureScene TuningPanel()
    {
        var tool = new TuningTool();
        tool.Panel.SetActiveSchematic(Session(), $"{Cell}.csch");
        return new FigureScene(new TuningToolView { DataContext = tool });
    }

    /// <summary>The Optimizer panel after the amplifier's run: one resistor railed at the top of a range
    /// drawn too narrow on purpose, and both goals unmet because of it.</summary>
    public static FigureScene OptimizerPanel()
    {
        var tool  = new OptimizerTool();
        var panel = tool.Panel;
        panel.PrepareCircuit  = Prepare;
        panel.StartBackground = work => { work(); return Task.CompletedTask; };
        panel.SetActiveSchematic(Session(), $"{Cell}.csch");
        panel.RunCommand.Execute(null);
        if (panel.Result is null)
            throw new InvalidOperationException(
                $"The {Example} example's {Cell} run did not finish: {panel.StatusText}");
        return new FigureScene(new OptimizerToolView { DataContext = tool });
    }

    private static string CschPath()
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException(
                $"No examples/ tree beside the generator or above it, so the {Example} figures have no document.");
        return Path.Combine(root, Example, Cell, "schematic", Cell + ".csch");
    }

    private static SchematicViewModel Session()
    {
        string path = CschPath();
        var (model, _, _) = SchematicPersistence.LoadFromFile(path);
        model.SchematicDirectory = Path.GetDirectoryName(path);
        return new SchematicViewModel(model);
    }

    /// <summary>The extraction every run verb reads, read back in memory — what the workspace's own
    /// preparation does for a run.</summary>
    private static PreparedCircuit Prepare(SchematicViewModel tuned)
        => PreparedCircuit.FromText(SchematicCircuit.CnlTextOf(tuned.EditModel, Cell), null, null);
}
