// brief-em3d-111 gate 1 — a small binary-glTF validator, written against the glTF 2.0 specification and nothing in src/: the header,
// the chunks, the JSON, every accessor inside its view and every view inside the buffer, indices below the vertex count, POSITION's
// min/max, NORMAL unit length, and every extension used declared in extensionsUsed and none required. Every export the tests make
// passes through it, and the readers below are what the other gates inspect a file with.

using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

internal sealed class GlbFile
{
    public JsonElement Json { get; }
    public byte[] Bin { get; }

    private GlbFile(JsonElement json, byte[] bin) { Json = json; Bin = bin; }

    /// <summary>Reads <paramref name="bytes"/>, asserting every structural rule; fails the test on the first one broken.</summary>
    public static GlbFile Validate(byte[] bytes)
    {
        Assert.True(bytes.Length >= 20, "shorter than a header and one chunk header");
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));                       // "glTF"
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((uint)bytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));            // "JSON"
        Assert.Equal(0, jsonLength % 4);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength)).RootElement.Clone();
        byte[] bin = [];
        int at = 20 + jsonLength;
        if (at < bytes.Length)
        {
            int binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4)));   // "BIN\0"
            Assert.Equal(0, binLength % 4);
            Assert.Equal(bytes.Length, at + 8 + binLength);
            bin = bytes.AsSpan(at + 8, binLength).ToArray();
        }
        else Assert.Equal(bytes.Length, at);

        Assert.Equal("2.0", json.GetProperty("asset").GetProperty("version").GetString());
        var file = new GlbFile(json, bin);
        if (json.TryGetProperty("buffers", out var buffers))
        {
            long bufferLength = buffers[0].GetProperty("byteLength").GetInt64();
            Assert.True(bufferLength <= bin.Length, "the buffer is longer than the BIN chunk");
            foreach (var view in json.GetProperty("bufferViews").EnumerateArray())
            {
                long offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt64() : 0;
                Assert.True(offset + view.GetProperty("byteLength").GetInt64() <= bufferLength, "a buffer view runs past the buffer");
            }
            for (int a = 0; a < json.GetProperty("accessors").GetArrayLength(); a++)
            {
                var acc = json.GetProperty("accessors")[a];
                var view = json.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
                long viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt64() : 0;
                int size = ComponentSize(acc.GetProperty("componentType").GetInt32());
                Assert.Equal(0, viewOffset % size);
                long need = (long)size * Components(acc.GetProperty("type").GetString()!) * acc.GetProperty("count").GetInt64();
                Assert.True(need <= view.GetProperty("byteLength").GetInt64(), $"accessor {a} runs past its buffer view");
            }
        }

        foreach (var mesh in Array(json, "meshes"))
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                var attrs = prim.GetProperty("attributes");
                int position = attrs.GetProperty("POSITION").GetInt32();
                var pos = file.Vec3(position);
                var acc = json.GetProperty("accessors")[position];
                var min = acc.GetProperty("min"); var max = acc.GetProperty("max");
                for (int c = 0; c < 3; c++)
                {
                    Assert.Equal(pos.Min(p => Get(p, c)), min[c].GetSingle());
                    Assert.Equal(pos.Max(p => Get(p, c)), max[c].GetSingle());
                }
                if (attrs.TryGetProperty("NORMAL", out var n))
                    foreach (var normal in file.Vec3(n.GetInt32())) Assert.InRange(normal.Length(), 1 - 1e-3, 1 + 1e-3);
                if (prim.TryGetProperty("indices", out var ix))
                    Assert.All(file.Indices(ix.GetInt32()), i => Assert.True(i < pos.Length, $"index {i} is past {pos.Length} vertices"));
            }

        var used = Array(json, "extensionsUsed").Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var m in Array(json, "materials"))
            if (m.TryGetProperty("extensions", out var ext))
                foreach (var p in ext.EnumerateObject()) Assert.Contains(p.Name, used);
        Assert.False(json.TryGetProperty("extensionsRequired", out _), "an extension is required: a viewer without it could not open the file");
        return file;
    }

    public static IEnumerable<JsonElement> Array(JsonElement e, string name)
        => e.TryGetProperty(name, out var a) ? a.EnumerateArray() : [];

    private static float Get(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;

    private static int ComponentSize(int type) => type switch { 5126 or 5125 => 4, 5123 => 2, 5121 => 1, _ => throw new InvalidDataException($"component type {type}") };

    private static int Components(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 0 };

    private ReadOnlySpan<byte> Data(int accessor)
    {
        var acc = Json.GetProperty("accessors")[accessor];
        var view = Json.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
        int offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        return Bin.AsSpan(offset, view.GetProperty("byteLength").GetInt32());
    }

    public Vector3[] Vec3(int accessor)
    {
        var d = Data(accessor);
        int count = Json.GetProperty("accessors")[accessor].GetProperty("count").GetInt32();
        var v = new Vector3[count];
        for (int i = 0; i < count; i++)
            v[i] = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[(12 * i)..]), BinaryPrimitives.ReadSingleLittleEndian(d[(12 * i + 4)..]),
                               BinaryPrimitives.ReadSingleLittleEndian(d[(12 * i + 8)..]));
        return v;
    }

    public uint[] Indices(int accessor)
    {
        var acc = Json.GetProperty("accessors")[accessor];
        var d = Data(accessor);
        int count = acc.GetProperty("count").GetInt32();
        bool small = acc.GetProperty("componentType").GetInt32() == 5123;
        var r = new uint[count];
        for (int i = 0; i < count; i++) r[i] = small ? BinaryPrimitives.ReadUInt16LittleEndian(d[(2 * i)..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[(4 * i)..]);
        return r;
    }

    public JsonElement[] Nodes => [.. Array(Json, "nodes")];
    public JsonElement[] Meshes => [.. Array(Json, "meshes")];
    public JsonElement[] Materials => [.. Array(Json, "materials")];

    /// <summary>The node named <paramref name="name"/>, or null.</summary>
    public int? NodeNamed(string name)
    {
        var nodes = Nodes;
        for (int i = 0; i < nodes.Length; i++) if (nodes[i].TryGetProperty("name", out var n) && n.GetString() == name) return i;
        return null;
    }

    /// <summary>The children of node <paramref name="node"/>.</summary>
    public int[] Children(int node) => Nodes[node].TryGetProperty("children", out var c) ? [.. c.EnumerateArray().Select(x => x.GetInt32())] : [];

    /// <summary>The materials node <paramref name="node"/>'s mesh draws with.</summary>
    public JsonElement[] MaterialsOf(int node)
        => [.. Meshes[Nodes[node].GetProperty("mesh").GetInt32()].GetProperty("primitives").EnumerateArray()
                .Select(p => Materials[p.GetProperty("material").GetInt32()])];
}
