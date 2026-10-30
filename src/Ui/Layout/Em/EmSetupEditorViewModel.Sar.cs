// brief-sar-6 R-sar6-1 / R-sar6-6 — the setup's Sar group, and the SAR a finished run computed.
//
// The openEMS section's rules exactly (R-em-11): every control writes the .cem's Sar group through the same undoable
// CommitEdit, a blank box is the key's default (SarSettings.Resolve's), and turning SAR off removes the group. Nothing that
// affects the answer lives only here.
//
// THE READOUT IS NUMBERS ONLY (owner preference): a value and its unit, normalised to 1 W accepted as the heading says, and a
// note only where a value is missing. What each number means is the design note's, not the panel's.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One line of a SAR readout: a label and its value.</summary>
public sealed record SarReadoutLine(string Label, string Value);

/// <summary>One driven port at one SAR frequency, as the panel shows it.</summary>
public sealed record SarReadoutRow(string Heading, IReadOnlyList<SarReadoutLine> Lines);

/// <summary>A B1 choice: automatic (on below 500 MHz), on, or off.</summary>
public sealed record SarB1Choice(bool? Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class EmSetupEditorViewModel
{
    [ObservableProperty] private bool _sarEnabled;
    [ObservableProperty] private string _sarFrequenciesText = "";
    [ObservableProperty] private string _sarMassesText = "";
    [ObservableProperty] private SarMethod _sarMethod = SarMethod.Cube;
    [ObservableProperty] private string _sarRegionText = "";
    [ObservableProperty] private SarB1Choice _sarB1Choice = SarB1Choices[0];
    [ObservableProperty] private string _sarB0Axis = "+z";
    [ObservableProperty] private string _sarB1RegionText = "";
    [ObservableProperty] private bool _sarCrossCheck;
    [ObservableProperty] private bool _sarPerPortFields;
    [ObservableProperty] private string? _sarFieldError;

    public static IReadOnlyList<SarMethod> SarMethodChoices { get; } = Enum.GetValues<SarMethod>();
    public static IReadOnlyList<string> SarB0AxisChoices { get; } = SarSettings.B0AxisSpellings;
    public static IReadOnlyList<SarB1Choice> SarB1Choices { get; } =
    [
        new(null, "Auto (below 500 MHz)"),
        new(true, "On"),
        new(false, "Off"),
    ];

    public static string SarDefaultMasses => string.Join(", ", SarSettings.DefaultMassesGrams.Select(m => G(m)));

    public const string SarTip =
        "Computes SAR (local, and averaged over cubes of each mass) and, for an MRI coil, B1+ and B1−, on the raw SAR dump openEMS writes, " +
        "normalised to 1 W accepted at the driven port. Each tissue needs a density. Turns the cell-constant material on.";

    public const string SarMethodTip =
        "Cube: the cube-averaging procedure of IEC/IEEE 62704-1 as openEMS's sar_calc implements it. Simple: every tissue cell takes " +
        "the centred cube of the mass, with no air limit.";

    /// <summary>The Sar group is shown for a driven 3D setup run on openEMS.</summary>
    public bool IsSarSetup => IsOpenEmsSetup && Is3DSetup && !IsStaticSetup && !IsEigenmodeSetup && !IsThermalSetup;

    /// <summary>The SAR of the last run of this setup, in this session.</summary>
    public ObservableCollection<SarReadoutRow> SarRows { get; } = [];

    public bool HasSarRows => SarRows.Count > 0;

    private void SyncSarFields()
    {
        var s = Working.Sar;
        bool was = _suppressCommit;
        _suppressCommit = true;
        SarEnabled = s is not null;
        SarFrequenciesText = s?.FrequenciesGHz is { } f ? string.Join(", ", f.Select(v => G(v))) : "";
        SarMassesText = s?.MassesGrams is { } m ? string.Join(", ", m.Select(v => G(v))) : "";
        SarMethod = s?.Method ?? SarMethod.Cube;
        SarRegionText = s?.Region is { } r ? string.Join(", ", r) : "";
        SarB1Choice = SarB1Choices.First(c => c.Value == s?.B1);
        SarB0Axis = s?.B0Axis is { } a && SarSettings.ParseB0Axis(a) is { } parsed ? SarSettings.B0AxisSpellings[parsed.Axis switch { 0 => 2, 1 => 4, _ => 0 } + (parsed.Sign < 0 ? 1 : 0)] : "+z";
        SarB1RegionText = s?.B1Region is { } b ? string.Join(", ", b) : "";
        SarCrossCheck = s?.CrossCheck ?? false;
        SarPerPortFields = s?.PerPortFields ?? false;
        SarFieldError = null;
        _suppressCommit = was;
        OnPropertyChanged(nameof(IsSarSetup));
    }

    partial void OnSarEnabledChanged(bool value) { if (!_suppressCommit) CommitSarField("Sar.Enabled"); }
    partial void OnSarMethodChanged(SarMethod value) { if (!_suppressCommit) CommitSarField("Sar.Method"); }
    partial void OnSarB1ChoiceChanged(SarB1Choice value) { if (!_suppressCommit) CommitSarField("Sar.B1"); }
    partial void OnSarB0AxisChanged(string value) { if (!_suppressCommit) CommitSarField("Sar.B0Axis"); }
    partial void OnSarCrossCheckChanged(bool value) { if (!_suppressCommit) CommitSarField("Sar.CrossCheck"); }
    partial void OnSarPerPortFieldsChanged(bool value) { if (!_suppressCommit) CommitSarField("Sar.PerPortFields"); }

    /// <summary>
    /// Commits one Sar key — <c>Sar.&lt;Key&gt;</c> is its tag. Turning SAR on starts the group at the sweep's centre; off removes
    /// it. Blank is the key's default; an invalid value stays in the box beside the message and writes nothing.
    /// </summary>
    public void CommitSarField(string tag)
    {
        string? error = null;
        CemSar? sar = Working.Sar?.Clone();
        if (tag == "Sar.Enabled")
        {
            if (!SarEnabled) sar = null;
            else sar ??= new CemSar { FrequenciesGHz = SweepCentreGHz() is { } c ? [c] : null };
        }
        else if (sar is null) return;
        else
            switch (tag)
            {
                case "Sar.FrequenciesGHz":
                    if (Numbers(SarFrequenciesText, out var f, positive: true) is { } bad1) error = bad1;
                    else if (f is null) error = "Enter the frequency SAR is computed at, in GHz (0.298 for 7 T, 1 for a 1 GHz phone).";
                    else sar.FrequenciesGHz = f;
                    break;
                case "Sar.MassesGrams":
                    if (Numbers(SarMassesText, out var m, positive: true) is { } bad2) error = bad2;
                    else sar.MassesGrams = m;
                    break;
                case "Sar.Method":
                    sar.Method = SarMethod == SarMethod.Cube ? null : SarMethod;
                    break;
                case "Sar.Region":
                    sar.Region = Names(SarRegionText);
                    break;
                case "Sar.B1":
                    sar.B1 = SarB1Choice.Value;
                    break;
                case "Sar.B0Axis":
                    sar.B0Axis = SarB0Axis == "+z" ? null : SarB0Axis;
                    break;
                case "Sar.B1Region":
                    sar.B1Region = Names(SarB1RegionText);
                    break;
                case "Sar.CrossCheck":
                    sar.CrossCheck = SarCrossCheck ? true : null;
                    break;
                case "Sar.PerPortFields":
                    sar.PerPortFields = SarPerPortFields ? true : null;
                    break;
                default:
                    return;
            }

        SarFieldError = error;
        if (error is not null) return;
        var before = SnapshotJson();
        Working.Sar = sar;
        if (SnapshotJson() == before) return;
        CommitEdit(before, $"Change SAR setting {tag["Sar.".Length..]}");
        if (tag == "Sar.Enabled") SyncSarFields();
    }

    /// <summary>The sweep's centre, GHz, as the materials are evaluated at without SAR — where a new Sar group starts.</summary>
    private double? SweepCentreGHz()
    {
        try
        {
            var f = Working.ExpandFrequencies().Where(v => v > 0).ToArray();
            return f.Length == 0 ? null : 0.5 * (f.Min() + f.Max()) / 1e9;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private static string? Numbers(string text, out List<double>? values, bool positive)
    {
        values = null;
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var list = new List<double>();
        foreach (string p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || positive && v <= 0)
                return $"'{p}' is not a positive number.";
            list.Add(v);
        }
        values = list;
        return null;
    }

    /// <summary>R-sar6-6 — the readouts of a finished run, from the records it left in its run directory.</summary>
    public void AdoptSar(string runDir)
    {
        SarRows.Clear();
        foreach (var (_, r) in CircuitRF.Design.Em3d.SarVolumeFiles.ReadAll(runDir)) SarRows.Add(Readout(r));
        OnPropertyChanged(nameof(HasSarRows));
    }

    /// <summary>One record as the panel's rows: values only, per 1 W accepted, a note where one is missing.</summary>
    public static SarReadoutRow Readout(CircuitRF.Design.Em3d.SarRunRecord r)
    {
        string N(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        string Mm(double[] p) => string.Join(", ", p.Select(v => (v * 1e3).ToString("0.#", CultureInfo.InvariantCulture))) + " mm";
        string Pct(double v) => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        var lines = new List<SarReadoutLine>();
        foreach (var p in r.Peaks.Where(p => p.Quantity != CircuitRF.Design.Em3d.SarVolumeFiles.LocalQuantity))
            lines.Add(new($"ps{p.Quantity}", r.PeakPerWatt(p.Quantity) is { } v
                ? $"{N(v)} W/kg at ({Mm(p.CentreM)})" + (p.Object is { } o ? $", {o}" + (p.Material is { } mat && mat != o ? $" ({mat})" : "") : "")
                : "not computed: no power accepted"));
        if (r.PeakPerWatt(CircuitRF.Design.Em3d.SarVolumeFiles.LocalQuantity) is { } local) lines.Add(new("Peak local SAR", $"{N(local)} W/kg"));
        lines.Add(new("Whole-region SAR", r.WholeSarPerWatt is { } wb ? $"{N(wb)} W/kg ({r.Region})" : "not computed: no tissue mass"));
        lines.Add(new("Absorbed power", r.AbsorbedFraction is { } a ? $"{N(a)} W ({Pct(a)})" : "not computed"));
        lines.Add(new("Tissue mass", $"{N(r.TissueMassKg)} kg"));
        lines.Add(new("Accepted power", $"1 W ({N(r.AcceptedPowerW)} W as solved)"));
        lines.Add(new("Radiated power", r.RadiationEfficiency is { } eta ? $"{N(eta)} W ({Pct(eta)})" : "no radiation pattern: turn it on to see it"));
        if (r.PowerBudget is { } budget) lines.Add(new("Power budget", Pct(budget)));
        if (r.B1Region is not null)
        {
            lines.Add(new("Mean |B1+|", r.B1pMeanPerSqrtWatt is { } b ? $"{N(b * 1e6)} µT/√W over {r.B1Region}" : $"not computed over {r.B1Region}"));
            if (r.SarEfficiency is { } e) lines.Add(new($"SAR efficiency ({r.EfficiencyQuantity})", $"{N(e * 1e6)} µT/√(W/kg)"));
        }
        foreach (var c in r.CrossCheck ?? [])
            lines.Add(new($"sar_calc {N(c.MassGrams)} g peak", c.SarCalcPeak is { } t && r.AcceptedPowerW > 0
                ? $"{N(t / r.AcceptedPowerW)} W/kg" + (c.RelativeDifference is { } d ? $" ({Pct(d)} apart)" : "")
                : "did not run"));
        string heading = $"{CircuitRF.Design.Layout.MaterialDispersion.Hz(r.FrequencyHz)}, port {r.Port}: per 1 W accepted";
        return new SarReadoutRow(heading, lines);
    }
}
