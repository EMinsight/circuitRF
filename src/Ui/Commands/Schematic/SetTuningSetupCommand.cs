using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Replaces a schematic's tuning block (brief-tuneopt-4 R-to4-10, overview D5): enabling a tunable,
/// its range, scale or step — each one undo step that marks the document dirty, the way an
/// analysis-card edit does. Both states are held as deep copies, so a later edit to the live block
/// cannot reach back into the history.
/// </summary>
internal sealed class SetTuningSetupCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly TuningSetup?       _old;
    private readonly TuningSetup?       _new;

    public string Description { get; }

    public SetTuningSetupCommand(SchematicEditModel model, TuningSetup? newSetup, string description)
    {
        _model      = model;
        _old        = model.Tuning?.Clone();
        _new        = newSetup is null || newSetup.IsEmpty ? null : newSetup.Clone();
        Description = description;
    }

    public void Execute() => Apply(_new);
    public void Undo()    => Apply(_old);

    private void Apply(TuningSetup? setup)
    {
        _model.Tuning = setup?.Clone();
        _model.NotifyChanged();
    }
}
