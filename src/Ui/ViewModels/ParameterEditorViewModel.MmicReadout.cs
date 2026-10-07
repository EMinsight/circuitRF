using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.ViewModels;

// ──────────────────────────────────────────────────────────────────────────────
//  What a TFR's resistance, a MIMCAP's capacitance and a spiral's inductance come out to — SHOWN, not
//  entered.
//
//  Owner request (2026-10-07): the geometry is the input, the value is what a designer reads. Computed
//  through MmicPassiveInjection.Readout — the injection a run makes and the model it builds — so the
//  number here is the number simulated. Every one is a closed form (microseconds), so it is computed
//  on the UI thread on every refresh. A spiral's is the modified Wheeler ESTIMATE, labelled as one, and
//  the note under it says what it leaves out. The layout's Inspector and Component Properties show the
//  same readout as a computed "R"/"C"/"L" row (LayoutShapePropertiesViewModel).
// ──────────────────────────────────────────────────────────────────────────────

public partial class ParameterEditorViewModel
{
    /// <summary>True for a TFR, a MIMCAP or a spiral.</summary>
    public bool IsMmicReadoutTarget => _target is not null && MmicPassiveInjection.HasReadout(_target.Symbol);

    /// <summary>"R", "C" or "L".</summary>
    public string MmicReadoutLabel => _target is null ? "" : MmicPassiveInjection.ReadoutName(_target.Symbol);

    /// <summary>The estimated value, or empty.</summary>
    public string MmicReadoutText { get; private set; } = "";

    /// <summary>Why no value is shown, or which process numbers it used. Empty when there is nothing to say.</summary>
    public string MmicReadoutNote { get; private set; } = "";

    public bool HasMmicReadoutNote => MmicReadoutNote.Length > 0;

    /// <summary>A spiral's series resistance, at DC and with the skin effect — this panel only; the
    /// layout's rows show L alone. Empty for every other part.</summary>
    public string MmicResistanceText { get; private set; } = "";

    public bool HasMmicResistance => MmicResistanceText.Length > 0;

    private void RefreshMmicReadout()
    {
        string text = "", note = "", resistance = "";
        if (IsMmicReadoutTarget)
        {
            var tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(_schematicVm?.EditModel.SchematicDirectory);
            text = MmicPassiveInjection.Readout(tech, _target!.Symbol, _target.Parameters, out string? n) ?? "";
            note = n ?? "";
            if (text.Length > 0 && MmicPassiveInjection.GeometryOf(_target.Parameters) is { } geometry)
                resistance = MmicPassiveInjection.ResistanceReadout(tech, _target.Symbol, geometry) ?? "";
        }
        MmicResistanceText = resistance;
        MmicReadoutText = text;
        MmicReadoutNote = note;
        OnPropertyChanged(nameof(IsMmicReadoutTarget));
        OnPropertyChanged(nameof(MmicReadoutLabel));
        OnPropertyChanged(nameof(MmicReadoutText));
        OnPropertyChanged(nameof(MmicReadoutNote));
        OnPropertyChanged(nameof(HasMmicReadoutNote));
        OnPropertyChanged(nameof(MmicResistanceText));
        OnPropertyChanged(nameof(HasMmicResistance));
    }
}
