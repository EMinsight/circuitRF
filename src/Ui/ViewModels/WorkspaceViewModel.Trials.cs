using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Views.Dialogs;
using RfCore.Data;

namespace CircuitRF.Ui.ViewModels;

/// <summary>A selected trial's context menu in a Data Display (brief-yield-9 R-ya9-6): the two actions that change the
/// schematic the result was run from — its Tuning sliders, and its corners.</summary>
public partial class WorkspaceViewModel
{
    /// <summary>Send trial to Tuning: the Tuning panel, on the schematic the result came from, takes the trial's values
    /// exactly as the Optimizer's Send to Tuning hands its best point (TuningPanelViewModel.LoadValues).</summary>
    internal void SendTrialToTuning(IReadOnlyDictionary<string, string> values, string source, string label)
    {
        if (SchematicForYieldResult(source) is not { } sd)
        {
            Messages.Warning("Send trial to Tuning: open the schematic these results came from, then send the trial again.");
            return;
        }
        if (_factory.TuningTool?.Panel is not { } tuning) return;
        var bench = sd.NavFrames[0].Session;
        if (!ReferenceEquals(tuning.Tuned, bench)) tuning.SetActiveSchematic(bench, InstancesRootHeaderOf(sd));
        ShowToolPanelCore(DockPanelIds.Tuning);
        tuning.LoadValues(values, label);
    }

    /// <summary>Save as corner…: asks for a name and adds a statistical corner naming the trial to the schematic's
    /// tuning block — <see cref="TrialReplay.CornerOf"/>, what <c>yield --save-corner</c> writes — as one undo step.</summary>
    internal async Task SaveTrialAsCornerAsync(string source, int trial, DataSet result)
    {
        if (SchematicForYieldResult(source) is not { } sd)
        {
            Messages.Warning("Save as corner: open the schematic these results came from, then save the corner again.");
            return;
        }
        if (ResolveOwner(null) is not { } owner) return;
        var name = (await new InputNameDialog("Save as Corner", "Corner name:", $"Trial{trial}").ShowDialog<string?>(owner))?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var model = sd.ViewModel.EditModel;
        var setup = model.Tuning?.Clone() ?? new TuningSetup();
        if (setup.Corners.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Messages.Warning($"Save as corner: there is already a corner named '{name}'.");
            return;
        }
        setup.Corners.Add(TrialReplay.CornerOf(result, name, trial));
        sd.ViewModel.Execute(new SetTuningSetupCommand(model, setup, $"Save corner {name}"));
        Messages.Info($"Saved corner {name} (trial {trial}).");
    }

    /// <summary>The open schematic a Monte Carlo result at <paramref name="source"/> was run from:
    /// <c>&lt;name&gt;.yield.npy</c> is written beside <c>&lt;name&gt;</c>'s schematic.</summary>
    private SchematicDocument? SchematicForYieldResult(string source)
    {
        string file = Path.GetFileName(source);
        const string suffix = ".yield.npy";
        string key = file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? file[..^suffix.Length] : Path.GetFileNameWithoutExtension(file);
        return _openDocsByPath.Values.OfType<SchematicDocument>().Concat(_scratchDocs)
            .FirstOrDefault(sd => string.Equals(RunResultsWriter.SchematicKey(sd.FilePath, sd.Id), key, StringComparison.OrdinalIgnoreCase));
    }
}
