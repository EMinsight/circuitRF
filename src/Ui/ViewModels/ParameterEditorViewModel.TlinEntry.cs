using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.ViewModels;

// TLIN's Electrical / Physical entry switch (brief-artsch-2 R-as2-3), in the style of MKLOPF's: one button
// naming the form it switches TO, converting rather than resetting. The arithmetic is
// TlinEntryConversion's (src/Design); this is the button and the undoable edit.
public sealed partial class ParameterEditorViewModel
{
    private IRelayCommand? _toggleTlinEntryCommand;

    public IRelayCommand ToggleTlinEntryCommand
        => _toggleTlinEntryCommand ??= new RelayCommand(ToggleTlinEntry, () => IsTlinTarget);

    /// <summary>True only for a placed TLIN — gates the switch in the view.</summary>
    public bool IsTlinTarget => _target?.Symbol == SymbolKind.Tline;

    /// <summary>True when the TLIN is stated by L/Eeff rather than E/F.</summary>
    public bool TlinUsesPhysicalEntry => _target is not null && TlinEntryConversion.IsPhysical(_target.Parameters);

    public string TlinEntryToggleLabel => TlinUsesPhysicalEntry ? "Use electrical length" : "Use physical length";

    /// <summary>What the last switch had to say (converted at 1 GHz, a value it could not read); empty
    /// otherwise, and cleared when the selection changes.</summary>
    [ObservableProperty] private string _tlinEntryNote = "";

    public bool HasTlinEntryNote => TlinEntryNote.Length > 0;

    partial void OnTlinEntryNoteChanged(string value) => OnPropertyChanged(nameof(HasTlinEntryNote));

    private void ToggleTlinEntry()
    {
        if (_target is null || _schematicVm is null || !IsTlinTarget) return;

        var tech = SchematicTechnology.Of(_schematicVm.EditModel);
        var (parameters, note) = TlinEntryConversion.Switch(_target.Parameters, MicrostripSubstrateInjection.LengthUnitFor(tech));
        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, parameters));
        TlinEntryNote = note;
    }

    private void NotifyTlinState(bool targetChanged)
    {
        if (targetChanged) TlinEntryNote = "";
        OnPropertyChanged(nameof(IsTlinTarget));
        OnPropertyChanged(nameof(TlinUsesPhysicalEntry));
        OnPropertyChanged(nameof(TlinEntryToggleLabel));
        ToggleTlinEntryCommand.NotifyCanExecuteChanged();
    }
}
