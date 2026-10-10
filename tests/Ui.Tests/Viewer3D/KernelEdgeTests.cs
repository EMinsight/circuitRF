// KernelEdgeTests.cs — a kernel solid's feature edges are the B-rep's own, not runs found in its tessellation. An imported STEP piece
// whose top face is degenerate meshed that face to no triangles, so no two triangles met along the top and its edges were never drawn.

using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

public sealed class KernelEdgeTests
{
    [Fact]
    public void AKernelSolidsEdges_AreDrawn_WhereItsTessellationHasNoTriangles()
    {
        const double S = 1e-3;
        // A unit box with every face meshed but the top (face 1), as the kernel hands back a face it cannot mesh.
        Point3[] v = [new(0, 0, 0), new(S, 0, 0), new(S, S, 0), new(0, S, 0), new(0, 0, S), new(S, 0, S), new(S, S, S), new(0, S, S)];
        int[][] quads = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        var tris = new List<Em3dTriangle>();
        for (int f = 0; f < quads.Length; f++)
            if (f != 1)
            {
                var q = quads[f];
                tris.Add(new Em3dTriangle(q[0], q[1], q[2], "box", f));
                tris.Add(new Em3dTriangle(q[0], q[2], q[3], "box", f));
            }
        var faces = quads.Select((_, f) => new Em3dShapeFace($"face{f + 1}", "plane", (0, 0, 0, S, S, S), 0, 0, 0)).ToList();
        var edges = new List<Em3dShapeEdge>();
        for (int f = 0; f < quads.Length; f++)
            for (int k = 0; k < 4; k++)
            {
                int a = quads[f][k], b = quads[f][(k + 1) % 4];
                int g = Enumerable.Range(0, quads.Length).First(o => o != f && quads[o].Contains(a) && quads[o].Contains(b));
                if (f < g) edges.Add(new Em3dShapeEdge($"face{f + 1}|face{g + 1}", $"face{f + 1}", $"face{g + 1}", "line", 0, [v[a], v[b]]));
            }
        var shape = new Em3dShapeSolid(ReadOnlyMemory<byte>.Empty, "0", new Em3dTriangleMesh(v, tris), faces, edges);
        var a0 = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem([new Em3dSolid("box", "Copper", Em3dRole.Conductor, shape, 0)], [], [], [],
            new Em3dAirBox(new Point3(-5e-3, -5e-3, -5e-3), new Point3(5e-3, 5e-3, 5e-3), new Em3dFaces(a0, a0, a0, a0, a0, a0)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);

        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0)));

        var batch = Assert.Single(scene.EdgeBatches);
        var drawn = scene.LineVertices.AsSpan(batch.FirstVertex, batch.VertexCount).ToArray();
        Assert.Equal(24, drawn.Length);                                               // all twelve edges
        bool Top(Scene3DVertex p) => Math.Abs(p.Z - S) < 1e-7;
        Assert.Equal(4, Enumerable.Range(0, 12).Count(k => Top(drawn[2 * k]) && Top(drawn[2 * k + 1])));   // the top's four among them
    }
}
