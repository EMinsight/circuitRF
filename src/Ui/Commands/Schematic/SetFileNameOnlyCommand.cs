using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Toggles <see cref="EditableComponent.ShowFileNameOnly"/> — the <c>File</c> label reads the file
/// name alone instead of its stored path (GitHub issue #31).
/// Undoable; fires NotifyChanged so the render snapshot refreshes.
/// </summary>
internal sealed class SetFileNameOnlyCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _comp;
    private readonly bool               _newValue;
    private readonly bool               _oldValue;

    public string Description =>
        $"{(_newValue ? "Show file name only" : "Show full file path")} on {_comp.InstanceName}";

    public SetFileNameOnlyCommand(SchematicEditModel model, EditableComponent comp, bool newValue)
    {
        _model    = model;
        _comp     = comp;
        _newValue = newValue;
        _oldValue = comp.ShowFileNameOnly;
    }

    public void Execute() => Apply(_newValue);
    public void Undo()    => Apply(_oldValue);

    private void Apply(bool value)
    {
        _comp.ShowFileNameOnly = value;
        _model.NotifyChanged();
    }
}
