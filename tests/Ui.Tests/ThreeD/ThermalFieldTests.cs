// ================================================================
//  ThermalFieldTests.cs — brief-em3d-75's field-side gates, headless and pixel-free:
//    3  the range: one hot node is the legend's top (the true maximum, D9)
//    4  the readout: FieldSampler inside a linear field returns the field's value
//    5  wire colouring: each ring's scalar is T at that ring's 3D arc length
//    6  Temperature Along: a straight line whose ends are the field's end values
//  The field files are written by brief 74's own writer (ThermalFieldFiles) and read back by the viewer's own reader
//  (FieldRun / FieldStep): no second reader, as the brief requires.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Thermal;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Thermal;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThermalFieldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th75f-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 3. the range ─────────────────────────────────────────────────────────────────────────────

    /// <summary>D9 — a field of 25 °C with one hot corner node at 500 °C: the range's top is exactly 500 (the maximum, not a
    /// percentile), its bottom 25, and the legend says "maximum". The EM default (99th percentile) would have clipped it.</summary>
    [Fact]
    public void Gate3_OneHotNode_IsTheTopOfTheRange()
    {
        var (mesh, _) = SyntheticThermal.Block(4, 10, 1, (_, _, _) => 25);
        var t = Enumerable.Repeat(25.0, mesh.NodeCount).ToArray();
        t[SyntheticThermal.NodeAt(mesh, 40, 0, 0)] = 500;         // a corner four drawn triangles share: under 1 % of the vertices
        var step = Open(mesh, [t]).First();
        var a = step.Load(FieldNames.TemperatureArray)!;
        var q = new FieldQuantity(a.Info, false, FieldMode.Value);
        Assert.True(q.IsTemperature);
        Assert.False(q.Signed);
        var surfaces = FieldSurfaces.Exterior(step.Mesh, a, (0, 0, 0)).Values.ToList();
        var scale = FieldColorScale.MinMax(q, surfaces);
        Assert.Equal(500, scale.Hi);
        Assert.Equal(25, scale.Lo);
        Assert.Contains("maximum", scale.Describe(), StringComparison.Ordinal);
        Assert.True(FieldColorScale.Auto(q, surfaces, false, 99).Hi < 500);    // what the EM default would have drawn
    }

    // ── 4. the readout ───────────────────────────────────────────────────────────────────────────

    /// <summary>At a point inside a quadratic mesh of a linear field, the sampler (the element's own shape functions, the
    /// hover readout's) returns the field's value there: every node value is an exact Float32, so nothing is lost but
    /// arithmetic.</summary>
    [Fact]
    public void Gate4_TheSamplerInsideALinearField_ReturnsItsValue()
    {
        static double T(double x, double y, double z) => 25 + 2 * x + 3 * y + 5 * z;      // µm → °C
        var (mesh, t) = SyntheticThermal.Block(2, 10, 2, T);
        var step = Open(mesh, [t]).First();
        var a = step.Load(FieldNames.TemperatureArray)!;
        var sampler = new FieldSampler(step.Mesh);
        Span<double> ch = stackalloc double[1];
        foreach (var (x, y, z) in new[] { (3.7, 11.2, 6.1), (13.3, 4.4, 17.9), (10.0, 10.0, 10.0) })
        {
            Assert.True(sampler.Sample(a, x, y, z, ch));
            Assert.Equal(T(x, y, z), ch[0], 1e-9);
        }
    }

    // ── 5. wire colouring ────────────────────────────────────────────────────────────────────────

    /// <summary>A two-segment wire that rises 100 µm and then runs 300 µm: its end is at s = 400 µm (the 3D length, not the
    /// 300 µm plan length), and every vertex of ring k carries T(s_k) of a table linear in s, to 1e-12.</summary>
    [Fact]
    public void Gate5_EachRingIsTAtItsOwn3DArcLength()
    {
        const double um = 1e-6;
        Point3[] path = [new(0, 0, 0), new(0, 0, 100 * um), new(300 * um, 0, 100 * um)];
        var sweep = new Em3dSweep(path, Em3dSection.Circle, 25 * um, [Square(path[0], 'z'), Square(path[1], 'x'), Square(path[2], 'x')]);
        var s = FieldWires.ArcLengths(path);
        Assert.Equal(400 * um, s[^1], 1e-18);

        double total = s[^1];
        var table = new WireTemperature("w1", [0, total], [30, 30 + 250 * total / (400 * um)]);   // 30 °C → 280 °C, linear in s
        var mesh = Em3dTessellation.Of(new Em3dSolid("w1", "Gold", Em3dRole.Conductor, sweep, 0));
        var origin = (0.0, 0.0, 0.0);
        var positions = mesh.Vertices.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToList();
        var indices = mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
        var painted = FieldWires.Colour("w1", sweep, positions, indices, origin, table)!;
        Assert.NotNull(painted);
        for (int r = 0; r < sweep.Rings.Count; r++)
            foreach (var p in sweep.Rings[r])
            {
                var local = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
                int v = Enumerable.Range(0, painted.Surface.VertexCount).First(i => positions[indices[i]] == local);
                Assert.Equal(s[r], painted.S[v]);
                Assert.Equal(30 + 250 * s[r] / (400 * um), painted.Surface.Values[v], 1e-12);
            }
        // No re-tessellation: the painted triangles are the scene's own, one vertex per index handed in.
        Assert.Equal(indices.Count, painted.Surface.VertexCount);
    }

    /// <summary>An array element's wire: the scene draws the prototype's vertices plus a float offset, which is not the float of
    /// the element's own ring points. Every vertex still finds its ring (it was left uncoloured, whole, on the first miss).</summary>
    [Fact]
    public void Gate5_AnArrayElementsWireIsColouredThroughItsFloatOffset()
    {
        const double um = 1e-6;
        var d = new Vector3(0.0123457f, 0.00731f, 0);                   // an array pitch that does not add exactly in float
        Point3 Shift(Point3 p) => new(p.X + d.X, p.Y + d.Y, p.Z + d.Z);
        Point3[] path = [new(0.001, 0.002, 0), new(0.001, 0.002, 100 * um), new(0.001 + 300 * um, 0.002, 100 * um)];
        var proto = new Em3dSweep(path, Em3dSection.Circle, 25 * um, [Square(path[0], 'z'), Square(path[1], 'x'), Square(path[2], 'x')]);
        var element = new Em3dSweep([.. path.Select(Shift)], Em3dSection.Circle, 25 * um, [.. proto.Rings.Select(r => (IReadOnlyList<Point3>)[.. r.Select(Shift)])]);
        var mesh = Em3dTessellation.Of(new Em3dSolid("w1", "Gold", Em3dRole.Conductor, proto, 0));
        var positions = mesh.Vertices.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z) + d).ToList();
        var indices = mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
        var table = new WireTemperature("w1", [0, 400 * um], [30, 280]);
        Assert.NotNull(FieldWires.Colour("w1", element, positions, indices, (0.0, 0.0, 0.0), table));
    }

    /// <summary>Plot Temperature on a face of a thin layer (a 25 µm attach under a 3 mm die) keeps its match tolerance inside
    /// the layer: 1 % of the diagonal (42 µm) reached the hidden opposite face, whose values then set the legend.</summary>
    [Fact]
    public void FaceMatchTolerance_StaysInsideAThinLayer()
    {
        double tol = CircuitRF.Ui.Viewer3D.Viewer3DViewModel.FaceMatchTolerance(Vector3.Zero, new Vector3(3e-3f, 3e-3f, 25e-6f));
        Assert.True(tol < 25e-6, $"tolerance {tol} m reaches the opposite face");
        // a chunky solid keeps 1 % of its diagonal
        Assert.Equal(0.01 * new Vector3(1e-3f).Length(), CircuitRF.Ui.Viewer3D.Viewer3DViewModel.FaceMatchTolerance(Vector3.Zero, new Vector3(1e-3f)), 1e-12);
    }

    /// <summary>Temperature Along whose end lies outside every solid says so (ΔT is not a number), rather than quoting the
    /// difference between two samples that are not the points picked.</summary>
    [Fact]
    public void ThermalLine_AnEndOutsideTheMeshGivesNoDeltaT()
    {
        var line = new CircuitRF.Ui.ThreeD.C3dThermalLine("t", [0, 1e-3, 2e-3], [double.NaN, 40, 50]);
        Assert.True(double.IsNaN(line.DeltaC));
        Assert.Equal(10.0, new CircuitRF.Ui.ThreeD.C3dThermalLine("t", [0, 1e-3], [40, 50]).DeltaC);
    }

    private static IReadOnlyList<Point3> Square(Point3 c, char normal)
    {
        const double h = 10e-6;
        return normal == 'z'
            ? [new(c.X - h, c.Y - h, c.Z), new(c.X + h, c.Y - h, c.Z), new(c.X + h, c.Y + h, c.Z), new(c.X - h, c.Y + h, c.Z)]
            : [new(c.X, c.Y - h, c.Z - h), new(c.X, c.Y + h, c.Z - h), new(c.X, c.Y + h, c.Z + h), new(c.X, c.Y - h, c.Z + h)];
    }

    // ── 6. Temperature Along ─────────────────────────────────────────────────────────────────────

    /// <summary>Between two points of a linear field, Temperature Along is a straight line: every sample, the two ends
    /// included, is the field's value there to 1e-9, and the distance axis runs 0 to the segment's length.</summary>
    [Fact]
    public void Gate6_TemperatureAlong_ALinearField_IsAStraightLineWithTheEndValues()
    {
        static double T(double x, double y, double z) => 25 + 2 * x + 3 * y + 5 * z;      // µm → °C
        var (mesh, t) = SyntheticThermal.Block(2, 10, 2, T);
        var step = Open(mesh, [t]).First();
        var a = step.Load(FieldNames.TemperatureArray)!;
        var q = new FieldQuantity(a.Info, false, FieldMode.Value);
        (double X, double Y, double Z) from = (1e-6, 2e-6, 3e-6), to = (19e-6, 15e-6, 11e-6);
        var line = FieldLine.Along(new FieldSampler(step.Mesh), a, q, 1e-6, from, to, 51);
        Assert.Equal(T(1, 2, 3), line.Values[0], 1e-9);
        Assert.Equal(T(19, 15, 11), line.Values[^1], 1e-9);
        double length = Math.Sqrt(18 * 18 + 13 * 13 + 8 * 8) * 1e-6;
        Assert.Equal(length, line.Length, 1e-18);
        for (int i = 0; i < line.Values.Length; i++)
        {
            double f = line.Distance[i] / length;
            Assert.Equal(T(1 + 18 * f, 2 + 13 * f, 3 + 8 * f), line.Values[i], 1e-9);
        }
    }

    /// <summary>brief-em3d-76 R-em3d76-4c — a symmetry plane's mirrored half: every vertex reflected, each triangle's winding
    /// reversed (so it still faces out), the same values, and the recipe reordered with the vertices so a sweep step revalues
    /// the mirror as it does the original.</summary>
    [Fact]
    public void Brief76_AMirroredSurface_ReflectsReversesAndKeepsItsValuesAndRecipe()
    {
        var s = new FieldSurface
        {
            Channels = 1, Xyz = [1, 0, 0, 2, 0, 0, 1, 1, 0], Values = [10, 20, 30],
            Recipe = new FieldRecipe { A = [4, 5, 6], B = [-1, -1, -1], T = [0, 0, 0], Cell = [0, 0, 0], NodeCount = 7, CellCount = 1 },
        };
        var m = s.Mirrored(0, 0.5);
        Assert.Equal([0, 0, 0, 0, 1, 0, -1, 0, 0], m.Xyz);          // (a, c, b), x → 1 − x
        Assert.Equal([10, 30, 20], m.Values);
        Assert.Equal([4, 6, 5], m.Recipe!.A);
        static Vector3 N(double[] x) => Vector3.Cross(new((float)(x[3] - x[0]), (float)(x[4] - x[1]), (float)(x[5] - x[2])),
                                                      new((float)(x[6] - x[0]), (float)(x[7] - x[1]), (float)(x[8] - x[2])));
        Assert.Equal(N(s.Xyz).Z, N(m.Xyz).Z);                          // reflected AND reversed: the same side faces out
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The steps of a thermal run written by brief 74's writer, opened by the viewer's reader.</summary>
    private IEnumerable<FieldStep> Open(ThermalMesh mesh, IReadOnlyList<double[]> steps)
    {
        string dir = Path.Combine(_root, "run" + Guid.NewGuid().ToString("N")[..6]);
        ThermalFieldFiles.Write(dir, mesh, steps, [.. Enumerable.Range(1, mesh.TetRegion.Max() + 1)]);
        var run = FieldRun.OpenPalace(dir)!;
        Assert.Equal(FieldProblemKind.Thermal, run.Kind);
        Assert.Equal(steps.Count, run.Solutions.Count);
        return run.Solutions.Select(s => FieldStep.Open(s.VolumePvtu!, run.ToMetres));
    }
}

