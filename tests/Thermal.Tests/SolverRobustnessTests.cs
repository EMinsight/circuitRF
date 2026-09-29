// The thermal series review (briefs 71–81): the solver's answers at the edges a sweep reaches — an isothermal point, and a
// body nothing holds at a temperature.

namespace CircuitRF.Thermal.Tests;

public sealed class SolverRobustnessTests
{
    private static readonly ThermalSolveOptions Direct = new() { Solver = ThermalSolverKind.Direct };

    private static ThermalMesh Cube(double x0) => TestMeshes.Box(TestMeshes.Linspace(x0, x0 + 1e-3, 2), TestMeshes.Linspace(0, 1e-3, 2),
                                                                 TestMeshes.Linspace(0, 1e-3, 2));

    /// <summary>No power and every boundary at one temperature: the span is round-off, and so is every Newton update, which a
    /// test relative to the span alone never accepted — 30 steps and "did not converge" on the exact answer.</summary>
    [Fact]
    public void KOfT_AtAnIsothermalPoint_Converges()
    {
        var p = new ThermalProblem
        {
            Mesh = Cube(0).ToSecondOrder(),
            Conductivity = [ThermalConductivity.Varying(150, t => (150 - 0.2 * (t - 25), -0.2))],
            Fixed = [new FixedTemperature(TestMeshes.ZMin, 25)],
            Convection = [new ConvectionCondition(TestMeshes.ZMax, 10, 25)],
        };
        var s = ThermalSolver.Solve(p, Direct);
        Assert.True(s.Converged, string.Join(" ", s.Notes));
        Assert.True(s.NewtonIterations < Direct.NewtonMaxIterations);
        Assert.All(s.Temperature, t => Assert.Equal(25, t, 1e-9));
    }

    /// <summary>A second body joined to nothing that fixes its temperature has a singular block: refused, naming its region,
    /// rather than factorised into a zero pivot or a silent 0 °C.</summary>
    [Fact]
    public void ABodyNothingHoldsAtATemperature_IsRefusedByRegion()
    {
        var mesh = MeshEdits.Merge(Cube(0), Cube(5e-3), tagOffset: 100, regionOffset: 1);
        var p = new ThermalProblem
        {
            Mesh = mesh,
            Conductivity = [ThermalConductivity.Constant(150), ThermalConductivity.Constant(150)],
            Fixed = [new FixedTemperature(TestMeshes.ZMin, 25)],
            VolumeSources = [new VolumeSource(1, 1e6)],
        };
        var x = Assert.Throws<FloatingRegionsException>(() => ThermalSolver.Solve(p, Direct));
        Assert.Equal([1], x.Regions);
    }
}
