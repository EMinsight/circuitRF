// brief-em3d-51 R-em3d51-1 — a dimension that holds an expression.
//
// THE NUMBER STAYS WHERE IT WAS. Every named dimension keeps its integer (or real) field, and that field is what every
// reader downstream reads — the kernel, the lowering, the tools, the scene. An expression is held BESIDE it, in the
// owning record's Exprs, one slot per component, and resolution (C3dResolver) writes its value into the number. So the
// number is the last resolved value of the expression, a cache, and nothing below the resolver has to know expressions
// exist. The file never writes that cache: a component with an expression is spelled as the expression, always with
// its unit (R-em3d51-1a), and the number reads back as 0 until the document is resolved.
//
// WHY THE UNIT IS STORED. Taking the site unit from the document's DisplayUnit would make changing the display unit move
// geometry, which layout-view.md §1.3 forbids. The unit in force when the user typed is written beside the expression.
//
// THE SPELLING is the owner's JSON, a component at a time: `"Size": [ { "Expr": "w", "Unit": "Mil" }, 1270000, 0 ]`,
// on one line like every point. It is produced by replacing each bindable property's JSON contract (Modify) with one of
// type C3dFieldJson, so the object model keeps its plain numeric properties and a document with no expressions is
// written byte for byte as before.

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CircuitRF.Design.ThreeD;

/// <summary>
/// An expression in a dimension field: its text, and the unit in force when it was typed (<c>Mil</c>, <c>Um</c> …, or
/// <c>Deg</c> for an angle). The unit is the SITE unit: it scales a bare expression, and is skipped when the expression is
/// unit-bearing — a unit literal (<c>10mil</c>) or a unit-bearing name (var-unit-wins, expressions.md §8) — where it goes
/// to the bare ADDITIVE operands instead: in <c>2*w + 5</c>, with <c>w</c> in mil, the 5 is five mil.
/// </summary>
[JsonConverter(typeof(C3dExprJsonConverter))]
[System.ComponentModel.Description("{ \"Expr\": expression, \"Unit\": unit }")]
public sealed record C3dExpr(string Expr, string? Unit = null);

/// <summary><c>{ "Expr": "2*a", "Unit": "Mil" }</c> on one line — an instance override's spelling, the same as a bound
/// component's.</summary>
internal sealed class C3dExprJsonConverter : JsonConverter<C3dExpr>
{
    public override C3dExpr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            throw new JsonException(C3dDiagnostics.ExpressionAsString(reader.GetString() ?? "").Render());
        var values = new List<object?>();
        var exprs = new List<C3dExpr?>();
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException(C3dDiagnostics.ExpressionShape(reader.TokenType.ToString()).Render());
        C3dFieldJsonConverter.ReadComponentPublic(ref reader, values, exprs);
        return exprs[0]!;
    }

    public override void Write(Utf8JsonWriter writer, C3dExpr value, JsonSerializerOptions options)
        => writer.WriteRawValue(C3dFieldJsonConverter.Spell(value), skipInputValidation: true);
}

/// <summary>A record that can hold expressions in some of its dimension fields.</summary>
public interface IC3dBindable
{
    /// <summary>By property name, one slot per component (null where the component is a number); null when nothing is
    /// bound. Never written by the serializer directly — see <see cref="C3dBindings"/>.</summary>
    Dictionary<string, C3dExpr?[]>? Exprs { get; set; }
}

/// <summary>What a bindable field holds.</summary>
public enum C3dFieldKind
{
    /// <summary>An integer DBU length.</summary>
    Length,
    /// <summary>Degrees (a rotation).</summary>
    Angle,
    /// <summary>An integer ≥ 1 (an array's count). A non-integer is refused, never rounded.</summary>
    Count,
    /// <summary>A real number of micrometres (a sheet's thickness, a wire's diameter).</summary>
    Microns,
}

/// <summary>One bindable property: its owner type, name, how many components it has, and what it holds.</summary>
public sealed record C3dFieldSpec(Type Owner, string Property, int Arity, C3dFieldKind Kind)
{
    internal PropertyInfo Info { get; } = Owner.GetProperty(Property)!;
}

