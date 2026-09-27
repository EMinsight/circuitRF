// brief-em3d-63 R-em3d63-4a — the geometry worker's frame, as bytes on a pipe:
//
//     [uint32 jsonLen][uint32 binLen][jsonLen bytes UTF-8 JSON][binLen bytes]        little-endian
//
// The device worker's layout on purpose, so a hex dump of either reads the same way — with one
// difference: the binary part is BYTES, not doubles, because a geometry reply carries doubles
// (vertices), 32-bit integers (triangles, face ids) and opaque B-rep/STEP bytes. The JSON header
// declares the sections in order: "blobs": [{"name":"vertices","type":"f64","count":3012}, …].
//
// A new codec, not DeviceWorkerFrame's: that one lives in src/Core and is doubles-only. It keeps the
// example worker's lessons: a reply is read until it is ALL here (a pipe hands back less than asked),
// and the writer flushes after every frame (or the other side waits on a buffer forever).

using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.ThreeD.Occ;

/// <summary>What one blob holds: 64-bit doubles, 32-bit unsigned integers, or opaque bytes.</summary>
public enum GeometryKernelBlobType { F64, U32, Bytes }

/// <summary>One named section of a frame's binary part.</summary>
public sealed class GeometryKernelBlob
{
    public GeometryKernelBlob(string name, GeometryKernelBlobType type, byte[] data)
    {
        int unit = UnitOf(type);
        if (data.Length % unit != 0)
            throw new ArgumentException($"A {Spell(type)} blob holds a whole number of {unit}-byte values; '{name}' has {data.Length} bytes.");
        Name = name;
        Type = type;
        Data = data;
    }

    public string Name { get; }
    public GeometryKernelBlobType Type { get; }

    /// <summary>The bytes as they travel: little-endian values, or the opaque content.</summary>
    public byte[] Data { get; }

    /// <summary>How many values: doubles, integers or bytes.</summary>
    public int Count => Data.Length / UnitOf(Type);

    public static GeometryKernelBlob OfDoubles(string name, ReadOnlySpan<double> values)
    {
        var b = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(8 * i), values[i]);
        return new(name, GeometryKernelBlobType.F64, b);
    }

    public static GeometryKernelBlob OfUInts(string name, ReadOnlySpan<uint> values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), values[i]);
        return new(name, GeometryKernelBlobType.U32, b);
    }

    public static GeometryKernelBlob OfBytes(string name, byte[] bytes) => new(name, GeometryKernelBlobType.Bytes, bytes);

    public double[] Doubles()
    {
        Expect(GeometryKernelBlobType.F64);
        var v = new double[Count];
        for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan(8 * i));
        return v;
    }

    public uint[] UInts()
    {
        Expect(GeometryKernelBlobType.U32);
        var v = new uint[Count];
        for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(4 * i));
        return v;
    }

    private void Expect(GeometryKernelBlobType t)
    {
        if (Type != t) throw new InvalidDataException($"Blob '{Name}' is {Spell(Type)}, not {Spell(t)}.");
    }

    internal static int UnitOf(GeometryKernelBlobType t) => t switch
    {
        GeometryKernelBlobType.F64 => 8,
        GeometryKernelBlobType.U32 => 4,
        _ => 1,
    };

    internal static string Spell(GeometryKernelBlobType t) => t switch
    {
        GeometryKernelBlobType.F64 => "f64",
        GeometryKernelBlobType.U32 => "u32",
        _ => "bytes",
    };

    internal static GeometryKernelBlobType? Parse(string? s) => s switch
    {
        "f64" => GeometryKernelBlobType.F64,
        "u32" => GeometryKernelBlobType.U32,
        "bytes" => GeometryKernelBlobType.Bytes,
        _ => null,
    };
}

/// <summary>One frame: a JSON object and the blobs its <c>blobs</c> array declares.</summary>
public sealed class GeometryKernelMessage
{
    public GeometryKernelMessage(JsonObject json, IReadOnlyList<GeometryKernelBlob>? blobs = null)
    {
        Json = json;
        Blobs = blobs ?? [];
    }

    public JsonObject Json { get; }
    public IReadOnlyList<GeometryKernelBlob> Blobs { get; }

    public GeometryKernelBlob? Blob(string name) => Blobs.FirstOrDefault(b => b.Name == name);

    /// <summary>True for a reply whose <c>ok</c> is true.</summary>
    public bool Ok => Json["ok"] is JsonValue v && v.TryGetValue(out bool ok) && ok;

