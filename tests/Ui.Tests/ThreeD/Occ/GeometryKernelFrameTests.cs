using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Ui.Tests.ThreeD.Occ;

/// <summary>brief-em3d-63 gate 4 — the frame codec (R-em3d63-4a), against in-memory streams.</summary>
public sealed class GeometryKernelFrameTests
{
    private static GeometryKernelMessage Sample() => new(
        new JsonObject { ["op"] = "tessellate", ["shape"] = "ab12", ["name"] = "µ-strip ✓" },
        [
            GeometryKernelBlob.OfDoubles("vertices", [0.1, -2.5e-300, double.MaxValue, -0.0]),
            GeometryKernelBlob.OfUInts("tris", [0, 1, uint.MaxValue]),
            GeometryKernelBlob.OfBytes("brep", [0, 255, 10, 13]),
        ]);

    /// <summary>A stream that hands back ONE byte per read, as a pipe may.</summary>
    private sealed class Trickle(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }

    [Fact]
    public void EveryBlobType_RoundTrips_WholeAndOneByteAtATime()
    {
        byte[] bytes = GeometryKernelFrame.Encode(Sample());

        foreach (var stream in new Stream[] { new MemoryStream(bytes), new Trickle(bytes) })
        {
            var m = GeometryKernelFrame.Read(stream)!;
            Assert.Equal("µ-strip ✓", m.Text("name"));
            Assert.Equal([0.1, -2.5e-300, double.MaxValue, -0.0], m.Blob("vertices")!.Doubles());
            Assert.True(double.IsNegative(m.Blob("vertices")!.Doubles()[3]));
            Assert.Equal([0u, 1u, uint.MaxValue], m.Blob("tris")!.UInts());
            Assert.Equal([0, 255, 10, 13], m.Blob("brep")!.Data);
            Assert.Null(GeometryKernelFrame.Read(stream));   // a clean end between frames
        }
        Assert.Equal(bytes, GeometryKernelFrame.Encode(GeometryKernelFrame.Decode(bytes)));
    }

    [Fact]
    public void TheLayout_IsLengthsThenJsonThenBytes_LittleEndian()
    {
        byte[] bytes = GeometryKernelFrame.Encode(new GeometryKernelMessage(new JsonObject { ["ok"] = true },
                                                                            [GeometryKernelBlob.OfUInts("x", [0x04030201])]));
        string json = """{"ok":true,"blobs":[{"name":"x","type":"u32","count":1}]}""";
        Assert.Equal((byte)json.Length, bytes[0]);
        Assert.Equal([0, 0, 0, 4, 0, 0, 0], bytes[1..8]);
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(bytes, 8, json.Length));
        Assert.Equal([1, 2, 3, 4], bytes[^4..]);
    }

    [Fact]
    public void AFrameCutShort_OrWithUndeclaredBytes_IsRefused()
    {
        byte[] bytes = GeometryKernelFrame.Encode(Sample());
        Assert.Throws<EndOfStreamException>(() => GeometryKernelFrame.Read(new MemoryStream(bytes[..^1])));
        Assert.Throws<EndOfStreamException>(() => GeometryKernelFrame.Read(new MemoryStream(bytes[..5])));

        byte[] extra = [.. bytes];
        extra[4]++;                                   // one more binary byte than the blobs declare
        Assert.Throws<InvalidDataException>(() => GeometryKernelFrame.Decode([.. extra, 0]));
    }
}
