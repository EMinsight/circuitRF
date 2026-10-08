using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Optimization;

/// <summary>What became of one key of a recalled preset.</summary>
public enum PresetRecallOutcome
{
    /// <summary>Loaded, inside its range.</summary>
    Applied,
    /// <summary>Loaded; it lay outside its entry's range, and the range was widened to include it.</summary>
    Widened,
    /// <summary>The key resolves to nothing — the component was deleted or renamed. Skipped.</summary>
    Missing,
    /// <summary>The key names a value that is not offered any more (now an expression), or one that
    /// cannot move (a sweep sweeps it). Skipped.</summary>
    NotTunable,
    /// <summary>The value cannot be applied — not a number, or complex parts no value has. Skipped.</summary>
    NotApplicable,
}

/// <param name="Key">The key as the preset spells it.</param>
/// <param name="Detail">Why it was skipped, or the range it was widened to; null when applied plainly.</param>
public sealed record PresetRecallItem(string Key, PresetRecallOutcome Outcome, string? Detail, string Short)
{
    public bool IsApplied => Outcome is PresetRecallOutcome.Applied or PresetRecallOutcome.Widened;
}

/// <param name="Values">Value key → value text to load: a real value under its own key, a complex value
/// WHOLE under the value's key, in the form the schematic writes it (overview D18).</param>
/// <param name="Setup">The tuning setup with every recalled key tune-enabled and every range a value fell
/// outside widened; null when the recall changes nothing of it.</param>
public sealed record PresetRecallResult(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<PresetRecallItem>     Items,
    TuningSetup?                        Setup)
{
    public int Applied => Items.Count(i => i.IsApplied);

    /// <summary>One sentence for a status line: <c>Applied 11 of 12 · R7 no longer exists</c>.</summary>
    public string Summary
    {
        get
        {
            var notes = Items.Where(i => i.Outcome != PresetRecallOutcome.Applied)
                             .OrderBy(i => i.IsApplied)          // what was skipped before what was widened
                             .ToList();
            string s = $"Applied {Applied} of {Items.Count}";
            if (notes.Count > 0) s += " · " + notes[0].Short;
            if (notes.Count > 1) s += $" · {notes.Count - 1} more";
            return s;
        }
    }

    /// <summary>One line per key, for a tooltip and the Messages panel.</summary>
    public IReadOnlyList<string> Lines => [.. Items.Select(i => i.Outcome switch
    {
        PresetRecallOutcome.Applied => $"{i.Key}: applied",
        PresetRecallOutcome.Widened => $"{i.Key}: applied; {i.Detail}",
        _                           => $"{i.Key}: skipped — {i.Detail}",
    })];
}