/// <summary>A path to one component of one bindable field of an item (an object, instance or port):
/// <c>Size[0]</c>, <c>Rect.Min[1]</c>, <c>Placement.Rotate[0].Deg</c>, <c>Array.Counts[2]</c>, <c>Radius</c>.</summary>
public readonly record struct C3dFieldPath(string Path)
{
    public override string ToString() => Path;
}

/// <summary>A bound component found in a document: which item, which field, and the owner that holds it.</summary>
public sealed record C3dBoundField(string Item, string Path, IC3dBindable Owner, C3dFieldSpec Spec, int Component, C3dExpr Expr);

/// <summary>The table of bindable fields, the accessors, and the JSON contract.</summary>
public static class C3dBindings
{
    /// <summary>R-em3d51-1b — every named dimension that may hold an expression. Point lists stay numbers.</summary>
    public static IReadOnlyList<C3dFieldSpec> Fields { get; } =
    [
        new(typeof(C3dBox), nameof(C3dBox.Min), 3, C3dFieldKind.Length),
        new(typeof(C3dBox), nameof(C3dBox.Size), 3, C3dFieldKind.Length),
        new(typeof(C3dPrism), nameof(C3dPrism.Offset), 1, C3dFieldKind.Length),
        new(typeof(C3dPrism), nameof(C3dPrism.Height), 1, C3dFieldKind.Length),
        new(typeof(C3dPrism), nameof(C3dPrism.Shear), 2, C3dFieldKind.Length),
        new(typeof(C3dSheet), nameof(C3dSheet.Offset), 1, C3dFieldKind.Length),
        new(typeof(C3dSheet), nameof(C3dSheet.ThicknessUm), 1, C3dFieldKind.Microns),
        new(typeof(C3dPolyline), nameof(C3dPolyline.Offset), 1, C3dFieldKind.Length),
        new(typeof(C3dCylinder), nameof(C3dCylinder.Base), 3, C3dFieldKind.Length),
        new(typeof(C3dCylinder), nameof(C3dCylinder.Length), 1, C3dFieldKind.Length),
        new(typeof(C3dCylinder), nameof(C3dCylinder.Radius), 1, C3dFieldKind.Length),
        new(typeof(C3dWire), nameof(C3dWire.DiameterUm), 1, C3dFieldKind.Microns),
        new(typeof(C3dRect), nameof(C3dRect.Min), 2, C3dFieldKind.Length),
        new(typeof(C3dRect), nameof(C3dRect.Size), 2, C3dFieldKind.Length),
        new(typeof(C3dPlacement), nameof(C3dPlacement.Origin), 3, C3dFieldKind.Length),
        new(typeof(C3dRotation), nameof(C3dRotation.Deg), 1, C3dFieldKind.Angle),
        new(typeof(C3dArray), nameof(C3dArray.Counts), 3, C3dFieldKind.Count),
        new(typeof(C3dArray), nameof(C3dArray.Pitch), 3, C3dFieldKind.Length),
        new(typeof(C3dWireArray), nameof(C3dWireArray.Count), 1, C3dFieldKind.Count),
        new(typeof(C3dWireArray), nameof(C3dWireArray.Pitch), 3, C3dFieldKind.Length),
        // brief-em3d-64 R-em3d64-1c — an operation's dimensions, exactly as every other.
        new(typeof(C3dFillet), nameof(C3dFillet.Radius), 1, C3dFieldKind.Length),
        new(typeof(C3dChamfer), nameof(C3dChamfer.Distance), 1, C3dFieldKind.Length),
        new(typeof(C3dChamfer), nameof(C3dChamfer.Distance2), 1, C3dFieldKind.Length),
    ];

    public static C3dFieldSpec? SpecOf(Type owner, string property)
        => Fields.FirstOrDefault(f => f.Owner == owner && f.Property == property);

    // ── numbers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Component <paramref name="k"/> of the field, as a double (DBU, degrees, a count or µm); null for an
    /// unset optional field.</summary>
    public static double? GetNumber(object owner, C3dFieldSpec spec, int k) => spec.Info.GetValue(owner) switch
    {
        C3dPoint3 p => k switch { 0 => p.X, 1 => p.Y, _ => p.Z },
        C3dPoint2 p => k == 0 ? p.U : p.V,
        long l => l,
        double d => d,
        List<int> l => k < l.Count ? l[k] : null,
        _ => null,
    };

