using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout;

/// <summary>
/// A list of materials' OWN rules — the ones that need no stackup, body or object to be broken
/// (brief-em3d-53 R-em3d53-4): duplicate names, a malformed εr tensor, the temperature-table
/// placeholders, and the reserved <c>@</c>. One function, so a <c>.ctech</c>'s <c>Materials</c> block and a
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

            ValidateTemperatureTable(m, m.SigmaVsTemp, "conductivity against temperature (SigmaVsTemp)",
                "every solver uses its conductivity at 20 °C (Sigma20)" +
                (m.Alpha20 is not null ? " and, for a bond wire, its temperature coefficient (Alpha20)" : ""),
                problems);
            ValidateTemperatureTable(m, m.ThermalKVsTemp, "thermal conductivity against temperature (ThermalKVsTemp)",
                "no thermal solver exists yet", problems);
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

    /// <summary>
    /// The owner's placeholders for temperature-dependent conductivity (2026-09-25): a table is
    /// validated as a table — finite, above absolute zero, positive, strictly increasing in
    /// temperature — and, being read by nothing yet, is reported at info so a stated table never
    /// looks as though it were in force.
    /// </summary>
    private static void ValidateTemperatureTable(
        TechMaterial m, List<TechTemperaturePoint>? table, string what, string instead,
        List<TechProblem> problems)
    {
        if (table is not { Count: > 0 }) return;

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

        if (fault is not null)
            problems.Add(Problem(TechValidation.Ids.MaterialInvalid, DiagnosticSeverity.Error,
                $"Material \"{m.Name}\" states {what}, but {fault}."));

        problems.Add(Problem(TechValidation.Ids.MaterialNotReadYet, DiagnosticSeverity.Info,
            $"Material \"{m.Name}\" states {what} ({table.Count} point{(table.Count == 1 ? "" : "s")}). " +
            $"It is carried in the file and read by nothing yet: {instead}."));
    }
}
