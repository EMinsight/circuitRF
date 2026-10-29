// The planar solver's memory limit (2026-10-29): a setup states how much memory one frequency point may
// hold, in place of the shipped unknown ceilings, and a run asks its caller about its size before it fills.
// The limit is a MEMORY rather than a count because nobody knows their unknown count before meshing.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarMemoryLimitTests
{
    [Fact]
    public void CeilingFor_WithNoLimit_IsTheShippedCeiling_AndWithOne_IsTheLargestCountThatFits()
    {
        Assert.Equal(SurfaceMesher.UnknownCeiling, SurfaceMesher.CeilingFor(false, null));
        Assert.Equal(SurfaceMesher.AcceleratedUnknownCeiling, SurfaceMesher.CeilingFor(true, null));

        const long Limit = 6_000_000_000;
        int n = SurfaceMesher.CeilingFor(false, Limit, cellCount: 9_000);
        Assert.True(PlanarSystem.ResidentBytes(n, 9_000) <= Limit);
        Assert.True(PlanarSystem.ResidentBytes(n + 1, 9_000) > Limit);

        // The accelerator holds far less than the dense matrix the count is sized on, so a small limit
        // never takes its ceiling below the shipped one.
        Assert.Equal(SurfaceMesher.AcceleratedUnknownCeiling, SurfaceMesher.CeilingFor(true, 1_000_000_000));
    }

    [Fact]
    public void TheMeshVerdict_IsJudgedAgainstTheLimit_AndTheRefusalSaysWhatTheMeshNeeds()
    {
        var problem = PlanarLineFixtures.Fr4Line(50e-3, 10e9);
        var free    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping);
        Assert.True(free.CanSolve);

        long tight = PlanarSystem.ResidentBytes(free.UnknownCount, free.CellCount) / 2;
        var refused = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping with { MemoryLimitBytes = tight });
        Assert.Equal(PlanarBudgetVerdict.Refused, refused.Verdict);
        Assert.Contains("memory limit this setup sets", refused.Refusal!, StringComparison.Ordinal);
        Assert.Contains(SurfaceMesher.FormatBytes(PlanarSystem.ResidentBytes(free.UnknownCount, free.CellCount)),
                        refused.Refusal!, StringComparison.Ordinal);

        // The same mesh is solvable again under a limit large enough for it.
        Assert.Equal(free.UnknownCount, refused.UnknownCount);
        var roomy = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping with { MemoryLimitBytes = 50_000_000_000 });
        Assert.True(roomy.CanSolve);
        Assert.Contains("memory limit", roomy.Budget, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardsBelowTheMesher_HonourTheLimit()
    {
        int past = SurfaceMesher.UnknownCeiling + 1;
        Assert.Null(Record.Exception(() => PlanarSystem.GuardCeiling(past, limit: 50_000_000_000)));
        Assert.Null(Record.Exception(() => SurfaceMesher.GuardCeiling(past, false, limit: 50_000_000_000)));

        var ex = Assert.Throws<InvalidOperationException>(() => SurfaceMesher.GuardCeiling(2_000, false, limit: 10_000_000));
        Assert.Contains("memory limit this EM setup sets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimit_SurvivesAuto()
        => Assert.Equal(7_000_000_000,
                        (PlanarMeshSettings.Default with { MemoryLimitBytes = 7_000_000_000 }).Resolved.MemoryLimitBytes);

    [Fact]
    public void ADeclinedRun_ThrowsBeforeAnythingIsFilled_AndTheHookSeesTheDenseEstimate()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 5e9);
        PlanarRunSize? seen = null;
        var st = new PlanarSolveSettings(Deembed: false) { Admit = size => { seen = size; return "declined here"; } };

        var ex = Assert.Throws<PlanarRunDeclinedException>(() => new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Shipping, PlanarLineFixtures.EndPorts(problem), [5e9], st));

        Assert.Equal("declined here", ex.Message);
        Assert.NotNull(seen);
        Assert.False(seen!.Accelerated);
        Assert.Equal(PlanarSystem.ResidentBytes(seen.Unknowns, seen.Cells), seen.DenseResidentBytes);
    }
}
