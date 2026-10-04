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
    /// <param name="scope">brief-em3d-105 — every material an appearance's <c>Like</c> may name: a technology's resolved
    /// materials (its own and its libraries'). Null is <paramref name="materials"/> itself — a <c>.cmat</c> on its own.</param>
    public static IReadOnlyList<TechProblem> Validate(IReadOnlyList<TechMaterial> materials, IReadOnlyList<TechMaterial>? scope = null)
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
        Appearances(materials, scope ?? materials, problems);
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
    public static bool IsColour(string s) => ParseColour(s) is not null;

    /// <summary>
    /// <b>The one reader of a material's <c>#rrggbb</c></b> — its display <c>Color</c> and an appearance's two colours alike
    /// (brief-em3d-105 R-em3d105-1b). The 3D scene's colour lookup, the appearance resolver and this validator all call it,
    /// so a colour the view would draw is exactly a colour <c>check</c> accepts. Null for anything else.
    /// </summary>
    public static (byte R, byte G, byte B)? ParseColour(string? s)
    {
        if (s is not { Length: 7 } || s[0] != '#' || s.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0) return null;
        uint v = uint.Parse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ── Appearance (brief-em3d-105 R-em3d105-1b) ─────────────────────────────────────────────────

    /// <summary>The ranges, one table: key, low, high, and whether the low end is excluded.</summary>
    private static readonly (string Key, Func<TechAppearance, double?> Get, double Lo, double Hi, bool LoOpen)[] Ranges =
    [
        (nameof(TechAppearance.Metallic), a => a.Metallic, 0, 1, false),
        (nameof(TechAppearance.Roughness), a => a.Roughness, 0, 1, false),
        (nameof(TechAppearance.Transmission), a => a.Transmission, 0, 1, false),
        (nameof(TechAppearance.Ior), a => a.Ior, 1, 3, false),
        (nameof(TechAppearance.Clearcoat), a => a.Clearcoat, 0, 1, false),
        (nameof(TechAppearance.ClearcoatRoughness), a => a.ClearcoatRoughness, 0, 1, false),
        (nameof(TechAppearance.AttenuationDistance), a => a.AttenuationDistance, 0, double.PositiveInfinity, true),
    ];

    /// <summary>brief-em3d-108 — the numeric keys' ranges, as the Materials editor's and the Inspector's sliders take them: the same
    /// table this validation reads, so a slider never offers a value <c>check</c> refuses.</summary>
    public static IReadOnlyList<(string Key, double Lo, double Hi, bool LoOpen)> AppearanceRanges { get; }
        = [.. Ranges.Select(r => (r.Key, r.Lo, r.Hi, r.LoOpen))];

    /// <summary>
    /// What is wrong with one appearance on its own: each value out of its range, each colour that is not <c>#rrggbb</c> — one
    /// phrase per fault (<c>Roughness 1.5 is outside 0 to 1</c>), for the caller to put after the owner's name. A material's
    /// and a <c>.c3d</c> object's appearance are held to these same phrases.
    /// </summary>
    public static IReadOnlyList<string> AppearanceFaults(TechAppearance? a)
    {
        var faults = new List<string>();
        if (a is null) return faults;
        foreach (var (key, colour) in new[] { (nameof(TechAppearance.BaseColor), a.BaseColor), (nameof(TechAppearance.AttenuationColor), a.AttenuationColor) })
            if (colour is not null && ParseColour(colour) is null) faults.Add($"{key} \"{colour}\" is not #rrggbb");
        foreach (var (key, get, lo, hi, loOpen) in Ranges)
        {
            if (get(a) is not { } v) continue;
            bool ok = double.IsFinite(v) && (loOpen ? v > lo : v >= lo) && v <= hi;
            if (ok) continue;
            faults.Add(double.IsPositiveInfinity(hi)
                ? string.Create(CultureInfo.InvariantCulture, $"{key} {v} is not a positive number of metres")
                : string.Create(CultureInfo.InvariantCulture, $"{key} {v} is outside {lo} to {hi}"));
        }
        return faults;
    }

    /// <summary>The material <paramref name="name"/> in <paramref name="scope"/>, compared as a technology compares names.</summary>
    private static TechMaterial? Find(IReadOnlyList<TechMaterial> scope, string name)
        => scope.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Each material's appearance: its own faults (errors), a <c>Like</c> naming nothing in <paramref name="scope"/>
    /// (a warning — the appearance falls through), and each <c>Like</c> cycle once (an error naming the chain).</summary>
    private static void Appearances(IReadOnlyList<TechMaterial> materials, IReadOnlyList<TechMaterial> scope, List<TechProblem> problems)
    {
        var cycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in materials)
        {
            if (m.Appearance is not { } a) continue;
            foreach (string fault in AppearanceFaults(a))
                problems.Add(Problem(TechValidation.Ids.AppearanceInvalid, DiagnosticSeverity.Error,
                    $"Material \"{m.Name}\" states an appearance whose {fault}."));
            if (a.Like is not { } like) continue;
            if (Find(scope, like) is null)
            {
                problems.Add(Problem(TechValidation.Ids.AppearanceLikeUnknown, DiagnosticSeverity.Warning,
                    $"Material \"{m.Name}\" looks Like \"{like}\", which no material here defines, so it takes nothing from it: " +
                    "its appearance falls through to its own fields and its role's default."));
                continue;
            }
            // A cycle through m: follow the Likes from m until a name repeats or the chain ends.
            var chain = new List<string> { m.Name };
            for (var at = Find(scope, like); at is not null; at = at.Appearance?.Like is { } next ? Find(scope, next) : null)
            {
                int seen = chain.FindIndex(n => string.Equals(n, at.Name, StringComparison.OrdinalIgnoreCase));
                chain.Add(at.Name);
                if (seen < 0) continue;
                var loop = chain.Skip(seen).ToList();
                // One finding per cycle, whichever of its materials is met first.
                string key = string.Join("|", loop.Skip(1).Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal));
                if (cycles.Add(key))
                    problems.Add(Problem(TechValidation.Ids.AppearanceLikeCycle, DiagnosticSeverity.Error,
                        $"Appearances name each other in a cycle: {string.Join(" → ", loop.Select(n => $"\"{n}\""))}. " +
                        "Remove one of the Likes."));
                break;
            }
        }
    }

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
