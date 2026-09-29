// brief-em3d-41 R-em3d41-3 — reading and writing a .c3d.
//
// The conventions are .clay's newer ones, because the reason is the same: a workspace's history is
// kept with git, so the file is written for a DIFF — a tab per level, LF on every platform, a point
// per line. Key order is the declaration order (JsonPropertyOrder where a base type's keys must frame
// a derived type's), nothing is ever sorted, and a field is omitted at its default only when it is a
// modifier: a placement that states nothing, a zero shear, an empty hole list. Geometry is always
// written, so a reader never has to know a default to know where a solid is.
//
// ROUND TRIP IS BYTE FOR BYTE (R-em3d41-3b): read then write gives the bytes that were read, for any
// file this writer produced. The one thing the writer changes is a NEGATIVE size, which it normalises
// by moving the corner (R-em3d41-2-dims) — in the model it is handed, so the document and the file
// agree afterwards.

using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD;

/// <summary>A <c>.c3d</c> this build cannot read, with the diagnostic that says why.</summary>
public sealed class C3dReadException(Diagnostic diagnostic) : Exception(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>Reads and writes <c>.c3d</c> files. Framework-free.</summary>
public static class C3dPersistence
{
    public const string Extension            = ".c3d";
    public const int    CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = Options(C3dSpelling.File);

    /// <summary>brief-em3d-51 — an object's in-memory spelling (undo entries, clones): each bound component carries its last
    /// resolved number beside its expression, so a copy is where the original is before anything re-resolves it.</summary>
    private static readonly JsonSerializerOptions ItemOpts = Options(C3dSpelling.WithValues);

    /// <summary>brief-em3d-51 R-em3d51-5b — the same spelling with every expression replaced by its resolved number: what
    /// the elaborator keys an object on, so changing a VAR re-elaborates exactly the objects whose values changed.</summary>
    private static readonly JsonSerializerOptions ResolvedOpts = Options(C3dSpelling.NumbersOnly);

    private static JsonSerializerOptions Options(C3dSpelling spelling) => new()
    {
        WriteIndented                     = true,
        IndentCharacter                   = '\t',
        IndentSize                        = 1,
        NewLine                           = "\n",
        DefaultIgnoreCondition            = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive       = true,
        // A hand-written file may put "$type" anywhere in an object. .clay requires it first and
        // refuses the whole file otherwise; nothing about a 3D view needs that trap.
        AllowOutOfOrderMetadataProperties = true,
        Converters =
        {
            new JsonStringEnumConverter(), new C3dInt64JsonConverter(), new C3dInt32JsonConverter(),
            new C3dDoubleJsonConverter(), new C3dIndexListJsonConverter(), new C3dIndexLoopsJsonConverter(),
            new C3dPoint2ListJsonConverter(), new C3dPoint3ListJsonConverter(), new C3dRingListJsonConverter(),
        },
        // brief-em3d-51 — an expression's '+' is written as a plus (C3dBindings.Encoder).
        Encoder = C3dBindings.Encoder,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info => C3dBindings.Modify(info, spelling), OmitEmpty },
        },
    };

    /// <summary>
    /// A list with nothing in it, and a placement that states nothing, are left out — except a
    /// <c>[JsonRequired]</c> one, which is <see cref="C3dDocument.Objects"/>: its presence is half of
    /// how a <c>.c3d</c> is recognised by content.
    /// </summary>
    private static void OmitEmpty(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        foreach (var p in info.Properties)
        {
            if (p.IsRequired || p.IsExtensionData) continue;
            // brief-em3d-64 R-em3d64-1b — a switch whose default is not its type's (Enabled, true) is written only when it
            // differs; an operand's empty Name is left out (R-em3d64-1d).
            if (p.AttributeProvider?.GetCustomAttributes(typeof(System.ComponentModel.DefaultValueAttribute), true) is [System.ComponentModel.DefaultValueAttribute dv, ..])
            {
                object? d = dv.Value;
                p.ShouldSerialize = (_, v) => !Equals(v, d);
                continue;
            }
            if (p.Name == nameof(C3dObject.Name) && typeof(C3dObject).IsAssignableFrom(info.Type))
            {
                p.ShouldSerialize = static (_, v) => v is string { Length: > 0 };
                continue;
            }
            if (typeof(ICollection).IsAssignableFrom(p.PropertyType))
                p.ShouldSerialize = static (_, v) => v is ICollection { Count: > 0 };
            else if (p.PropertyType == typeof(C3dPlacement))
                p.ShouldSerialize = static (_, v) => v is C3dPlacement { IsDefault: false };
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document as the file's text. Normalises a negative size in place first.</summary>
    public static string Serialize(C3dDocument doc)
    {
        Normalize(doc);
        return JsonSerializer.Serialize(doc, JsonOpts);
    }

    /// <summary>
    /// brief-em3d-83 R-em3d83-2 — the document as a RUN sees it: the file's text without its field plots. What Simulate keeps
    /// beside a run and what the stale banner compares with it, so adding, editing or hiding a plot never makes a result
    /// stale. Every such comparison reads this, never <see cref="Serialize"/>.
    /// </summary>
    public static string SerializeForRun(C3dDocument doc)
    {
        var plots = doc.FieldPlots;
        doc.FieldPlots = [];
        try { return Serialize(doc); }
        finally { doc.FieldPlots = plots; }
    }

    /// <summary>brief-em3d-83 — the field plots as the file spells them (an undo entry's before and after).</summary>
    public static string SerializeFieldPlots(IReadOnlyList<C3dFieldPlot> list) => JsonSerializer.Serialize(list, JsonOpts);

    public static List<C3dFieldPlot> DeserializeFieldPlots(string json)
        => JsonSerializer.Deserialize<List<C3dFieldPlot>>(json, JsonOpts) ?? [];

    public static void SaveToFile(string path, C3dDocument doc)
        => AtomicFile.WriteAllText(path, Serialize(doc));

    /// <summary>brief-em3d-42 R-em3d42-4 — one object as the file spells it: the elaborator's per-object cache
    /// key, so an object is re-elaborated exactly when what the file would say about it changes.</summary>
    public static string SerializeObject(C3dObject obj) => JsonSerializer.Serialize(obj, ItemOpts);

    /// <summary>brief-em3d-51 R-em3d51-5b — one object with its expressions replaced by their resolved numbers: the
    /// elaborator's cache key. Never read back.</summary>
    public static string SerializeResolved(C3dObject obj) => JsonSerializer.Serialize(obj, ResolvedOpts);

    /// <summary>brief-em3d-51 — the VAR list as the file spells it (an undo entry's before and after).</summary>
    public static string SerializeVariables(IReadOnlyList<C3dVariable> list) => JsonSerializer.Serialize(list, JsonOpts);

    public static List<C3dVariable> DeserializeVariables(string json) => JsonSerializer.Deserialize<List<C3dVariable>>(json, JsonOpts) ?? [];

    /// <summary>brief-em3d-43 — one object read back from <see cref="SerializeObject"/>'s text: what an undo
    /// entry stores, so an entry holds only the objects it changed, never the document.</summary>
    public static C3dObject DeserializeObject(string json)
        => JsonSerializer.Deserialize<C3dObject>(json, ItemOpts) ?? throw new C3dReadException(C3dDiagnostics.NotAnObject());

    /// <summary>brief-em3d-43 — one instance as the file spells it, and back.</summary>
    public static string SerializeInstance(C3dInstance inst) => JsonSerializer.Serialize(inst, ItemOpts);

    /// <summary>brief-em3d-49 — the document's face boundaries as the file spells them (an undo entry's before and after).</summary>
    public static string SerializeFaceBoundaries(IReadOnlyList<C3dFaceBoundary> list) => JsonSerializer.Serialize(list, JsonOpts);

    public static List<C3dFaceBoundary> DeserializeFaceBoundaries(string json)
        => JsonSerializer.Deserialize<List<C3dFaceBoundary>>(json, JsonOpts) ?? [];

    /// <summary>brief-em3d-49 — the document's ports, as the file spells them.</summary>
    public static string SerializePorts(IReadOnlyList<C3dPort> list) => JsonSerializer.Serialize(list, ItemOpts);

    public static List<C3dPort> DeserializePorts(string json) => JsonSerializer.Deserialize<List<C3dPort>>(json, ItemOpts) ?? [];

    /// <summary>brief-em3d-75 — the thermal places (heat sources, probes, mesh regions, contact overrides) as one text, in the
    /// in-memory spelling (each bound component keeps its number): what an undo entry of a thermal edit holds.</summary>
    public static string SerializeThermalPlaces(C3dDocument doc)
        => JsonSerializer.Serialize(new ThermalPlaces(doc.HeatSources, doc.Probes, doc.MeshRegions, doc.ContactResistances,
                                                      doc.EffectiveBlocks, doc.SymmetryPlanes), ItemOpts);

    /// <summary>Writes <see cref="SerializeThermalPlaces"/>' text back into <paramref name="doc"/>.</summary>
    public static void ApplyThermalPlaces(C3dDocument doc, string json)
    {
        var t = JsonSerializer.Deserialize<ThermalPlaces>(json, ItemOpts);
        doc.HeatSources = t?.HeatSources ?? [];
        doc.Probes = t?.Probes ?? [];
        doc.MeshRegions = t?.MeshRegions ?? [];
        doc.ContactResistances = t?.ContactResistances ?? [];
        doc.EffectiveBlocks = t?.EffectiveBlocks ?? [];
        doc.SymmetryPlanes = t?.SymmetryPlanes ?? [];
    }

    private sealed record ThermalPlaces(List<C3dHeatSource> HeatSources, List<C3dProbe> Probes, List<C3dMeshRegion> MeshRegions,
                                        List<C3dContactResistance> ContactResistances, List<C3dEffectiveBlock>? EffectiveBlocks = null,
                                        List<C3dSymmetryPlane>? SymmetryPlanes = null);

    /// <summary>brief-em3d-49 — an embedded setup list, as the file spells it.</summary>
    public static string SerializeSetups(IReadOnlyList<JsonElement> list) => JsonSerializer.Serialize(list, JsonOpts);

    public static List<JsonElement> DeserializeSetups(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json, JsonOpts) ?? [];

    public static C3dInstance DeserializeInstance(string json)
        => JsonSerializer.Deserialize<C3dInstance>(json, ItemOpts) ?? throw new C3dReadException(C3dDiagnostics.NotAnObject());

    /// <summary>R-em3d41-2-dims: a size component is positive; a negative one moves the corner. A
    /// prism's height and a cylinder's length keep their sign — each says which way it was pulled.</summary>
    /// <para>brief-em3d-51 — an object holding an expression is left alone: moving a corner under a size the expression
    /// will recompute would change what the file means. A negative size from an expression is refused at resolution.</para>
    public static void Normalize(C3dDocument doc)
    {
        foreach (var o in doc.Objects.SelectMany(C3dOperands.SelfAndDescendants))
        {
            if (C3dBindings.HasAny(o)) continue;
            switch (o)
            {
                case C3dBox b:
                    var (mx, sx) = Positive(b.Min.X, b.Size.X);
                    var (my, sy) = Positive(b.Min.Y, b.Size.Y);
                    var (mz, sz) = Positive(b.Min.Z, b.Size.Z);
                    b.Min  = new C3dPoint3(mx, my, mz);
                    b.Size = new C3dPoint3(sx, sy, sz);
                    break;
                case C3dSheet { Rect: { } r }:
                    var (mu, su) = Positive(r.Min.U, r.Size.U);
                    var (mv, sv) = Positive(r.Min.V, r.Size.V);
                    r.Min  = new C3dPoint2(mu, mv);
                    r.Size = new C3dPoint2(su, sv);
                    break;
            }
        }

        static (long Min, long Size) Positive(long min, long size) => size < 0 ? (min + size, -size) : (min, size);
    }

    // ── Read ──────────────────────────────────────────────────────────────────────────────────

    /// <exception cref="C3dReadException">The text is not a 3D view this build can read — not JSON, a
    /// newer format, an object kind it does not know, or a string where a number belongs. Each names
    /// what it found. A document that READS but is wrong (a duplicate name, an open polyhedron) is not
    /// refused here: <see cref="C3dValidation"/> lists those, all of them.</exception>
    public static C3dDocument Deserialize(string json)
    {
        PreScan(json);

        C3dDocument? doc;
        try { doc = JsonSerializer.Deserialize<C3dDocument>(json, JsonOpts); }
        catch (JsonException ex) { throw new C3dReadException(C3dDiagnostics.Unreadable(ex.Message)); }

        return doc ?? throw new C3dReadException(C3dDiagnostics.NotAnObject());
    }

    public static C3dDocument LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    /// <summary>
    /// What the serializer would report badly or not at all: a newer format (checked before binding, so
    /// a later build's new keys are not what the user is told about), and every object kind this build
    /// does not know — ALL of them, by name, not the first one the serializer tripped on.
    /// </summary>
    private static void PreScan(string json)
    {
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false }); }
        catch (JsonException ex) { throw new C3dReadException(C3dDiagnostics.Unreadable(ex.Message)); }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new C3dReadException(C3dDiagnostics.NotAnObject());

            if (TryGet(root, nameof(C3dDocument.FormatVersion), out var fv)
                && fv.ValueKind == JsonValueKind.Number && fv.TryGetInt32(out int version)
                && version > CurrentFormatVersion)
                throw new C3dReadException(C3dDiagnostics.NewerFormat(version, CurrentFormatVersion));

            if (!TryGet(root, nameof(C3dDocument.Objects), out var objects) || objects.ValueKind != JsonValueKind.Array)
                return;   // the serializer's own "required" refusal names it

            var known   = C3dObject.Kinds.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
            var unknown = new List<string>();
            int index   = 0;
            foreach (var o in objects.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.Object)
                {
                    if (!o.TryGetProperty("$type", out var kind) || kind.ValueKind != JsonValueKind.String)
                        throw new C3dReadException(C3dDiagnostics.MissingKind(index));
                    Scan(o, kind.GetString() ?? "", $"object {index}", known, unknown);
                }
                index++;
            }

            if (unknown.Count > 0)
                throw new C3dReadException(C3dDiagnostics.UnknownKinds(
                    string.Join(", ", unknown), string.Join(", ", C3dObject.Kinds.Select(k => k.Name))));
        }
    }

    /// <summary>
    /// One object's kind, and — brief-em3d-64 R-em3d64-1f — every operand inside it: an unknown kind nested in a boolean is
    /// named as clearly as one at the top. An operand is named by its own Name, or by where it sits (<c>the Blank of 'lid'</c>).
    /// </summary>
    private static void Scan(JsonElement o, string kind, string where, HashSet<string> known, List<string> unknown)
    {
        string name = TryGet(o, nameof(C3dObject.Name), out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        string label = name.Length > 0 ? $"object '{name}'" : where;
        if (!known.Contains(kind))
        {
            unknown.Add($"{label} (a \"{kind}\")");
            return;
        }
        void Operand(JsonElement e, string at)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (!e.TryGetProperty("$type", out var k) || k.ValueKind != JsonValueKind.String)
                throw new C3dReadException(C3dDiagnostics.MissingOperandKind(at));
            Scan(e, k.GetString() ?? "", at, known, unknown);
        }
        string owner = name.Length > 0 ? $"'{name}'" : where;
        foreach (string key in (string[])[nameof(C3dBoolean.Blank), nameof(C3dFillet.Target)])
            if (TryGet(o, key, out var e)) Operand(e, $"the {key} of {owner}");
        if (TryGet(o, nameof(C3dBoolean.Tools), out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            int k = 0;
            foreach (var t in tools.EnumerateArray()) Operand(t, $"tool {k++} of {owner}");
        }
    }

    /// <summary>A property by name, case-insensitive — the reader's own matching.</summary>
    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    // ── Recognition by content (overview §1c) ─────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="path"/> holds a circuitRF 3D view: a JSON object with a
    /// <c>FormatVersion</c> and an <c>Objects</c> list at its top level. <b>By content, not by
    /// extension</b>, because an established motion-capture format also uses <c>.c3d</c> — a file from
    /// that program is binary and answers false on its first byte, so it is named as foreign rather
    /// than reported as a broken 3D view. Reads only until both keys are seen.
    /// </summary>
    public static bool LooksLikeC3d(string path)
    {
        byte[] bytes;
        try { bytes = GzipTextFile.ReadAllBytesAutoGzip(path); }
        catch { return false; }

        var span = new ReadOnlySpan<byte>(bytes);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..];

        try
        {
            var reader = new Utf8JsonReader(span, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            bool version = false, objects = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) break;
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;

                string name = reader.GetString() ?? "";
                reader.Read();
                if (string.Equals(name, nameof(C3dDocument.FormatVersion), StringComparison.OrdinalIgnoreCase))
                    version = reader.TokenType == JsonTokenType.Number;
                else if (string.Equals(name, nameof(C3dDocument.Objects), StringComparison.OrdinalIgnoreCase))
                    objects = reader.TokenType == JsonTokenType.StartArray;
                if (version && objects) return true;
                reader.Skip();
            }
            return false;
        }
        catch (JsonException) { return false; }
    }
}
