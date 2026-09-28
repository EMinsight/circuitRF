using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout;

/// <summary>
/// A list of materials' OWN rules — the ones that need no stackup, body or object to be broken
/// (brief-em3d-53 R-em3d53-4): duplicate names, a malformed εr tensor, a malformed temperature table and — brief-em3d-73
/// R-em3d73-2b — a table that disagrees with its own constant at 20 °C, and the reserved <c>@</c>. One function, so a <c>.ctech</c>'s <c>Materials</c> block and a
/// <c>.cmat</c> are checked by the same code, and the Materials editor shows exactly what <c>check</c>
/// reports. <b>No material rule lives in a view model.</b> The ids and sentences of the rules that
/// existed before this brief are unchanged.
/// </summary>
public static class MaterialValidation
{
    public static IReadOnlyList<TechProblem> Validate(IReadOnlyList<TechMaterial> materials)
    {
        var problems = new List<TechProblem>();

        // ── tech.material.duplicate — one problem per duplicated NAME, naming how many share it ───
        foreach (var group in materials
                     .GroupBy(m => m.Name ?? "", StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add(Problem(TechValidation.Ids.MaterialDuplicate, DiagnosticSeverity.Error,
                $"{group.Count()} materials are named \"{group.Key}\" (material names ignore case). " +
                "A stackup entry naming it could mean any of them; rename all but one."));

        // ── the material's own shape: a tensor of three, and the temperature tables ─────────────
        foreach (var m in materials)
        {
            // §1g — `Gold@gaas-mmic` is how two technologies' same-name materials are told apart, so a
            // name containing `@` would be indistinguishable from a qualified one.
            if (m.Name?.Contains('@') == true)
                problems.Add(Problem(MaterialLibraries.ReservedCharacterId, DiagnosticSeverity.Error,
                    $"Material \"{m.Name}\" contains '@', which is reserved: 'Name@technology' is how a 3D view tells " +
                    "two technologies' same-name materials apart. Rename it."));

            if (m.EpsrTensor is { } tensor
                && (tensor.Length != 3 || tensor.Any(v => !double.IsFinite(v) || v < 1)))
                problems.Add(Problem(TechValidation.Ids.MaterialInvalid, DiagnosticSeverity.Error,
                    $"Material \"{m.Name}\" states an εr tensor that is not three finite values of at " +
                    "least 1 (xx, yy, zz)."));

            if (m.Color is { } colour && !IsColour(colour))
                problems.Add(Problem(TechValidation.Ids.MaterialInvalid, DiagnosticSeverity.Error,
                    $"Material \"{m.Name}\" states the colour \"{colour}\", which is not #rrggbb."));

            bool sigmaOk = ValidateTemperatureTable(m, m.SigmaVsTemp, "conductivity against temperature (SigmaVsTemp)", problems);
            bool kOk = ValidateTemperatureTable(m, m.ThermalKVsTemp, "thermal conductivity against temperature (ThermalKVsTemp)", problems);

            // R-em3d73-2b — the table wins (owner decision, 2026-09-27), so a constant beside it that says something else
            // at 20 °C is a second answer nobody reads in a thermal run: said, never resolved. It retires the info this rule
            // replaced, which reported every stated table as "read by nothing yet".
            if (sigmaOk && ThermalProperties.SigmaDisagreementAt20(m) is { } sd)
                problems.Add(Problem(TechValidation.Ids.MaterialTableDisagrees, DiagnosticSeverity.Warning,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Material \"{m.Name}\" states SigmaVsTemp, which gives {sd.Table:G5} S/m at 20 °C, and Sigma20 = {sd.Constant:G5} S/m — {Percent(sd.Table, sd.Constant)} apart. A thermal run uses the table; every EM solver uses Sigma20. Make them agree, or remove the one that is wrong.")));
            if (kOk && ThermalProperties.ThermalKDisagreementAt20(m) is { } kd)
                problems.Add(Problem(TechValidation.Ids.MaterialTableDisagrees, DiagnosticSeverity.Warning,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Material \"{m.Name}\" states ThermalKVsTemp, which gives {kd.Table:G5} W/(m·K) at 20 °C, and ThermalK = {kd.Constant:G5} W/(m·K) — {Percent(kd.Table, kd.Constant)} apart. A thermal run uses the table. Make them agree, or remove the one that is wrong.")));

            if (m.ThermalKTensor is { } kt)
            {
                if (kt.Length != 3 || kt.Any(v => !(v > 0 && double.IsFinite(v))))
                    problems.Add(Problem(TechValidation.Ids.MaterialInvalid, DiagnosticSeverity.Error,
                        $"Material \"{m.Name}\" states a thermal conductivity tensor (ThermalKTensor) that is not three positive " +
                        "numbers of W/(m·K) (xx, yy, zz)."));
                else if (m.ThermalK is null && m.ThermalKVsTemp is not { Count: > 0 })
                    problems.Add(Problem(TechValidation.Ids.MaterialInvalid, DiagnosticSeverity.Error,
                        $"Material \"{m.Name}\" states ThermalKTensor but neither ThermalK nor ThermalKVsTemp. The tensor refines " +
                        "the scalar, which a bond wire and an effective block still read: state ThermalK as well."));
            }

            if (m.ThermalK is { } k && !(k > 0 && double.IsFinite(k)))
                problems.Add(Problem(TechValidation.Ids.MaterialInvalid, DiagnosticSeverity.Error,
                    $"Material \"{m.Name}\" states a thermal conductivity (ThermalK) of {k}; it must be a positive number of W/(m·K)."));
        }
        return problems;
    }

