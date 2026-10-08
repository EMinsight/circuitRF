using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Design.Optimization;

/// <param name="Key">The value key (a complex value whole).</param>
/// <param name="A">The first side's value text; null when that side does not hold the key.</param>
/// <param name="B">The second side's.</param>
/// <param name="Difference">B − A in the value's unit (<c>+3 Ohm</c>), for a complex value the difference
/// of each tuned part (<c>Δreal +2 Ohm, Δmag −1.3 Ohm</c>), <c>=</c> when equal, empty when there is
/// nothing to subtract.</param>
public sealed record PresetComparisonRow(string Key, string? A, string? B, string Difference);

/// <summary>
/// The preset edits the Tuning and Optimizer windows make (brief-tuneopt-5), as pure functions from one
/// setup to the next — the caller wraps the result in one undoable command. Presets are addressed by
/// INDEX in <see cref="TuningSetup.Presets"/>: a hand-written file may hold two of one name.
/// </summary>
public static class TuningPresets
{
    /// <summary><c>Preset n</c>, one past the highest <c>Preset k</c> already there.</summary>
    public static string NextName(TuningSetup? setup)
    {
        int n = 0;
        foreach (var p in setup?.Presets ?? [])
            if (p.Name.StartsWith("Preset ", StringComparison.Ordinal)
                && int.TryParse(p.Name.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int k))
                n = Math.Max(n, k);
        return $"Preset {n + 1}";
    }

    /// <summary>
    /// Lock in (R-to5-1): a new preset holding <paramref name="values"/> — every tune-enabled entry's
    /// current value, a complex value whole — appended to the setup. The Optimizer passes its best values
    /// and <paramref name="cost"/>.
    /// </summary>
    public static (TuningSetup Setup, TuningPreset Preset) LockIn(
        TuningSetup? setup, IReadOnlyDictionary<string, string> values, DateTime now,
        double? cost = null, string? name = null)
    {
        var next   = setup?.Clone() ?? new TuningSetup();
        var preset = new TuningPreset { Name = name ?? NextName(setup), Created = now, Cost = cost };
        foreach (var (k, v) in values) preset.Values[k] = v;
        next.Presets.Add(preset);
        return (next, preset);
    }

    /// <summary>
    /// "Last tuned" (overview D6): writes or overwrites the single automatic preset with
    /// <paramref name="values"/>. Null when it already holds exactly those values, so a second save
    /// changes nothing. Any extra <see cref="TuningPreset.IsLastTuned"/> preset a hand-edited file
    /// carried goes — there is one.
    /// </summary>
    public static TuningSetup? WithLastTuned(TuningSetup? setup, IReadOnlyDictionary<string, string> values, DateTime now)
    {
        var existing = setup?.Presets.FirstOrDefault(p => p.IsLastTuned);
        if (existing is not null && setup!.Presets.Count(p => p.IsLastTuned) == 1 && SameValues(existing.Values, values))
            return null;

        var next   = setup?.Clone() ?? new TuningSetup();
        var preset = new TuningPreset { Name = TuningPreset.LastTunedName, Created = now, IsLastTuned = true };
        foreach (var (k, v) in values) preset.Values[k] = v;

        int at = next.Presets.FindIndex(p => p.IsLastTuned);
        next.Presets.RemoveAll(p => p.IsLastTuned);
        next.Presets.Insert(at < 0 ? next.Presets.Count : Math.Min(at, next.Presets.Count), preset);
        return next;
    }

