using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-42 R-em3d42-1 — the neutral problem's polyhedron and its sheet in any plane: Validate
//  reports every problem a polyhedron has, the tessellation keeps each triangle's face, and the FDTD
//  grid puts a line on every axis-aligned edge and names an oblique face.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class Em3dPolyhedronTests
{
    private static readonly Point3[] Cube =
        [.. Enumerable.Range(0, 8).Select(k => new Point3(k & 1, (k >> 1) & 1, (k >> 2) & 1))];

    private static Em3dFace[] CubeFaces() =>
    [
        new([0, 4, 6, 2], [], "xmin"), new([1, 3, 7, 5], [], "xmax"),
        new([0, 1, 5, 4], [], "ymin"), new([2, 6, 7, 3], [], "ymax"),
        new([0, 2, 3, 1], [], "zmin"), new([4, 5, 7, 6], [], "zmax"),
    ];

    [Fact]
    public void Validate_ReportsEveryProblem_NonPlanarOpenAndInsideOut_AtOnce()
    {
        var sound = new Em3dPolyhedron(Cube, CubeFaces());
        Assert.Empty(sound.Problems("c"));
        Assert.Equal(1, sound.SignedVolume(), 12);

        // One corner pushed out along its diagonal (its three faces bent), one face removed (open), every face
        // turned (negative volume).
        var bent = new Em3dPolyhedron([.. Cube.Select((v, k) => k == 7 ? new Point3(1.1, 1.1, 1.1) : v)], CubeFaces());
        Assert.Equal(3, bent.Problems("b").Count(p => p.Contains("is not planar", StringComparison.Ordinal)));
        var open = new Em3dPolyhedron(Cube, CubeFaces()[..5]);
        Assert.Contains(open.Problems("o"), p => p.Contains("is not closed", StringComparison.Ordinal));
        var inside = new Em3dPolyhedron(Cube, [.. CubeFaces().Select(f => f with { Outer = [.. f.Outer.Reverse()] })]);
        var problems = inside.Problems("i");
        Assert.Contains(problems, p => p.Contains("no positive volume", StringComparison.Ordinal));

        // Through the problem: every one of them, for every solid.
        var box = new Em3dAirBox(new(-1, -1, -1), new(2, 2, 3), new(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
            Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing));
        var problem = new Em3dProblem(
            [new Em3dSolid("b", "m", Em3dRole.Conductor, bent, 1), new Em3dSolid("o", "m", Em3dRole.Conductor, open, 2)],
            [], [new Em3dMaterial("m", 1, null, 0, 1, 1e7)], [], box, new Em3dFrequency(1e9, 2e9, 2, Em3dSweepKind.Linear), 20);
        var all = problem.Validate();
        Assert.Contains(all, p => p.Contains("'b'", StringComparison.Ordinal) && p.Contains("not planar", StringComparison.Ordinal));
        Assert.Contains(all, p => p.Contains("'o'", StringComparison.Ordinal) && p.Contains("not closed", StringComparison.Ordinal));
    }

    [Fact]
    public void Tessellation_TriangulatesEachFaceInItsPlane_OutwardAndTaggedWithTheFace_HolesIncluded()
    {
        // A square tube: the caps are faces with holes.
        Point3 P(double x, double y, double z) => new(x, y, z);
        var v = new List<Point3>();
        foreach (double z in new[] { 0.0, 1.0 })
            foreach (var (x, y) in new[] { (0.0, 0.0), (3.0, 0.0), (3.0, 3.0), (0.0, 3.0), (1.0, 1.0), (2.0, 1.0), (2.0, 2.0), (1.0, 2.0) })
                v.Add(P(x, y, z));
        var faces = new List<Em3dFace>
        {
            new([0, 3, 2, 1], [[4, 5, 6, 7]], "bottom"), new([8, 9, 10, 11], [[12, 15, 14, 13]], "top"),
        };
        for (int k = 0; k < 4; k++)
        {
            int j = (k + 1) % 4;
            faces.Add(new([k, j, j + 8, k + 8], [], $"side{k}"));
            faces.Add(new([4 + k, 12 + k, 12 + j, 4 + j], [], $"hole0.side{k}"));
        }
        var tube = new Em3dPolyhedron(v, faces);
        Assert.Empty(tube.Problems("tube"));
        Assert.Equal(8, tube.SignedVolume(), 12);

        var mesh = Em3dTessellation.Of(new Em3dSolid("tube", "m", Em3dRole.Conductor, tube, 1));
        Assert.Equal(8, Em3dSizeEstimate.MeshVolume(mesh), 12);
        Assert.All(mesh.Triangles, t => Assert.InRange(t.Face, 0, faces.Count - 1));
        Assert.Equal(Enumerable.Range(0, faces.Count), mesh.Triangles.Select(t => t.Face).Distinct().Order());
        // Outward: each triangle's normal agrees with its face's.
        foreach (var t in mesh.Triangles)
        {
            var (a, b, c) = (mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C]);
            var n = tube.Normal(faces[t.Face]);
            double dot = ((b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y)) * n.X
                       + ((b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z)) * n.Y
                       + ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * n.Z;
            Assert.True(dot > 0, $"face {faces[t.Face].Name}");
        }
    }

    [Fact]
    public void Grid_PutsALineOnEveryAxisAlignedEdge_NamesAnObliqueFace_AndASheetInAPlaneTakesItsPlane()
    {
        const double mm = 1e-3;
        // A wedge: an x-axis prism whose triangular end is on YZ; its slope is normal to no axis.
        var wedge = new Em3dPolyhedron(
            [new(0, 0, 0), new(0, 2 * mm, 0), new(0, 0, 1 * mm), new(3 * mm, 0, 0), new(3 * mm, 2 * mm, 0), new(3 * mm, 0, 1 * mm)],
            [
                new([0, 2, 1], [], "end0"), new([3, 4, 5], [], "end1"),
                new([0, 1, 4, 3], [], "bottom"), new([0, 3, 5, 2], [], "back"), new([1, 2, 5, 4], [], "slope"),
            ]);
        Assert.Empty(wedge.Problems("w"));
        var sheet = new Em3dSheet("s", "m", [new(0, 0), new(1 * mm, 0), new(1 * mm, 1 * mm), new(0, 1 * mm)], [], 0, 0, 2)
        {
            Frame = new Em3dPlaneFrame(new Point3(5 * mm, 0, 0), new Point3(0, 1, 0), new Point3(0, 0, 1)),
        };
        var box = new Em3dAirBox(new(-2 * mm, -2 * mm, -2 * mm), new(8 * mm, 4 * mm, 3 * mm), new(Em3dBoundaryKind.Pec,
            Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pec));
        var problem = new Em3dProblem([new Em3dSolid("w", "m", Em3dRole.Conductor, wedge, 1)], [sheet],
            [new Em3dMaterial("m", 1, null, 0, 1, 1e7)], [], box, new Em3dFrequency(1e9, 2e9, 2, Em3dSweepKind.Linear), 20);
        Assert.Empty(problem.Validate());

        var settings = new OpenEmsGridSettings(20, 1.5, false, null, 8);
        var y = FdtdGrid.CollectRequired(problem, FdtdAxis.Y, settings, 1e-6);
        Assert.Contains(y, l => Math.Abs(l.PositionM - 2 * mm) < 1e-15 && l.Sources[0].Feature == "w");       // the bottom's far edge
        var x = FdtdGrid.CollectRequired(problem, FdtdAxis.X, settings, 1e-6);
        Assert.Contains(x, l => Math.Abs(l.PositionM - 5 * mm) < 1e-15 && l.Fixed && l.Sources[0].Kind == FdtdLineKind.SheetPlane);
        Assert.Contains(FdtdGrid.Build(problem, settings).Warnings, w => w.Contains("'w'", StringComparison.Ordinal) &&
                                                                        w.Contains("staircases", StringComparison.Ordinal));

        var flat = Em3dTessellation.OfSheet(sheet);
        Assert.All(flat.Vertices, q => Assert.Equal(5 * mm, q.X));
    }
}
