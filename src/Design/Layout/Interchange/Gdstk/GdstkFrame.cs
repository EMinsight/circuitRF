// One gdstk cell as the worker's protocol carries it, both directions (brief-oasis-gdstk.md §5;
// tools/gdstk-worker/README.md "The protocol"): the JSON lists, and the two f64 blobs "xy" (every
// polygon's vertices, in order) and "path_xy" (every path's spine). The frame itself — the
// [jsonLen][binLen][JSON][bytes] codec — is the geometry worker's GeometryKernelFrame, reused rather than
// copied (§5); this file is only what goes inside it.
//
// COORDINATES travel as doubles that ARE integers, in the file's own database units (the worker rounds
// before it sends). Decode asserts that: a value that is not integral means the two sides disagree about
// the protocol, and a protocol disagreement is an error (GdstkFailure.Malformed), never a value to round
// on this side.

using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

/// <summary>A repetition as gdstk HOLDS it — five kinds for OASIS's eleven. <see cref="Kind"/> is
/// <c>rectangular</c> (<see cref="Columns"/> × <see cref="Rows"/> at <see cref="V1X"/>, <see cref="V2Y"/>),
/// <c>regular</c> (columns along v1, rows along v2), <c>explicit</c> (<see cref="Values"/> as x,y pairs),
/// or <c>explicit_x</c> / <c>explicit_y</c> (<see cref="Values"/> as coordinates along one axis).</summary>
public sealed record GdstkRepetition(
    string Kind, long Columns = 1, long Rows = 1,
    long V1X = 0, long V1Y = 0, long V2X = 0, long V2Y = 0, long[]? Values = null)
{
    /// <summary>How many copies, the original included — gdstk's <c>get_count</c>.</summary>
    public long Count => Kind switch
    {
        "rectangular" or "regular" => Columns * Rows,
        "explicit" => (Values?.Length ?? 0) / 2 + 1,
        _ => (Values?.Length ?? 0) + 1,
    };

    /// <summary>Every copy's displacement from the element's own position, the original's (0, 0) first —
    /// gdstk's <c>get_offsets</c>, in its order.</summary>
    public IEnumerable<(long X, long Y)> Offsets()
    {
        switch (Kind)
        {
            case "rectangular":
                for (long i = 0; i < Columns; i++)
                    for (long j = 0; j < Rows; j++)
                        yield return (i * V1X, j * V2Y);
                break;
            case "regular":
                for (long i = 0; i < Columns; i++)
                    for (long j = 0; j < Rows; j++)
                        yield return (i * V1X + j * V2X, i * V1Y + j * V2Y);
                break;
            case "explicit":
                yield return (0, 0);
                for (int k = 0; k + 1 < (Values?.Length ?? 0); k += 2) yield return (Values![k], Values[k + 1]);
                break;
            default:
                yield return (0, 0);
                foreach (long v in Values ?? []) yield return Kind == "explicit_x" ? (v, 0) : (0, v);
                break;
        }
    }
}

public sealed record GdstkPolygon(int Layer, int Datatype, long[] Xy, GdstkRepetition? Repetition = null);

/// <summary><see cref="End"/> is gdstk's name: <c>flush</c>, <c>round</c>, <c>halfwidth</c>,
/// <c>extended</c> (with <see cref="ExtensionStart"/>/<see cref="ExtensionEnd"/>), or one gdstk holds
/// for paths it built itself (<c>smooth</c>, <c>function</c>).</summary>
public sealed record GdstkPath(
    int Layer, int Datatype, long Width, string End, long ExtensionStart, long ExtensionEnd, long[] Xy,
    GdstkRepetition? Repetition = null);

/// <summary>Rotation in degrees. <see cref="Anchor"/> is gdstk's (GDSII PRESENTATION's low four bits).</summary>
public sealed record GdstkLabel(
    int Layer, int TextType, string Text, long X, long Y, int Anchor, double RotationDegrees,
    double Magnification, bool Mirror, GdstkRepetition? Repetition = null);

/// <summary>Rotation in degrees, mirror applied first (about x), as in GDSII.</summary>
public sealed record GdstkReference(
    string Cell, long X, long Y, double RotationDegrees, double Magnification, bool Mirror,
    GdstkRepetition? Repetition = null);

/// <summary>What the worker read but circuitRF has no form for, counted (the <c>cell</c> reply's <c>notes</c>).</summary>
public sealed record GdstkCellNotes(
    long ElementsWithProperties, long RobustPaths, long MultiElementPaths, bool CellProperties, long OffGridRounded)
{
    public static readonly GdstkCellNotes None = new(0, 0, 0, false, 0);
}

public sealed record GdstkCell(
    string Name,
    IReadOnlyList<GdstkPolygon> Polygons,
    IReadOnlyList<GdstkPath> Paths,
    IReadOnlyList<GdstkLabel> Labels,
    IReadOnlyList<GdstkReference> References,
    GdstkCellNotes Notes);

