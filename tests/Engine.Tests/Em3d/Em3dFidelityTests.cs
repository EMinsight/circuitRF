using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-em3d-65 §5 gates 5 and 7 — what the FDTD grid takes from a kernel solid, and the fidelity rows each solver gives it.
// Face tables built by hand (no geometry kernel): every property here is of a face table, a grid and a sentence.

public sealed class Em3dFidelityTests
{
    private const double Um = 1e-6;
    private static readonly Em3dMaterial Gold = new("Gold", 1, null, 0, 1, 4.1e7);
    private static readonly Em3dMaterial Ceramic = new("Ceramic", 9.8, null, 0, 1, 0);

    // ── 5. grid lines from a kernel box-with-a-bore conductor ─────────────────────────────────

    [Fact]
    public void Gate5_ABoxWithABore_GivesItsSixFaces_ThirdsAtItsStraightEdges_AndOnlyTheBoresExtremes()
    {
        // A 1000 × 1000 × 500 µm conductor, bored through at (500, 500) with r = 200 µm.
        var lid = new Shape().Box(0, 0, 0, 1000, 1000, 500).Bore("bore:side", 500, 500, 200, 0, 500).Solid();
        var p = Problem([new Em3dSolid("lid", "Gold", Em3dRole.Conductor, lid, 0)], 30e9);
        var settings = OpenEmsGridSettings.Default;

        List<FdtdLineSource> Lid(FdtdAxis a) =>
            [.. FdtdGrid.CollectRequired(p, a, settings, 1 * Um).SelectMany(l => l.Sources).Where(s => s.Feature == "lid")];

        // λ/20 at 30 GHz is 499.7 µm; no wider than 3/5 of the metal across the edge (600 µm on x and y, 300 µm on z).
        foreach (var axis in new[] { FdtdAxis.X, FdtdAxis.Y })
        {
            var lines = Lid(axis);
            Assert.Equal(8, lines.Count);
            Assert.Equal([0, 300, 700, 1000],
                         lines.Where(s => s.Kind == FdtdLineKind.MetalExtreme).Select(s => Math.Round(s.FeatureAtM / Um, 6)).Order());
            var inside = lines.Where(s => s.Kind == FdtdLineKind.ThirdsInside).ToList();
            var outside = lines.Where(s => s.Kind == FdtdLineKind.ThirdsOutside).ToList();
            Assert.Equal(2, inside.Count);
            Assert.Equal(2, outside.Count);
            Assert.All(inside.Concat(outside), s => Assert.Equal(299_792_458.0 / (30e9 * 20), s.LocalCellM!.Value, 12));
            // Nothing between the bore's extremes: its wall is staircased.
            var at = FdtdGrid.CollectRequired(p, axis, settings, 1 * Um).Where(l => l.Sources.Any(s => s.Feature == "lid"))
                             .Select(l => l.PositionM).ToList();
            Assert.DoesNotContain(at, v => v > 300.001 * Um && v < 699.999 * Um);
        }
        var z = Lid(FdtdAxis.Z);
        Assert.Equal(6, z.Count);                     // the two faces; the bore's extremes are those faces
        Assert.Equal(2, z.Count(s => s.Kind == FdtdLineKind.ThirdsInside));
        Assert.All(z.Where(s => s.Kind == FdtdLineKind.ThirdsInside), s => Assert.Equal(300 * Um, s.LocalCellM!.Value, 9));
    }

    // ── 7. the fidelity table ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(50, Em3dFidelitySeverity.Warning, "openEMS will not represent the 50 µm fillet on 'lid' (fillet(xmax|zmax)): the grid cell there is 100 µm, so the edge is solved as sharp.")]
    [InlineData(200, Em3dFidelitySeverity.Warning, "openEMS staircases the 200 µm fillet on 'lid' (fillet(xmax|zmax)) with about 2 cells; expect the answer near it to depend on the grid, not the radius.")]
    [InlineData(500, Em3dFidelitySeverity.Note, "openEMS staircases the curved faces of 'lid' at ≥ 4 cells across their smallest radius (500 µm); refining the grid converges them.")]
    public void Gate7a_OpenEms_CellsAcrossAFillet(double rUm, Em3dFidelitySeverity severity, string sentence)
    {
        var lid = new Shape().Box(0, 0, 0, 1000, 1000, 500).Fillet("fillet(xmax|zmax)", 1000 - rUm, 500 - rUm, rUm, 0, 1000).Solid();
        var rows = Em3dFidelity.For(Problem([new Em3dSolid("lid", "Gold", Em3dRole.Conductor, lid, 0)], 10e9),
                                    Em3dFidelitySolver.OpenEms, UniformGrid(100));
        var row = Assert.Single(rows);
        Assert.Equal((severity, sentence, "fillet(xmax|zmax)"), (row.Severity, row.Sentence, row.Face));
    }

