// brief-em3d-9 R-em3d9-6 — the openEMS section's fields, shown when openEMS is the picked solver: brief
// 8's grid fields and this brief's two run fields.
//
// The Palace section's rules exactly (R-em-11): every control writes a .cem field through the same
// undoable CommitEdit, a blank box is the field's default — OpenEmsGridSettings.Default's and
// OpenEmsRunSettings.Default's, the ones the grid generator and the writer read — and a section with
// every field at its default is written as no section.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>brief-em3d-120 — one row of the openEMS Grid picker.</summary>
public sealed record OpenEmsGridChoice(OpenEmsGridKind Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class EmSetupEditorViewModel
{
    [ObservableProperty] private string _openEmsCellsPerWavelengthText = "";
    [ObservableProperty] private string _openEmsGradingRatioText       = "";
    [ObservableProperty] private string _openEmsMinCellUmText          = "";
    [ObservableProperty] private string _openEmsPmlCellsText           = "";
    [ObservableProperty] private string _openEmsEndCriterionDbText     = "";
    [ObservableProperty] private string _openEmsMaxTimeStepsText       = "";
    [ObservableProperty] private bool   _openEmsThirdsRule             = true;
    [ObservableProperty] private string? _openEmsFieldError;

    // brief-em3d-120 R-em3d120-5 — a 3D view's own setup chooses a cylindrical grid: Grid, Axis and Origin.
    [ObservableProperty] private OpenEmsGridChoice _openEmsGridChoice = OpenEmsGridChoices[0];
    [ObservableProperty] private OpenEmsGridAxis   _openEmsAxis       = OpenEmsGridAxis.Z;
    [ObservableProperty] private string            _openEmsAxisOriginText = "";

    public static IReadOnlyList<OpenEmsGridChoice> OpenEmsGridChoices { get; } =
    [
        new(OpenEmsGridKind.Cartesian,   "Cartesian"),
        new(OpenEmsGridKind.Cylindrical, "Cylindrical"),
    ];

    public static IReadOnlyList<OpenEmsGridAxis> OpenEmsAxisChoices { get; } = [OpenEmsGridAxis.X, OpenEmsGridAxis.Y, OpenEmsGridAxis.Z];

    public const string OpenEmsGridTip =
        "Cartesian (the default) suits every problem. Cylindrical puts round conductors coaxial with one axis on the grid's own " +
        "circles: a coax section, step, bead or adapter, or a pin in a round bore. Its wave ports must be coaxial terminals on " +
        "the two faces the axis crosses. A coax meeting a board stays Cartesian: openEMS has one grid per run.";

    /// <summary>The Grid picker is offered on a 3D view's own setup only (C5: a .cem stays Cartesian).</summary>
    public bool ShowOpenEmsGrid => IsEmbedded;

    /// <summary>Axis and Origin are read only on a cylindrical grid.</summary>
    public bool IsOpenEmsCylindrical => IsEmbedded && OpenEmsGridChoice.Value == OpenEmsGridKind.Cylindrical;

    partial void OnOpenEmsGridChoiceChanged(OpenEmsGridChoice value)
    {
        OnPropertyChanged(nameof(IsOpenEmsCylindrical));
        if (_suppressCommit) return;
        CommitOpenEmsField("OpenEms.Grid");
    }

    partial void OnOpenEmsAxisChanged(OpenEmsGridAxis value)
    {
        if (_suppressCommit) return;
        CommitOpenEmsField("OpenEms.Axis");
    }

    /// <summary>What a blank box stands for — the defaults the generator and the writer read.</summary>
    public static string OpenEmsDefaultCellsPerWavelength => G(CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.CellsPerWavelength);
    public static string OpenEmsDefaultGradingRatio       => G(CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.GradingRatio);
    public static string OpenEmsDefaultMinCellUm          => "auto";
    public static string OpenEmsDefaultPmlCells           => CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.PmlCells.ToString(CultureInfo.InvariantCulture);
    public static string OpenEmsDefaultEndCriterionDb     => G(OpenEmsRunSettings.Default.EndCriterionDb);
    public static string OpenEmsDefaultMaxTimeSteps       => "auto";

    private void SyncOpenEmsFields()
    {
        var o = Working.OpenEms;
        OpenEmsCellsPerWavelengthText = G(o?.CellsPerWavelength);
        OpenEmsGradingRatioText       = G(o?.GradingRatio);
        OpenEmsMinCellUmText          = G(o?.MinCellUm);
        OpenEmsPmlCellsText           = o?.PmlCells?.ToString(CultureInfo.InvariantCulture) ?? "";
        OpenEmsEndCriterionDbText     = G(o?.EndCriterionDb);
        OpenEmsMaxTimeStepsText       = o?.MaxTimeSteps?.ToString(CultureInfo.InvariantCulture) ?? "";
        OpenEmsThirdsRule             = o?.ThirdsRule ?? CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.ThirdsRule;
        OpenEmsSaveFieldsText         = SaveFieldsText(o?.SaveFieldsGHz);         // brief-em3d-83
        OpenEmsGridChoice             = OpenEmsGridChoices.First(c => c.Value == (o?.Grid ?? OpenEmsGridKind.Cartesian));
        OpenEmsAxis                   = o?.Axis ?? OpenEmsGridAxis.Z;
        OpenEmsAxisOriginText         = o?.AxisOriginUm is { } origin ? string.Join(", ", origin.Select(v => G(v))) : "";
        OpenEmsFieldError = null;
    }

    /// <summary>The thirds rule is a check box: on is the default (written as no field), off writes false.</summary>
    partial void OnOpenEmsThirdsRuleChanged(bool value)
    {
        if (_suppressCommit) return;
        CommitOpenEmsField("OpenEms.ThirdsRule");
    }

    /// <summary>
    /// Commits one openEMS field — <c>OpenEms.&lt;Field&gt;</c> is its tag. Blank writes the field as
    /// omitted (the default); an invalid value stays in the box beside the message and writes nothing.
    /// </summary>
    public void CommitOpenEmsField(string tag)
    {
        var section = Working.OpenEms?.Clone() ?? new CemOpenEms();
        string? error = null;

        void Real(string text, Func<double, bool> ok, string rule, Action<double?> set)
        {
            if (text.Trim().Length == 0) { set(null); return; }
            if (TryDouble(text, out double v) && ok(v)) { set(v); return; }
            error = rule;
        }
        void Whole(string text, long min, long max, string rule, Action<long?> set)
        {
            if (text.Trim().Length == 0) { set(null); return; }
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v >= min && v <= max)
            { set(v); return; }
            error = rule;
        }

        switch (tag)
        {
            case "OpenEms.CellsPerWavelength":
                Real(OpenEmsCellsPerWavelengthText, v => v >= 1 && double.IsFinite(v), "Enter a number of cells of at least 1, e.g. 20.",
                     v => section.CellsPerWavelength = v);
                break;
            case "OpenEms.GradingRatio":
                Real(OpenEmsGradingRatioText, v => v > 1 && v <= 4, "Enter a ratio above 1 and at most 4, e.g. 1.3.",
                     v => section.GradingRatio = v);
                break;
            case "OpenEms.MinCellUm":
                Real(OpenEmsMinCellUmText, v => v > 0 && double.IsFinite(v), "Enter a positive length in µm, or leave it blank.",
                     v => section.MinCellUm = v);
                break;
            case "OpenEms.PmlCells":
                Whole(OpenEmsPmlCellsText, 0, 64, "Enter a whole number of cells from 0 to 64.", v => section.PmlCells = (int?)v);
                break;
            case "OpenEms.EndCriterionDb":
                Real(OpenEmsEndCriterionDbText, v => v < 0 && v >= -200, "Enter a decay below 0 dB, at least −200, e.g. −50.",
                     v => section.EndCriterionDb = v);
                break;
            case "OpenEms.MaxTimeSteps":
                Whole(OpenEmsMaxTimeStepsText, 1, long.MaxValue, "Enter a whole number of time steps of at least 1, or leave it blank.",
                      v => section.MaxTimeSteps = v);
                break;
            case "OpenEms.SaveFieldsGHz":               // brief-em3d-83 R-em3d83-6 — the frequencies the E (and H) dumps are made at
                if (TryParseSaveFields(OpenEmsSaveFieldsText, out var saves, out error)) section.SaveFieldsGHz = saves;
                break;
            case "OpenEms.Grid":                        // brief-em3d-120 — Cartesian is written as no field at all
                section.Grid = OpenEmsGridChoice.Value == OpenEmsGridKind.Cartesian ? null : OpenEmsGridChoice.Value;
                break;
            case "OpenEms.Axis":
                section.Axis = OpenEmsAxis == OpenEmsGridAxis.Z ? null : OpenEmsAxis;
                break;
            case "OpenEms.AxisOriginUm":
            {
                string text = OpenEmsAxisOriginText.Trim();
                if (text.Length == 0) { section.AxisOriginUm = null; break; }
                var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
                var values = new List<double>();
                foreach (string part in parts)
                    if (TryDouble(part, out double v) && double.IsFinite(v)) values.Add(v);
                if (parts.Length != 3 || values.Count != 3) { error = "Enter the axis origin as three lengths in µm, x, y, z — e.g. 0, 0, 0."; break; }
                section.AxisOriginUm = values;
                break;
            }
            case "OpenEms.ThirdsRule":
                section.ThirdsRule = OpenEmsThirdsRule == CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.ThirdsRule
                    ? null : OpenEmsThirdsRule;
                break;
            default:
                return;
        }

        OpenEmsFieldError = error;
        if (error is not null) return;

        var before = SnapshotJson();
        Working.OpenEms = section.IsEmpty ? null : section;
        if (SnapshotJson() == before) return;      // the same value, retyped: no undo entry
        CommitEdit(before, $"Change openEMS setting {tag["OpenEms.".Length..]}");
    }
}