/// <summary>Encodes and decodes a <see cref="GdstkCell"/> inside a worker frame.</summary>
public static class GdstkFrame
{
    /// <summary>The <c>add-cell</c> request for <paramref name="cell"/> into the write <paramref name="handle"/>.</summary>
    public static GeometryKernelMessage AddCell(int handle, GdstkCell cell)
    {
        var xy = new List<double>();
        var pathXy = new List<double>();
        var polygons = new JsonArray();
        foreach (var p in cell.Polygons)
        {
            var o = new JsonObject { ["layer"] = p.Layer, ["datatype"] = p.Datatype, ["n"] = p.Xy.Length / 2 };
            AddRepetition(o, p.Repetition);
            polygons.Add(o);
            foreach (long v in p.Xy) xy.Add(v);
        }
        var paths = new JsonArray();
        foreach (var p in cell.Paths)
        {
            var o = new JsonObject
            {
                ["layer"] = p.Layer, ["datatype"] = p.Datatype, ["width"] = p.Width, ["end"] = p.End, ["n"] = p.Xy.Length / 2,
            };
            if (p.End == "extended") o["ext"] = new JsonArray(p.ExtensionStart, p.ExtensionEnd);
            AddRepetition(o, p.Repetition);
            paths.Add(o);
            foreach (long v in p.Xy) pathXy.Add(v);
        }
        var labels = new JsonArray();
        foreach (var l in cell.Labels)
        {
            var o = new JsonObject
            {
                ["layer"] = l.Layer, ["texttype"] = l.TextType, ["text"] = l.Text, ["x"] = l.X, ["y"] = l.Y,
                ["anchor"] = l.Anchor, ["rotation"] = l.RotationDegrees, ["magnification"] = l.Magnification, ["mirror"] = l.Mirror,
            };
            AddRepetition(o, l.Repetition);
            labels.Add(o);
        }
        var refs = new JsonArray();
        foreach (var r in cell.References)
        {
            var o = new JsonObject
            {
                ["cell"] = r.Cell, ["x"] = r.X, ["y"] = r.Y,
                ["rotation"] = r.RotationDegrees, ["magnification"] = r.Magnification, ["mirror"] = r.Mirror,
            };
            AddRepetition(o, r.Repetition);
            refs.Add(o);
        }

        var json = new JsonObject
        {
            ["op"] = "add-cell", ["handle"] = handle, ["name"] = cell.Name,
            ["polygons"] = polygons, ["paths"] = paths, ["labels"] = labels, ["refs"] = refs,
        };
        return new GeometryKernelMessage(json,
        [
            GeometryKernelBlob.OfDoubles("xy", xy.ToArray()),
            GeometryKernelBlob.OfDoubles("path_xy", pathXy.ToArray()),
        ]);
    }

    private static void AddRepetition(JsonObject o, GdstkRepetition? rep)
    {
        if (rep is null) return;
        var r = new JsonObject { ["kind"] = rep.Kind };
        switch (rep.Kind)
        {
            case "rectangular":
                r["columns"] = rep.Columns; r["rows"] = rep.Rows;
                r["spacing"] = new JsonArray(rep.V1X, rep.V2Y);
                break;
            case "regular":
                r["columns"] = rep.Columns; r["rows"] = rep.Rows;
                r["v1"] = new JsonArray(rep.V1X, rep.V1Y);
                r["v2"] = new JsonArray(rep.V2X, rep.V2Y);
                break;
            case "explicit":
                r["offsets"] = new JsonArray((rep.Values ?? []).Select(v => (JsonNode?)v).ToArray());
                break;
            default:
                r["coords"] = new JsonArray((rep.Values ?? []).Select(v => (JsonNode?)v).ToArray());
                break;
        }
        o["rep"] = r;
    }