    [Theory]
    [InlineData(50, Em3dFidelitySeverity.Warning, "openEMS will not represent the 50 µm chamfer on 'pin' (chamfer(xmax|zmax)): the grid cell there is 100 µm, so the edge is solved as square.")]
    [InlineData(200, Em3dFidelitySeverity.Note, "openEMS solves the 200 µm chamfer on 'pin' (chamfer(xmax|zmax)) as a staircase of about 2 steps.")]
    public void Gate7b_OpenEms_ChamferWidthAgainstTheCell(double wUm, Em3dFidelitySeverity severity, string sentence)
    {
        var pin = new Shape().Box(0, 0, 0, 1000, 1000, 500).Chamfer("chamfer(xmax|zmax)", 1000, 500, wUm, 0, 1000).Solid();
        var row = Assert.Single(Em3dFidelity.For(Problem([new Em3dSolid("pin", "Gold", Em3dRole.Conductor, pin, 0)], 10e9),
                                                 Em3dFidelitySolver.OpenEms, UniformGrid(100)));
        Assert.Equal((severity, sentence), (row.Severity, row.Sentence));
    }

    [Fact]
    public void Gate7c_FortyFillets_AreOneRow_NamingTheWorst()
    {
        var shape = new Shape().Box(0, 0, 0, 5000, 1000, 500);
        for (int i = 0; i < 40; i++)
        {
            double r = i == 17 ? 30 : 60 + i;                       // #17 is the worst
            shape.Fillet($"fillet(e{i})", 100 * i + 100, 500 - r, r, 0, 1000);
        }
        var row = Assert.Single(Em3dFidelity.For(Problem([new Em3dSolid("conn", "Gold", Em3dRole.Conductor, shape.Solid(), 0)], 10e9),
                                                 Em3dFidelitySolver.OpenEms, UniformGrid(100)));
        Assert.Equal("fillet(e17)", row.Face);
        Assert.Contains("the 30 µm fillet on 'conn'", row.Sentence, StringComparison.Ordinal);
        Assert.EndsWith("It is the worst of 40 rounded features on 'conn'.", row.Sentence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(15, false)]
    public void Gate7d_Palace_TheFlatSurfaceLossModel_UnderTenSkinDepths(double depths, bool warned)
    {
        double delta = Em3dFidelity.SkinDepth(1e9, Gold);
        double rUm = depths * delta / Um;
        var pin = new Shape().Box(0, 0, 0, 1000, 1000, 500).Fillet("pin:side", 500, 250, rUm, 0, 1000).Solid();
        var rows = Em3dFidelity.For(Problem([new Em3dSolid("pin", "Gold", Em3dRole.Conductor, pin, 0)], 10e9, startHz: 1e9),
                                    Em3dFidelitySolver.Palace, null);
        Assert.Equal(warned, rows.Count == 1);
        if (!warned) return;
        Assert.Equal(Em3dFidelitySeverity.Warning, rows[0].Severity);
        Assert.StartsWith("Palace treats the surface of 'pin' as flat for its loss; its ", rows[0].Sentence, StringComparison.Ordinal);
        Assert.Contains("is about 5 skin depths at 1 GHz", rows[0].Sentence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(2, false)]
    public void Gate7e_Palace_ASmallRadiusThatSetsTheMesh_IsANote(double timesSmaller, bool noted)
    {
        const double smallest = 100 * Um;
        double r = smallest / timesSmaller * Em3dFidelity.PalaceCurvatureElements / (2 * Math.PI);
        var lid = new Shape().Box(0, 0, 0, 1000, 1000, 500).Fillet("fillet(xmax|zmax)", 1000 - r / Um, 500 - r / Um, r / Um, 0, 1000).Solid();
        var rows = Em3dFidelity.For(Problem([new Em3dSolid("lid", "Ceramic", Em3dRole.Dielectric, lid, 0)], 10e9),
                                    Em3dFidelitySolver.Palace, null, smallest);
        Assert.Equal(noted, rows.Count == 1);
        if (!noted) return;
        Assert.Equal(Em3dFidelitySeverity.Note, rows[0].Severity);
        Assert.Contains("puts elements of about 5 µm on it — a twentieth of the smallest elsewhere — and will dominate the mesh.",
                        rows[0].Sentence, StringComparison.Ordinal);
        Assert.EndsWith("Disabling it (its Enabled box) shows whether it matters.", rows[0].Sentence, StringComparison.Ordinal);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static Em3dProblem Problem(IReadOnlyList<Em3dSolid> solids, double stopHz, double startHz = 1e9)
    {
        var a = Em3dBoundaryKind.Absorbing;
        var box = new Em3dAirBox(new Point3(-3000 * Um, -3000 * Um, -2000 * Um), new Point3(8000 * Um, 4000 * Um, 2500 * Um),
                                 new Em3dFaces(a, a, a, a, a, a));
        return new Em3dProblem(solids, [], [Gold, Ceramic], [], box, new Em3dFrequency(startHz, stopHz, 11, Em3dSweepKind.Linear), 25);
    }

    /// <summary>A grid of equal cells, from −5 mm to 10 mm on every axis.</summary>
    private static FdtdGridResult UniformGrid(double cellUm)
    {
        var lines = new List<double>();
        for (double v = -5000; v <= 10000 + 1e-9; v += cellUm) lines.Add(v * Um);
        FdtdAxisGrid Axis(FdtdAxis a) => new(a, lines, [], cellUm * Um, lines[0], [], 0, 0);
        return new FdtdGridResult(Axis(FdtdAxis.X), Axis(FdtdAxis.Y), Axis(FdtdAxis.Z), cellUm * Um, 1, 1e-15, 1e-10, 1, 1, [], [], null);
    }

    /// <summary>A face table and its triangles, in micrometres, built by hand as the kernel would report them.</summary>
    private sealed class Shape
    {
        private readonly List<Point3> _v = [];
        private readonly List<Em3dTriangle> _t = [];
        private readonly List<Em3dShapeFace> _f = [];
        private readonly List<Em3dShapeEdge> _e = [];

        private static Point3 P(double x, double y, double z) => new(x * Um, y * Um, z * Um);

        private void Face(string name, string kind, double radiusUm, IReadOnlyList<Point3[]> triangles)
        {
            int first = _t.Count;
            var pts = triangles.SelectMany(t => t).ToList();
            foreach (var tri in triangles)
            {
                int b = _v.Count;
                _v.AddRange(tri);
                _t.Add(new Em3dTriangle(b, b + 1, b + 2, "", _f.Count));
            }
            _f.Add(new Em3dShapeFace(name, kind, (pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z), pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z)),
                                     radiusUm * Um, first, triangles.Count));
        }

        private void Quad(string name, Point3 a, Point3 b, Point3 c, Point3 d) => Face(name, "plane", 0, [[a, b, c], [a, c, d]]);

        /// <summary>An axis-aligned box's six faces, outward, and its twelve straight edges.</summary>
        public Shape Box(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            Point3 V(int bits) => P((bits & 1) == 0 ? x0 : x1, (bits & 2) == 0 ? y0 : y1, (bits & 4) == 0 ? z0 : z1);
            string[] names = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
            int[][] quads = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
            for (int k = 0; k < 6; k++) Quad(names[k], V(quads[k][0]), V(quads[k][1]), V(quads[k][2]), V(quads[k][3]));
            // An edge per pair of adjacent faces: the corners they share.
            for (int i = 0; i < 6; i++)
                for (int j = i + 1; j < 6; j++)
                {
                    var shared = quads[i].Intersect(quads[j]).ToList();
                    if (shared.Count != 2) continue;
                    _e.Add(new Em3dShapeEdge($"{names[i]}|{names[j]}", names[i], names[j], "line", 0, [V(shared[0]), V(shared[1])]));
                }
            return this;
        }

        /// <summary>A bore's wall along z: a full cylinder, facets facing its axis.</summary>
        public Shape Bore(string name, double cx, double cy, double r, double z0, double z1)
        {
            var tris = new List<Point3[]>();
            const int n = 32;
            for (int k = 0; k < n; k++)
            {
                double a = 2 * Math.PI * k / n, b = 2 * Math.PI * (k + 1) / n;
                Point3 A0 = P(cx + r * Math.Cos(a), cy + r * Math.Sin(a), z0), B0 = P(cx + r * Math.Cos(b), cy + r * Math.Sin(b), z0);
                Point3 A1 = A0 with { Z = z1 * Um }, B1 = B0 with { Z = z1 * Um };
                tris.Add([A0, A1, B1]);
                tris.Add([A0, B1, B0]);
            }
            Face(name, "cylinder", r, tris);
            return this;
        }

        /// <summary>A quarter cylinder along y, centred at (cx, cz): a fillet on a box's +x/+z edge.</summary>
        public Shape Fillet(string name, double cx, double cz, double r, double y0, double y1)
        {
            var tris = new List<Point3[]>();
            const int n = 8;
            for (int k = 0; k < n; k++)
            {
                double a = Math.PI / 2 * k / n, b = Math.PI / 2 * (k + 1) / n;
                Point3 A0 = P(cx + r * Math.Cos(a), y0, cz + r * Math.Sin(a)), B0 = P(cx + r * Math.Cos(b), y0, cz + r * Math.Sin(b));
                Point3 A1 = A0 with { Y = y1 * Um }, B1 = B0 with { Y = y1 * Um };
                tris.Add([A0, B0, B1]);
                tris.Add([A0, B1, A1]);
            }
            Face(name, "cylinder", r, tris);
            return this;
        }

        /// <summary>A 45° chamfer of width <paramref name="w"/> on a box's +x/+z edge at (x, z), along y.</summary>
        public Shape Chamfer(string name, double x, double z, double w, double y0, double y1)
        {
            Quad(name, P(x - w, y0, z), P(x, y0, z - w), P(x, y1, z - w), P(x - w, y1, z));
            return this;
        }

        public Em3dShapeSolid Solid() => new(ReadOnlyMemory<byte>.Empty, "0123456789abcdef0123456789abcdef", new Em3dTriangleMesh(_v, _t), _f, _e);
    }
}
