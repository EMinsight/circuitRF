using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Schematic;

/// <summary>What reading a <c>.csch</c> found wrong with it without refusing it.</summary>
public enum SchematicLoadFindingKind
{
    /// <summary>A key the reader does not know, which it IGNORED.</summary>
    UnknownField,

    /// <summary>A component with no <c>Symbol</c> at all — read as the enum's first member, a
    /// resistor.</summary>
    MissingSymbol,

    /// <summary>A <c>Symbol</c> naming no type this build knows — read as a placeholder. The GUI
    /// already names these on open (<c>ReportUnknownComponents</c>), so it does not post this kind
    /// a second time.</summary>
    UnknownSymbol,

    /// <summary>A value of the wrong JSON shape — a list written as an object, a number written as a
    /// string — which the reader could not bind.</summary>
    WrongShape,

    /// <summary>An analysis the reader dropped, because its <c>Type</c> is not one it builds or the
    /// fields that type needs are missing.</summary>
    AnalysisSkipped,

    /// <summary>A top-level <c>Measurements</c> list: read and saved, never evaluated — a run
    /// evaluates the rows of a <c>Meas</c> block.</summary>
    MeasurementsNotEvaluated,
}

/// <summary>
/// One thing <see cref="SchematicPersistence"/> noticed while loading a schematic. Worded to be shown
/// as-is — the GUI posts it to the Messages panel on a fresh load and <c>circuitrf check</c> reports
/// it — so both say the same sentence about the same file.
/// </summary>
public sealed record SchematicLoadFinding(SchematicLoadFindingKind Kind, bool IsError, string Message);

/// <summary>
/// The silent losses a <c>.csch</c> read can suffer, reported rather than refused — the schematic
/// half of <see cref="CircuitRF.Design.Layout.LayoutLoadAudit"/>, on the same terms.
///
/// <para><b>Why a hand-written schematic needs this.</b> The reader is lenient by design: a key it
/// does not know is ignored so a file from a newer circuitRF still opens, and one component it cannot
/// bind becomes a placeholder rather than failing the whole file. But a misspelt key is ignored the
/// same way, and the results were wrong circuits that still simulated: a component whose type was
/// written as <c>"Kind"</c> instead of <c>"Symbol"</c> was read as the enum's first member — a
/// RESISTOR — and a <c>"Parameters"</c> written as an object made the component an
/// <c>Unknown</c> placeholder. An analysis whose <c>Type</c> was not one of the six tags was dropped
/// with nothing said. So the reader still does all of that, and now says it did.</para>
///
/// <para><b>A walk over the JSON, not a capture inside the read.</b> The layout audit hooks the
/// serializer because a board can be 61 MB; a schematic is small, its components are parsed one by
/// one by a tolerant reader that swallows the very failures worth reporting, and the walk is the only
/// place that still sees what the author wrote. It reads the DTO types by reflection with the
/// reader's own case-insensitive matching, so what it calls unknown is what the reader did not
/// bind.</para>
/// </summary>
public static class SchematicLoadAudit
{
    private const int MaxPlacesNamed = 3;

    /// <summary>Keys a hand-written component uses for its TYPE. None is read; each is named in the
    /// missing-Symbol finding rather than also as an unknown field.</summary>
    private static readonly string[] SymbolAliases =
        ["Kind", "Type", "$type", "ComponentKind", "SymbolKind", "Component", "ComponentType"];

