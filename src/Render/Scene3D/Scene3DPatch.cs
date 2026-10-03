// brief-em3d-43 R-em3d43-1b / gate 6 — what changed between two scenes, as byte ranges of the three
// buffers a backend holds, so an edit to one object of a thousand uploads that object's bytes and not the
// scene's.
//
// A PATCH EXISTS ONLY WHEN THE LAYOUT IS THE SAME: the same buffer lengths, the same batches at the same
// offsets, the same objects over the same vertex ranges. Then every object's bytes are compared, and a
// range is listed only where they differ — a rename changes no byte at all and uploads nothing; a new
// material recolours one object and uploads its vertices. Anything else (an object added, deleted, grown,
// or the origin moved) has no patch, and the scene is uploaded whole, as before.
//
// brief-em3d-48 R-em3d48-3a — an array element owns no bytes: it is its prototype's triangles under a per-draw
// transform. So only the objects and batches that OWN geometry are compared for the layout, and an element added,
// removed or moved is counted as a TRANSFORM (ElementTransforms) — 80 bytes a frame's per-draw uniform carries —
// never as a range. Adding a column to a 20 × 20 array is 20 transforms and no geometry.

using System.Runtime.InteropServices;

namespace CircuitRF.Render.Scene3D;

/// <summary>Which of a scene's buffers a range is in.</summary>
public enum Scene3DPatchBuffer { Vertices, Indices, Lines }

/// <summary><see cref="ByteLength"/> bytes at <see cref="ByteOffset"/> of one buffer.</summary>
public readonly record struct Scene3DPatchRange(Scene3DPatchBuffer Buffer, int ByteOffset, int ByteLength);

/// <summary>The ranges to rewrite to turn one scene's buffers into another's.</summary>
public sealed class Scene3DPatch
{
    public required IReadOnlyList<Scene3DPatchRange> Ranges { get; init; }

    /// <summary>The bytes the ranges cover.</summary>
    public long Bytes => Ranges.Sum(r => (long)r.ByteLength);

    /// <summary>brief-em3d-48 — array elements standing where none stood before (by group and translation): each is one
    /// per-draw transform, and no geometry.</summary>
    public int ElementTransforms { get; init; }

    /// <summary>The patch from <paramref name="from"/> to <paramref name="to"/>, or null when their layouts
    /// differ and <paramref name="to"/> must be uploaded whole.</summary>
    public static Scene3DPatch? Between(Scene3DModel from, Scene3DModel to)
    {
        int oo = to.OwnedObjects, ob = to.OwnedBatches, oe = to.OwnedEdgeBatches;
        if (from.Vertices.Length != to.Vertices.Length || from.Indices.Length != to.Indices.Length ||
            from.LineVertices.Length != to.LineVertices.Length || from.OwnedObjects != oo ||
            from.OwnedBatches != ob || from.OwnedEdgeBatches != oe ||
            !from.Batches.AsSpan(0, ob).SequenceEqual(to.Batches.AsSpan(0, ob)) || !from.LineBatches.AsSpan().SequenceEqual(to.LineBatches) ||
            !from.EdgeBatches.AsSpan(0, oe).SequenceEqual(to.EdgeBatches.AsSpan(0, oe)))
            return null;
        // brief-em3d-101 — the image stream is not patched: a scene whose images or image vertices differ at all is uploaded whole
        // (its TEXTURES are kept by identity either way — Scene3DTextureResidency — so this costs vertices, never pixels).
        if (!from.ImageBatches.AsSpan().SequenceEqual(to.ImageBatches) || from.Images.Length != to.Images.Length ||
            !from.Images.Zip(to.Images).All(p => ReferenceEquals(p.First, p.Second)) ||
            !MemoryMarshal.AsBytes(from.ImageVertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(to.ImageVertices.AsSpan())))
            return null;
        for (int k = 0; k < oo; k++)
            if (from.Objects[k].FirstVertex != to.Objects[k].FirstVertex || from.Objects[k].VertexCount != to.Objects[k].VertexCount)
                return null;

        var ranges = new List<Scene3DPatchRange>();
        void Add(Scene3DPatchBuffer buf, int offset, int length)
        {
            if (length == 0) return;
            if (ranges.Count > 0 && ranges[^1] is var last && last.Buffer == buf && last.ByteOffset + last.ByteLength == offset)
                ranges[^1] = last with { ByteLength = last.ByteLength + length };
            else ranges.Add(new Scene3DPatchRange(buf, offset, length));
        }

        foreach (var o in to.Objects.Take(oo))
            if (!Same(from.Vertices, to.Vertices, o.FirstVertex, o.VertexCount))
                Add(Scene3DPatchBuffer.Vertices, o.FirstVertex * Scene3DVertex.Stride, o.VertexCount * Scene3DVertex.Stride);
        foreach (var b in to.Batches.Take(ob).OrderBy(b => b.FirstIndex))
            if (!Same(from.Indices, to.Indices, b.FirstIndex, b.IndexCount))
                Add(Scene3DPatchBuffer.Indices, b.FirstIndex * sizeof(uint), b.IndexCount * sizeof(uint));
        var unitBox = to.UnitBox.VertexCount > 0 ? [to.UnitBox] : Array.Empty<Scene3DLineBatch>();
        foreach (var b in to.LineBatches.Concat(unitBox).Concat(to.EdgeBatches.Take(oe)).OrderBy(b => b.FirstVertex))
            if (!Same(from.LineVertices, to.LineVertices, b.FirstVertex, b.VertexCount))
                Add(Scene3DPatchBuffer.Lines, b.FirstVertex * Scene3DVertex.Stride, b.VertexCount * Scene3DVertex.Stride);
        // An element is identified by where it stands, not by its index: a column added to an array renumbers the elements
        // after it, which changes their ids and not their translations.
        var had = new HashSet<(int, System.Numerics.Vector3)>(from.Elements.Select(e => (e.Group, e.Offset)));
        int moved = to.Elements.Count(e => !had.Contains((e.Group, e.Offset)));
        return new Scene3DPatch { Ranges = ranges, ElementTransforms = moved };
    }

    private static bool Same<T>(T[] a, T[] b, int first, int count) where T : unmanaged
        => MemoryMarshal.AsBytes(a.AsSpan(first, count)).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan(first, count)));

    /// <summary>The source bytes of <paramref name="r"/> in <paramref name="scene"/>.</summary>
    public static ReadOnlySpan<byte> Source(Scene3DModel scene, Scene3DPatchRange r) => r.Buffer switch
    {
        Scene3DPatchBuffer.Vertices => MemoryMarshal.AsBytes(scene.Vertices.AsSpan()).Slice(r.ByteOffset, r.ByteLength),
        Scene3DPatchBuffer.Indices  => MemoryMarshal.AsBytes(scene.Indices.AsSpan()).Slice(r.ByteOffset, r.ByteLength),
        _                           => MemoryMarshal.AsBytes(scene.LineVertices.AsSpan()).Slice(r.ByteOffset, r.ByteLength),
    };
}