    private static bool SameValues(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Why <paramref name="name"/> cannot name preset <paramref name="index"/>, or null.</summary>
    public static string? NameProblem(TuningSetup? setup, int index, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a preset needs a name";
        if (name.Contains('"')) return "a preset's name cannot contain a double quote";
        var presets = setup?.Presets ?? [];
        for (int i = 0; i < presets.Count; i++)
            if (i != index && presets[i].Name == name.Trim()) return $"a preset named '{name.Trim()}' already exists";
        return null;
    }

    /// <summary>Renames preset <paramref name="index"/>. Renaming "Last tuned" keeps it as an ordinary
    /// preset — the next save writes a new automatic one. Null when <see cref="NameProblem"/> objects.</summary>
    public static TuningSetup? Rename(TuningSetup setup, int index, string name)
    {
        if (index < 0 || index >= setup.Presets.Count || NameProblem(setup, index, name) is not null) return null;
        var next = setup.Clone();
        var p    = next.Presets[index];
        if (p.Name == name.Trim()) return null;
        p.Name        = name.Trim();
        p.IsLastTuned = false;
        return next;
    }

    /// <summary>A copy of preset <paramref name="index"/> right after it, named <c>X copy</c> (then
    /// <c>X copy 2</c> …). A copy of "Last tuned" is an ordinary preset.</summary>
    public static TuningSetup Duplicate(TuningSetup setup, int index, DateTime now)
    {
        var next = setup.Clone();
        if (index < 0 || index >= next.Presets.Count) return next;
        var copy = next.Presets[index].Clone();
        string stem = copy.Name + " copy", name = stem;
        for (int n = 2; next.Presets.Any(p => p.Name == name); n++) name = $"{stem} {n}";
        copy.Name        = name;
        copy.IsLastTuned = false;
        copy.Created     = now;
        next.Presets.Insert(index + 1, copy);
        return next;
    }

    public static TuningSetup Delete(TuningSetup setup, int index)
    {
        var next = setup.Clone();
        if (index >= 0 && index < next.Presets.Count) next.Presets.RemoveAt(index);
        return next;
    }

    /// <summary>"Copy as .cnl" (R-to5-4): the preset's own <c>preset</c> line, which a <c>.cnl</c> or a
    /// <c>.csch</c>'s tuning block reads back to the same preset.</summary>
    public static string CnlText(TuningPreset preset) => TuningDirectiveText.WritePreset(preset);

    /// <summary>
    /// Compare (R-to5-6): every key either side holds, both values and the difference. A null side is
    /// the schematic — its value of each key as the catalog reads it. No simulation.
    /// </summary>
    public static IReadOnlyList<PresetComparisonRow> Compare(
        TuningPreset? a, TuningPreset? b, TunableCatalog catalog, TuningSetup? setup)
    {
        var keys = (a?.Values.Keys ?? Enumerable.Empty<string>())
            .Concat(b?.Values.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToList();
        if (a is null && b is null) keys.Clear();

        var rows = new List<PresetComparisonRow>();
        foreach (var key in keys)
        {
            string? va = a is null ? SchematicText(key, catalog) : a.Values.GetValueOrDefault(key);
            string? vb = b is null ? SchematicText(key, catalog) : b.Values.GetValueOrDefault(key);
            rows.Add(new(key, va, vb, va is null || vb is null ? "" : Difference(key, va, vb, catalog, setup)));
        }
        return rows;
    }

    /// <summary>The schematic's value of a key, as text — a complex value whole; null when it names nothing.</summary>
    public static string? SchematicText(string key, TunableCatalog catalog)
    {
        if (catalog.Find(key) is { Part: null } t) return t.ValueText;
        var parts = catalog.PartsOf(key);
        return parts.Count > 0 ? ComplexValue.Format(parts[0].Whole, parts[0].WholeUnit, parts[0].Form, "G15") : null;
    }

    private static string Difference(string key, string a, string b, TunableCatalog catalog, TuningSetup? setup)
    {
        if (a == b) return "=";

        if (catalog.PartsOf(key) is { Count: > 0 } parts)
        {
            string unit = parts[0].WholeUnit;
            if (PresetRecall.ParseComplex(a, unit) is not { } za || PresetRecall.ParseComplex(b, unit) is not { } zb) return "";
            if (za == zb) return "=";

            // The parts that are tuned; all four when none is.
            var tuned = parts.Where(p => setup?.Variables.Any(v => v.Key == p.Key && v.Tune) == true)
                             .Select(p => p.Part!.Value).ToList();
            if (tuned.Count == 0) tuned = [.. Enum.GetValues<ComplexPart>()];
            return string.Join(", ", tuned.Select(p =>
            {
                double d = ComplexValue.Get(zb, p) - ComplexValue.Get(za, p);
                if (p == ComplexPart.Phase) d = ComplexValue.Unwrap(d, 0);
                return $"Δ{TunableKey.PartWord(p)} {Signed(d, p == ComplexPart.Phase ? "deg" : unit)}";
            }));
        }

        string u = catalog.Find(key)?.Unit ?? "";
        if (TunableValue.InUnit(a, u) is not { } na || TunableValue.InUnit(b, u) is not { } nb) return "";
        return nb == na ? "=" : Signed(nb - na, u);
    }

    private static string Signed(double d, string unit)
    {
        string n = (d == 0 ? 0.0 : d).ToString("G6", CultureInfo.InvariantCulture);
        if (d > 0) n = "+" + n;
        return unit.Length == 0 ? n : $"{n} {unit}";
    }
}