    /// <summary>A reply's member as a string, or null.</summary>
    public string? Text(string key) => Json[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>Reads and writes <see cref="GeometryKernelMessage"/>s in the worker's framing.</summary>
public static class GeometryKernelFrame
{
    /// <summary>A header larger than this is refused rather than allocated: a corrupt length would
    /// otherwise ask for gigabytes.</summary>
    public const int MaxJsonBytes = 256 << 20;

    /// <summary>The frame's bytes. The header's <c>blobs</c> array is written from <see cref="GeometryKernelMessage.Blobs"/>,
    /// whatever the JSON object held.</summary>
    public static byte[] Encode(GeometryKernelMessage message)
    {
        var header = (JsonObject)message.Json.DeepClone();
        header.Remove("blobs");
        if (message.Blobs.Count > 0)
        {
            var list = new JsonArray();
            foreach (var b in message.Blobs)
                list.Add(new JsonObject { ["name"] = b.Name, ["type"] = GeometryKernelBlob.Spell(b.Type), ["count"] = b.Count });
            header["blobs"] = list;
        }
        byte[] json = Encoding.UTF8.GetBytes(header.ToJsonString());
        long bin = message.Blobs.Sum(b => (long)b.Data.Length);
        if (bin > uint.MaxValue) throw new ArgumentException("A frame's binary part is limited to 4 GB.");

        var frame = new byte[8 + json.Length + bin];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0), (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)bin);
        json.CopyTo(frame, 8);
        long at = 8 + json.Length;
        foreach (var b in message.Blobs)
        {
            b.Data.CopyTo(frame, at);
            at += b.Data.Length;
        }
        return frame;
    }

    /// <summary>Writes one frame and flushes it.</summary>
    public static void Write(Stream stream, GeometryKernelMessage message)
    {
        stream.Write(Encode(message));
        stream.Flush();
    }

    /// <summary>
    /// Reads one frame. Null when the stream ends cleanly BEFORE a frame starts (the other side closed
    /// its end); <see cref="EndOfStreamException"/> when it ends inside one; <see cref="InvalidDataException"/>
    /// when the header is not a JSON object or its blobs do not account for the binary part exactly.
    /// </summary>
    public static GeometryKernelMessage? Read(Stream stream)
    {
        var head = new byte[8];
        int got = Fill(stream, head);
        if (got == 0) return null;
        if (got < head.Length) throw new EndOfStreamException("The geometry worker's pipe closed inside a frame header.");
        uint jl = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0));
        uint bl = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        if (jl > MaxJsonBytes) throw new InvalidDataException($"A frame declares a {jl}-byte header; the limit is {MaxJsonBytes}.");
        if (bl > int.MaxValue) throw new InvalidDataException($"A frame declares a {bl}-byte binary part; the limit is 2 GB.");

        var json = new byte[jl];
        var bin = new byte[bl];
        if (Fill(stream, json) < json.Length || Fill(stream, bin) < bin.Length)
            throw new EndOfStreamException("The geometry worker's pipe closed inside a frame.");
        return Parse(json, bin);
    }

    /// <summary>Decodes a whole frame held in memory (the disk cache stores replies this way).</summary>
    public static GeometryKernelMessage Decode(byte[] frame)
    {
        using var ms = new MemoryStream(frame, writable: false);
        var m = Read(ms) ?? throw new EndOfStreamException("An empty frame.");
        if (ms.Position != ms.Length) throw new InvalidDataException("Bytes follow the frame.");
        return m;
    }

    private static GeometryKernelMessage Parse(byte[] json, byte[] bin)
    {
        JsonObject header;
        try
        {
            header = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("A frame's header is not a JSON object.");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new InvalidDataException("A frame's header is not valid JSON: " + e.Message, e);
        }

        var blobs = new List<GeometryKernelBlob>();
        long at = 0;
        if (header["blobs"] is JsonArray decls)
        {
            foreach (var d in decls)
            {
                string? name = d?["name"]?.GetValue<string>();
                var type = GeometryKernelBlob.Parse(d?["type"]?.GetValue<string>());
                long count = d?["count"] is JsonValue c && c.TryGetValue(out long n) ? n : -1;
                if (name is null || type is null || count < 0)
                    throw new InvalidDataException("A frame declares a blob without a name, a known type and a count.");
                long len = count * GeometryKernelBlob.UnitOf(type.Value);
                if (at + len > bin.Length) throw new InvalidDataException($"Blob '{name}' runs past the frame's binary part.");
                blobs.Add(new GeometryKernelBlob(name, type.Value, bin.AsSpan((int)at, (int)len).ToArray()));
                at += len;
            }
        }
        if (at != bin.Length) throw new InvalidDataException("A frame carries bytes no blob declares.");
        return new GeometryKernelMessage(header, blobs);
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends — a pipe may hand back
    /// fewer bytes than asked on any read. Returns how many arrived.</summary>
    private static int Fill(Stream stream, byte[] buffer)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            int n = stream.Read(buffer, at, buffer.Length - at);
            if (n == 0) break;
            at += n;
        }
        return at;
    }
}
