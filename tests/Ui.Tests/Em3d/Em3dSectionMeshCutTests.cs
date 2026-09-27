// brief-em3d-70 — a section cuts a kernel solid: its tessellation arrives face by face, each face with its own copies of
// the nodes it shares with its neighbours, and Em3dSectionScene.MeshCut closed no loop through such a mesh, so every
// kernel solid was silently missing from `render --section` (the 3D Connector's housing and pin were).

using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class Em3dSectionMeshCutTests
{
    [Fact]
    public void ACubeTessellatedFaceByFace_CutsToOneClosedLoop()
    {
        var v = new List<Point3>();
        var t = new List<Em3dTriangle>();
        void Face(Point3 a, Point3 b, Point3 c, Point3 d)
        {
            int k = v.Count;
            v.AddRange([a, b, c, d]);                                   // this face's own copies: nothing is shared
            t.Add(new Em3dTriangle(k, k + 1, k + 2, "cube"));
            t.Add(new Em3dTriangle(k, k + 2, k + 3, "cube"));
        }
        Point3 P(double x, double y, double z) => new(x, y, z);
        Face(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0));
        Face(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1));
        Face(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));
        Face(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
        Face(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1));
        Face(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));

        var loop = Assert.Single(Em3dSectionScene.MeshCut(new Em3dTriangleMesh(v, t), 1, 0.5));
        Assert.InRange(loop.Count, 4, 8);
        Assert.All(loop, p => Assert.Equal(0.5, p.Y, 12));
    }
}
