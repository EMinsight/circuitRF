using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Devices.Planar;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Tests.Devices.Microstrip;
using Xunit;
using static CircuitRF.Core.Tests.Devices.Planar.PlanarReferenceData;

namespace CircuitRF.Core.Tests.Devices.Planar;

/// <summary>brief-artsch-1's CPWG gate: the code is the formulas (1e-6 against an independent
/// restatement), the formulas are the physics (a field solve, each row's tolerance stated), a lossless line
/// is reciprocal and passive, and the far-ground and far-plane limits are MLIN and the coplanar line.</summary>
public class CpwgModelTests
{
    private static CoplanarLineModel Model(double w, double g, double h, double t, double er,
        double sigma = 5.8e7, double tanD = 0.0, double rough = 0.0)
        => new(w, g, 1e-3, h, t, er, sigma, tanD, "CPWG:X1", rough);

    [Fact]
    public void ImplementationTable_IsTheFormulas_To1e6()
    {
        var rows = Implementation("CPWG");
        Assert.True(rows.Count >= 20);
        foreach (var r in rows)
        {
            var p = Model(r.W, r.GOrH1, r.HOrH2, r.T, r.Er, r.Sigma, r.TanD, r.Roughness).LineParameters(r.F);
            string at = $"W={r.W} G={r.GOrH1} H={r.HOrH2} f={r.F}";
            Assert.True(Rel(p.Z0, r.Z0) < 1e-6, $"Z0 {p.Z0} vs {r.Z0} at {at}");
            Assert.True(Rel(p.Eeff, r.Eeff) < 1e-6, $"eeff {p.Eeff} vs {r.Eeff} at {at}");
            Assert.True(Rel(p.ConductorLossNpPerM, r.AlphaC) < 1e-6, $"alphaC {p.ConductorLossNpPerM} vs {r.AlphaC} at {at}");
            Assert.True(Rel(p.DielectricLossNpPerM, r.AlphaD) < 1e-6, $"alphaD {p.DielectricLossNpPerM} vs {r.AlphaD} at {at}");
        }
    }

    /// <summary>Tolerances from testdata/planar-lines/README.md: where the coplanar ground is close the
    /// model is Ghione–Naldi with the slot walls, within 1 %; where it is far the model is MLIN's
    /// Hammerstad–Jensen, whose thickness correction puts εeff up to 4.1 % high on 35 µm copper.</summary>
    [Fact]
    public void PhysicsTable_AgreesWithTheFieldSolve_ToEachBranchsStatedTolerance()
    {
        var rows = Physics("CPWG");
        Assert.True(rows.Count >= 6);
        foreach (var r in rows)
        {
            var reporter = new MicrostripValidityReporter("check");
            var s = GroundedCoplanar.Static(r.W, r.GOrH1, r.HOrH2, r.T, r.Er, reporter);
            double zTol = s.Microstrip ? 0.025 : 0.01, eTol = s.Microstrip ? 0.045 : 0.01;
            string at = $"W={r.W} G={r.GOrH1} H={r.HOrH2} T={r.T} er={r.Er} ({(s.Microstrip ? "microstrip" : "coplanar")} branch)";
            Assert.True(Rel(s.Z0, r.Z0) < zTol, $"Z0 {s.Z0} vs field {r.Z0} at {at}");
            Assert.True(Rel(s.Eeff, r.Eeff) < eTol, $"eeff {s.Eeff} vs field {r.Eeff} at {at}");
        }
    }

    [Fact]
    public void ALosslessLine_IsReciprocalAndPassive()
    {
        var line = new CoplanarLineModel(1e-3, 0.2e-3, 25e-3, 0.508e-3, 35e-6, 3.66, 1e30, 0.0, "CPWG:X1");
        var mna = new CapturingMnaContext();
        line.Stamp(mna, new ElaboratedComponent("CPWG", "X1", [1, 2], new Dictionary<string, Value>(), line), 2 * Math.PI * 7e9);

        Complex y11 = mna.Entries[(1, 1)], y12 = mna.Entries[(1, 2)], y21 = mna.Entries[(2, 1)], y22 = mna.Entries[(2, 2)];
        Assert.True((y12 - y21).Magnitude < 1e-12 * y12.Magnitude);

        // S = (I − z0·Y)(I + z0·Y)⁻¹ at 50 Ω; lossless means |S11|² + |S21|² = 1, passive ≤ 1.
        const double z0 = 50;
        Complex a = 1 + z0 * y11, b = z0 * y12, c = z0 * y21, d = 1 + z0 * y22, det = a * d - b * c;
        Complex m11 = 1 - z0 * y11, m12 = -z0 * y12, m21 = -z0 * y21;
        Complex s11 = (m11 * d - m12 * c) / det, s21 = (m21 * d - (1 - z0 * y22) * c) / det;
        double power = s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude;
        Assert.InRange(power, 1 - 1e-9, 1 + 1e-9);
    }

    /// <summary>The coplanar ground taken 10⁵ substrate heights away: the line is MLIN, at every frequency,
    /// because the model hands both the capacitances and the dispersion to MLIN's own. (The slot walls fall
    /// off as t/G, so at a thousand heights they still move Z0 by 2e-5.)</summary>
    [Fact]
    public void AFarCoplanarGround_IsMlin()
    {
        double w = 1.0e-3, h = 1.0e-3, t = 35e-6, er = 4.4;
        var cpwg = new CoplanarLineModel(w, 1e5 * h, 1e-3, h, t, er, 5.8e7, 0.02, "CPWG:X1");
        var mlin = new MicrostripLineModel(w, 1e-3, h, t, er, 5.8e7, 0.02, "MLIN:X1");
        foreach (double f in new[] { 0.0, 1e9, 20e9 })
        {
            var a = cpwg.LineParameters(f);
            var b = mlin.LineParameters(f);
            Assert.True(Rel(a.Z0, b.Z0) < 1e-6, $"Z0 {a.Z0} vs MLIN {b.Z0} at {f}");
            Assert.True(Rel(a.Eeff, b.Eeff) < 1e-6, $"eeff {a.Eeff} vs MLIN {b.Eeff} at {f}");
        }
    }

    /// <summary>The backing plane taken far away: the coplanar line on a half-space of substrate,
    /// (η₀/4)·K(k′)/K(k)/√εeff with εeff = (εr+1)/2, zero thickness.</summary>
    [Fact]
    public void AFarBackingPlane_IsTheCoplanarLine()
    {
        double w = 0.3e-3, g = 0.2e-3, er = 9.8, h = 1e3 * (w + 2 * g);
        var s = GroundedCoplanar.Static(w, g, h, 0.0, er, new MicrostripValidityReporter("check"));

        double k = w / (w + 2 * g);
        double eeff = (er + 1) / 2;
        double z0 = PlanarLineLoss.Eta0 / 4 / EllipticRatio.Of(k, Math.Sqrt(1 - k * k)) / Math.Sqrt(eeff);
        Assert.False(s.Microstrip);
        Assert.True(Rel(s.Eeff, eeff) < 1e-4, $"eeff {s.Eeff} vs {eeff}");
        Assert.True(Rel(s.Z0, z0) < 1e-4, $"Z0 {s.Z0} vs {z0}");
    }
}
