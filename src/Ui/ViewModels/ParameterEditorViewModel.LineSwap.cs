namespace CircuitRF.Ui.ViewModels;

// brief-artsch-11 R-as11-5: for a line component the type field is a combo among MLIN, CPWG, SLIN and TLIN, and
// choosing one is the context menu's Swap Line Type for this one component — the same SchematicViewModel call,
// so the two cannot swap differently. The rows rebuild on the model change the swap raises; the Z0 readout below
// them then shows what the new type makes of the same geometry.

public partial class ParameterEditorViewModel
{
    /// <summary>True for a line component: the type field is then the line-type combo.</summary>
    public bool IsLineSwapTarget => LineTypeSwap.IsSwappable(_target);

    /// <summary>The combo's rows, in <see cref="LineTypeSwap.LineKinds"/> order.</summary>
    public static IReadOnlyList<string> LineTypeOptions { get; } = [.. LineTypeSwap.LineKinds.Select(LineTypeSwap.MenuName)];

    /// <summary>The selected row: the target's own type. Setting another one swaps it.</summary>
    public int LineTypeIndex
    {
        get => _target is null ? -1 : LineTypeSwap.LineKinds.ToList().IndexOf(_target.Symbol);
        set
        {
            if (_target is null || _schematicVm is null || !IsLineSwapTarget) return;
            if (value < 0 || value >= LineTypeSwap.LineKinds.Count || LineTypeSwap.LineKinds[value] == _target.Symbol) return;
            _ = SwapLineTypeAsync(LineTypeSwap.LineKinds[value]);
        }
    }

    private async Task SwapLineTypeAsync(SymbolKind toType)
    {
        if (_target is null || _schematicVm is null) return;
        await _schematicVm.SwapLineTypeAsync([_target], toType);
        // A refused swap leaves the type as it was — and the combo, which already moved, must say so.
        OnPropertyChanged(nameof(LineTypeIndex));
    }

    private void NotifyLineSwapState()
    {
        OnPropertyChanged(nameof(IsLineSwapTarget));
        OnPropertyChanged(nameof(LineTypeIndex));
    }
}
