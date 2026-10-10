// A selected object's silhouette (Scene3DSilhouette) and the closed-volume bit the realistic view's faded selection reads.
// Builder-level, no GPU.

using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

public sealed class SilhouetteTests
{
    private static Scene3DModel Build(params Em3dSolid[] solids)
    {
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(solids, [], [], [],
            new Em3dAirBox(new Point3(-5e-3, -5e-3, -5e-3), new Point3(5e-3, 5e-3, 5e-3), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        return Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0)));
    }

    private static List<(Vector3 A, Vector3 B)> Outline(Scene3DModel scene, string name, Camera3D cam)
    {
        var o = scene.Objects.Single(x => x.Name == name);
        var into = new List<(Vector3 A, Vector3 B)>();
        Scene3DSilhouette.Collect(scene, o.Id, cam, Matrix4x4.Identity, into);
        return into;
    }

    [Fact]
    public void ASpheresSilhouetteIsItsRimAcrossTheView_ABoxHasNone()
    {
        const float r = 5e-4f;
        var scene = Build(new Em3dSolid("ball", "Copper", Em3dRole.Conductor, new Em3dSphere(new(0, 0, 0), r), 0),
                          new Em3dSolid("box", "Copper", Em3dRole.Conductor, new Em3dBox(new(2e-3, 2e-3, 0), new(3e-3, 3e-3, 1e-3)), 0));
        var cam = new Camera3D { Target = Vector3.Zero, Distance = 0.02f, FovY = Camera3D.DefaultFovY, Projection = Projection3D.Orthographic };
        cam.SetStandardView(StandardView3D.Front);

        var ring = Outline(scene, "ball", cam);
        Assert.True(ring.Count >= 8, $"{ring.Count} segments");
        // Seen along y, the rim lies near the xz great circle: every end at about the radius, close to y = 0.
        foreach (var p in ring.SelectMany(s => new[] { s.A, s.B }))
        {
            Assert.InRange(p.Length(), 0.9f * r, 1.01f * r);
            Assert.InRange(MathF.Abs(p.Y), 0f, 0.35f * r);
        }
        // It goes all the way round: ends in all four quadrants of the picture.
        Assert.Equal(4, ring.Select(s => (s.A.X > 0, s.A.Z > 0)).Distinct().Count());

        // A box's outline is its feature edges, which the GPU draws already.
        Assert.Empty(Outline(scene, "box", cam));
        // The realistic view's faded selection draws a solid's near side only.
        var ball = scene.Objects.Single(x => x.Name == "ball");
        Assert.All(scene.ShadeVertices.Skip(ball.FirstVertex).Take(ball.VertexCount),
                   v => Assert.NotEqual(0u, v.Slot & Scene3DShadeVertex.Closed));
    }
}
