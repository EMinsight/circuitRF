using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// brief-artsch-11: Swap Line Type for one or several lines as ONE undo step. Each component is rewritten IN
/// PLACE from what <see cref="LineTypeSwap.Swap"/> returned — type, parameter rows, label offsets and what the
/// swap set aside — so its Id, its place in the component list (and so the netlist's order), the selection and
/// an open parameter editor all still point at it. <see cref="ChangeComponentTypeCommand"/> replaces the object
/// instead, which is right for a retype that renames, and wrong here.
/// </summary>
internal sealed class SwapLineTypeCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly List<(EditableComponent Target, State Before, State After)> _swaps = [];

    public string Description { get; }

    /// <param name="swaps">Each component in the model, with the swapped component <see cref="LineTypeSwap.Swap"/>
    /// returned for it (not in the model).</param>
    public SwapLineTypeCommand(SchematicEditModel model, IReadOnlyList<(EditableComponent Target, EditableComponent Swapped)> swaps)
    {
        _model = model;
        foreach (var (target, swapped) in swaps)
            _swaps.Add((target, State.Of(target), State.Of(swapped)));
        Description = swaps.Count == 1
            ? $"Swap {swaps[0].Target.InstanceName} to {ComponentTypeRegistry.DisplayName(swaps[0].Swapped.Symbol)}"
            : $"Swap {swaps.Count} lines to {ComponentTypeRegistry.DisplayName(swaps[0].Swapped.Symbol)}";
    }

    public void Execute()
    {
        foreach (var (target, _, after) in _swaps) after.ApplyTo(target);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (target, before, _) in _swaps) before.ApplyTo(target);
        _model.NotifyChanged();
    }

    /// <summary>Everything a swap changes, copied, so the two directions never share a mutable row.</summary>
    private sealed record State(
        SymbolKind Symbol,
        List<EditableParameter> Parameters,
        List<(double DX, double DY)> LabelOffsets,
        List<KeyValuePair<string, RememberedParameter>> Remembered)
    {
        public static State Of(EditableComponent c) =>
            new(c.Symbol, [.. c.Parameters.Select(p => p.Clone())], [.. c.LabelOffsets], [.. c.SwapRemembered]);

        public void ApplyTo(EditableComponent c)
        {
            c.Symbol = Symbol;
            c.Parameters.Clear();
            c.Parameters.AddRange(Parameters.Select(p => p.Clone()));
            c.LabelOffsets.Clear();
            c.LabelOffsets.AddRange(LabelOffsets);
            c.SwapRemembered.Clear();
            foreach (var (k, v) in Remembered) c.SwapRemembered[k] = v;
        }
    }
}
