using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Statistics;

/// <summary>One kit axis of a cross product: the key a corner records it by, the name it is shown by, and the
/// sections to take.</summary>
public sealed record GeneratorAxis(string Key, string Label, IReadOnlyList<string> Options);

/// <summary>One global variable or tunable key of a cross product, and the value texts to take.</summary>
public sealed record GeneratorValues(string Name, IReadOnlyList<string> Values);

/// <summary>What a cross product produced: the corners, or why not.</summary>
public sealed record CornerGeneration(IReadOnlyList<CornerDefinition> Corners, Diagnostic? Refusal);

/// <summary>
/// The corner generator (brief-yield-6 R-ya6-5, yield overview D10): kit axes × options, temperatures and variable
/// values crossed into EXPLICIT corner definitions, named from their parts (<c>tt_25</c>, <c>ss_85_Vdd3p0</c>),
/// deduplicated and capped at <see cref="Cap"/>. It writes lines; it is not a grammar — what it produces is ordinary
/// <c>corner</c> lines, edited afterwards like any other.
/// </summary>
public static class CornerGenerator
{
    /// <summary>The most corners one cross product makes; more is a refusal naming the count.</summary>
    public const int Cap = 256;

    public static CornerGeneration CrossProduct(
        IReadOnlyList<GeneratorAxis> axes, IReadOnlyList<string> temps, IReadOnlyList<GeneratorValues> values)
    {
        // Multiplied only while it is a count the refusal can state: past int.MaxValue it is refused anyway, and a long
        // could otherwise wrap to a small number and pass the cap (brief-yield-15 R-ya15-9).
        long count = 1;
        void Times(int n) { if (count <= int.MaxValue) count *= Math.Max(1, n); }
        foreach (var a in axes) Times(a.Options.Count);
        Times(temps.Count);
        foreach (var v in values) Times(v.Values.Count);
        if (count > Cap) return new CornerGeneration([], StatisticsDiagnostics.GeneratorTooMany((int)Math.Min(count, int.MaxValue), Cap));

        // Each dimension is a list of (name part, apply) choices; an empty dimension is left out of the product.
        var dims = new List<List<(string Part, Action<CornerDefinition> Apply)>>();
        foreach (var a in axes.Where(a => a.Options.Count > 0))
            dims.Add([.. a.Options.Distinct(StringComparer.Ordinal).Select(o => (Word(o),
                (Action<CornerDefinition>)(c => (c.AxisSelections ??= new(StringComparer.Ordinal))[a.Key] = o)))]);
        // A temperature that would start the name reads t25, tm40; after a kit option, tt_25.
        string lead = dims.Count == 0 ? "t" : "";
        if (temps.Count > 0)
            dims.Add([.. temps.Distinct(StringComparer.Ordinal).Select(t => (lead + Number(t), (Action<CornerDefinition>)(c => c.Temp = t)))]);
        foreach (var v in values.Where(v => v.Values.Count > 0))
            dims.Add([.. v.Values.Distinct(StringComparer.Ordinal).Select(x => (Word(v.Name) + Number(NumberOf(x)),
                (Action<CornerDefinition>)(c => c.Values[v.Name] = x)))]);

        var corners = new List<CornerDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindings = new HashSet<string>(StringComparer.Ordinal);
        if (dims.Count == 0) return new CornerGeneration(corners, null);

        var index = new int[dims.Count];
        while (true)
        {
            var c = new CornerDefinition();
            var parts = new List<string>();
            for (int d = 0; d < dims.Count; d++)
            {
                var (part, apply) = dims[d][index[d]];
                apply(c);
                parts.Add(part);
            }
            if (bindings.Add(Signature(c)))
            {
                string name = string.Join("_", parts.Where(p => p.Length > 0));
                if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_')) name = "c" + name;   // a key spelled with a leading digit
                string unique = name;
                for (int k = 2; !names.Add(unique); k++) unique = $"{name}_{k}";
                c.Name = unique;
                corners.Add(c);
            }

            int i = dims.Count - 1;
            while (i >= 0 && ++index[i] == dims[i].Count) index[i--] = 0;
            if (i < 0) break;
        }
        return new CornerGeneration(corners, null);
    }