    /// <summary>Sets component <paramref name="k"/> of the field. A length or a count is rounded to an integer here —
    /// the resolver has already decided whether that rounding is allowed.</summary>
    public static void SetNumber(object owner, C3dFieldSpec spec, int k, double value)
    {
        long n = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        object? now = spec.Info.GetValue(owner);
        object next = now switch
        {
            C3dPoint3 p => k switch { 0 => p with { X = n }, 1 => p with { Y = n }, _ => p with { Z = n } },
            C3dPoint2 p => k == 0 ? p with { U = n } : p with { V = n },
            long => n,
            List<int> l => SetAt(l, k, (int)n),
            _ => value,   // double, double?, or an unset double?
        };
        spec.Info.SetValue(owner, next);

        static List<int> SetAt(List<int> l, int k, int v)
        {
            while (l.Count <= k) l.Add(1);
            l[k] = v;
            return l;
        }
    }

    public static C3dExpr? GetExpr(IC3dBindable owner, string property, int k)
        => owner.Exprs is { } e && e.TryGetValue(property, out var slots) && k < slots.Length ? slots[k] : null;

    /// <summary>Binds (or, with null, unbinds) one component. An owner with nothing bound keeps a null map.</summary>
    public static void SetExpr(IC3dBindable owner, C3dFieldSpec spec, int k, C3dExpr? expr)
    {
        var map = owner.Exprs;
        if (expr is null && map is null) return;
        map ??= new Dictionary<string, C3dExpr?[]>(StringComparer.Ordinal);
        if (!map.TryGetValue(spec.Property, out var slots)) slots = map[spec.Property] = new C3dExpr?[spec.Arity];
        slots[k] = expr;
        if (slots.All(s => s is null)) map.Remove(spec.Property);
        owner.Exprs = map.Count == 0 ? null : map;
    }

    /// <summary>A deep copy of an owner's expression map (a record is immutable, so the slots are what is copied).</summary>
    public static Dictionary<string, C3dExpr?[]>? Copy(Dictionary<string, C3dExpr?[]>? map)
        => map?.ToDictionary(kv => kv.Key, kv => (C3dExpr?[])kv.Value.Clone(), StringComparer.Ordinal);

    // ── walking a document ─────────────────────────────────────────────────────────────────────────

    /// <summary>Every bindable owner of an item, with the path prefix its fields are named under.</summary>
    public static IEnumerable<(string Prefix, IC3dBindable Owner)> OwnersOf(object item)
    {
        switch (item)
        {
            case C3dObject o:
                yield return ("", o);
                if (o is C3dSheet { Rect: { } r }) yield return ("Rect.", r);
                if (o is C3dWire { Array: { } wa }) yield return ("Array.", wa);
                foreach (var p in Placement(o.Placement)) yield return p;
                // brief-em3d-64 — an operation's operands are owned inline, so their fields are the operation's own,
                // named under the operand's path (Blank.Size[0], Tools[1].Radius, Target.Blank.Min[2]).
                foreach (var (prefix, child) in C3dOperands.Of(o))
                    foreach (var (p, owner) in OwnersOf(child)) yield return (prefix + p, owner);
                break;
            case C3dInstance i:
                foreach (var p in Placement(i.Placement)) yield return p;
                if (i.Array is { } a) yield return ("Array.", a);
                break;
            case C3dPort port:
                yield return ("Rect.", port.Rect);
                break;
        }

        static IEnumerable<(string, IC3dBindable)> Placement(C3dPlacement p)
        {
            yield return ("Placement.", p);
            for (int k = 0; k < p.Rotate.Count; k++) yield return ($"Placement.Rotate[{k}].", p.Rotate[k]);
        }
    }

    /// <summary>The fields of an owner, as (spec, component, path).</summary>
    public static IEnumerable<(C3dFieldSpec Spec, int Component, string Path)> FieldsOf(IC3dBindable owner, string prefix)
    {
        foreach (var spec in Fields)
        {
            if (spec.Owner != owner.GetType()) continue;
            if (spec.Arity == 1) { yield return (spec, 0, prefix + spec.Property); continue; }
            for (int k = 0; k < spec.Arity; k++) yield return (spec, k, $"{prefix}{spec.Property}[{k}]");
        }
    }

