using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  Two 3D-view pictures that did not show what the design is: a plated via drew as a solid copper rod under an
//  unbroken pad (its bore is a higher-order air cylinder the solver subtracts and a surface renderer cannot), and
//  Copy as Vector drew a perspective view orthographically.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class PlatedViaAndPerspectiveTests
{
    private const double Mm = 1e-3;

    /// <summary>A pad, a plated barrel through it and the barrel's air bore, in an air box.</summary>
    private static Em3dProblem Via(bool bore)
    {
        var pec = Em3dBoundaryKind.Pec;
        var lo = new Point3(-3 * Mm, -3 * Mm, 0); var hi = new Point3(3 * Mm, 3 * Mm, 2 * Mm);
        Point2[] pad = [new(-1 * Mm, -1 * Mm), new(1 * Mm, -1 * Mm), new(1 * Mm, 1 * Mm), new(-1 * Mm, 1 * Mm)];
        var solids = new List<Em3dSolid>
        {
            new("pad", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(pad, [], 0.5 * Mm, 0.535 * Mm), 1),
            new("via", "cu", Em3dRole.Conductor, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.535 * Mm), 0.15 * Mm), 2),
        };
        if (bore) solids.Add(new("via/fill", "air", Em3dRole.Air, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.535 * Mm), 0.125 * Mm), 3));
        return new Em3dProblem(solids, [], [new Em3dMaterial("air", 1, null, 0, 1, 0), new Em3dMaterial("cu", 1, null, 0, 1, 5.8e7)], [],
                               new Em3dAirBox(lo, hi, new Em3dFaces(pec, pec, pec, pec, pec, pec)),
                               new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    /// <summary>The barrel is drawn as a tube and the pad with a hole the bore's size; a problem with no bore costs nothing.</summary>
    [Fact]
    public void APlatedBarrel_IsDrawnAsATube_ThroughAHoleInItsPad()
    {
        Assert.Null(Scene3DBores.Of(Via(bore: false)));

        var problem = Via(bore: true);
        var bores = Scene3DBores.Of(problem)!;
        var pad = bores.Carved(problem.Solids[0])!.Value.Make();
        var tube = bores.Carved(problem.Solids[1])!.Value.Make();

        // Nothing of the pad's top covers the bore's centre; its own faces keep their numbers, the bore wall is no face.
        Assert.DoesNotContain(pad.Triangles.Where(t => t.Face == 1), t => Covers(pad, t, 0, 0));
        Assert.Contains(pad.Triangles, t => t.Face == -1);
        // The barrel has vertices on the bore's radius, and its top is an annulus that does not reach the axis.
        Assert.Contains(tube.Vertices, v => Math.Abs(Math.Sqrt(v.X * v.X + v.Y * v.Y) - 0.125 * Mm) < 1e-12);
        Assert.DoesNotContain(tube.Vertices, v => Math.Sqrt(v.X * v.X + v.Y * v.Y) < 0.1 * Mm);
    }

    private static bool Covers(Em3dTriangleMesh m, Em3dTriangle t, double x, double y)
    {
        var (a, b, c) = (m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C]);
        double d1 = (x - b.X) * (a.Y - b.Y) - (a.X - b.X) * (y - b.Y);
        double d2 = (x - c.X) * (b.Y - c.Y) - (b.X - c.X) * (y - c.Y);
        double d3 = (x - a.X) * (c.Y - a.Y) - (c.X - a.X) * (y - a.Y);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    /// <summary>
    /// A box seen from the front, from an eye 50 mm away and 20 mm to its right: the near face lies on the focal plane and
    /// lands where the orthographic view puts it (x = ±5), and the right face is seen converging toward the eye's axis —
    /// its far edge at 20 + (5 − 20)·50/70 = 9.286 mm, where the orthographic view shows no right face at all.
    /// </summary>
    [Fact]
    public void APerspectiveCamera_IsDrawnInPerspective_AtTheOrthographicScaleOnItsFocalPlane()
    {
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(
            [new Em3dSolid("b", "Copper", Em3dRole.Conductor, new Em3dBox(new(-5 * Mm, 0, -5 * Mm), new(5 * Mm, 20 * Mm, 5 * Mm)), 0)],
            [], [], [], new Em3dAirBox(new(-50 * Mm, -50 * Mm, -50 * Mm), new(50 * Mm, 50 * Mm, 50 * Mm), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        var front = Em3dProjection.Standard(Em3dStandardView.Front);
        double MaxU(Em3dProjection p) => Em3dSectionScene.Outline(problem, p, new(), out _).Lines.Max(l => Math.Max(l.A.U, l.B.U)) / Mm;
        double MinU(Em3dProjection p) => Em3dSectionScene.Outline(problem, p, new(), out _).Lines.Min(l => Math.Min(l.A.U, l.B.U)) / Mm;

        var perspective = front.WithEye(new Point3(20 * Mm, -50 * Mm, 0), 50 * Mm);
        Assert.Equal(5, MaxU(front), 9);
        Assert.Equal(20 + (5 - 20) * 50.0 / 70, MaxU(perspective), 6);
        Assert.Equal(-5, MinU(perspective), 6);
    }
}