    /// <summary>The reason a name may not be used, or null — the editor's rename refusal, from the same
    /// rule <see cref="Validate"/> reports.</summary>
    public static string? NameRefusal(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A material needs a name.";
        if (name.Contains('@'))
            return "'@' is reserved: 'Name@technology' is how a 3D view tells two technologies' same-name materials apart.";
        return null;
    }

    /// <summary>Whether <paramref name="s"/> is a display colour a material may state: <c>#rrggbb</c>.</summary>
    public static bool IsColour(string s)
        => s.Length == 7 && s[0] == '#' && s.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static TechProblem Problem(string id, DiagnosticSeverity severity, string message)
        => new(TechProblemArea.Materials, message, Id: id, Severity: severity);

    private static string Percent(double a, double b)
        => (Math.Abs(a - b) / Math.Abs(b) * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";

    /// <summary>
    /// A temperature table is validated as a table — finite, above absolute zero, positive, strictly increasing in
    /// temperature. Returns whether it is sound (an absent table is), so the 20 °C comparison reads only a sound one.
    /// </summary>
    private static bool ValidateTemperatureTable(TechMaterial m, List<TechTemperaturePoint>? table, string what,
                                                 List<TechProblem> problems)
    {
        if (table is not { Count: > 0 }) return true;

        string? fault = null;
        for (int i = 0; i < table.Count && fault is null; i++)
        {
            var p = table[i];
            if (!double.IsFinite(p.TempC) || !double.IsFinite(p.Value))
                fault = $"point {i + 1} is not a finite number";
            else if (p.TempC < -273.15)
                fault = $"point {i + 1} is below absolute zero ({p.TempC} °C)";
            else if (p.Value <= 0)
                fault = $"point {i + 1} has a value of zero or less ({p.Value})";
            else if (i > 0 && p.TempC <= table[i - 1].TempC)
                fault = $"its temperatures do not strictly increase (point {i + 1}, {p.TempC} °C, " +
                        $"follows {table[i - 1].TempC} °C)";
        }

        if (fault is null) return true;
        problems.Add(Problem(TechValidation.Ids.MaterialInvalid, DiagnosticSeverity.Error,
            $"Material \"{m.Name}\" states {what}, but {fault}."));
        return false;
    }

    /// <summary>
    /// brief-em3d-73 R-em3d73-3a — a list of thermal interfaces' OWN rules, for a <c>.ctech</c>'s list and a <c>.cmat</c>'s
    /// alike: both materials named, a positive finite resistance, and one value per pair within the file (a pair stated
    /// twice with equal values is said at info; with different values it is an error — the library rule, inside one file).
    /// Whether the two names are materials is a question only a technology can answer, and <c>check</c> of a technology
    /// asks it.
    /// </summary>
    public static IReadOnlyList<TechProblem> ValidateInterfaces(IReadOnlyList<TechThermalInterface>? interfaces)
    {
        var problems = new List<TechProblem>();
        if (interfaces is not { Count: > 0 }) return problems;
        foreach (var i in interfaces)
        {
            if (string.IsNullOrWhiteSpace(i.MaterialA) || string.IsNullOrWhiteSpace(i.MaterialB))
                problems.Add(Problem(TechValidation.Ids.InterfaceInvalid, DiagnosticSeverity.Error,
                    $"A thermal interface names {(string.IsNullOrWhiteSpace(i.MaterialA) && string.IsNullOrWhiteSpace(i.MaterialB) ? "no material" : "only one material")}; " +
                    "it is between two (MaterialA and MaterialB)."));
            else if (!(i.ResistanceM2KW > 0) || !double.IsFinite(i.ResistanceM2KW))
                problems.Add(Problem(TechValidation.Ids.InterfaceInvalid, DiagnosticSeverity.Error,
                    string.Create(CultureInfo.InvariantCulture,
                        $"The thermal interface between \"{i.MaterialA}\" and \"{i.MaterialB}\" states a resistance of {i.ResistanceM2KW} m²·K/W; it must be positive. Two materials in perfect contact need no interface record.")));
        }
        foreach (var g in interfaces.Where(i => !string.IsNullOrWhiteSpace(i.MaterialA) && !string.IsNullOrWhiteSpace(i.MaterialB))
                                    .GroupBy(i => i.PairKey, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var first = g.First();
            bool same = g.All(i => i.ResistanceM2KW == first.ResistanceM2KW);
            problems.Add(Problem(TechValidation.Ids.InterfaceDuplicate, same ? DiagnosticSeverity.Info : DiagnosticSeverity.Error,
                $"The thermal interface between \"{first.MaterialA}\" and \"{first.MaterialB}\" is stated {g.Count()} times" +
                (same ? " with the same resistance; one record is enough." : " with different resistances; a pair has one answer.")));
        }
        return problems;
    }

}
