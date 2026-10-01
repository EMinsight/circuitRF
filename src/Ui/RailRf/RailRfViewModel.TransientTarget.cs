using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

// Designer feedback round 11 — the Target card's "derive from the load" row.
//
// The model has carried a transient target (ΔI, ΔV, rise time → flat Z = ΔV/ΔI, band top 0.35/t_rise)
// since railRF's first brief, and the .crail persists it, but nothing in the window could state one: the
// card took a flat Z in milliohms and nothing else. This row is the PDN budget's usual first step —
// ripple % of the rail voltage over the load's current step — and its Set button stores the result as
// the transient target the model already has.
//
// The four boxes are a FORM, not the target. They open on the source row's voltage, 5 % and the loads'
// current (or, where a transient target is stored, on its own ΔV and ΔI), and Set computes ΔV once and
// stores ΔV and ΔI as numbers. Editing a source or load row later does NOT move a stored target: the
// document holds ΔV and ΔI, not the voltage and percentage they came from, and a target that silently
// followed a row edit would be a target nobody stated. The readout below the row always shows what is
// stored, with its arithmetic.
public sealed partial class RailRfViewModel
{
    /// <summary>The ripple percentage a fresh form opens on.</summary>
    public const double DefaultRipplePercent = 5.0;

    private string? _rippleVoltsText, _ripplePercentText, _stepCurrentText, _riseTimeText;
    private string _transientProblem = "";

    /// <summary>The rail voltage the ripple is a percentage of. Opens on the source row's.</summary>
    public string RippleVoltsEntry
    {
        get => _rippleVoltsText ?? (SelectedRail is { } r && RailTransientSpec.RailVoltageOf(r) is { } v
                                        ? RailValueFormat.FormatWithUnit(v, RailQuantity.Voltage) : "");
        set { _rippleVoltsText = value; OnPropertyChanged(); }
    }

    /// <summary>The ripple allowed, as a percentage of <see cref="RippleVoltsEntry"/>. Opens on 5 %, or on
    /// the stored target's own ΔV over the source voltage.</summary>
    public string RipplePercentEntry
    {
        get
        {
            if (_ripplePercentText is { } typed) return typed;
            double pct = StoredTransient is { } t && SelectedRail is { } r && RailTransientSpec.RailVoltageOf(r) is { } v
                ? t.DeltaVVolts / v * 100.0 : DefaultRipplePercent;
            return RailValueFormat.Significant(pct, 4) + " %";
        }
        set { _ripplePercentText = value; OnPropertyChanged(); }
    }

    /// <summary>ΔI, the load step. Opens on the stored target's, else on the loads' peak (or DC) current.</summary>
    public string StepCurrentEntry
    {
        get => _stepCurrentText ?? ((StoredTransient?.DeltaIAmps ?? (SelectedRail is { } r ? RailTransientSpec.LoadStepOf(r) : null)) is { } a
                                        ? RailValueFormat.FormatWithUnit(a, RailQuantity.Current) : "");
        set { _stepCurrentText = value; OnPropertyChanged(); }
    }

    /// <summary>The load's 10–90 % rise time, optional. Empty leaves the rail's own band in force.</summary>
    public string RiseTimeEntry
    {
        get => _riseTimeText ?? (StoredTransient?.RiseTimeSeconds is { } tr ? FormatSeconds(tr) : "");
        set { _riseTimeText = value; OnPropertyChanged(); }
    }

    /// <summary>The stored target with its arithmetic, or empty when the rail's target is not a transient one.</summary>
    public string TransientTargetReadout => StoredTransient?.Describe() ?? "";

    /// <summary>Why Set did not store a target, or empty.</summary>
    public string TransientTargetProblem
    {
        get => _transientProblem;
        private set => SetProperty(ref _transientProblem, value);
    }

    private RailTransientSpec? StoredTransient => SelectedRail?.ImpedanceTarget?.Transient;

    /// <summary>
    /// Store the form as the rail's transient target: ΔV = ripple % × V, ΔI as typed, the rise time if one
    /// is given. It replaces a flat Z target, as typing a Z replaces this one — a rail has one
    /// frequency-domain target (<see cref="RailTarget"/>).
    /// </summary>
    [RelayCommand]
    private void SetTransientTarget()
    {
        if (SelectedRail is not { } rail) return;

        if (!RailValueFormat.TryParse(RippleVoltsEntry, RailQuantity.Voltage, out double volts) || !(volts > 0))
        { TransientTargetProblem = "State the rail voltage the ripple is a percentage of — 3.3 V, for instance."; return; }

        string pctText = RipplePercentEntry.Trim().TrimEnd('%').Trim();
        if (!NumericText.TryParseDouble(pctText, out double pct) || !(pct > 0))
        { TransientTargetProblem = "State the ripple allowed as a positive percentage — 5 %, for instance."; return; }

        if (!RailValueFormat.TryParse(StepCurrentEntry, RailQuantity.Current, out double amps) || !(amps > 0))
        { TransientTargetProblem = "State the load's current step ΔI — 35 mA, for instance. No load row draws a current to start from."; return; }

        double? rise = null;
        if (!string.IsNullOrWhiteSpace(RiseTimeEntry))
        {
            if (!RailTransientSpec.TryParseSeconds(RiseTimeEntry, out double s) || !(s > 0))
            { TransientTargetProblem = "State the rise time with its unit — 10 ns, for instance — or leave it empty."; return; }
            rise = s;
        }

        var spec = RailTransientSpec.FromRipple(volts, pct, amps, rise);
        rail.ImpedanceTarget = RailTarget.OfTransient(spec.DeltaIAmps, spec.DeltaVVolts, spec.RiseTimeSeconds);
        TransientTargetProblem = "";
        _rippleVoltsText = _ripplePercentText = _stepCurrentText = _riseTimeText = null;
        RefreshTransientTarget();
        OnPropertyChanged(nameof(ImpedanceTargetEntry));
        QueueResolve();
    }

    /// <summary>Re-reads the form and the readout from the selected rail — on a rail switch, a document
    /// load, and after Set.</summary>
    private void RefreshTransientTarget()
    {
        _rippleVoltsText = _ripplePercentText = _stepCurrentText = _riseTimeText = null;
        OnPropertyChanged(nameof(RippleVoltsEntry));
        OnPropertyChanged(nameof(RipplePercentEntry));
        OnPropertyChanged(nameof(StepCurrentEntry));
        OnPropertyChanged(nameof(RiseTimeEntry));
        OnPropertyChanged(nameof(TransientTargetReadout));
    }

    private static string FormatSeconds(double s)
        => s >= 1e-6 ? RailValueFormat.Significant(s * 1e6, 4) + " µs"
         : s >= 1e-9 ? RailValueFormat.Significant(s * 1e9, 4) + " ns"
         :             RailValueFormat.Significant(s * 1e12, 4) + " ps";
}
