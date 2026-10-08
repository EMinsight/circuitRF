using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Planar;
using Xunit;
using static CircuitRF.Core.Tests.Devices.Planar.PlanarReferenceData;

namespace CircuitRF.Core.Tests.Devices.Planar;

/// <summary>brief-artsch-1's SLIN gate: the code is the formulas (1e-6), the formulas are the physics (a
/// field solve, each row's tolerance stated), the offset form IS the centred one at zero offset, and which
/// plane is above changes nothing.</summary>
public class SlinModelTests
{
    private static StriplineModel Model(double w, double h1, double h2, double t, double er,
        double sigma = 5.8e7, double tanD = 0.0, double rough = 0.0)
        => new(w, 1e-3, h1, h2, t, er, sigma, tanD, "SLIN:X1", rough);

    [Fact]
    public void ImplementationTable_IsTheFormulas_To1e6()
    {
        var rows = Implementation("SLIN");
        Assert.True(rows.Count >= 20);
        foreach (var r in rows)
        {
            var p = Model(r.W, r.GOrH1, r.HOrH2, r.T, r.Er, r.Sigma, r.TanD, r.Roughness).LineParameters(r.F);
            string at = $"W={r.W} H1={r.GOrH1} H2={r.HOrH2} f={r.F}";
            Assert.True(Rel(p.Z0, r.Z0) < 1e-6, $"Z0 {p.Z0} vs {r.Z0} at {at}");
            Assert.True(Rel(p.Eeff, r.Eeff) < 1e-6, $"eeff {p.Eeff} vs {r.Eeff} at {at}");
            Assert.True(Rel(p.ConductorLossNpPerM, r.AlphaC) < 1e-6, $"alphaC {p.ConductorLossNpPerM} vs {r.AlphaC} at {at}");
            Assert.True(Rel(p.DielectricLossNpPerM, r.AlphaD) < 1e-6, $"alphaD {p.DielectricLossNpPerM} vs {r.AlphaD} at {at}");
        }
    }

    /// <summary>Tolerances from testdata/planar-lines/README.md: centred, Cohn exact with Wheeler's thickness
    /// ratio, 0.1 %; offset 2:1, the parallel combination, 1.5 %; offset 4:1 — outside the stated range,
    /// so the line also says so — 3.5 %.</summary>
    [Fact]
    public void PhysicsTable_AgreesWithTheFieldSolve_ToEachRowsStatedTolerance()
    {
        var rows = Physics("SLIN");
        Assert.True(rows.Count >= 6);
        foreach (var r in rows)
        {
            double ratio = Math.Max(r.GOrH1, r.HOrH2) / Math.Min(r.GOrH1, r.HOrH2);
            double tol = ratio <= 1.0 + 1e-9 ? 0.001 : ratio <= 2.0 + 1e-9 ? 0.015 : 0.035;
            var model = Model(r.W, r.GOrH1, r.HOrH2, r.T, r.Er);
            var p = model.LineParameters(0);
            Assert.True(Rel(p.Z0, r.Z0) < tol, $"Z0 {p.Z0} vs field {r.Z0} at W={r.W} H1={r.GOrH1} H2={r.HOrH2}");
            Assert.Equal(r.Eeff, p.Eeff, 12);
            bool warned = model.DrainWarnings().Any(w => w.Message.Contains("max(H1,H2)/min(H1,H2)"));
            Assert.Equal(ratio > 2.0 + 1e-9, warned);
        }
    }

    [Fact]
    public void TheOffsetForm_AtZeroOffset_IsTheCentredLine()
    {
        double w = 0.3e-3, h = 0.3e-3, t = 17e-6;
        Assert.Equal(Stripline.CentredAir(w, 2 * h + t, t), Stripline.Z0Air(w, h, h, t), 12);
    }

    [Fact]
    public void SwappingH1AndH2_ChangesNothing()
    {
        var a = Model(0.2e-3, 0.15e-3, 0.3e-3, 17e-6, 3.5, tanD: 0.004).LineParameters(10e9);
        var b = Model(0.2e-3, 0.3e-3, 0.15e-3, 17e-6, 3.5, tanD: 0.004).LineParameters(10e9);
        Assert.Equal(a, b);
    }
}
