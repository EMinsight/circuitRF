using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// brief-artsch-8 R-as8-5, overview D5: the parameter editor's "Models existing artwork" check box. Clearing it hands
/// the component back to ordinary layout sync and changes nothing else — the anchor and what was measured stay, so
/// Show in Artwork still finds the copper and checking the box again is the exact inverse. One undo step.
/// </summary>
internal sealed class SetFromArtworkCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _comp;
    private readonly bool               _newValue;
    private readonly bool               _oldValue;

    public string Description =>
        $"{(_newValue ? "Mark" : "Unmark")} {_comp.InstanceName} as modelling existing artwork";

    public SetFromArtworkCommand(SchematicEditModel model, EditableComponent comp, bool newValue)
    {
        _model    = model;
        _comp     = comp;
        _newValue = newValue;
        _oldValue = comp.FromArtwork;
    }

    public void Execute() => Apply(_newValue);
    public void Undo()    => Apply(_oldValue);

    private void Apply(bool value)
    {
        _comp.FromArtwork = value;
        _model.NotifyChanged();
    }
}