    /// <summary>
    /// <c>"axis=opt1,opt2;temp=-40,25,85;Vdd=3.0 V,3.6 V"</c> — the CLI's spelling — crossed. A name is a kit axis when
    /// one of <paramref name="kitAxes"/> answers to it (its label, its file stem, or its key), <c>temp</c> the ambient,
    /// and anything else a global variable or tunable key; each binding is checked when the corners are, by
    /// <c>check</c>.
    /// </summary>
    public static CornerGeneration Parse(string spec, IReadOnlyList<WorkspaceCornerAxis> kitAxes)
    {
        var axes = new List<GeneratorAxis>();
        var temps = new List<string>();
        var values = new List<GeneratorValues>();
        foreach (var raw in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) return new CornerGeneration([], StatisticsDiagnostics.GeneratorMalformed(raw, "write name=value,value"));
            string name = raw[..eq].Trim();
            var list = raw[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (list.Count == 0) return new CornerGeneration([], StatisticsDiagnostics.GeneratorMalformed(raw, "no values are given"));

            if (name.Equals("temp", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var t in list)
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        return new CornerGeneration([], StatisticsDiagnostics.GeneratorMalformed(raw, $"'{t}' is not a number of degC"));
                temps.AddRange(list);
                continue;
            }
            var axis = kitAxes.FirstOrDefault(a => a.Label.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? kitAxes.FirstOrDefault(a => a.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? kitAxes.FirstOrDefault(a => a.Key.Equals(name, StringComparison.Ordinal));
            if (axis is not null)
            {
                var unknown = list.Where(o => !axis.Options.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
                if (unknown.Count > 0)
                    return new CornerGeneration([], StatisticsDiagnostics.GeneratorMalformed(raw,
                        $"'{axis.Label}' offers {string.Join(", ", axis.Options)}, not {string.Join(", ", unknown)}"));
                axes.Add(new GeneratorAxis(axis.Key, axis.Label,
                    [.. list.Select(o => axis.Options.First(x => x.Equals(o, StringComparison.OrdinalIgnoreCase)))]));
                continue;
            }
            values.Add(new GeneratorValues(name, list));
        }
        return CrossProduct(axes, temps, values);
    }

    /// <summary>What makes two corners the same corner: every binding, in a fixed order.</summary>
    private static string Signature(CornerDefinition c)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in (c.AxisSelections ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append(k).Append('=').Append(v).Append('\n');
        sb.Append("temp=").Append(c.Temp).Append('\n');
        foreach (var (k, v) in c.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append(k).Append('=').Append(v).Append('\n');
        return sb.ToString();
    }

    /// <summary>A value's number without its unit: <c>3.0 V</c> → <c>3.0</c>.</summary>
    private static string NumberOf(string value)
    {
        string v = value.Trim();
        int space = v.IndexOf(' ');
        return space > 0 ? v[..space] : v;
    }

    /// <summary>A number as a corner's one-word name can hold it: <c>-</c> → <c>m</c>, <c>.</c> → <c>p</c>
    /// (<c>-40</c> → <c>m40</c>, <c>3.0</c> → <c>3p0</c>), anything else that is not a letter or digit dropped.</summary>
    private static string Number(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.Trim())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch == '-') sb.Append('m');
            else if (ch == '.') sb.Append('p');
        }
        return sb.ToString();
    }

    /// <summary>A name or key as a name part: its letters, digits and underscores (<c>R2.R</c> → <c>R2R</c>).</summary>
    private static string Word(string text) => new([.. text.Where(ch => char.IsLetterOrDigit(ch) || ch == '_')]);
}
