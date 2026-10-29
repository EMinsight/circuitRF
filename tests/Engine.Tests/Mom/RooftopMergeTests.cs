// Brief PM-2 Part 2 — rooftop merging: the merged system Tᵀ Z T, filled in panels.
//
// One test per claim the build makes: the panelled fill IS Fill when nothing merges, it IS Tᵀ Z T when
// something does, every macro is a coarse rooftop (so there is no line charge at a join), and the flag
// off changes nothing while the flag on changes only the unknown count the dense path holds.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class RooftopMergeTests(ITestOutputHelper output)
{
    private const double F = 6e9;

    /// <summary>A 10 mm patch with a 1 mm feed inset 3 mm through two 0.5 mm slots, as one polygon: the
    /// notch corners' fans cross the whole patch, which is what merging is for.</summary>
    private static PlanarProblem InsetPatch()
    {
        EmPoint P(double x, double y) => new(x * 1e-3, y * 1e-3);
        var ring = new List<EmPoint>
        {
            P(0, 0), P(4, 0), P(4, 3), P(4.5, 3), P(4.5, -5), P(5.5, -5),
            P(5.5, 3), P(6, 3), P(6, 0), P(10, 0), P(10, 10), P(0, 10),
        };
        return new([new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
                   GroundedSlab.Fr4Starter, F);
    }

    private static PlanarMeshSettings Graded(bool merge) =>
        new(Auto: false, CellsPerWavelength: 10, EdgeMesh: true, EdgeCells: 2, MinCellsAcrossConductor: 2,
            RooftopMerging: merge);

    private static PlanarFillSettings Lossy(PlanarProblem p) =>
        PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(p) };

    private static (Mat<Complex> Elemental, Mat<Complex> Merged, PlanarRooftopMerge Merge) Fills(
        PlanarMesh elemental, PlanarRooftopMerge merge, PlanarFillSettings st, int panelWidth)
    {
        var cores  = PlanarFill.BuildCores(elemental, st);
        var mcores = PlanarFill.BuildCores(elemental with { Merge = merge }, st);
        var k = PlanarLineFixtures.Kernel(GroundedSlab.Fr4Starter, F).For(cores, st.Order);
        double w = 2 * Math.PI * F;
        return (PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, w),
                PlanarFill.FillMerged(mcores, k.VectorPotential, k.Scalar, w, panelWidth), merge);
    }

    /// <summary>Every block a single cell ⇒ T = I ⇒ the panelled fill is <see cref="PlanarFill.Fill"/>
    /// to the bit, sheet term included, across panel boundaries that cut the triangle anywhere.</summary>
    [Fact]
    public void WhenNothingMerges_TheMergedFillIsFillToTheBit()
    {
        var problem = InsetPatch();
        var mesh = SurfaceMesher.Mesh(problem, Graded(false)).Mesh;
        var widths  = mesh.Cells.Select(c => c.Width).ToArray();
        var heights = mesh.Cells.Select(c => c.Height).ToArray();
        var merge = PlanarRooftopMerge.Build(mesh, widths, heights, Enumerable.Repeat(true, mesh.Cells.Count).ToArray());
        Assert.Equal(mesh.Bases.Count, merge.Count);

        var (z, zm, _) = Fills(mesh, merge, Lossy(problem), panelWidth: 37);
        int n = z.RowCount, differ = 0;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                if (z[i, j] != zm[i, j]) differ++;
        output.WriteLine($"N = {n}, entries that differ: {differ}");
        Assert.Equal(0, differ);
    }

    /// <summary>A real merge: the panelled fill equals Tᵀ Z T formed from the whole elemental matrix,
    /// to round-off.</summary>
    [Fact]
    public void TheMergedFill_IsTtZT()
    {
        var problem = InsetPatch();
        var mesh = SurfaceMesher.Mesh(problem, Graded(true)).Mesh;
        var merge = mesh.Merge!;
        var elemental = mesh with { Merge = null };
        Assert.True(merge.Count < mesh.Bases.Count);

        var (z, zm, _) = Fills(elemental, merge, Lossy(problem), panelWidth: 53);
        int n = z.RowCount, nm = merge.Count;
        var cols = Enumerable.Range(0, nm).Select(a => merge.Column(a).ToArray()).ToArray();
        double worst = 0, scale = 0;
        for (int b = 0; b < nm; b++)
            for (int a = 0; a < nm; a++)
            {
                Complex r = Complex.Zero;
                foreach (var (e, we) in cols[a])
                    foreach (var (f, wf) in cols[b]) r += we * wf * z[e, f];
                worst = Math.Max(worst, (zm[a, b] - r).Magnitude);
                scale = Math.Max(scale, r.Magnitude);
            }
        output.WriteLine($"N {n} → {nm}; worst |Δ| / max |entry| = {worst / scale:e2}");
        Assert.True(worst <= 1e-12 * scale, $"{worst / scale:e2}");
    }

    /// <summary>Every macro is a rooftop on its coarse support: its charge is uniform over the cells it
    /// ramps across on each side and is ±1 in total, so a join between a merged block and anything else
    /// carries no line charge.</summary>
    [Fact]
    public void EveryMacro_IsACoarseRooftop_WithNoChargeAtTheJoin()
    {
        var mesh = SurfaceMesher.Mesh(InsetPatch(), Graded(true)).Mesh;
        var merge = mesh.Merge!;
        int checkedMacros = 0;
        for (int a = 0; a < merge.Count; a++)
        {
            var charge = new Dictionary<int, double>();
            foreach (var (e, w) in merge.Column(a))
            {
                var (ha, hb) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[e]);
                charge[ha.CellIndex] = charge.GetValueOrDefault(ha.CellIndex) + w * ha.Sign;
                charge[hb.CellIndex] = charge.GetValueOrDefault(hb.CellIndex) + w * hb.Sign;
            }
            var pos = charge.Where(kv => Math.Abs(kv.Value) > 1e-12 && kv.Value > 0).ToList();
            var neg = charge.Where(kv => Math.Abs(kv.Value) > 1e-12 && kv.Value < 0).ToList();
            Assert.Equal(1.0, pos.Sum(kv => kv.Value), 12);
            Assert.Equal(-1.0, neg.Sum(kv => kv.Value), 12);
            foreach (var side in new[] { pos, neg })
            {
                double area = side.Sum(kv => mesh.Cells[kv.Key].Area);
                foreach (var (c, q) in side)
                    Assert.Equal(Math.Sign(q) / area, q / mesh.Cells[c].Area, 1e-9 / area);
            }
            if (merge.Column(a).Count() > 1) checkedMacros++;
        }
        output.WriteLine($"{merge.Count} macros, {checkedMacros} of them sums of more than one rooftop");
        Assert.True(checkedMacros > 0);
    }

    /// <summary>Off is off: no merge and the default's own mesh. On: the same cells and elemental
    /// rooftops, fewer unknowns solved, and a de-embedded answer close to the elemental one.</summary>
    [Fact]
    public void TheFlag_ChangesOnlyTheBasisSolved()
    {
        var problem = InsetPatch();
        var off  = SurfaceMesher.Mesh(problem, Graded(false));
        var on   = SurfaceMesher.Mesh(problem, Graded(true));
        Assert.False(PlanarMeshSettings.Default.RooftopMerging);
        Assert.Null(off.Mesh.Merge);
        Assert.Equal(off.Mesh.Cells, on.Mesh.Cells);
        Assert.Equal(off.Mesh.Bases, on.Mesh.Bases);
        Assert.Equal(on.Mesh.Merge!.Count, on.UnknownCount);
        Assert.True(on.UnknownCount < off.UnknownCount);

        // The end-to-end answer on the shipped resolution: the coarse fixture above merges a larger
        // share of a mesh that is one cell across in places, which is not what a user runs.
        var ports = new[] { new PlanarPort(1, new EmPoint(5e-3, -5e-3), PlanarPortSide.MinY, 50.0) };
        Complex S11(bool merge) =>
            new PlanarKernel().Solve(problem, new PlanarMeshSettings(Auto: false,
                                                                     RooftopMerging: merge),
                                     ports, [F]).Solve.Points[0].S[0, 0];
        Complex sOff = S11(false), sOn = S11(true);
        output.WriteLine($"coarse fixture N {off.UnknownCount} → {on.UnknownCount}; at the shipped mesh " +
                         $"S11 off {sOff}, on {sOn}, |ΔS11| = {(sOn - sOff).Magnitude:e2}");
        Assert.True((sOn - sOff).Magnitude < 1e-3, $"|ΔS11| = {(sOn - sOff).Magnitude:e2}");
    }
}
