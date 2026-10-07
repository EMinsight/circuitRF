using System.Collections.Generic;

namespace CircuitRF.Ui.Commands;

/// <summary>
/// Any number of commands as ONE undo step: run in order, undone in reverse. The two-command
/// <see cref="CompositeCommand"/> generalised, for an action that edits an open-ended set of rows —
/// a tuning Push writes every value one document owns in one step (brief-tuneopt-4 R-to4-8).
/// </summary>
internal sealed class CommandBatch(string description, IReadOnlyList<IUiCommand> commands) : IUiCommand
{
    public string Description { get; } = description;

    public IReadOnlyList<IUiCommand> Commands { get; } = commands;

    public void Execute()
    {
        foreach (var c in Commands) c.Execute();
    }

    public void Undo()
    {
        for (int i = Commands.Count - 1; i >= 0; i--) Commands[i].Undo();
    }
}
