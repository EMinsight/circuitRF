// ================================================================
//  TuningRowViewModel.cs  —  one tune-enabled entry as a slider row
//  (brief-tuneopt-4 R-to4-4)
//
//  The VALUE is session state and lives here; the RANGE, scale and
//  step are the entry's and every change to them is handed back to
//  the panel as a document edit (R-to4-10). Values are in the
//  tunable's own unit — a pF row's 1.8 is 1.8 pF.
//
//  A row for one PART of a complex value (overview D18) does not own
//  its value: the panel holds the whole complex value, and every part
//  row of it shows its own view of that one number. Moving the row
//  asks the panel, which moves the value along this part's path and
//  stops it at the edge of the ranges of all its parts.
// ================================================================

using System;
using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tuning;

public sealed partial class TuningRowViewModel : ObservableObject
{
    private readonly TuningPanelViewModel _panel;
    private bool _syncing;

    public TuningRowViewModel(TuningPanelViewModel panel, string key)
    {
        _panel = panel;
        Key    = key;
    }

    /// <summary>The entry's key (overview D3).</summary>
    public string Key { get; }

    /// <summary>The tunable it resolves to; null when the key names nothing in the design now.</summary>
    public Tunable? Tunable { get; private set; }

    /// <summary><c>DUT · ×2</c> for a sub-cell row; empty at the top.</summary>
    public string Location => Tunable is { Cell: not null } t ? t.Location : "";

    public bool HasLocation => Location.Length > 0;

    public string Unit => Tunable?.Unit ?? "";

    public bool IsInteger { get; private set; }

    /// <summary>Why the slider cannot move (a parametric sweep sweeps it, or the key names nothing);
    /// null when it can.</summary>
    public string? DisabledReason { get; private set; }

    public bool IsDisabled => DisabledReason is not null;

    public bool IsEnabled => DisabledReason is null;

    /// <summary>Why Push skips this row; null when it can write it.</summary>
    public string? ReadOnlyReason => Tunable?.ReadOnlyReason is { } r ? $"Push unavailable: {r}" : null;

    public bool IsReadOnly => ReadOnlyReason is not null;

    public double Min { get; private set; }
    public double Max { get; private set; }
    public TuneScale Scale { get; private set; }
    public double? Step { get; private set; }

    /// <summary>The range is the zero-value guess (overview D4).</summary>
    public bool RangeGuessed { get; private set; }

    /// <summary>"log" or "lin" — what the slider is actually doing.</summary>
    public string ScaleLabel => TunableValue.Effective(Scale, Min, Max) == TuneScale.Log ? "log" : "lin";

    public bool IsLog => TunableValue.Effective(Scale, Min, Max) == TuneScale.Log;

    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _maxText = "";
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private bool   _isEditingMin;
    [ObservableProperty] private bool   _isEditingMax;
    [ObservableProperty] private bool   _isEditingStep;

    /// <summary>The sliders are ahead of the display for this row (R-to4-6).</summary>
    [ObservableProperty] private bool _isLagging;

    private double _value;

    /// <summary>The tuned value, in <see cref="Unit"/>.</summary>
    public double Value => _value;

    /// <summary>What the schematic holds, in <see cref="Unit"/>.</summary>
    public double SchematicValue => Tunable?.Value ?? _value;

    /// <summary>A part row: the whole complex value the session holds, or null while it is the
    /// schematic's.</summary>
    internal Complex? WholeValue { get; private set; }

    /// <summary>The tuned value differs from the schematic's — for a part row, the WHOLE value does,
    /// since moving the real part changes what the magnitude row's parameter holds too.</summary>
    public bool DiffersFromSchematic => Tunable is { Part: not null } p
        ? WholeValue is { } z && z != p.Whole
        : Tunable is not null && _value != Tunable.Value;

    /// <summary>The middle of the range — which turn a phase row reads its angle in.</summary>
    private double Centre => (Min + Max) / 2;

    /// <summary>The value as the session and Push spell it, to the panel's digits: <c>1.8 pF</c>.</summary>
    public string ValueText => Spell(_value, Unit);

    private string Spell(double v, string unit) => TuningDigits.Format(v, unit, _panel.Digits);

