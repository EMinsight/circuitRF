// brief-em3d-138 — a slab's outline walls are carved where a conductor's side lies on them, and what is left of them draws with
// no polygon offset (Scene3DWalls). The leak itself is MetalPrecedenceTests' board-side pair; these hold the geometry and the
// per-batch tie that close it.

using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

public sealed class BoardSideWallTests
{
    private const double mm = 1e-3;

    private static readonly Em3dMaterial[] Materials =
        [new Em3dMaterial("Sub", 4.4, null, 0, 1, 0), new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7)];

    private static Em3dProblem Problem(params Em3dSolid[] solids)
        => new(solids, [], Materials, [],
               new Em3dAirBox(new(-2 * mm, -2 * mm, -1 * mm), new(2 * mm, 2 * mm, 1 * mm),
                              new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                            Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing)),
               new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);

    private static Em3dSolid Slab(double z1 = 0.2)
        => new("slab", "Sub", Em3dRole.Dielectric,
               new Em3dExtrudedPolygon([new(-1 * mm, -1 * mm), new(1 * mm, -1 * mm), new(1 * mm, 1 * mm), new(-1 * mm, 1 * mm)], [], 0, z1 * mm), 1);

    /// <summary>Two conductors on the slab's y = −1 mm wall (its face 2), at two heights: a box flush with the slab's bottom, and a
    /// polygon with an extra vertex on that edge, thicker.</summary>
    private static readonly Em3dSolid Box = new("a", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.8 * mm, -1 * mm, 0), new(-0.3 * mm, -0.5 * mm, 0.02 * mm)), 2);
    private static readonly Em3dSolid Poly = new("b", "Cu", Em3dRole.Conductor,
        new Em3dExtrudedPolygon([new(0.1 * mm, -1 * mm), new(0.4 * mm, -1 * mm), new(0.7 * mm, -1 * mm), new(0.7 * mm, -0.6 * mm), new(0.1 * mm, -0.6 * mm)],
                                [], 0, 0.035 * mm), 3);

    private static Em3dTriangleMesh Plain(Em3dSolid s) => Em3dTessellation.Of(s);

    [Fact]
    public void TheWallPiecesAndTheCoppersSides_TileTheWall_WithNoCopperVertexMidEdge()
    {
        var slab = Slab();
        var inner = Plain(slab);
        var carved = Scene3DWalls.Of(Problem(slab, Box, Poly))!.Carved(slab, slab.Primitive, inner)!.Value;
        Assert.Equal(new[] { 2 }, carved.Faces);
        var mesh = carved.Make();
        var pieces = mesh.Triangles.Where(t => t.Face == 2).ToList();
        // the wall round the conductors is the free run at the end; the rest of the rebuilt wall keeps its slab's bias
        Assert.InRange(carved.Free, 1, pieces.Count - 1);
        Assert.All(mesh.Triangles.TakeLast(carved.Free), t => Assert.Equal(2, t.Face));

        // In the wall's own coordinates: x along it, z up.
        Vector2 W(int v) => new((float)(mesh.Vertices[v].X / mm), (float)(mesh.Vertices[v].Z / mm));
        Assert.All(pieces, t => Assert.All(new[] { t.A, t.B, t.C }, v => Assert.Equal(-1 * mm, mesh.Vertices[v].Y, 12)));
        (float X0, float X1, float Z1)[] copper = [(-0.8f, -0.3f, 0.02f), (0.1f, 0.7f, 0.035f)];
        double area = 0;
        foreach (var t in pieces)
        {
            Vector2 a = W(t.A), b = W(t.B), c = W(t.C);
            area += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
            var m = (a + b + c) / 3;
            Assert.DoesNotContain(copper, r => m.X > r.X0 && m.X < r.X1 && m.Y < r.Z1);
            // facing out of the slab, as the wall it replaces did
            var n = Vector3.Cross(Vector3.Subtract(P(mesh, t.B), P(mesh, t.A)), Vector3.Subtract(P(mesh, t.C), P(mesh, t.A)));
            Assert.True(n.Y < 0, $"a wall piece faces {n}");
        }
        double copperArea = copper.Sum(r => (r.X1 - r.X0) * r.Z1);
        Assert.Equal(2 * 0.2, area + copperArea, 5);

        // Every conductor vertex on the edge, at both its heights, is no wall piece's T-junction.
        Vector3[] corners = [.. copper.SelectMany(r => new[] { new Vector3(r.X0, 0, 0), new Vector3(r.X1, 0, 0), new Vector3(r.X0, 0, r.Z1), new Vector3(r.X1, 0, r.Z1) }),
                             new Vector3(0.4f, 0, 0), new Vector3(0.4f, 0, 0.035f)];
        Vector3 M(int v) => new((float)(mesh.Vertices[v].X / mm), (float)(mesh.Vertices[v].Y / mm + 1), (float)(mesh.Vertices[v].Z / mm));
        foreach (var p in corners)
            foreach (var t in pieces)
                foreach (var (a, b) in new[] { (M(t.A), M(t.B)), (M(t.B), M(t.C)), (M(t.C), M(t.A)) })
                {
                    if (Vector3.Distance(p, a) < 1e-5f || Vector3.Distance(p, b) < 1e-5f) continue;
                    float along = Vector3.Dot(p - a, b - a) / (b - a).LengthSquared();
                    Assert.False(along > 0 && along < 1 && Vector3.Distance(a + (b - a) * along, p) < 1e-5f,
                                 $"conductor vertex {p} lies mid-edge on wall piece {a}–{b}");
                }
    }

    [Fact]
    public void ACylinderTangentToTheWall_LeavesItUncarvedAndBiased()
    {
        var slab = Slab();
        const double r = 0.1;
        var post = new Em3dSolid("post", "Cu", Em3dRole.Conductor, new Em3dCylinder(new(0, (-1 + r) * mm, 0), new(0, (-1 + r) * mm, 0.2 * mm), r * mm), 4);
        var problem = Problem(slab, Box, post);
        Assert.Null(Scene3DWalls.Of(problem)!.Carved(slab, slab.Primitive, Plain(slab)));
        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false));
        uint id = scene.Objects.Single(o => o.Name == "slab").Id;
        var batch = Assert.Single(scene.Batches, b => b.ObjectId == id);
        Assert.Equal(Scene3DDepthTie.Behind, Scene3DFramePlan.TieOf(scene, batch));
    }

    [Fact]
    public void ACarvedSlab_DrawsItsFreeWallsUnbiased_AndPicksStillNameSlabAndCopper()
    {
        var problem = Problem(Slab(), Box);
        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false));
        uint slab = scene.Objects.Single(o => o.Name == "slab").Id, copper = scene.Objects.Single(o => o.Name == "a").Id;
        var batches = scene.Batches.Where(b => b.ObjectId == slab).ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(new[] { false, true }, batches.Select(b => b.Free));
        Assert.Equal(batches[0].FirstIndex + batches[0].IndexCount, batches[1].FirstIndex);
        Assert.Equal(Scene3DDepthTie.Behind, Scene3DFramePlan.TieOf(scene, batches[0]));
        Assert.Equal(Scene3DDepthTie.None, Scene3DFramePlan.TieOf(scene, batches[1]));

        const int w = 400, h = 300;
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h, Projection3D.Orthographic);
        (cam.Yaw, cam.Pitch) = (-80 * MathF.PI / 180, 10 * MathF.PI / 180);
        cam.Target = scene.ToLocal(-0.55 * mm, -1 * mm, 0.01 * mm);
        cam.Distance = 0.5e-3f;
        foreach (var (x, z, want) in new[] { (-0.55, 0.01, copper), (-0.55, 0.15, slab), (-0.15, 0.01, slab) })
        {
            var q = cam.Project(scene.ToLocal(x * mm, -1 * mm, z * mm), w, h);
            Assert.Equal(want, Scene3DPicking.Pick(scene, cam, q.X, q.Y, w, h, []).Id);
            Assert.Equal(want, Scene3DPicking.IdAtPixel(scene, cam, q.X, q.Y, w, h, []));
        }

        // Its drawn edges are the uncarved slab's: the rim round the copper is no edge line.
        var alone = Scene3DBuilder.Build(Problem(Slab()), 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false));
        Assert.Equal(alone.EdgeBatches.Single().VertexCount, scene.EdgeBatches.Single(e => e.ObjectId == slab).VertexCount);

        // A slab edit that keeps the carving's shape still patches rather than re-uploading the scene.
        var taller = Scene3DBuilder.Build(Problem(Slab(z1: 0.25), Box), 2,
                                          options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false));
        Assert.NotNull(Scene3DPatch.Between(scene, taller));
    }

    private static Vector3 P(Em3dTriangleMesh m, int v) => new((float)m.Vertices[v].X, (float)m.Vertices[v].Y, (float)m.Vertices[v].Z);
}
