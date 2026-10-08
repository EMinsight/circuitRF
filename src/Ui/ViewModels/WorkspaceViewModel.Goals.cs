using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>"Add as goal…" from a Data Display trace (brief-tuneopt-9 R-to9-4).</summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// Receives the goal a trace pre-filled and adds it to the schematic whose run the trace is drawn
    /// from, as one undo step. This is the seam the Optimizer window's goal editor (TO-10) replaces:
    /// until that editor exists the goal goes straight into the document, and a goal the trace gave no
    /// limit (no visible marker) is added DISABLED, since an enabled goal with no limit is a check error.
    /// </summary>
    internal void AddGoalFromTrace(OptimizationGoal pre, string? sourcePath)
    {
        if (SchematicForResults(sourcePath) is not { } sd)
        {
            Messages.Warning("Add as goal: open the schematic these results came from, then add the goal again.");
            return;
        }

        var model = sd.ViewModel.EditModel;
        var goal  = pre.Clone();
        goal.Analysis ??= GoalTemplates.AnalysisReferencedBy(goal.Expression, model.Analyses, model.Measurements);
        var setup = model.Tuning?.Clone() ?? new TuningSetup();
        goal.Name = UniqueGoalName(goal.Name, setup.Goals);
        if (goal.Limit.Length == 0) goal.Enabled = false;
        setup.Goals.Add(goal);
        sd.ViewModel.Execute(new SetTuningSetupCommand(model, setup, $"Add goal {goal.Name}"));

        Messages.Info($"Added goal {goal.Name}: {goal.Expression}" +
                      (goal.Analysis is { } a ? $" · {a}" : "") +
                      (goal.Range is { } r ? $" · {r.Lo} to {r.Hi}" : "") +
                      (goal.Enabled ? $" · {goal.Type.ToString().ToLowerInvariant()} {goal.Limit}"
                                    : " · disabled until it has a limit"));
    }

    /// <summary>The open schematic whose run wrote <paramref name="sourcePath"/> — a results file is
    /// named by <see cref="RunResultsWriter.SchematicKey"/> — or null.</summary>
    private SchematicDocument? SchematicForResults(string? sourcePath)
    {
        if (sourcePath is null) return null;
        string key = Path.GetFileNameWithoutExtension(sourcePath);
        return _openDocsByPath.Values.OfType<SchematicDocument>().Concat(_scratchDocs)
            .FirstOrDefault(sd => string.Equals(RunResultsWriter.SchematicKey(sd.FilePath, sd.Id), key,
                                                StringComparison.OrdinalIgnoreCase));
    }

    private static string UniqueGoalName(string name, IEnumerable<OptimizationGoal> goals)
    {
        var taken = goals.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (int k = 2; ; k++)
            if (!taken.Contains($"{name}_{k}")) return $"{name}_{k}";
    }
}
