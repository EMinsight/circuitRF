// Brief PM-1 R-pm1-3 — a graded fan at every re-entrant Manhattan corner.
//
// Found while merging touching copper (R-pm1-2): a patch's inset feed drawn as its own rectangle got a
// fan at its top edge only because that edge's two corners were CONVEX. Merged with the patch, the same
// edge is the bottom of the inset notch, its corners are re-entrant, and the mesher — which graded only
// at convex caps and long edges — gave it none. A re-entrant corner is where the current is most
// singular, so the gap was in the mesher, not in that drawing.
//
// Everything here is meshing. No Green's function, no fill, no solve.

using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class ReentrantCornerFanTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>A 10 mm patch with a 1 mm feed inset 3 mm into it through two 0.5 mm slots, drawn as ONE
    /// polygon — what the extractor hands the mesher once the feed and the patch are merged. Its four
    /// re-entrant corners are the notch bottoms: (4, 3), (4.5, 3), (5.5, 3), (6, 3) mm.</summary>
    private static PlanarProblem InsetPatch()
    {
        EmPoint P(double x, double y) => new(x * 1e-3, y * 1e-3);
        var ring = new List<EmPoint>
        {
            P(0, 0), P(4, 0), P(4, 3), P(4.5, 3), P(4.5, -5), P(5.5, -5),
            P(5.5, 3), P(6, 3), P(6, 0), P(10, 0), P(10, 10), P(0, 10),
        };
        return new([new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
                   GroundedSlab.Fr4Starter, 6e9);
    }

    private static PlanarMeshSettings Graded =>
        new(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3);

    [Fact]
    public void ANotchsReentrantCorners_CarryAttractors()
    {
        var problem = InsetPatch();
        var (_, edgeY) = SurfaceMesher.EdgeAttractors(problem);
        var (withX, withY) = SurfaceMesher.EdgeAttractors(problem, reentrantFans: true);
        _out.WriteLine($"edge rule alone Y = [{string.Join(", ", edgeY.Select(v => $"{v * 1e3:F3}"))}] mm; " +
                       $"with corners X = [{string.Join(", ", withX.Select(v => $"{v * 1e3:F3}"))}], " +
                       $"Y = [{string.Join(", ", withY.Select(v => $"{v * 1e3:F3}"))}] mm");

        // The notch bottom is two 0.5 mm edges with a re-entrant corner at each end: no cap, and far short
        // of a fifth of the part — the edge rule alone gives it nothing.
        Assert.DoesNotContain(edgeY, v => Math.Abs(v - 3e-3) < 1e-12);

        // Four corners share y = 3 mm: ONE attractor, not four.
        Assert.Single(withY, v => Math.Abs(v - 3e-3) < 1e-12);
        foreach (double x in new[] { 4e-3, 4.5e-3, 5.5e-3, 6e-3 })
            Assert.Single(withX, v => Math.Abs(v - x) < 1e-12);

        var report = SurfaceMesher.Mesh(problem, Graded);
        Assert.Contains("Edge fans at 4 re-entrant corner(s).", report.Notes);
    }

    /// <summary>R-pm1-3: with the edge mesh off nothing here runs. The numbers are the mesh this artwork got
    /// before the change, read off the mesher at the commit before it.</summary>
    [Fact]
    public void WithTheEdgeMeshOff_TheMeshIsTheOneItWasBefore()
    {
        var report = SurfaceMesher.Mesh(InsetPatch(), Graded with { EdgeMesh = false });
        var g = report.Mesh;
        _out.WriteLine($"N = {report.UnknownCount}, grid {g.GridX.Count} × {g.GridY.Count}, " +
                       $"Σx = {g.GridX.Sum():R}, Σy = {g.GridY.Sum():R}");

        Assert.DoesNotContain(report.Notes, n => n.Contains("re-entrant", StringComparison.Ordinal));
        Assert.Equal(PinnedUnknowns, report.UnknownCount);
        Assert.Equal(PinnedLinesX, g.GridX.Count);
        Assert.Equal(PinnedLinesY, g.GridY.Count);
        Assert.Equal(PinnedSumX, g.GridX.Sum());
        Assert.Equal(PinnedSumY, g.GridY.Sum());
    }

    private const int    PinnedUnknowns = 676;
    private const int    PinnedLinesX   = 41;
    private const int    PinnedLinesY   = 15;
    private const double PinnedSumX     = 0.20500000000000013;
    private const double PinnedSumY     = 0.03350000000000001;
}