    /// <summary>The number box. Typing changes nothing until Enter (<see cref="CommitValueText"/>).</summary>
    [ObservableProperty] private string _valueBoxText = "";

    /// <summary>The slider, 0..1.</summary>
    public double Position
    {
        get => TuningSliderMapping.ToPosition(_value, Min, Max, Scale);
        set
        {
            if (_syncing || IsDisabled) return;
            double v = TuningSliderMapping.Snap(
                TuningSliderMapping.FromPosition(value, Min, Max, Scale), Min, Max, IsInteger, Step);
            SetValue(v, final: false);
        }
    }

    /// <summary>The slider was released: the value it rests on is the final one (D7).</summary>
    public void Release() { if (!IsDisabled) _panel.OnRowValueChanged(this, final: true); }

    // ---- Refresh from the entry and the catalog --------------------------------

    /// <summary>Reads the entry's range and the tunable; <paramref name="value"/> is the tuned value to
    /// show, or null to start from the schematic's.</summary>
    internal void Bind(TunableEntry entry, Tunable? tunable, double? value, Complex? whole = null)
    {
        Tunable   = tunable;
        string u  = tunable?.Unit ?? "";
        IsInteger = tunable?.IsInteger == true || entry.Discrete == TuneDiscrete.Integer;
        DisabledReason = tunable is null ? "not in the design" : tunable.DisabledReason;

        Min = InUnit(entry.Min, u) ?? InUnit(tunable?.DefaultMin, u) ?? 0;
        Max = InUnit(entry.Max, u) ?? InUnit(tunable?.DefaultMax, u) ?? 1;
        if (!(Max > Min)) Max = Min + 1;
        // An angle's range is not a ratio: a phase reads Auto as linear (overview D18).
        Scale        = entry.Scale == TuneScale.Auto && tunable?.Part == ComplexPart.Phase ? TuneScale.Lin : entry.Scale;
        Step         = InUnit(entry.Step, u) is > 0 and var s ? s : null;
        RangeGuessed = tunable?.RangeGuessed == true && entry.Min == tunable.DefaultMin && entry.Max == tunable.DefaultMax;

        WholeValue = tunable?.Part is not null ? whole : null;
        _value = tunable?.Part is { } part
            ? ComplexValue.Get(whole ?? tunable.Whole, part, phaseNear: Centre)
            : value ?? tunable?.Value ?? Min;
        _syncing = true;
        MinText      = FormatValue(Min, "");
        MaxText      = FormatValue(Max, "");
        StepText     = Step is { } st ? FormatValue(st, "") : "";
        ValueBoxText = Spell(_value, "");
        _syncing = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Returns to the schematic's value without sending anything (Revert, a Push's echo).</summary>
    internal void ResetToSchematic()
    {
        if (Tunable is null) return;
        WholeValue = null;
        _value = Tunable.Part is { } part ? ComplexValue.Get(Tunable.Whole, part, phaseNear: Centre) : Tunable.Value;
        RaiseValue();
    }

    /// <summary>A part row: shows its view of the whole value <paramref name="z"/>, sending nothing.</summary>
    internal void ShowWhole(Complex z)
    {
        if (Tunable?.Part is not { } part) return;
        WholeValue = z;
        _value = ComplexValue.Get(z, part, phaseNear: Centre);
        RaiseValue();
    }

    private void SetValue(double v, bool final)
    {
        if (Tunable?.Part is not null)
        {
            _panel.OnPartMoved(this, v, final);
            return;
        }
        if (v == _value && !final) return;
        _value = v;
        RaiseValue();
        _panel.OnRowValueChanged(this, final);
    }