    /// <summary>The spelling an author most likely meant, for a key the reader does not know.</summary>
    private static readonly Dictionary<string, string> Suggestions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = "InstanceName", ["Instance"] = "InstanceName", ["RefDes"] = "InstanceName",
        ["Params"] = "Parameters", ["Properties"] = "Parameters",
        ["Rot"] = "Rotation", ["Orientation"] = "Rotation", ["Angle"] = "Rotation",
        ["Mirror"] = "MirrorX", ["Flip"] = "MirrorX",
        ["Val"] = "Expression", ["Expr"] = "Expression", ["Units"] = "Unit",
        ["Pts"] = "Points", ["Vertices"] = "Points", ["Path"] = "Points",
    };

    private static readonly string[] AnalysisTags = ["dc", "sp", "hb", "sweep", "lp", "lpp"];

    /// <summary>Every finding for one schematic document, before it is bound.</summary>
    public static List<SchematicLoadFinding> Audit(JsonObject root, JsonSerializerOptions readerOptions)
    {
        var unknown  = new Tally();
        var findings = new List<SchematicLoadFinding>();

        Walk(root, typeof(CschFile), "the schematic file", "", insideComponent: false,
             unknown, findings, skipKeys: null);

        if (Child(root, "Components") is JsonArray components)
        {
            var missing = new List<string>();
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is not JsonObject c) continue;
                string who = Who(c, i);

                if (Child(c, "Symbol") is null)
                {
                    var aliases = SymbolAliases.Where(a => Child(c, a) is not null).ToArray();
                    missing.Add(aliases.Length > 0 ? $"{who} (its '{aliases[0]}' is not read)" : who);
                }
                else if (Child(c, "Symbol") is JsonValue v && v.TryGetValue<string>(out var s)
                         && !Enum.TryParse<SymbolKind>(s, ignoreCase: true, out _))
                {
                    findings.Add(new SchematicLoadFinding(SchematicLoadFindingKind.UnknownSymbol, IsError: true,
                        $"{who} names Symbol '{s}', which is not a symbol kind this build knows, so it was " +
                        "read as a placeholder that simulates as nothing. `circuitrf reference components` " +
                        "lists every kind (the name after each .cnl token, e.g. Mlin, Capacitor, TermG)."));
                }
            }

            if (missing.Count > 0)
            {
                string subject = missing.Count == 1
                    ? $"A component has no \"Symbol\": {missing[0]}"
                    : $"{missing.Count} components have no \"Symbol\": {string.Join(", ", missing.Take(MaxPlacesNamed))}" +
                      (missing.Count > MaxPlacesNamed ? ", …" : "");
                findings.Add(new SchematicLoadFinding(SchematicLoadFindingKind.MissingSymbol, IsError: true,
                    $"{subject}. Each was read as a {SymbolKind.Resistor} — a different component that " +
                    "still simulates. A component names its type in \"Symbol\", as a symbol kind " +
                    "(Mlin, Capacitor, TermG, …); `circuitrf reference components` lists them."));
            }
        }

        if (Child(root, "Analyses") is JsonArray analyses)
            for (int i = 0; i < analyses.Count; i++)
                if (analyses[i] is JsonObject a && SkippedReason(a, readerOptions) is { } why)
                    findings.Add(new SchematicLoadFinding(SchematicLoadFindingKind.AnalysisSkipped, IsError: false,
                        $"Analyses[{i}] ({NameOf(a)}) was dropped when the schematic was read: {why}"));

        if (Child(root, "Measurements") is JsonArray { Count: > 0 } measurements)
            findings.Add(new SchematicLoadFinding(SchematicLoadFindingKind.MeasurementsNotEvaluated, IsError: false,
                $"The top-level Measurements list ({measurements.Count}) is kept but never evaluated: a run " +
                "evaluates the rows of a Meas block (Symbol \"Meas\", one Parameters row per measurement, " +
                "Name = expression)."));

        findings.InsertRange(0, unknown.Findings());
        return findings;
    }

    // ── the walk ─────────────────────────────────────────────────────────────

    private static void Walk(JsonNode? node, Type type, string owner, string place, bool insideComponent,
                             Tally unknown, List<SchematicLoadFinding> findings, ISet<string>? skipKeys)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (node is null || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(object)) return;

        if (ElementType(type) is { } element)
        {
            if (node is not JsonArray array)
            {
                findings.Add(Shape(place, "a list ([ … ])", node, insideComponent));
                return;
            }
            for (int i = 0; i < array.Count; i++)
                Walk(array[i], element, OwnerOf(element), $"{place}[{i}]", insideComponent || element == typeof(CschComponent),
                     unknown, findings, element == typeof(CschComponent) ? AliasSkip(array[i]) : null);
            return;
        }

        if (IsDictionary(type, out var valueType))
        {
            if (node is not JsonObject dict) { findings.Add(Shape(place, "an object ({ … })", node, insideComponent)); return; }
            foreach (var kv in dict) Walk(kv.Value, valueType!, owner, Join(place, kv.Key), insideComponent, unknown, findings, null);
            return;
        }

        if (IsLeaf(type))
        {
            if (!LeafAccepts(type, node, out string expected))
                findings.Add(Shape(place, expected, node, insideComponent));
            return;
        }

        if (node is not JsonObject obj)
        {
            findings.Add(Shape(place, "an object ({ … })", node, insideComponent));
            return;
        }

        var properties = PropertiesOf(type);
        foreach (var kv in obj)
        {
            if (!properties.TryGetValue(kv.Key, out var p))
            {
                if (skipKeys?.Contains(kv.Key) == true) continue;
                unknown.Add(owner, kv.Key, place.Length == 0 ? "the top level" : place);
                continue;
            }
            // The component list is parsed component by component, so the root-level walk descends
            // into it the same way every other list does.
            Walk(kv.Value, p.PropertyType, OwnerOf(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType),
                 Join(place, p.Name), insideComponent, unknown, findings, null);
        }
    }

    private static string Join(string place, string key) => place.Length == 0 ? key : $"{place}.{key}";

    private static ISet<string>? AliasSkip(JsonNode? component)
        => component is JsonObject c && Child(c, "Symbol") is null
            ? new HashSet<string>(SymbolAliases, StringComparer.OrdinalIgnoreCase)
            : null;

    private static SchematicLoadFinding Shape(string place, string expected, JsonNode node, bool insideComponent)
    {
        string consequence = insideComponent
            ? "so the component could not be read and was loaded as a placeholder that simulates as nothing"
            : "so the reader could not use it";
        return new SchematicLoadFinding(SchematicLoadFindingKind.WrongShape, IsError: true,
            $"{place} is {Describe(node)} where {expected} is expected, {consequence}." +
            (place.EndsWith(".Parameters", StringComparison.Ordinal)
                ? " Parameters are a list of objects: [{\"Name\": \"W\", \"Expression\": \"30um\"}, …]."
                : ""));
    }

    private static string Describe(JsonNode node) => node switch
    {
        JsonObject => "an object",
        JsonArray  => "a list",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => $"the string \"{v}\"",
        JsonValue v => $"the value {v.ToJsonString()}",
        _ => "a value",
    };

    // ── leaves ───────────────────────────────────────────────────────────────

    private static bool IsLeaf(Type t)
        => t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal);

    private static bool LeafAccepts(Type t, JsonNode node, out string expected)
    {
        var kind = node is JsonValue v ? v.GetValueKind() : node is JsonArray ? JsonValueKind.Array : JsonValueKind.Object;
        if (t == typeof(string)) { expected = "a string"; return kind is JsonValueKind.String or JsonValueKind.Null; }
        if (t == typeof(bool))   { expected = "true or false"; return kind is JsonValueKind.True or JsonValueKind.False; }
        if (t.IsEnum)
        {
            expected = "one of " + string.Join(", ", Enum.GetNames(t).Take(8)) + (Enum.GetNames(t).Length > 8 ? ", …" : "");
            // Symbol has its own finding: the tolerant reader turns an unknown one into a placeholder.
            if (t == typeof(SymbolKind)) return kind == JsonValueKind.String;
            if (kind == JsonValueKind.Number) return true;
            return kind == JsonValueKind.String
                && Enum.TryParse(t, ((JsonValue)node).GetValue<string>(), ignoreCase: true, out _);
        }
        expected = "a number";
        return kind == JsonValueKind.Number;
    }

    // ── types ────────────────────────────────────────────────────────────────

    private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> _properties = [];

    /// <summary>The properties the reader binds, by the name it binds them under — matched without
    /// regard to case, as the reader's own options do.</summary>
    private static Dictionary<string, PropertyInfo> PropertiesOf(Type type)
    {
        lock (_properties)
        {
            if (_properties.TryGetValue(type, out var known)) return known;
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                map[p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name] = p;
            }
            return _properties[type] = map;
        }
    }

    private static Type? ElementType(Type t)
    {
        if (t == typeof(string)) return null;
        if (t.IsArray) return t.GetElementType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() is var g
            && (g == typeof(List<>) || g == typeof(IReadOnlyList<>) || g == typeof(IList<>) || g == typeof(IEnumerable<>)))
            return t.GetGenericArguments()[0];
        return null;
    }

    private static bool IsDictionary(Type t, out Type? valueType)
    {
        valueType = null;
        if (!t.IsGenericType || !typeof(IDictionary).IsAssignableFrom(t) && t.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            return false;
        valueType = t.GetGenericArguments()[1];
        return true;
    }

    private static string OwnerOf(Type element) => element.Name switch
    {
        nameof(CschComponent)    => "a component",
        nameof(CschParameter)    => "a parameter",
        nameof(CschWire)         => "a wire",
        nameof(CschNetLabel)     => "a net label",
        nameof(CschAnalysis)     => "an analysis",
        nameof(CschMeasurement)  => "a measurement",
        nameof(CschFrequencySpec) => "a sweep",
        nameof(CschFile)         => "the schematic file",
        nameof(CschViewState)    => "the view state",
        _                        => $"a {element.Name}",
    };

    // ── analyses ─────────────────────────────────────────────────────────────

    /// <summary>Why <see cref="AnalysisSerialization.FromDto(CschAnalysis)"/> drops this analysis, or
    /// null when it does not.</summary>
    private static string? SkippedReason(JsonObject a, JsonSerializerOptions options)
    {
        CschAnalysis? dto;
        try { dto = a.Deserialize<CschAnalysis>(options); }
        catch (JsonException) { return null; }   // a shape the walk has already reported
        if (dto is null || AnalysisSerialization.FromDto(dto) is not null) return null;

        if (!AnalysisTags.Contains(dto.Type, StringComparer.Ordinal))
            return $"its Type '{dto.Type}' is not one of {string.Join(", ", AnalysisTags)} (lower case).";
        return dto.Type switch
        {
            "sp"    => "an \"sp\" analysis needs at least one entry in Sweeps.",
            "sweep" => "a \"sweep\" needs PsaVarName, PsaInnerName, and either PsaValues or PsaMode, PsaStart, " +
                       "PsaStop and PsaStepOrCount.",
            _       => "the fields that type needs are missing.",
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonNode? Child(JsonObject o, string key)
    {
        foreach (var kv in o)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static string Who(JsonObject c, int i)
        => Child(c, "InstanceName") is JsonValue v && v.TryGetValue<string>(out var n) && n.Length > 0
            ? $"Components[{i}] '{n}'" : $"Components[{i}]";

    private static string NameOf(JsonObject a)
        => Child(a, "Name") is JsonValue v && v.TryGetValue<string>(out var n) && n.Length > 0 ? $"'{n}'" : "unnamed";

    /// <summary>Unknown keys, aggregated per key per kind of object with the first few places — one
    /// misspelling repeated on every component is one line, not one per component.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<(string Owner, string Key), (int Count, List<string> Places)> _seen = [];
        private readonly List<(string Owner, string Key)> _order = [];

        public void Add(string owner, string key, string place)
        {
            if (!_seen.TryGetValue((owner, key), out var s)) { s = (0, []); _order.Add((owner, key)); }
            if (s.Places.Count < MaxPlacesNamed) s.Places.Add(place);
            _seen[(owner, key)] = (s.Count + 1, s.Places);
        }

        public IEnumerable<SchematicLoadFinding> Findings()
        {
            foreach (var (owner, key) in _order)
            {
                var (count, places) = _seen[(owner, key)];
                string where = count == 1
                    ? $"at {places[0]}"
                    : $"on {count} of them (first at {string.Join(", ", places)})";
                string hint = Suggestions.TryGetValue(key, out var meant) ? $" The field is spelled '{meant}'." : "";
                yield return new SchematicLoadFinding(SchematicLoadFindingKind.UnknownField, IsError: false,
                    $"'{key}' is not a field of {owner} and was ignored when the schematic was read, {where}.{hint} " +
                    "A misspelt field is lost this way; so is one written by a newer circuitRF.");
            }
        }
    }
}