    /// <summary>The document's items, each named as <c>check</c> and the panels name it: an object by its name, an
    /// instance by its name, a port as <c>port &lt;number&gt;</c>.</summary>
    public static IEnumerable<(string Name, object Item)> ItemsOf(C3dDocument doc)
    {
        foreach (var o in doc.Objects) yield return (o.Name, o);
        foreach (var i in doc.Instances) yield return (i.Name, i);
        foreach (var p in doc.Ports) yield return ($"port {p.Number}", p);
    }

    /// <summary>Every bound component in the document.</summary>
    public static IEnumerable<C3dBoundField> Bound(C3dDocument doc)
    {
        foreach (var (name, item) in ItemsOf(doc))
            foreach (var f in BoundOf(name, item)) yield return f;
    }

    /// <summary>Every bound component of one item.</summary>
    public static IEnumerable<C3dBoundField> BoundOf(string name, object item)
    {
        foreach (var (prefix, owner) in OwnersOf(item))
        {
            if (owner.Exprs is null) continue;
            foreach (var (spec, k, path) in FieldsOf(owner, prefix))
                if (GetExpr(owner, spec.Property, k) is { } e) yield return new C3dBoundField(name, path, owner, spec, k, e);
        }
    }

    /// <summary>The owner, spec and component a path names in <paramref name="item"/>; null when the item has no such
    /// field (a sheet with an outline has no Rect).</summary>
    public static (IC3dBindable Owner, C3dFieldSpec Spec, int Component)? Find(object item, string path)
    {
        foreach (var (prefix, owner) in OwnersOf(item))
            foreach (var (spec, k, p) in FieldsOf(owner, prefix))
                if (p == path) return (owner, spec, k);
        return null;
    }

    /// <summary>True when the item holds an expression anywhere.</summary>
    public static bool HasAny(object item) => OwnersOf(item).Any(o => o.Owner.Exprs is { Count: > 0 });

    // ── the JSON contract ──────────────────────────────────────────────────────────────────────────

