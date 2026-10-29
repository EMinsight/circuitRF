// Brief PM-3 Part 1 — the two classification rules that keep a merge away from where it was measured
// to fail (RESOLVED.md §PM-3): a run may merge only rooftops TRANSVERSE to the current where the current
// model states a direction, and nothing within one narrowest-conductor width of a polygon vertex merges.
// One test per rule, stated on the merge map itself.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom;

public class RooftopMergeClassificationTests
{
    private static readonly GroundedSlab Slab = new(0.127e-3, new EmMaterial(3.0, 0.001));
    private const double F = 65e9;

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    /// <summary>A 10 mm × 0.347 mm line with an island beyond one end whose two horizontal edges raise
    /// thin fan rows across the whole line — the internal test geometry's output branch, reduced.
    /// <paramref name="vertical"/> swaps x and y.</summary>
    private static PlanarProblem LineWithRows(bool vertical)
    {
        var polys = new[] { Rect(0, 0, 10e-3, 0.347e-3), Rect(-6.3e-3, 0.0488e-3, -6.0e-3, 0.1525e-3) };
        if (vertical)
            polys = polys.Select(p => new PlanarPolygon(p.Outer.Select(v => new EmPoint(v.Y, v.X)).ToList())).ToArray();
        return new([new PlanarConductorLayer("Metal", polys, 5.8e7, 35e-6)], Slab, F);
    }

    private static readonly PlanarMeshSettings Tl =
        new(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3,
            CurrentModel: PlanarCurrentModel.TransmissionLine, RooftopMerging: true);

    /// <summary>The direction of every run of more than one cell: the basis direction of its
    /// macro's elemental rooftops (one direction per macro by construction).</summary>
    private static List<PlanarBasisDirection> RunDirections(PlanarMesh mesh)
    {
        var m = mesh.Merge!;
        var dirs = new List<PlanarBasisDirection>();
        for (int k = 0; k < m.Count; k++)
        {
            var col = m.Column(k).ToList();
            if (col.Count > 1) dirs.Add(mesh.Bases[col[0].Basis].Direction);
        }
        return dirs;
    }

    [Fact]
    public void WhereTheCurrentHasADirection_OnlyRooftopsTransverseToItMerge()
    {
        var along = SurfaceMesher.Mesh(LineWithRows(vertical: false), Tl);
        var dirs  = RunDirections(along.Mesh);
        Assert.NotEmpty(dirs);                                          // the fixture merges something
        Assert.All(dirs, d => Assert.Equal(PlanarBasisDirection.Y, d)); // current along x ⇒ y-runs only

        var up    = SurfaceMesher.Mesh(LineWithRows(vertical: true), Tl);
        var dirsU = RunDirections(up.Mesh);
        Assert.NotEmpty(dirsU);
        Assert.All(dirsU, d => Assert.Equal(PlanarBasisDirection.X, d)); // current along y ⇒ x-runs only
    }

    [Fact]
    public void NothingWithinOneConductorWidthOfAVertex_Merges()
    {
        // RooftopMergeTests' inset patch: the notch corners' fans cross the whole patch, and under the
        // per-axis current model there is no direction, so this rule is the only one acting.
        EmPoint P(double x, double y) => new(x * 1e-3, y * 1e-3);
        var ring = new List<EmPoint>
        {
            P(0, 0), P(4, 0), P(4, 3), P(4.5, 3), P(4.5, -5), P(5.5, -5),
            P(5.5, 3), P(6, 3), P(6, 0), P(10, 0), P(10, 10), P(0, 10),
        };
        var problem = new PlanarProblem([new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
                                        GroundedSlab.Fr4Starter, 6e9);
        var report = SurfaceMesher.Mesh(problem, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10,
                                                                        EdgeMesh: true, EdgeCells: 2,
                                                                        MinCellsAcrossConductor: 2, RooftopMerging: true));
        var mesh = report.Mesh; var merge = mesh.Merge;
        Assert.NotNull(merge);
        Assert.True(merge!.Count < mesh.Bases.Count, "the patch merged nothing");

        double r2 = report.NarrowestConductorWidthM * report.NarrowestConductorWidthM;
        for (int k = 0; k < merge.Count; k++)
        {
            var col = merge.Column(k).ToList();
            if (col.Count < 2) continue;
            foreach (var (e, _) in col)
                foreach (int c in new[] { mesh.Bases[e].CellA, mesh.Bases[e].CellB })
                {
                    var cell = mesh.Cells[c];
                    foreach (var v in ring)
                    {
                        double dx = v.X - cell.CenterX, dy = v.Y - cell.CenterY;
                        Assert.True(dx * dx + dy * dy >= r2 * (1 - 1e-9),
                                    $"a merged cell at ({cell.CenterX * 1e3:F3}, {cell.CenterY * 1e3:F3}) mm lies within " +
                                    $"{report.NarrowestConductorWidthM * 1e3:F3} mm of the vertex ({v.X * 1e3}, {v.Y * 1e3})");
                    }
                }
        }
    }
}
