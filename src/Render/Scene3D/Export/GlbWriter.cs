// brief-em3d-111 R-em3d111-1a — the binary glTF container: one JSON chunk and one BIN chunk, each padded to 4 bytes, behind the
// 12-byte header (glTF 2.0 §4.4). It knows nothing about a scene: GltfExport decides what is IN the file and hands this the arrays.
//
// One buffer view per accessor, each starting on a 4-byte boundary, so every accessor is aligned for its component type and nothing
// needs a byteStride. The JSON is written in insertion order with shortest round-trip numbers and no timestamp, so the same content
// is the same bytes (gate 7).

using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Render.Scene3D.Export;

/// <summary>The binary chunk and the JSON arrays that describe it: buffer views and accessors, appended as the exporter goes.</summary>
public sealed class GlbWriter
{
    public const uint Magic = 0x46546C67, JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;
    public const int Float = 5126, UnsignedInt = 5125, UnsignedShort = 5123;
    private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    private readonly MemoryStream _bin = new();
    public JsonArray BufferViews { get; } = [];
    public JsonArray Accessors { get; } = [];

    /// <summary>A VEC3 float accessor of <paramref name="values"/>; with <paramref name="bounds"/>, its min and max (POSITION).</summary>
    public int Vec3(IReadOnlyList<Vector3> values, bool bounds = false)
    {
        var bytes = new byte[values.Count * 12];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12 * i), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12 * i + 4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12 * i + 8), v.Z);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        var accessor = Accessor(View(bytes, ArrayBuffer), Float, values.Count, "VEC3");
        if (bounds && values.Count > 0)
        {
            accessor["min"] = new JsonArray(min.X, min.Y, min.Z);
            accessor["max"] = new JsonArray(max.X, max.Y, max.Z);
        }
        return Accessors.Count - 1;
    }

    /// <summary>A SCALAR index accessor: UNSIGNED_SHORT when every index addresses fewer than 65,536 vertices, else UNSIGNED_INT.</summary>
    public int Indices(IReadOnlyList<uint> indices, int vertexCount)
    {
        bool small = vertexCount < 65536;
        var bytes = new byte[indices.Count * (small ? 2 : 4)];
        for (int i = 0; i < indices.Count; i++)
        {
            if (small) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), (ushort)indices[i]);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), indices[i]);
        }
        Accessor(View(bytes, ElementArrayBuffer), small ? UnsignedShort : UnsignedInt, indices.Count, "SCALAR");
        return Accessors.Count - 1;
    }

    private int View(byte[] bytes, int target)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        long offset = _bin.Length;
        _bin.Write(bytes);
        BufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bytes.Length, ["target"] = target });
        return BufferViews.Count - 1;
    }

    private JsonObject Accessor(int view, int componentType, int count, string type)
    {
        var a = new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
        Accessors.Add(a);
        return a;
    }

    /// <summary>The file: <paramref name="root"/> (its buffers, views and accessors are set here) as the JSON chunk, the binary
    /// chunk after it.</summary>
    public byte[] Finish(JsonObject root)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        if (_bin.Length > 0)
        {
            root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length });
            root["bufferViews"] = BufferViews;
            root["accessors"] = Accessors;
        }
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        int jsonPadded = (json.Length + 3) & ~3;
        int binLength = (int)_bin.Length;
        int total = 12 + 8 + jsonPadded + (binLength > 0 ? 8 + binLength : 0);
        var file = new byte[total];
        var s = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], JsonChunk);
        json.CopyTo(s[20..]);
        s.Slice(20 + json.Length, jsonPadded - json.Length).Fill((byte)' ');       // the JSON chunk pads with spaces (§4.4.3.1)
        if (binLength > 0)
        {
            int at = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(s[at..], (uint)binLength);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 4)..], BinChunk);
            _bin.GetBuffer().AsSpan(0, binLength).CopyTo(s[(at + 8)..]);
        }
        return file;
    }
}
