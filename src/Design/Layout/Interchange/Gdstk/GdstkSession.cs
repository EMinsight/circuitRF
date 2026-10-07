// The worker's operations, one method per request (brief-oasis-gdstk.md §5): open, cell, close to read a
// library one cell at a time, and begin-write, add-cell, finish-write to write one. Each request goes
// through GdstkWorker.Send, so each is bounded by a deadline and a memory cap scaled to the bytes it
// concerns, and a refusal becomes a GdstkException whose sentence names the file.

using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.Layout.Interchange.Gdstk;

/// <summary>The two formats gdstk reads and writes.</summary>
public enum GdstkFormat { Gdsii, Oasis }

/// <summary>One cell of an opened library, as <c>open</c> lists it. <see cref="Top"/>: no other cell references it.</summary>
public sealed record GdstkCellSummary(string Name, long Polygons, long Paths, long Labels, long References, bool Top);

/// <summary>An OASIS <c>LAYERNAME</c> record: a name for an interval of layers and of datatypes (or text types).</summary>
public sealed record GdstkLayerName(string Name, bool IsText, int LayerIntervalType, long LayerA, long LayerB,
                                    int TypeIntervalType, long TypeA, long TypeB);

/// <summary>An opened library: its handle, its own units, its cells and what gdstk said while reading it.</summary>
public sealed record GdstkLibrary(
    int Handle, string Path, long FileBytes, double UnitMeters, double PrecisionMeters,
    IReadOnlyList<GdstkCellSummary> Cells, IReadOnlyList<GdstkLayerName> LayerNames,
    IReadOnlyList<string> Messages, bool LibraryProperties)
{
    /// <summary>The file's database units per micrometre — circuitRF's <c>DbuPerMicron</c> vocabulary.</summary>
    public double SourceDbuPerMicron => 1e-6 / PrecisionMeters;
}

/// <summary>What <c>finish-write</c> reported.</summary>
public sealed record GdstkWriteResult(long Bytes, long Cells, IReadOnlyList<string> Messages);

public sealed class GdstkSession(GdstkWorker worker)
{
    public static string WireName(GdstkFormat format) => format == GdstkFormat.Oasis ? "oas" : "gds";

    public static string DisplayName(GdstkFormat format) => format == GdstkFormat.Oasis ? "OASIS" : "GDSII";

    // ── Reading ────────────────────────────────────────────────────────────────

