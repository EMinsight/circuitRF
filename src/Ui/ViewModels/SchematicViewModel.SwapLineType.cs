using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief-artsch-11 R-as11-5: Swap Line Type ▸ MLIN / CPWG / SLIN / TLIN, from the context menu and from the
/// parameter editor's type combo. Every swap is <see cref="LineTypeSwap.Swap"/>'s; this file only gathers the
/// targets, asks for a CPWG gap a line has no other source for, lands the swaps as ONE undo step and says what
/// happened — a line that refuses is listed and the rest still swap.
/// </summary>
public sealed partial class SchematicViewModel
{
    /// <summary>What a swap of several lines did.</summary>
    public sealed record LineSwapOutcome(IReadOnlyList<string> Swapped, IReadOnlyList<string> Refused);

    /// <summary>
    /// Asks the user for a CPWG gap, in metres, for the named lines — the ones with neither a measured nor an
    /// earlier gap. Null when cancelled, or when there is nobody to ask (a test, no window); those lines are
    /// then refused, naming G. Installed by the view.
    /// </summary>
    public Func<IReadOnlyList<string>, Task<double?>>? AskCpwgGap { get; set; }

    /// <summary>The lines a swap acts on from <paramref name="clickedId"/>: every selected line when the clicked
    /// component is part of the selection, else the clicked one alone (when it is a line).</summary>
    public IReadOnlyList<EditableComponent> LineSwapTargets(string? clickedId)
    {
        var clicked = clickedId is null ? null : EditModel.FindComponent(clickedId);
        if (!LineTypeSwap.IsSwappable(clicked)) return [];
        if (!Selection.Ids.Contains(clicked!.Id)) return [clicked];
        return [.. Selection.Ids.Select(EditModel.FindComponent).OfType<EditableComponent>().Where(LineTypeSwap.IsSwappable)];
    }

    /// <summary>Swaps <paramref name="targets"/> to <paramref name="toType"/>, asking for a gap first when a CPWG
    /// needs one (<see cref="AskCpwgGap"/>).</summary>
    public async Task<LineSwapOutcome> SwapLineTypeAsync(IReadOnlyList<EditableComponent> targets, SymbolKind toType)
    {
        var ctx = SwapContext();
        double? gap = null;
        if (toType == SymbolKind.Cpwg && AskCpwgGap is { } ask)
        {
            var needing = targets.Where(t => t.Symbol != toType && LineTypeSwap.Swap(t, toType, ctx).NeedsGap)
                                 .Select(t => t.InstanceName).ToList();
            if (needing.Count > 0) gap = await ask(needing);
        }
        return SwapLineType(targets, toType, gap);
    }

    /// <summary>
    /// Swaps <paramref name="targets"/> to <paramref name="toType"/> in ONE undo step. A line that refuses is
    /// listed on Messages and left as it was; the rest still swap. <paramref name="gapMeters"/> is the gap the
    /// user was asked for, used only by a line with no measured or earlier one.
    /// </summary>
    public LineSwapOutcome SwapLineType(IReadOnlyList<EditableComponent> targets, SymbolKind toType, double? gapMeters = null)
    {
        var ctx = SwapContext() with { GapMeters = gapMeters };
        var swaps = new List<(EditableComponent Target, EditableComponent Swapped)>();
        var refused = new List<string>();
        var notes = new List<string>();
        foreach (var target in targets.Where(t => t.Symbol != toType))
        {
            var r = LineTypeSwap.Swap(target, toType, ctx);
            if (r.Component is { } swapped)
            {
                swaps.Add((target, swapped));
                notes.AddRange(r.Notes);
            }
            else refused.Add(r.Refusal ?? $"'{target.InstanceName}' was not swapped.");
        }

        if (swaps.Count > 0)
        {
            Execute(new SwapLineTypeCommand(EditModel, swaps));
            notes.AddRange(LineTypeSwap.DiscontinuityNotes(EditModel, swaps.Select(s => s.Target.InstanceName), toType));
            string names = string.Join(", ", swaps.Select(s => s.Target.InstanceName));
            _messageSink?.Info($"Swapped {names} to {LineTypeSwap.MenuName(toType)}.");
        }
        foreach (var note in notes) _messageSink?.Info(note);
        foreach (var why in refused) _messageSink?.Warning($"Not swapped: {why}");
        return new LineSwapOutcome([.. swaps.Select(s => s.Target.InstanceName)], refused);
    }

    private LineTypeSwapContext SwapContext()
        => new(SchematicTechnology.Of(EditModel), LineTypeSwap.BenchTopFrequencyHz(EditModel));
}