/// <summary>
/// Recalling a preset (brief-tuneopt-5 R-to5-2, overview D3/D18): BEST EFFORT, never a refusal. A key
/// that names nothing is skipped and said so; a value outside its range is applied and the range grows to
/// include it; a value that is no longer tunable is skipped. Nothing here throws for any of those, and
/// nothing touches a document — the caller loads <see cref="PresetRecallResult.Values"/> into the session
/// and applies <see cref="PresetRecallResult.Setup"/> as one undoable edit.
/// </summary>
public static class PresetRecall
{
    public static PresetRecallResult Apply(TuningPreset preset, TunableCatalog catalog, TuningSetup? setup)
    {
        var values  = new Dictionary<string, string>(StringComparer.Ordinal);
        var items   = new List<PresetRecallItem>();
        var next    = setup?.Clone() ?? new TuningSetup();
        bool edited = false;

        // A complex value's whole key and any part keys of it are one recall: the parts compose with the
        // whole when both are given, with the schematic's value otherwise (TunableOverrides' own rule).
        var complexKeys = new Dictionary<string, (string? Whole, Dictionary<ComplexPart, (string Key, string Text)> Parts)>(StringComparer.Ordinal);
        foreach (var (key, text) in preset.Values)
        {
            if (TunableKey.TryParse(key, out var k) && k.Part is { } part)
            {
                string w = k.Whole.ToString();
                var g = complexKeys.TryGetValue(w, out var e) ? e : (null, new());
                g.Parts[part] = (key, text);
                complexKeys[w] = g;
            }
            else if (catalog.PartsOf(key).Count > 0)
            {
                var g = complexKeys.TryGetValue(key, out var e) ? e : (null, new());
                complexKeys[key] = (text, g.Parts);
            }
        }

        foreach (var (key, text) in preset.Values)
        {
            if (TunableKey.TryParse(key, out var k) && k.Part is not null) continue;      // with its whole
            if (complexKeys.ContainsKey(key)) continue;

            if (catalog.Find(key) is not { } t)
            {
                items.Add(Skip(key, catalog));
                continue;
            }
            if (t.DisabledReason is { } disabled)
            {
                items.Add(new(key, PresetRecallOutcome.NotTunable, disabled, $"{key} is {disabled}"));
                continue;
            }
            if (TunableValue.InUnit(text, t.Unit) is not { } n)
            {
                items.Add(new(key, PresetRecallOutcome.NotApplicable, $"'{text}' is not a number", $"{key} is not a number"));
                continue;
            }

            edited |= EnsureTuned(next, t);
            string? widened = Widen(next, key, n, t.Unit);
            edited |= widened is not null;
            values[key] = text;
            items.Add(widened is null
                ? new(key, PresetRecallOutcome.Applied, null, key)
                : new(key, PresetRecallOutcome.Widened, widened, $"{key} range widened"));
        }

        foreach (var (whole, (wholeText, parts)) in complexKeys)
        {
            var keys  = (wholeText is null ? [] : new[] { whole }).Concat(parts.Values.Select(p => p.Key)).ToList();
            var tuned = catalog.PartsOf(whole);
            if (tuned.Count == 0)
            {
                foreach (var key in keys) items.Add(Skip(key, catalog));
                continue;
            }
            var t = tuned[0];
            if (t.DisabledReason is { } disabled)
            {
                foreach (var key in keys) items.Add(new(key, PresetRecallOutcome.NotTunable, disabled, $"{key} is {disabled}"));
                continue;
            }

            var start = t.Whole;
            if (wholeText is not null)
            {
                if (ParseComplex(wholeText, t.WholeUnit) is not { } z0)
                {
                    foreach (var key in keys)
                        items.Add(new(key, PresetRecallOutcome.NotApplicable, $"'{wholeText}' is not a complex number",
                                      $"{key} is not a complex number"));
                    continue;
                }
                start = z0;
            }

            var numbers = new Dictionary<ComplexPart, double>();
            string? bad = null;
            foreach (var (part, (_, text)) in parts)
                if (TunableValue.InUnit(text, part == ComplexPart.Phase ? "deg" : t.WholeUnit) is { } n) numbers[part] = n;
                else bad ??= $"'{text}' is not a number";

            Complex? composed = bad is null ? ComplexValue.Compose(start, numbers) : null;
            if (composed is not { } z)
            {
                string why = bad ?? (numbers.Count > 2
                    ? $"{numbers.Count} parts of {whole} given, and a complex value has two"
                    : $"no value of {whole} has those parts");
                foreach (var key in keys)
                    items.Add(new(key, PresetRecallOutcome.NotApplicable, why, $"{key} is no longer applicable"));
                continue;
            }

            edited |= EnsureTuned(next, tuned, t.Form);
            var widened = new List<string>();
            foreach (var e in next.Variables)
            {
                if (!TunableKey.TryParse(e.Key, out var ek) || ek.Part is not { } part || ek.Whole.ToString() != whole) continue;
                string u = part == ComplexPart.Phase ? "deg" : t.WholeUnit;
                double lo = TunableValue.InUnit(e.Min, u) ?? double.NegativeInfinity;
                double hi = TunableValue.InUnit(e.Max, u) ?? double.PositiveInfinity;
                double near = double.IsFinite(lo) && double.IsFinite(hi) ? (lo + hi) / 2 : ComplexValue.Get(t.Whole, part);
                if (Widen(next, e.Key, ComplexValue.Get(z, part, phaseNear: near), u) is { } w) widened.Add(w);
            }
            edited |= widened.Count > 0;

            values[whole] = ComplexValue.Format(z, t.WholeUnit, t.Form, "G15");
            foreach (var key in keys)
                items.Add(widened.Count == 0
                    ? new(key, PresetRecallOutcome.Applied, null, key)
                    : new(key, PresetRecallOutcome.Widened, string.Join("; ", widened), $"{key} range widened"));
        }

        // The report reads in the preset's own order.
        var order = preset.Values.Keys.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i, StringComparer.Ordinal);
        items.Sort((a, b) => order.GetValueOrDefault(a.Key).CompareTo(order.GetValueOrDefault(b.Key)));
        return new PresetRecallResult(values, items, edited ? next : null);
    }

    /// <summary>The report for a key the catalog does not offer: no longer tunable when it still names a
    /// value of the design, missing when it names nothing.</summary>
    private static PresetRecallItem Skip(string key, TunableCatalog catalog)
    {
        if (catalog.WhyNotOffered(key) is { } why)
            return new(key, PresetRecallOutcome.NotTunable, why, $"{key} is no longer tunable");

        // "R7 no longer exists" when nothing of the instance is left; the key itself when only the
        // parameter went.
        string shown = key;
        if (TunableKey.TryParse(key, out var k) && k.Instance is { } inst
            && !catalog.Tunables.Any(t => t.Cell == k.Cell && t.Owner == inst && t.Kind != TunableKind.Variable))
            shown = (k.Cell is null ? "" : k.Cell + ":") + inst;
        return new(key, PresetRecallOutcome.Missing, "it names nothing in the design", $"{shown} no longer exists");
    }

    /// <summary>Turns tuning on for a real tunable, creating its entry with the D4 default range.</summary>
    private static bool EnsureTuned(TuningSetup setup, Tunable t)
    {
        var entry = setup.Variables.FirstOrDefault(v => v.Key == t.Key);
        if (entry is { Tune: true }) return false;
        if (entry is null) setup.Variables.Add(entry = TuningSetupEdits.NewEntry(t));
        entry.Tune = true;
        return true;
    }

    /// <summary>A complex value with no tuned part gets the pair its form is written in: magnitude and
    /// phase for <c>polar(…)</c>, real and imaginary otherwise — the rows a person would have tuned.</summary>
    private static bool EnsureTuned(TuningSetup setup, IReadOnlyList<Tunable> parts, ComplexForm form)
    {
        if (parts.Any(p => setup.Variables.Any(v => v.Key == p.Key && v.Tune))) return false;
        var pair = form == ComplexForm.Polar ? new[] { ComplexPart.Mag, ComplexPart.Phase } : [ComplexPart.Real, ComplexPart.Imag];
        bool changed = false;
        foreach (var p in parts.Where(p => p.Part is { } part && pair.Contains(part)))
            changed |= EnsureTuned(setup, p);
        return changed;
    }

    /// <summary>Grows the entry's range to include <paramref name="n"/> (in <paramref name="unit"/>); the
    /// new range as a sentence, or null when it already did.</summary>
    private static string? Widen(TuningSetup setup, string key, double n, string unit)
    {
        if (setup.Variables.FirstOrDefault(v => v.Key == key) is not { } e) return null;
        double? lo = TunableValue.InUnit(e.Min, unit), hi = TunableValue.InUnit(e.Max, unit);
        bool below = lo is { } l && n < l, above = hi is { } h && n > h;
        if (!below && !above) return null;
        if (below) e.Min = TunableValue.Format(n, unit);
        if (above) e.Max = TunableValue.Format(n, unit);
        return $"{key} range widened to {e.Min} .. {e.Max}";
    }

    /// <summary>A complex literal in <paramref name="unit"/>: one written with no unit is already in it, one
    /// with another unit is scaled. Null for anything else.</summary>
    internal static Complex? ParseComplex(string text, string unit)
    {
        if (!ComplexValue.TryParse(text, out var z, out string u, out _)) return null;
        if (u.Length == 0 || u == unit || unit.Length == 0) return z;
        return Units.Scale(u) is { } from && Units.Scale(unit) is { } to and not 0 ? z * (from / to) : null;
    }
}