    /// <summary>Opens <paramref name="path"/>. The worker adds Windows' long-path prefix itself, so the
    /// path goes as it is (tools/gdstk-worker/RESOLVED.md).</summary>
    public GdstkLibrary Open(string path, GdstkFormat format, CancellationToken token = default)
    {
        long size = File.Exists(path) ? new FileInfo(path).Length : 0;
        var (deadline, cap) = worker.BoundsFor(size);
        var reply = worker.Send(new GeometryKernelMessage(new JsonObject
        {
            ["op"] = "open", ["path"] = path, ["format"] = WireName(format),
            // G0's finding: gdstk drops path points closer than the tolerance (strictly), so 0.5 keeps a
            // one-unit segment.
            ["tolerance_dbu"] = 0.5,
        }), deadline, cap, $"reading {path}", token);
        ThrowIfRefused(reply, $"gdstk could not read {path}");

        var j = reply.Json;
        var cells = (j["cells"] as JsonArray ?? []).Select(c => new GdstkCellSummary(
            c!["name"]!.GetValue<string>(), Long(c, "polygons"), Long(c, "paths"), Long(c, "labels"), Long(c, "refs"),
            c["top"] is JsonValue t && t.GetValue<bool>())).ToList();
        var layerNames = (j["layer_names"] as JsonArray ?? []).Select(n => new GdstkLayerName(
            n!["name"]!.GetValue<string>(), n["kind"]?.GetValue<string>() == "text",
            (int)Long(n, "layer_type"), Long(n, "layer_a"), Long(n, "layer_b"),
            (int)Long(n, "type_type"), Long(n, "type_a"), Long(n, "type_b"))).ToList();
        var messages = (j["messages"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).ToList();
        double unit = j["unit_m"]!.GetValue<double>();
        double precision = j["precision_m"]!.GetValue<double>();
        if (!(precision > 0) || !(unit > 0))
            throw new GdstkException(GdstkFailure.Malformed, GdstkDiagnostics.Malformed($"units {unit:R} m / {precision:R} m for {path}."));
        return new GdstkLibrary((int)Long(j, "handle"), path, size, unit, precision, cells, layerNames, messages,
                                Long(j, "library_properties") != 0);
    }

    public GdstkCell ReadCell(GdstkLibrary library, string name, CancellationToken token = default)
    {
        var (deadline, cap) = worker.BoundsFor(library.FileBytes);
        var reply = worker.Send(new GeometryKernelMessage(new JsonObject
        {
            ["op"] = "cell", ["handle"] = library.Handle, ["name"] = name,
        }), deadline, cap, $"reading {library.Path}", token);
        ThrowIfRefused(reply, $"gdstk could not read cell \"{name}\" of {library.Path}");
        return GdstkFrame.DecodeCell(reply);
    }

    public void Close(GdstkLibrary library, CancellationToken token = default)
    {
        var (deadline, cap) = worker.BoundsFor(0);
        var reply = worker.Send(new GeometryKernelMessage(new JsonObject { ["op"] = "close", ["handle"] = library.Handle }),
                                deadline, cap, $"closing {library.Path}", token);
        ThrowIfRefused(reply, $"gdstk could not close {library.Path}");
    }

    // ── Writing ────────────────────────────────────────────────────────────────

    private long _sent;

    /// <summary>Starts a write. <paramref name="options"/> are the format's (§9b: OASIS compression,
    /// validation…); null takes the worker's defaults.</summary>
    public int BeginWrite(GdstkFormat format, double unitMeters, double precisionMeters, JsonObject? options = null,
                          CancellationToken token = default)
    {
        _sent = 0;
        var json = new JsonObject
        {
            ["op"] = "begin-write", ["format"] = WireName(format), ["unit_m"] = unitMeters, ["precision_m"] = precisionMeters,
        };
        if (options is not null) json["options"] = options;
        var (deadline, cap) = worker.BoundsFor(0);
        var reply = worker.Send(new GeometryKernelMessage(json), deadline, cap, "starting a write", token);
        ThrowIfRefused(reply, "gdstk could not start a write");
        return (int)Long(reply.Json, "handle");
    }

    public void AddCell(int handle, GdstkCell cell, string path, CancellationToken token = default)
    {
        var request = GdstkFrame.AddCell(handle, cell);
        long bytes = request.Blobs.Sum(b => (long)b.Data.Length);
        _sent += bytes;
        var (deadline, cap) = worker.BoundsFor(bytes);
        var reply = worker.Send(request, deadline, cap, $"writing {path}", token);
        ThrowIfRefused(reply, $"gdstk could not take cell \"{cell.Name}\" for {path}");
    }

    /// <summary>Writes the file. The worker writes <c>&lt;path&gt;.part</c> and renames it, so a failed
    /// write leaves no partial file at <paramref name="path"/>.</summary>
    public GdstkWriteResult FinishWrite(int handle, string path, CancellationToken token = default)
    {
        var (deadline, cap) = worker.BoundsFor(_sent);
        var reply = worker.Send(new GeometryKernelMessage(new JsonObject
        {
            ["op"] = "finish-write", ["handle"] = handle, ["path"] = path,
        }), deadline, cap, $"writing {path}", token);
        ThrowIfRefused(reply, $"gdstk could not write {path}");
        var messages = (reply.Json["messages"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).ToList();
        return new GdstkWriteResult(Long(reply.Json, "bytes"), Long(reply.Json, "cells"), messages);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void ThrowIfRefused(GeometryKernelMessage reply, string what)
    {
        if (reply.Ok) return;
        string code = reply.Text("code") ?? "unknown";
        string detail = reply.Text("detail") ?? "";
        throw new GdstkException(GdstkFailure.Refused, GdstkDiagnostics.Refused(what, code, detail), code);
    }

    private static long Long(JsonNode o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? GdstkFrame.Integral(d, key) : 0;
}