/// <summary>A block of k³ cubes of <c>cell</c> µm, six tetrahedra each about the main diagonal, first or second order, with a
/// temperature field given in µm. Region 0 below mid-height, 1 above (two solids for the tests that want two).</summary>
internal static class SyntheticThermal
{
    public static (ThermalMesh Mesh, double[] T) Block(int k, double cell, int order, Func<double, double, double, double> t)
    {
        var nodes = new List<double>();
        var index = new Dictionary<(long, long, long), int>();
        // Keys in half-cells, so mid-edge nodes (order 2) share one index with every tetrahedron that uses them.
        int Node(double x, double y, double z)
        {
            var key = ((long)Math.Round(2 * x / cell), (long)Math.Round(2 * y / cell), (long)Math.Round(2 * z / cell));
            if (index.TryGetValue(key, out int n)) return n;
            index[key] = n = nodes.Count / 3;
            nodes.Add(x * 1e-6); nodes.Add(y * 1e-6); nodes.Add(z * 1e-6);
            return n;
        }
        int[][] six = [[0, 1, 3, 7], [0, 1, 5, 7], [0, 2, 3, 7], [0, 2, 6, 7], [0, 4, 5, 7], [0, 4, 6, 7]];
        var tets = new List<int>();
        var region = new List<int>();
        for (int l = 0; l < k; l++)
            for (int j = 0; j < k; j++)
                for (int i = 0; i < k; i++)
                    foreach (var tt in six)
                    {
                        var c = tt.Select(b => ((i + (b & 1)) * cell, (j + ((b >> 1) & 1)) * cell, (l + ((b >> 2) & 1)) * cell)).ToArray();
                        foreach (var (x, y, z) in c) tets.Add(Node(x, y, z));
                        if (order == 2)
                            foreach (var (a, b) in ThermalMesh.TetEdges)
                                tets.Add(Node((c[a].Item1 + c[b].Item1) / 2, (c[a].Item2 + c[b].Item2) / 2, (c[a].Item3 + c[b].Item3) / 2));
                        region.Add(2 * l < k ? 0 : 1);
                    }
        var mesh = new ThermalMesh([.. nodes], order, [.. tets], [.. region], [], []);
        var temps = new double[mesh.NodeCount];
        for (int n = 0; n < temps.Length; n++) temps[n] = t(nodes[3 * n] * 1e6, nodes[3 * n + 1] * 1e6, nodes[3 * n + 2] * 1e6);
        return (mesh, temps);
    }

    /// <summary>The node at (x, y, z) µm.</summary>
    public static int NodeAt(ThermalMesh mesh, double x, double y, double z)
    {
        for (int n = 0; n < mesh.NodeCount; n++)
            if (Math.Abs(mesh.Nodes[3 * n] * 1e6 - x) < 1e-9 && Math.Abs(mesh.Nodes[3 * n + 1] * 1e6 - y) < 1e-9 && Math.Abs(mesh.Nodes[3 * n + 2] * 1e6 - z) < 1e-9)
                return n;
        throw new ArgumentException($"no node at ({x}, {y}, {z}) µm");
    }
}