    private void RaiseValue()
    {
        _syncing = true;
        ValueBoxText = Spell(_value, "");
        _syncing = false;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(DiffersFromSchematic));
    }

    // ---- The number box and the keyboard (overview §3) -----------------------

    /// <summary>Enter in the number box: a plain number is in the row's unit; a number with a unit is
    /// converted (<c>2000 fF</c> on a pF row is 2). Text that is not a number puts the box back.</summary>
    public void CommitValueText()
    {
        if (IsDisabled) return;
        if (ParseInUnit(ValueBoxText, Unit) is { } v)
            SetValue(IsInteger ? Math.Round(v) : v, final: true);
        else
            RevertValueText();
    }

    /// <summary>Esc in the number box.</summary>
    public void RevertValueText() => ValueBoxText = Spell(_value, "");

    /// <summary>An arrow, Shift+arrow or Page key on the focused slider.</summary>
    public void Nudge(int direction, TuningNudge size)
    {
        if (IsDisabled) return;
        SetValue(TuningSliderMapping.Nudge(_value, direction, size, Min, Max, Scale, IsInteger, Step), final: true);
    }

    // ---- Range, scale, step — document edits (R-to4-10) ---------------------

    public void CommitMinText()
    {
        IsEditingMin = false;
        if (ParseInUnit(MinText, Unit) is { } v && v < Max) _panel.EditEntry(this, e => e.Min = FormatValue(v, Unit), "Set tuning range");
        else MinText = FormatValue(Min, "");
    }

    public void CommitMaxText()
    {
        IsEditingMax = false;
        if (ParseInUnit(MaxText, Unit) is { } v && v > Min) _panel.EditEntry(this, e => e.Max = FormatValue(v, Unit), "Set tuning range");
        else MaxText = FormatValue(Max, "");
    }

    public void CommitStepText()
    {
        IsEditingStep = false;
        if (string.IsNullOrWhiteSpace(StepText)) { _panel.EditEntry(this, e => e.Step = null, "Clear tuning step"); return; }
        if (ParseInUnit(StepText, Unit) is { } v && v > 0) _panel.EditEntry(this, e => e.Step = FormatValue(v, Unit), "Set tuning step");
        else StepText = Step is { } st ? FormatValue(st, "") : "";
    }

    /// <summary>Esc in a min, max or step box: the box closes and shows the entry's value again.</summary>
    public void CancelRangeEdit()
    {
        IsEditingMin = IsEditingMax = IsEditingStep = false;
        MinText  = FormatValue(Min, "");
        MaxText  = FormatValue(Max, "");
        StepText = Step is { } st ? FormatValue(st, "") : "";
    }

    [RelayCommand] private void EditMin()  => IsEditingMin = true;
    [RelayCommand] private void EditMax()  => IsEditingMax = true;
    [RelayCommand] private void EditStep() => IsEditingStep = true;

    [RelayCommand] private void SetLinear() => _panel.EditEntry(this, e => e.Scale = TuneScale.Lin, "Tune on a linear scale");
    [RelayCommand] private void SetLog()    => _panel.EditEntry(this, e => e.Scale = TuneScale.Log, "Tune on a log scale");

    /// <summary>Keeps the range's width — its ratio on a log row, its span on a linear one — and
    /// centres it on the current value.</summary>
    [RelayCommand]
    private void RecentreRange()
    {
        double lo, hi;
        if (IsLog && _value > 0)
        {
            double half = Math.Sqrt(Max / Min);
            (lo, hi) = (_value / half, _value * half);
        }
        else
        {
            double half = (Max - Min) / 2;
            (lo, hi) = (_value - half, _value + half);
        }
        _panel.EditEntry(this, e => { e.Min = FormatValue(lo, Unit); e.Max = FormatValue(hi, Unit); }, "Re-centre tuning range");
    }

    [RelayCommand]
    private void ResetRange()
    {
        if (Tunable is not { } t) return;
        _panel.EditEntry(this, e => { e.Min = t.DefaultMin; e.Max = t.DefaultMax; }, "Reset tuning range");
    }

    [RelayCommand] private void Remove() => _panel.SetTuned(Key, on: false);

    [RelayCommand] private void Reveal() => _panel.RevealRow(this);

    // ---- Text ---------------------------------------------------------------

    /// <summary>A value as rows, the session and Push spell it: six significant figures, then the unit.</summary>
    public static string FormatValue(double v, string unit)
    {
        string n = v.ToString("G6", CultureInfo.InvariantCulture);
        return unit.Length == 0 ? n : $"{n} {unit}";
    }

    /// <summary>Value text in <paramref name="unit"/>: a bare number is already in it.</summary>
    internal static double? ParseInUnit(string? text, string unit) => TunableValue.InUnit(text, unit);

    private static double? InUnit(string? text, string unit) => ParseInUnit(text, unit);
}