    /// <summary>The encoder the <c>.c3d</c> writes with: an expression's <c>+</c> is written as a plus, not
    /// <c>+</c>, so the file reads as it was typed.</summary>
    public static JavaScriptEncoder Encoder => JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// The resolver modifier (<see cref="C3dSpelling"/> says which spelling): each bindable property's contract is replaced by one of type <see cref="C3dFieldJson"/>
    /// that writes a number where the component is a number and <c>{ "Expr": …, "Unit": … }</c> where it is bound. With
    /// <paramref name="numbersOnly"/>, expressions are left out and the resolved numbers written — the elaborator's
    /// cache key (R-em3d51-5b: an object is keyed on its RESOLVED fields).
    /// </summary>
    internal static void Modify(JsonTypeInfo info, C3dSpelling spelling)
    {
        bool numbersOnly = spelling == C3dSpelling.NumbersOnly;
        if (info.Kind != JsonTypeInfoKind.Object || !typeof(IC3dBindable).IsAssignableFrom(info.Type)) return;
        for (int i = 0; i < info.Properties.Count; i++)
        {
            var p = info.Properties[i];
            if (p.AttributeProvider is not PropertyInfo pi || SpecOf(info.Type, pi.Name) is not { } spec) continue;
            var get = p.Get!;
            var set = p.Set!;
            bool omitDefault = pi.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.WhenWritingDefault };
            var np = info.CreateJsonPropertyInfo(typeof(C3dFieldJson), p.Name);
            np.Order = p.Order;
            np.Get = owner =>
            {
                var slots = numbersOnly ? null : ((IC3dBindable)owner).Exprs?.GetValueOrDefault(spec.Property);
                return new C3dFieldJson(spec, get(owner), slots) { WithValues = spelling == C3dSpelling.WithValues };
            };
            np.Set = (owner, v) =>
            {
                var f = (C3dFieldJson)v!;
                set(owner, ToProperty(spec, pi.PropertyType, f.Value));
                if (f.Exprs is { } slots)
                    for (int k = 0; k < slots.Length; k++)
                        if (slots[k] is { } e) SetExpr((IC3dBindable)owner, spec, k, e);
            };
            np.ShouldSerialize = (owner, v) =>
            {
                var f = (C3dFieldJson)v!;
                if (f.Exprs is { } s && s.Any(e => e is not null)) return true;
                if (f.Value is null) return false;
                if (f.Value is ICollection { Count: 0 }) return false;
                return !omitDefault || !Equals(f.Value, Activator.CreateInstance(pi.PropertyType));
            };
            info.Properties[i] = np;
        }
    }

    /// <summary>A value read in its generic shape, as the property's own type — with the refusals the number readers
    /// give (a point's arity, an integer where one belongs).</summary>
    private static object? ToProperty(C3dFieldSpec spec, Type type, object? raw)
    {
        object?[] parts = raw is RawComponents rc ? rc.Values : [raw];
        long Int(object? o) => o switch
        {
            long l => l,
            double d => throw new JsonException(C3dDiagnostics.IntegerExpected(d.ToString("R", CultureInfo.InvariantCulture)).Render()),
            _ => throw new JsonException(C3dDiagnostics.NumberExpected("null").Render()),
        };
        void Arity(int n, string shape)
        {
            if (raw is not RawComponents) throw new JsonException(C3dDiagnostics.ArrayExpected(shape).Render());
            if (parts.Length != n) throw new JsonException(C3dDiagnostics.WrongArity(shape, n, parts.Length).Render());
        }
        if (type == typeof(C3dPoint3)) { Arity(3, "[x, y, z]"); return new C3dPoint3(Int(parts[0]), Int(parts[1]), Int(parts[2])); }
        if (type == typeof(C3dPoint2)) { Arity(2, "[u, v]"); return new C3dPoint2(Int(parts[0]), Int(parts[1])); }
        if (type == typeof(List<int>))
        {
            if (raw is not RawComponents) throw new JsonException(C3dDiagnostics.ArrayExpected("[integer]").Render());
            return parts.Select(o => checked((int)Int(o))).ToList();
        }
        if (raw is RawComponents) throw new JsonException(C3dDiagnostics.NumberExpected("list").Render());
        if (type == typeof(long)) return Int(raw);
        if (type == typeof(double)) return raw is null ? throw new JsonException(C3dDiagnostics.NumberExpected("null").Render()) : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        if (type == typeof(double?)) return raw is null ? null : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        _ = spec;
        return raw;
    }
}

/// <summary>Which spelling a serializer writes a bound component in.</summary>
public enum C3dSpelling
{
    /// <summary>The file: the expression and its unit. The resolved number is a cache and is not written.</summary>
    File,
    /// <summary>An object's in-memory copy (an undo entry, a clone): the expression AND its last resolved number
    /// (<c>"Value"</c>), so a copy is at the same place as the original before anything re-resolves it.</summary>
    WithValues,
    /// <summary>Numbers only: the elaborator's cache key.</summary>
    NumbersOnly,
}

/// <summary>A bindable field on its way to or from the file: the numeric value (as the property holds it) and the
/// expression slots.</summary>
[JsonConverter(typeof(C3dFieldJsonConverter))]
public sealed class C3dFieldJson(C3dFieldSpec? spec, object? value, C3dExpr?[]? exprs)
{
    public C3dFieldSpec? Spec { get; } = spec;
    public object? Value { get; } = value;
    public C3dExpr?[]? Exprs { get; } = exprs;

    /// <summary>Write each bound component's resolved number beside its expression (<see cref="C3dSpelling.WithValues"/>).</summary>
    public bool WithValues { get; init; }
}

