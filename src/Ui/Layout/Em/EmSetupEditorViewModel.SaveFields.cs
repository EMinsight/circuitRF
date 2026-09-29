// brief-em3d-83 R-em3d83-6 — "Save fields at (GHz)": the frequencies a 3D run saves its fields at, made VISIBLE and EDITABLE
// in the setup panel (until now `SaveFieldsGHz` was .cem text only, and a run that saved one frequency — the sweep's centre,
// by default — looked like a picker that offered only that one). Blank is the default the writers read: the sweep's centre,
// shown as the placeholder with its value. `none` saves nothing (`[]`). Palace and openEMS each have their own list.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

public sealed partial class EmSetupEditorViewModel
{
    [ObservableProperty] private string _palaceSaveFieldsText = "";
    [ObservableProperty] private string _openEmsSaveFieldsText = "";

    /// <summary>What a blank box saves: the sweep's centre, GHz.</summary>
    public string SaveFieldsPlaceholder => PalaceConfigWriter.SweepCentreGHz(Working) is { } c ? G(c) : "the sweep's centre";

    /// <summary>The box's tooltip: what it means, and what it costs (em-setup.md, "Fields for the 3D view").</summary>
    public const string SaveFieldsTip =
        "The frequencies, GHz, whose fields a run saves for the 3D view's field plots — a comma list, e.g. 2, 6, 10. Blank saves the " +
        "sweep's centre; none saves no field. A frequency must lie inside the sweep. Fields are large: measured on the bond-wire " +
        "example (146,769 tetrahedra, element order 2) one saved frequency took 137 MB per driven port, so a two-port setup saving " +
        "twenty frequencies writes about 5.5 GB; the size grows with the mesh.";

    /// <summary>A list as the box shows it: blank for none named, <c>none</c> for <c>[]</c>.</summary>
    private static string SaveFieldsText(IReadOnlyList<double>? list)
        => list is null ? "" : list.Count == 0 ? "none" : string.Join(", ", list.Select(v => G(v)));

    /// <summary>The box's text as a list: null (blank), <c>[]</c> (<c>none</c>), or positive GHz; false with the rule when not.</summary>
    private static bool TryParseSaveFields(string text, out List<double>? list, out string? error)
    {
        list = null;
        error = null;
        string t = text.Trim();
        if (t.Length == 0) return true;
        if (string.Equals(t, "none", StringComparison.OrdinalIgnoreCase)) { list = []; return true; }
        var values = new List<double>();
        foreach (string part in t.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0) || double.IsInfinity(v))
            {
                error = $"'{part}' is not a frequency: enter positive GHz separated by commas (e.g. 2, 6, 10), none, or leave it blank.";
                return false;
            }
            if (!values.Contains(v)) values.Add(v);
        }
        values.Sort();
        list = values;
        return true;
    }
}