    /// <summary>Decodes a <c>cell</c> reply. Throws <see cref="GdstkException"/> (<see cref="GdstkFailure.Malformed"/>) when the reply does
    /// not hold what the protocol says — a missing member, a vertex count the blobs do not carry, or a
    /// coordinate that is not an integer.</summary>
    public static GdstkCell DecodeCell(GeometryKernelMessage reply)
    {
        var json = reply.Json;
        string name = Str(json, "name");
        double[] xy = reply.Blob("xy")?.Doubles() ?? [];
        double[] pathXy = reply.Blob("path_xy")?.Doubles() ?? [];
        int xi = 0, pi = 0;

        var polygons = new List<GdstkPolygon>();
        foreach (var p in Arr(json, "polygons"))
        {
            int n = (int)Int(p, "n");
            polygons.Add(new GdstkPolygon((int)Int(p, "layer"), (int)Int(p, "datatype"), Take(xy, ref xi, n, "xy"), Repetition(p)));
        }

        var paths = new List<GdstkPath>();
        foreach (var p in Arr(json, "paths"))
        {
            int n = (int)Int(p, "n");
            long e0 = 0, e1 = 0;
            if (p["ext"] is JsonArray ext && ext.Count == 2) { e0 = Integral(ext[0]!.GetValue<double>(), "ext"); e1 = Integral(ext[1]!.GetValue<double>(), "ext"); }
            paths.Add(new GdstkPath((int)Int(p, "layer"), (int)Int(p, "datatype"), Int(p, "width"), Str(p, "end"), e0, e1,
                                    Take(pathXy, ref pi, n, "path_xy"), Repetition(p)));
        }
        if (xi != xy.Length || pi != pathXy.Length)
            throw Bad($"cell \"{name}\" carries vertices no element declares.");

        var labels = new List<GdstkLabel>();
        foreach (var l in Arr(json, "labels"))
            labels.Add(new GdstkLabel((int)Int(l, "layer"), (int)Int(l, "texttype"), Str(l, "text"), Int(l, "x"), Int(l, "y"),
                                      (int)Int(l, "anchor"), Num(l, "rotation"), Num(l, "magnification"), Bool(l, "mirror"),
                                      Repetition(l)));

        var refs = new List<GdstkReference>();
        foreach (var r in Arr(json, "refs"))
            refs.Add(new GdstkReference(Str(r, "cell"), Int(r, "x"), Int(r, "y"), Num(r, "rotation"), Num(r, "magnification"),
                                        Bool(r, "mirror"), Repetition(r)));

        var notes = GdstkCellNotes.None;
        if (json["notes"] is JsonObject no)
            notes = new GdstkCellNotes(IntOr(no, "elements_with_properties"), IntOr(no, "robust_paths"),
                                       IntOr(no, "multi_element_paths"), no["cell_properties"] is JsonValue cp && cp.GetValue<bool>(),
                                       IntOr(no, "off_grid_rounded"));

        return new GdstkCell(name, polygons, paths, labels, refs, notes);
    }

    private static GdstkRepetition? Repetition(JsonNode o)
    {
        if (o["rep"] is not JsonObject r) return null;
        string kind = Str(r, "kind");
        switch (kind)
        {
            case "rectangular":
                var s = Pair(r, "spacing");
                return new GdstkRepetition(kind, Int(r, "columns"), Int(r, "rows"), V1X: s.X, V2Y: s.Y);
            case "regular":
                var v1 = Pair(r, "v1");
                var v2 = Pair(r, "v2");
                return new GdstkRepetition(kind, Int(r, "columns"), Int(r, "rows"), v1.X, v1.Y, v2.X, v2.Y);
            case "explicit":
                return new GdstkRepetition(kind, Values: Longs(r, "offsets"));
            case "explicit_x" or "explicit_y":
                return new GdstkRepetition(kind, Values: Longs(r, "coords"));
            default:
                throw Bad($"a repetition of unknown kind \"{kind}\".");
        }
    }

    // ── Reading members, strictly ─────────────────────────────────────────────

    private static IEnumerable<JsonNode> Arr(JsonNode o, string key) =>
        o[key] is JsonArray a ? a.Select(n => n ?? throw Missing(key)) : [];

    private static string Str(JsonNode o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : throw Missing(key);

    private static double Num(JsonNode o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? d : throw Missing(key);

    private static bool Bool(JsonNode o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : throw Missing(key);

    private static long Int(JsonNode o, string key) => Integral(Num(o, key), key);

    private static long IntOr(JsonNode o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? Integral(d, key) : 0;

    private static (long X, long Y) Pair(JsonNode o, string key) =>
        o[key] is JsonArray a && a.Count == 2
            ? (Integral(a[0]!.GetValue<double>(), key), Integral(a[1]!.GetValue<double>(), key))
            : throw Missing(key);

    private static long[] Longs(JsonNode o, string key) =>
        o[key] is JsonArray a ? a.Select(n => Integral(n!.GetValue<double>(), key)).ToArray() : throw Missing(key);

    private static long[] Take(double[] blob, ref int at, int n, string blobName)
    {
        if (n < 0 || at + 2 * n > blob.Length)
            throw Bad($"the elements declare more vertices than \"{blobName}\" carries.");
        var v = new long[2 * n];
        for (int k = 0; k < v.Length; k++) v[k] = Integral(blob[at + k], blobName);
        at += 2 * n;
        return v;
    }

    /// <summary>§5: every coordinate is an exact integer number of database units. Anything else is a
    /// protocol disagreement, not a value to round.</summary>
    internal static long Integral(double v, string what)
    {
        if (!double.IsFinite(v) || v != Math.Round(v) || Math.Abs(v) > 9.0e15)
            throw Bad($"{v:R} for \"{what}\" is not a whole number of database units.");
        return (long)v;
    }

    private static GdstkException Missing(string key) => Bad($"no usable \"{key}\".");

    private static GdstkException Bad(string problem) =>
        new(GdstkFailure.Malformed, GdstkDiagnostics.Malformed(problem));
}