/// <summary>Writes and reads <see cref="C3dFieldJson"/> — a component is a number or <c>{ "Expr", "Unit" }</c>.</summary>
internal sealed class C3dFieldJsonConverter : JsonConverter<C3dFieldJson>
{
    public override C3dFieldJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Which property this is comes from the type info that called us, and the reader cannot see it — so the value is
        // read in its generic shape (a scalar or a list of components) and C3dFieldJson.Value is converted on Set.
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<object?>();
            var exprs = new List<C3dExpr?>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return Shape(values, exprs, array: true);
                ReadComponent(ref reader, values, exprs);
            }
            throw new JsonException(C3dDiagnostics.ArrayExpected("[…]").Render());
        }
        var one = new List<object?>();
        var e1 = new List<C3dExpr?>();
        ReadComponent(ref reader, one, e1);
        return Shape(one, e1, array: false);
    }

    internal static void ReadComponentPublic(ref Utf8JsonReader reader, List<object?> values, List<C3dExpr?> exprs)
        => ReadComponent(ref reader, values, exprs);

    private static void ReadComponent(ref Utf8JsonReader reader, List<object?> values, List<C3dExpr?> exprs)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                values.Add(reader.TryGetInt64(out long l) ? (object)l : reader.GetDouble());
                exprs.Add(null);
                return;
            case JsonTokenType.Null:
                values.Add(null);
                exprs.Add(null);
                return;
            case JsonTokenType.String:
                throw new JsonException(C3dDiagnostics.ExpressionAsString(reader.GetString() ?? "").Render());
            case JsonTokenType.StartObject:
                string? text = null, unit = null;
                object cached = 0L;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string key = reader.GetString() ?? "";
                    reader.Read();
                    if (string.Equals(key, "Expr", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String) text = reader.GetString();
                    else if (string.Equals(key, "Unit", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String) unit = reader.GetString();
                    else if (string.Equals(key, "Value", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.Number)
                        cached = reader.TryGetInt64(out long cl) ? (object)cl : reader.GetDouble();
                    else throw new JsonException(C3dDiagnostics.ExpressionShape(key).Render());
                }
                if (string.IsNullOrWhiteSpace(text)) throw new JsonException(C3dDiagnostics.ExpressionShape("Expr").Render());
                values.Add(cached);
                exprs.Add(new C3dExpr(text, unit));
                return;
            default:
                throw new JsonException(C3dDiagnostics.NumberExpected(reader.TokenType.ToString()).Render());
        }
    }

    private static C3dFieldJson Shape(List<object?> values, List<C3dExpr?> exprs, bool array)
        => new(null, array ? new RawComponents([.. values]) : values[0], exprs.Any(e => e is not null) ? [.. exprs] : null);

    public override void Write(Utf8JsonWriter writer, C3dFieldJson value, JsonSerializerOptions options)
    {
        var spec = value.Spec!;
        var slots = value.Exprs;
        if (spec.Arity == 1 && value.Value is not List<int>)
        {
            if (slots?[0] is { } e) { writer.WriteRawValue(Spell(e, value.WithValues ? Number(value.Value) : null), skipInputValidation: true); return; }
            switch (value.Value)
            {
                case long l: writer.WriteNumberValue(l); break;
                case double d: writer.WriteNumberValue(d); break;
                case null: writer.WriteNullValue(); break;
                default: writer.WriteNumberValue(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture)); break;
            }
            return;
        }
        var parts = new List<string>(spec.Arity);
        int n = value.Value is List<int> list ? list.Count : spec.Arity;
        for (int k = 0; k < n; k++)
        {
            double? v = value.Value switch
            {
                C3dPoint3 p => k switch { 0 => p.X, 1 => p.Y, _ => p.Z },
                C3dPoint2 p => k == 0 ? p.U : p.V,
                List<int> l => l[k],
                _ => null,
            };
            if (slots is not null && k < slots.Length && slots[k] is { } e)
            {
                parts.Add(Spell(e, value.WithValues ? ((long)(v ?? 0)).ToString(CultureInfo.InvariantCulture) : null));
                continue;
            }
            parts.Add(((long)(v ?? 0)).ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteRawValue("[" + string.Join(", ", parts) + "]", skipInputValidation: true);
    }

    /// <summary><c>{ "Expr": "w", "Unit": "Mil" }</c>, on one line.</summary>
    internal static string Spell(C3dExpr e, string? value = null)
    {
        var sb = new StringBuilder("{ \"Expr\": ").Append(Quote(e.Expr));
        if (e.Unit is { Length: > 0 } u) sb.Append(", \"Unit\": ").Append(Quote(u));
        if (value is not null) sb.Append(", \"Value\": ").Append(value);
        return sb.Append(" }").ToString();
    }

    private static string? Number(object? v) => v switch
    {
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Quote(string s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = C3dBindings.Encoder });
}

/// <summary>A list of components read before the property's own type is known.</summary>
internal sealed class RawComponents(object?[] values)
{
    public object?[] Values { get; } = values;
}
